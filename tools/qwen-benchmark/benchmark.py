"""Offline resident-worker latency/quality comparison. Uses only Python's standard library."""
from __future__ import annotations

import argparse
from collections import deque
import ctypes
from ctypes import wintypes
import hashlib
import json
import math
import os
from pathlib import Path
import queue
import re
import statistics
import subprocess
import threading
import time
import unicodedata
import uuid
import wave


def words(text):
    text = unicodedata.normalize('NFKC', text).lower().replace('’', "'")
    text = re.sub(r'\bmr\.', 'mister', text)
    text = re.sub(r'\bmrs\.', 'missus', text)
    text = re.sub(r'\bdr\.', 'doctor', text)
    return re.findall(r"[^\W_]+(?:'[^\W_]+)*", text)


def word_errors(reference, actual):
    expected, observed = words(reference), words(actual)
    previous = list(range(len(observed) + 1))
    for i, word in enumerate(expected, 1):
        current = [i]
        for j, other in enumerate(observed, 1):
            current.append(min(previous[j] + 1, current[-1] + 1, previous[j-1] + (word != other)))
        previous = current
    return previous[-1], len(expected)


def compare_reports(baseline, candidate):
    if not baseline.get('completed') or not candidate.get('completed'):
        raise ValueError('Only completed benchmark reports can be compared')
    if baseline.get('manifest_sha256') != candidate.get('manifest_sha256'):
        raise ValueError('Reports used different fixture manifests')
    previous = {r['id']: r for r in baseline['results']}
    current = {r['id']: r for r in candidate['results']}
    if previous.keys() != current.keys():
        raise ValueError('Reports have different sample ids')
    cases, regressions, unstable = [], [], []
    for identity, result in current.items():
        old = previous[identity]
        old_errors = min(r['errors'] for r in old['runs'])
        new_errors = max(r['errors'] for r in result['runs'])
        if new_errors > old_errors:
            regressions.append(identity)
        if not result['stable_text']:
            unstable.append(identity)
        cases.append(dict(id=identity, baseline_errors=old_errors, candidate_errors=new_errors,
            text_changed={r['text_sha256'] for r in old['runs']} != {r['text_sha256'] for r in result['runs']},
            speedup=old['median_seconds']/result['median_seconds'],
            baseline_median_seconds=old['median_seconds'], candidate_median_seconds=result['median_seconds'],
            baseline_p95_seconds=old['p95_seconds'], candidate_p95_seconds=result['p95_seconds']))
    return dict(cases=cases, regressions=regressions, unstable=unstable,
                quality_gate_passed=not regressions and not unstable)


class WindowsJob:
    """Kill only the owned worker and its descendants when the benchmark ends."""
    def __init__(self, process):
        self.handle = None
        if os.name != 'nt':
            return
        class Basic(ctypes.Structure):
            _fields_ = [('process_time', ctypes.c_int64), ('job_time', ctypes.c_int64),
                        ('flags', wintypes.DWORD), ('min_working_set', ctypes.c_size_t),
                        ('max_working_set', ctypes.c_size_t), ('active_processes', wintypes.DWORD),
                        ('affinity', ctypes.c_size_t), ('priority', wintypes.DWORD), ('scheduling', wintypes.DWORD)]
        class Extended(ctypes.Structure):
            _fields_ = [('basic', Basic), ('io', ctypes.c_uint64 * 6),
                        ('process_memory', ctypes.c_size_t), ('job_memory', ctypes.c_size_t),
                        ('peak_process_memory', ctypes.c_size_t), ('peak_job_memory', ctypes.c_size_t)]
        self.kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        self.kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        self.kernel.CreateJobObjectW.restype = wintypes.HANDLE
        self.kernel.SetInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        self.kernel.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.handle = self.kernel.CreateJobObjectW(None, None)
        limits = Extended()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not self.handle or not self.kernel.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)) or not self.kernel.AssignProcessToJobObject(self.handle, wintypes.HANDLE(process._handle)):
            error = ctypes.get_last_error()
            self.close()
            raise OSError(error, 'Cannot contain benchmark worker in an owned Windows job')

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None

    def confirm_owned_pid(self, pid):
        # Suspended creation attached the launcher before any user code ran.
        # With breakaway disabled, only that process and inherited descendants
        # can be members of this private job. Do not adopt arbitrary reported PIDs.
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.IsProcessInJob.argtypes = [wintypes.HANDLE, wintypes.HANDLE, ctypes.POINTER(wintypes.BOOL)]
        process = self.kernel.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not process:
            raise RuntimeError('Bootstrap PID is not an accessible owned process')
        try:
            owned = wintypes.BOOL()
            if not self.kernel.IsProcessInJob(process, self.handle, ctypes.byref(owned)) or not owned.value:
                raise RuntimeError('Bootstrap PID is not in the owned worker job')
        finally:
            self.kernel.CloseHandle(process)


def resume_suspended_process(process):
    """Resume the initial thread using documented Win32 APIs after containment."""
    class ThreadEntry(ctypes.Structure):
        _fields_ = [('size', wintypes.DWORD), ('usage', wintypes.DWORD),
                    ('identity', wintypes.DWORD), ('owner', wintypes.DWORD),
                    ('base_priority', wintypes.LONG), ('delta_priority', wintypes.LONG),
                    ('flags', wintypes.DWORD)]
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel.Thread32First.argtypes = [wintypes.HANDLE, ctypes.POINTER(ThreadEntry)]
    kernel.Thread32Next.argtypes = [wintypes.HANDLE, ctypes.POINTER(ThreadEntry)]
    kernel.OpenThread.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenThread.restype = wintypes.HANDLE
    kernel.ResumeThread.argtypes = [wintypes.HANDLE]
    kernel.ResumeThread.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    snapshot = kernel.CreateToolhelp32Snapshot(4, 0)  # TH32CS_SNAPTHREAD
    if snapshot == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        entry = ThreadEntry()
        entry.size = ctypes.sizeof(entry)
        found = kernel.Thread32First(snapshot, ctypes.byref(entry))
        while found:
            if entry.owner == process.pid:
                thread = kernel.OpenThread(2, False, entry.identity)  # THREAD_SUSPEND_RESUME
                if not thread:
                    raise ctypes.WinError(ctypes.get_last_error())
                try:
                    previous = kernel.ResumeThread(thread)
                    if previous != 1:
                        raise OSError('Unexpected initial-thread suspend count: ' + str(previous))
                    return
                finally:
                    kernel.CloseHandle(thread)
            found = kernel.Thread32Next(snapshot, ctypes.byref(entry))
        raise OSError('Initial suspended worker thread was not found')
    finally:
        kernel.CloseHandle(snapshot)


def start_owned_process(command, **options):
    """Return a process and job, with no Windows execution before job assignment."""
    if os.name == 'nt':
        options['creationflags'] = options.get('creationflags', 0) | subprocess.CREATE_NO_WINDOW | 4  # CREATE_SUSPENDED
    else:
        options['start_new_session'] = True
    process = subprocess.Popen(command, **options)
    job = None
    try:
        job = WindowsJob(process)
        if os.name == 'nt':
            resume_suspended_process(process)
        return process, job
    except BaseException:
        if job:
            job.close()
        if process.poll() is None:
            process.kill()
        process.wait(timeout=10)
        for stream in (process.stdin, process.stdout, process.stderr):
            if stream:
                stream.close()
        raise


class Worker:
    def __init__(self, command, startup_timeout=300, request_timeout=120):
        self.request_timeout = request_timeout
        self.messages = queue.Queue()
        self.stderr = deque(maxlen=20)
        self.job = None
        self.threads = []
        start = time.perf_counter()
        self.process, self.job = start_owned_process(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding='utf-8', bufsize=1)
        try:
            self.threads = [threading.Thread(target=target, daemon=True) for target in (self._read_stdout, self._read_stderr)]
            for thread in self.threads:
                thread.start()
            deadline = start + startup_timeout
            ready = self._message(max(0, deadline-time.perf_counter()))
            if ready.get('type') == 'bootstrap':
                pid = ready.get('pid')
                if (set(ready) != {'type', 'protocol', 'pid'} or type(ready.get('protocol')) is not int
                        or ready['protocol'] != 1 or type(pid) is not int or not 0 < pid <= 0xffffffff):
                    raise RuntimeError('Malformed worker bootstrap response')
                if os.name == 'nt':
                    self.job.confirm_owned_pid(pid)
                else:
                    try:
                        owned = os.getpgid(pid) == self.process.pid
                    except ProcessLookupError:
                        owned = False
                    if not owned:
                        raise RuntimeError('Bootstrap PID is not in the owned worker process group')
                self.process.stdin.write(json.dumps({'type': 'start', 'protocol': 1}) + '\n')
                self.process.stdin.flush()
                ready = self._message(max(0, deadline-time.perf_counter()))
            if ready.get('type') != 'ready' or ready.get('protocol') != 1:
                raise RuntimeError('Unsupported worker readiness response')
            self.startup_seconds = time.perf_counter() - start
        except BaseException:
            self.close()
            raise

    def _read_stdout(self):
        for line in self.process.stdout:
            self.messages.put(line)
        self.messages.put(None)

    def _read_stderr(self):
        for line in self.process.stderr:
            self.stderr.append(line.rstrip())

    def _message(self, timeout):
        try:
            line = self.messages.get(timeout=timeout)
        except queue.Empty as error:
            raise TimeoutError('Worker response deadline exceeded') from error
        if line is None:
            raise RuntimeError('Worker exited before its response')
        try:
            response = json.loads(line)
        except ValueError as error:
            raise RuntimeError('Worker emitted invalid JSON') from error
        if not isinstance(response, dict):
            raise RuntimeError('Worker response must be a JSON object')
        return response

    def transcribe(self, path):
        request_id = uuid.uuid4().hex
        start = time.perf_counter()
        self.process.stdin.write(json.dumps({'id': request_id, 'audioPath': str(path.resolve())}) + '\n')
        self.process.stdin.flush()
        response = self._message(self.request_timeout)
        elapsed = time.perf_counter() - start
        if response.get('id') != request_id:
            raise RuntimeError('Worker returned a mismatched request id')
        if response.get('type') != 'result' or not isinstance(response.get('text'), str):
            raise RuntimeError('Worker returned an unsuccessful transcription response')
        return response['text'], elapsed

    def close(self):
        if self.job:
            self.job.close()
        if os.name != 'nt':
            import signal
            try:
                os.killpg(self.process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            if self.process.poll() is None:
                self.process.kill()
            self.process.wait(timeout=10)
        for thread in self.threads:
            thread.join(timeout=2)
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
            if stream:
                stream.close()

    def __enter__(self):
        return self

    def __exit__(self, *unused):
        self.close()


def load_manifest(path):
    rows = json.loads(path.read_text(encoding='utf-8-sig'))
    if not isinstance(rows, list) or not rows:
        raise ValueError('Manifest must be a nonempty sample list')
    seen = set()
    for row in rows:
        if row['id'] in seen:
            raise ValueError('Duplicate sample id')
        seen.add(row['id'])
        if row.get('kind') not in ('public', 'synthetic') or not isinstance(row.get('reference'), str):
            raise ValueError('Only public/synthetic fixtures with independent reference text are accepted')
        if str(row['path']).startswith(('\\\\', '//')) or '://' in str(row['path']):
            raise ValueError('Fixture path must be local')
        audio = Path(row['path'])
        if not audio.is_absolute():
            audio = path.parent / audio
        audio = audio.resolve(strict=True)
        if str(audio).startswith(('\\\\', '//')):
            raise ValueError('Resolved fixture path must be local')
        if hashlib.sha256(audio.read_bytes()).hexdigest() != row['sha256']:
            raise ValueError(f"Fixture hash mismatch: {row['id']}")
        with wave.open(str(audio), 'rb') as wav:
            if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate(), wav.getcomptype()) != (1, 2, 16000, 'NONE'):
                raise ValueError('Fixtures must be 16 kHz mono PCM16 WAV')
            row['audio_seconds'] = wav.getnframes()/16000
        row['path'] = str(audio)
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--command', type=Path, required=True, help='JSON array of executable and arguments (no shell)')
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--baseline', type=Path, help='Compare to a completed report and fail on any per-case accuracy regression or unstable output')
    parser.add_argument('--repeats', type=int, default=3)
    parser.add_argument('--warmup', type=int, default=1)
    parser.add_argument('--startup-timeout', type=float, default=300)
    parser.add_argument('--request-timeout', type=float, default=120)
    args = parser.parse_args()
    if args.repeats < 1 or args.warmup < 0 or min(args.startup_timeout, args.request_timeout) <= 0:
        parser.error('Repeat/time values must be positive; warmup may be zero')
    samples = load_manifest(args.manifest.resolve())
    command = json.loads(args.command.read_text(encoding='utf-8-sig'))
    if not isinstance(command, list) or not command or not all(isinstance(s, str) for s in command):
        parser.error('Command must be a nonempty JSON string array')
    report = {'manifest_sha256': hashlib.sha256(args.manifest.read_bytes()).hexdigest(),
              'command': command, 'repeats': args.repeats, 'results': []}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    def save():
        args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    try:
        with Worker(command, args.startup_timeout, args.request_timeout) as worker:
            report['startup_seconds'] = worker.startup_seconds
            report['worker_pid'] = worker.process.pid
            report['warmup_seconds'] = []
            warmup = next((s for s in samples if s['reference']), samples[0])
            for _ in range(args.warmup):
                _, elapsed = worker.transcribe(Path(warmup['path']))
                report['warmup_seconds'].append(elapsed)
            for sample in samples:
                runs = []
                for repetition in range(args.repeats):
                    actual, elapsed = worker.transcribe(Path(sample['path']))
                    errors, reference_words = word_errors(sample['reference'], actual)
                    runs.append({'seconds': elapsed, 'errors': errors, 'reference_words': reference_words,
                        'characters': len(actual), 'text_sha256': hashlib.sha256(actual.encode()).hexdigest()})
                ordered = sorted(r['seconds'] for r in runs)
                result = {'id': sample['id'], 'speaker': sample.get('speaker'), 'audio_seconds': sample['audio_seconds'],
                    'median_seconds': statistics.median(ordered), 'p95_seconds': ordered[math.ceil(.95*len(ordered))-1],
                    'stable_text': len({r['text_sha256'] for r in runs}) == 1, 'runs': runs}
                report['results'].append(result)
                save()
                print(json.dumps({k:v for k,v in result.items() if k != 'runs'}), flush=True)
        report['completed'] = True
        if args.baseline:
            report['comparison'] = compare_reports(json.loads(args.baseline.read_text(encoding='utf-8-sig')), report)
    finally:
        save()
    if args.baseline and not report['comparison']['quality_gate_passed']:
        parser.exit(2, 'Candidate accuracy/stability gate failed; see per-case comparison in report.\n')


if __name__ == '__main__':
    main()
