"""Resident native Qwen ASR adapter; stdout uses the existing worker protocol."""
from __future__ import annotations

import argparse
import base64
import contextlib
import ctypes
from ctypes import wintypes
import io
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import wave

sys.dont_write_bytecode = True
import qwen_worker as worker


MODEL_ID = 'Qwen/Qwen3-ASR-1.7B-hf'
LLAMA_COMMIT = '620d2225cc436ab0b001e3200e590e3b286ef9ab'
MODEL_REVISION = '36a678687ba7d07a74ca70ccb0e36902e005fb80'
CONTEXT_TOKENS = 8192
# Conservatively reserve 4096 output tokens plus prompt overhead. Longer audio
# keeps the existing whole-recording Transformers path instead of being cut up.
NATIVE_MAX_SECONDS = 300


def read_config(path: Path) -> dict:
    path = worker.local_path(str(path))
    try:
        config = json.loads(path.read_text(encoding='utf-8-sig'))
    except (OSError, UnicodeError, ValueError):
        raise worker.WorkerError('The native Qwen configuration could not be read.') from None
    expected = {'schema':1, 'modelId':MODEL_ID, 'llamaCommit':LLAMA_COMMIT,
                'modelRevision':MODEL_REVISION}
    if (not isinstance(config, dict) or type(config.get('schema')) is not int
            or any(config.get(key) != value for key, value in expected.items())):
        raise worker.WorkerError('The native Qwen configuration has an unsupported model or revision.')
    for name in ('serverPath', 'modelPath', 'projectorPath'):
        config[name] = str(worker.local_path(config.get(name)))
    return config


def unused_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as reservation:
        reservation.bind(('127.0.0.1', 0))
        return reservation.getsockname()[1]


def server_command(config: dict, port: int, key: str, alias: str) -> list[str]:
    return [config['serverPath'], '-m', config['modelPath'], '--mmproj', config['projectorPath'],
            '--host', '127.0.0.1', '--port', str(port), '--alias', alias,
            '--api-key', key, '--device', 'CUDA0', '-ngl', '99', '-c', str(CONTEXT_TOKENS), '-np', '1',
            '--flash-attn', 'on', '--no-context-shift', '--no-webui', '--jinja', '--log-disable']


class WindowsJob:
    """Keep a child and its descendants alive only while their owner is alive."""
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
        self.kernel.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
        self.kernel.QueryInformationJobObject.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p,
                                                       wintypes.DWORD, ctypes.c_void_p]
        self.kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        self.handle = self.kernel.CreateJobObjectW(None, None)
        limits = Extended()
        limits.basic.flags = 0x2000  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not self.handle or not self.kernel.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)) or not self.kernel.AssignProcessToJobObject(self.handle, wintypes.HANDLE(process._handle)):
            error = ctypes.get_last_error()
            self.close()
            raise OSError(error, 'Cannot contain the native Qwen child in an owned Windows job')

    def close(self):
        if self.handle:
            class Accounting(ctypes.Structure):
                _fields_ = [('user_time', ctypes.c_int64), ('kernel_time', ctypes.c_int64),
                            ('period_user_time', ctypes.c_int64), ('period_kernel_time', ctypes.c_int64),
                            ('page_faults', wintypes.DWORD), ('total_processes', wintypes.DWORD),
                            ('active_processes', wintypes.DWORD), ('terminated_processes', wintypes.DWORD)]
            try:
                if not self.kernel.TerminateJobObject(self.handle, 1):
                    raise ctypes.WinError(ctypes.get_last_error())
                deadline = time.monotonic() + 10
                while True:
                    accounting = Accounting()
                    if not self.kernel.QueryInformationJobObject(self.handle, 1, ctypes.byref(accounting),
                                                                ctypes.sizeof(accounting), None):
                        raise ctypes.WinError(ctypes.get_last_error())
                    if accounting.active_processes == 0:
                        break
                    if time.monotonic() >= deadline:
                        raise worker.WorkerError('The previous Qwen process tree did not stop before its deadline.')
                    time.sleep(.01)
            finally:
                self.kernel.CloseHandle(self.handle)
                self.handle = None


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


def stop_owned_process(process, job) -> None:
    if job:
        job.close()
    if os.name != 'nt':
        import signal
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    if process.poll() is None:
        process.kill()
    process.wait(timeout=10)
    for stream in (process.stdin, process.stdout, process.stderr):
        if stream:
            stream.close()


class NoRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        # Even an erroneous listener must never forward local audio elsewhere.
        return None


class NativeBackend:
    def __init__(self, config_path: Path, *, startup_timeout: float = 120, request_timeout: float = 120):
        config = read_config(config_path)
        self.process = None
        self.job = None
        self.request_timeout = request_timeout
        self.port = unused_port()
        self.key = secrets.token_hex(32)
        # A foreign listener cannot accidentally satisfy readiness on a raced port.
        self.alias = 'qwen-local-' + secrets.token_hex(16)
        self.base_url = f'http://127.0.0.1:{self.port}'
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirects())
        try:
            self.process, self.job = start_owned_process(server_command(config, self.port, self.key, self.alias),
                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            deadline = time.monotonic() + startup_timeout
            while time.monotonic() < deadline:
                if self.process.poll() is not None:
                    raise worker.WorkerError('The native Qwen server exited during startup.')
                try:
                    response = self._call('/v1/models', timeout=min(1, max(.01, deadline-time.monotonic())))
                    ready = any(isinstance(item, dict) and item.get('id') == self.alias
                                for item in response.get('data', []))
                    if ready and self.process.poll() is None:
                        return
                except worker.WorkerError:
                    pass
                time.sleep(min(.05, max(0, deadline-time.monotonic())))
            raise worker.WorkerError('The native Qwen server did not become ready before its deadline.')
        except BaseException:
            self.close()
            raise

    def _call(self, route: str, body: dict | None = None, *, timeout: float | None = None) -> dict:
        request = urllib.request.Request(self.base_url + route,
            data=json.dumps(body).encode('utf-8') if body is not None else None,
            headers={'Content-Type':'application/json', 'Authorization':'Bearer ' + self.key})
        try:
            with self.opener.open(request, timeout=timeout or self.request_timeout) as response:
                result = json.load(response)
            if not isinstance(result, dict):
                raise ValueError('Response must be an object')
            return result
        except urllib.error.HTTPError as error:
            # Never expose server bodies, which may contain a recording or prompt.
            status = error.code
            error.close()
            raise worker.WorkerError(f'The native Qwen server rejected the request (HTTP {status}).') from None
        except (OSError, urllib.error.URLError, ValueError, UnicodeError):
            raise worker.WorkerError('The native Qwen server did not return a valid response before its deadline.') from None

    def transcribe(self, recording: worker.Recording) -> worker.GeneratedTranscript:
        if self.process is None or self.process.poll() is not None:
            raise worker.WorkerError('The native Qwen server is no longer running; restart the worker.')
        buffer = io.BytesIO()
        with wave.open(buffer, 'wb') as audio:
            audio.setparams((1, 2, recording.sample_rate, 0, 'NONE', 'not compressed'))
            audio.writeframes(recording.pcm)
        limit = worker.generation_budget(recording.duration_seconds)
        payload = {'model':self.alias,
            'messages':[{'role':'system', 'content':'English'},
                        {'role':'user', 'content':[{'type':'input_audio', 'input_audio':{
                            'data':base64.b64encode(buffer.getvalue()).decode('ascii'), 'format':'wav'}}]}],
            'temperature':0, 'top_k':1, 'repeat_penalty':1, 'max_tokens':limit,
            'seed':0, 'stream':False, 'cache_prompt':False}
        response = self._call('/v1/chat/completions', payload)
        try:
            choices = response['choices']
            if not isinstance(choices, list) or len(choices) != 1:
                raise ValueError('Unexpected choices')
            choice = choices[0]
            count = response['usage']['completion_tokens']
            prompt_count = response['usage']['prompt_tokens']
            if type(count) is not int or type(prompt_count) is not int or count < 0 or prompt_count < 1:
                raise ValueError('Unexpected usage')
            if (choice['finish_reason'] != 'stop' or count >= limit
                    or prompt_count + count > CONTEXT_TOKENS
                    or response.get('truncated', False) or choice.get('truncated', False)):
                raise worker.WorkerError('Native Qwen reached a context or output limit. No truncated transcript was returned; use a shorter recording.')
            raw = choice['message']['content']
            prefix = 'language English<asr_text>'
            if not isinstance(raw, str) or not raw.startswith(prefix) or raw.count('<asr_text>') != 1:
                raise ValueError('Unexpected transcription format')
            text = raw[len(prefix):].strip()
            if '<|' in text:
                raise ValueError('Unexpected control token')
            return worker.GeneratedTranscript(text, count, limit)
        except (KeyError, IndexError, TypeError, ValueError):
            raise worker.WorkerError('The native Qwen server returned an invalid transcription response.') from None

    def close(self) -> None:
        if self.process is not None:
            stop_owned_process(self.process, self.job)


def hf_command(model: Path) -> list[str]:
    return [sys.executable, '-u', str(Path(worker.__file__).resolve()), '--model', str(model)]


def run_hf_request(model: Path, line: str, *, timeout: float = 600) -> str:
    # A separate process keeps Torch/CUDA out of the native worker. Closing the
    # nested job also kills venv-launcher descendants on timeout or cancellation.
    process, job = start_owned_process(hf_command(model), stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding='utf-8')
    try:
        try:
            stdout, _ = process.communicate(input=line.rstrip('\r\n') + '\n', timeout=timeout)
        except subprocess.TimeoutExpired:
            raise worker.WorkerError('The whole-recording Qwen fallback exceeded its deadline.') from None
        if process.returncode != 0:
            raise worker.WorkerError('The whole-recording Qwen fallback exited unsuccessfully.')
        try:
            messages = [json.loads(value) for value in stdout.splitlines()]
            if len(messages) != 2 or messages[0] != {'type':'ready', 'protocol':worker.PROTOCOL_VERSION}:
                raise ValueError('Unexpected fallback readiness')
            result = messages[1]
            if not isinstance(result, dict) or result.get('id') != json.loads(line)['id']:
                raise ValueError('Unexpected fallback request id')
            if result.get('type') == 'error':
                raise worker.WorkerError('The whole-recording Qwen fallback could not complete the recording. No partial transcript was returned.')
            if result.get('type') != 'result' or not isinstance(result.get('text'), str):
                raise ValueError('Unexpected fallback result')
            return result['text']
        except (KeyError, TypeError, ValueError):
            raise worker.WorkerError('The whole-recording Qwen fallback returned an invalid response.') from None
    finally:
        stop_owned_process(process, job)


class RequestRouter:
    def __init__(self, model: Path, config: Path):
        self.model, self.config = model, config
        self.backend = NativeBackend(config)
        self.line = ''

    def process(self, line: str) -> dict:
        self.line = line
        try:
            return worker.process_request(line, self)
        finally:
            self.line = ''

    def transcribe(self, recording: worker.Recording) -> worker.GeneratedTranscript:
        if recording.duration_seconds > NATIVE_MAX_SECONDS:
            self.close()
            text = run_hf_request(self.model, self.line)
            # The unchanged worker already enforced its original generation cap.
            return worker.GeneratedTranscript(text, 0, worker.MAX_NEW_TOKENS)
        if self.backend is None:
            self.backend = NativeBackend(self.config)
        return self.backend.transcribe(recording)

    def close(self) -> None:
        if self.backend is not None:
            self.backend.close()
            self.backend = None


def run_native_worker(model: str, config: str, source, destination) -> int:
    worker.configure_offline_environment()
    model_path = worker.local_path(model, directory=True)
    worker.emit(destination, {'type':'bootstrap', 'protocol':worker.PROTOCOL_VERSION, 'pid':os.getpid()})
    try:
        acknowledgement = json.loads(source.readline())
    except (ValueError, TypeError):
        acknowledgement = None
    if (not isinstance(acknowledgement, dict) or type(acknowledgement.get('protocol')) is not int
            or acknowledgement != {'type':'start', 'protocol':worker.PROTOCOL_VERSION}):
        raise worker.WorkerError('Native Qwen requires its parent to confirm process ownership before startup.')
    with contextlib.redirect_stdout(sys.stderr):
        router = RequestRouter(model_path, Path(config))
        try:
            worker.emit(destination, {'type':'ready', 'protocol':worker.PROTOCOL_VERSION})
            for line in source:
                worker.emit(destination, router.process(line))
        finally:
            router.close()
    return 0


def main(argv=None) -> int:
    with worker.protocol_stdout() as destination:
        sys.stdin.reconfigure(encoding='utf-8')
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument('--model', required=True)
        parser.add_argument('--native-config', required=True)
        args = parser.parse_args(argv)
        try:
            return run_native_worker(args.model, args.native_config, sys.stdin, destination)
        except worker.WorkerError as error:
            print(str(error), file=sys.stderr, flush=True)
        except Exception as error:
            print(f'Native Qwen startup failed ({type(error).__name__}).', file=sys.stderr, flush=True)
    return 1


if __name__ == '__main__':
    raise SystemExit(main())
