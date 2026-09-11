"""Isolated benchmark of the app's selected Parakeet persistent-server path.

Uses only the Python standard library. Reads a settings snapshot; never writes
app settings or contacts/stops the installed app's server. See README-parakeet.md.
"""

from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
import http.client
import json
import math
import os
from pathlib import Path
import re
import socket
import statistics
import struct
import subprocess
import sys
import time
import uuid


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def emit(event, **fields):
    print(json.dumps({"event": event, **fields}, ensure_ascii=False), flush=True)


def clean(text):
    return re.sub("<EOU>", "", text, flags=re.IGNORECASE).strip()


class PcmWave:
    def __init__(self, wav):
        if len(wav) < 44 or wav[:4] != b"RIFF" or wav[8:12] != b"WAVE":
            raise ValueError("Not a RIFF/WAVE recording")
        fmt = pcm = None
        offset = 12
        while offset <= len(wav) - 8:
            tag, size = struct.unpack_from("<4sI", wav, offset)
            start = offset + 8
            if start + size > len(wav):
                raise ValueError("Truncated WAV chunk")
            if tag == b"fmt " and size >= 16:
                fmt = struct.unpack_from("<HHIIHH", wav, start)
            elif tag == b"data":
                pcm = wav[start : start + size]
            offset = start + size + (size & 1)
        if fmt is None or fmt[0] != 1 or not all(fmt) or pcm is None:
            raise ValueError("Recording must contain PCM WAV audio")
        self.fmt, self.pcm = fmt, pcm
        self.byte_rate, self.block_align = fmt[3], fmt[4]
        self.duration = len(pcm) / self.byte_rate

    def segment(self, seconds):
        byte_offset = min(
            len(self.pcm), math.floor(seconds * self.byte_rate) // self.block_align * self.block_align
        )
        pcm = self.pcm[byte_offset:]
        return (
            struct.pack("<4sI4s4sI", b"RIFF", 36 + len(pcm), b"WAVE", b"fmt ", 16)
            + struct.pack("<HHIIHH", *self.fmt)
            + struct.pack("<4sI", b"data", len(pcm))
            + pcm
        )


def parse_segment(response):
    if not isinstance(response.get("text"), str):
        raise ValueError("Parakeet response did not contain transcript text")
    eou, words = None, []
    for word in response.get("words", []):
        if not all(key in word for key in ("word", "start", "end")):
            continue
        start, end = float(word["start"]), float(word["end"])
        if not math.isfinite(start) or not math.isfinite(end):
            raise ValueError("Nonfinite word timestamp")
        if "<eou>" in word["word"].lower():
            eou = end
        text = clean(word["word"])
        if text:
            words.append({"text": text, "start": start, "end": end, "confidence": word.get("conf")})
    return clean(response["text"]), words, eou


def transcribe_all(wav, send_segment):
    """Match PersistentParakeetServerTranscriber's 64-step EOU continuation."""
    audio = PcmWave(wav)
    texts, words, segments = [], [], []
    offset = 0.0
    for _ in range(64):
        began = time.perf_counter()
        response = send_segment(audio.segment(offset))
        text, segment_words, eou = parse_segment(response)
        segments.append({"offset_seconds": offset, "wall_seconds": time.perf_counter() - began,
                         "end_of_utterance_seconds": eou, "response": response})
        if text:
            texts.append(text)
        words.extend({**word, "start": word["start"] + offset, "end": word["end"] + offset}
                     for word in segment_words)
        if eou is None or eou <= 0.080 or offset + eou >= audio.duration - 0.080:
            return {"text": " ".join(texts), "words": words, "segments": segments}
        offset += eou
    raise ValueError("Parakeet returned too many end-of-utterance segments")


def tcp_rows():
    """Read IPv4 owner PID table through the same Win32 API as the app."""
    if os.name != "nt":
        raise RuntimeError("This benchmark requires Windows process-bound loopback checks")
    get_table = ctypes.WinDLL("iphlpapi", use_last_error=True).GetExtendedTcpTable
    get_table.argtypes = [ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD), wintypes.BOOL,
                         wintypes.ULONG, ctypes.c_int, wintypes.ULONG]
    get_table.restype = wintypes.DWORD
    size = wintypes.DWORD(0)
    code = get_table(None, ctypes.byref(size), False, socket.AF_INET, 5, 0)
    if code not in (0, 122):
        raise OSError(code, "GetExtendedTcpTable size failed")
    for _ in range(4):
        buffer = ctypes.create_string_buffer(size.value)
        code = get_table(buffer, ctypes.byref(size), False, socket.AF_INET, 5, 0)
        if code == 122:
            continue
        if code != 0:
            raise OSError(code, "GetExtendedTcpTable failed")
        count = struct.unpack_from("<I", buffer)[0]
        return [struct.unpack_from("<IIIIII", buffer, 4 + 24 * index) for index in range(count)]
    raise OSError(122, "TCP table changed too often")


def socket_is_owned(connection, expected_pid):
    client_host, client_port = connection.getsockname()
    server_host, server_port = connection.getpeername()
    client_address = struct.unpack("<I", socket.inet_aton(client_host))[0]
    server_address = struct.unpack("<I", socket.inet_aton(server_host))[0]
    return any(state == 5 and pid == expected_pid
               and local_address == server_address and socket.ntohs(local_port & 0xFFFF) == server_port
               and remote_address == client_address and socket.ntohs(remote_port & 0xFFFF) == client_port
               for state, local_address, local_port, remote_address, remote_port, pid in tcp_rows())


class OwnedConnection(http.client.HTTPConnection):
    def __init__(self, port, expected_pid, timeout):
        super().__init__("127.0.0.1", port, timeout=timeout)
        self.expected_pid = expected_pid

    def connect(self):
        super().connect()
        if not socket_is_owned(self.sock, self.expected_pid):
            self.close()
            raise RuntimeError("Another process accepted the benchmark audio connection")


def multipart(wav):
    boundary = "asr-benchmark-" + uuid.uuid4().hex
    body = (
        f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\n"
        "Content-Type: audio/wav\r\n\r\n"
    ).encode() + wav + b"\r\n"
    for name, value in (("response_format", "verbose_json"), ("timestamp_granularities[]", "word")):
        body += f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode()
    body += f"--{boundary}--\r\n".encode()
    return body, f"multipart/form-data; boundary={boundary}"


def post_audio(process, port, wav, timeout):
    if process.poll() is not None:
        raise RuntimeError(f"Owned server exited with {process.returncode}")
    body, content_type = multipart(wav)
    connection = OwnedConnection(port, process.pid, timeout)
    try:
        connection.request("POST", "/v1/audio/transcriptions", body,
                           {"Content-Type": content_type, "Connection": "close"})
        response = connection.getresponse()
        data = response.read().decode("utf-8")
        if response.status != 200:
            raise RuntimeError(f"Parakeet HTTP {response.status}: {data}")
        return json.loads(data)
    finally:
        connection.close()


def process_memory(process):
    class Counters(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
            (name, ctypes.c_size_t) for name in (
                "PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage", "QuotaPagedPoolUsage",
                "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage",
                "PrivateUsage")]
    counters = Counters()
    counters.cb = ctypes.sizeof(counters)
    query = ctypes.WinDLL("psapi", use_last_error=True).GetProcessMemoryInfo
    query.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
    query.restype = wintypes.BOOL
    if not query(int(process._handle), ctypes.byref(counters), counters.cb):
        return {"error": f"GetProcessMemoryInfo: {ctypes.get_last_error()}"}
    return {"working_set_bytes": counters.WorkingSetSize, "peak_working_set_bytes": counters.PeakWorkingSetSize,
            "private_bytes": counters.PrivateUsage, "peak_pagefile_bytes": counters.PeakPagefileUsage}


def save(path, data):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    temporary.replace(path)


def run(args):
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    report = {"schema_version": 1, "engine": "parakeet-current", "started_utc": utc_now(),
              "status": "running", "runs": [], "errors": [], "timing_notes": [
                  "Cold means fresh owned server process; operating-system disk caches are not purged.",
                  "Readiness is process-owned TCP listening; first request measures remaining lazy setup.",
                  "Request wall time includes WAV read/parse, EOU continuation, loopback HTTP and JSON parsing.",
                  "No app correction dictionary is applied; transcripts are raw recognition results.",
                  "One untimed-for-summary first request precedes warm repeats; recordings run serially."]}
    process = None
    try:
        settings = json.loads(args.settings.read_text(encoding="utf-8-sig"))
        cli = Path(settings["runtimePath"]).resolve(strict=True)
        server = cli.with_name("parakeet-server.exe").resolve(strict=True)
        model = Path(settings["modelPath"]).resolve(strict=True)
        manifest_path = args.corpus if args.corpus is not None else args.inputs / "manifest.json"
        input_root = manifest_path.resolve().parent
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        inputs = []
        for entry in manifest:
            path = (input_root / entry["file"]).resolve(strict=True)
            actual_hash = sha256(path)
            if actual_hash.lower() != entry["sha256"].lower():
                raise ValueError(f"Preserved input hash mismatch: {entry['id']}")
            wav = PcmWave(path.read_bytes())
            inputs.append({"id": entry["id"], "path": str(path), "sha256": actual_hash,
                           "duration_seconds": wav.duration, "sample_rate": wav.fmt[2], "channels": wav.fmt[1],
                           **{key: entry[key] for key in ("kind", "source", "reference") if key in entry}})
        if not inputs:
            raise ValueError("No preserved recordings in the input manifest")
        report.update({"selected_model_id": settings["selectedModelId"], "device": settings["devicePreference"],
                       "transcription_mode": settings["transcriptionMode"], "model_path": str(model),
                       "model_sha256": sha256(model), "server_path": str(server), "server_sha256": sha256(server),
                       "settings_snapshot_sha256": sha256(args.settings), "inputs": inputs,
                       "corpus_manifest": str(manifest_path.resolve()), "corpus_sha256": sha256(manifest_path),
                       "startup_timeout_seconds": args.startup_timeout,
                       "request_timeout_seconds": args.request_timeout})
        with socket.socket() as reserve:
            reserve.bind(("127.0.0.1", 0))
            port = reserve.getsockname()[1]
        env = os.environ.copy()
        dll_paths = [str(cli.parent)] + [str(path) for path in sorted(cli.parent.parent.glob("cudart-*")) if path.is_dir()]
        env["PATH"] = os.pathsep.join(dll_paths + [env.get("PATH", "")])
        command = [str(server), "--model", str(model), "--host", "127.0.0.1", "--port", str(port)]
        stdout_path, stderr_path = output.with_suffix(".server.stdout.log"), output.with_suffix(".server.stderr.log")
        report.update({"command": command, "server_stdout": str(stdout_path), "server_stderr": str(stderr_path)})
        save(output, report)
        with stdout_path.open("wb") as stdout_log, stderr_path.open("wb") as stderr_log:
            began = time.perf_counter()
            process = subprocess.Popen(command, cwd=server.parent, env=env, stdin=subprocess.DEVNULL,
                                       stdout=stdout_log, stderr=stderr_log, creationflags=subprocess.CREATE_NO_WINDOW)
            report["owned_server_pid"] = process.pid
            while True:
                if process.poll() is not None:
                    raise RuntimeError(f"Server exited during startup: {process.returncode}")
                if time.perf_counter() - began > args.startup_timeout:
                    raise TimeoutError("Server did not become ready within startup timeout")
                try:
                    with socket.create_connection(("127.0.0.1", port), timeout=0.5) as connection:
                        if not socket_is_owned(connection, process.pid):
                            raise RuntimeError("Another process claimed the benchmark server port")
                        break
                except (ConnectionRefusedError, TimeoutError):
                    time.sleep(0.05)
            report["cold_process_readiness_seconds"] = time.perf_counter() - began
            report["ready_process_memory"] = process_memory(process)
            save(output, report)
            emit("ready", pid=process.pid, cold_process_readiness_seconds=report["cold_process_readiness_seconds"])
            scheduled = [("first_request", 0, inputs[0])] + [
                ("warm", repeat, item) for repeat in range(1, args.warm_runs + 1) for item in inputs]
            for phase, repeat, item in scheduled:
                row = {"phase": phase, "repeat": repeat, "id": item["id"], "started_utc": utc_now()}
                began = time.perf_counter()
                try:
                    def send_before_deadline(wav):
                        remaining = args.request_timeout - (time.perf_counter() - began)
                        if remaining <= 0:
                            raise TimeoutError("Recording transcription exceeded the request deadline")
                        return post_audio(process, port, wav, remaining)
                    row.update(transcribe_all(Path(item["path"]).read_bytes(),
                                              send_before_deadline))
                    row["status"] = "ok"
                except Exception as exc:
                    row.update({"status": "error", "error": f"{type(exc).__name__}: {exc}"})
                    report["errors"].append({"phase": phase, "id": item["id"], "repeat": repeat, "error": row["error"]})
                row["wall_seconds"] = time.perf_counter() - began
                row["rtfx"] = item["duration_seconds"] / row["wall_seconds"]
                row["process_memory"] = process_memory(process)
                report["runs"].append(row)
                save(output, report)
                emit("run", **{key: row[key] for key in ("phase", "repeat", "id", "status", "wall_seconds")})
            report["summary"] = []
            for item in inputs:
                rows = [row for row in report["runs"] if row["id"] == item["id"] and row["phase"] == "warm"]
                successes = [row for row in rows if row["status"] == "ok"]
                summary = {"id": item["id"], "duration_seconds": item["duration_seconds"],
                           "successful_warm_runs": len(successes), "failed_warm_runs": len(rows) - len(successes)}
                if successes:
                    median = statistics.median(row["wall_seconds"] for row in successes)
                    summary.update({"median_wall_seconds": median, "rtfx": item["duration_seconds"] / median,
                                    "transcript": successes[0]["text"],
                                    "transcripts_identical": len({row["text"] for row in successes}) == 1})
                report["summary"].append(summary)
            report["status"] = "complete" if not report["errors"] else "complete_with_errors"
    except BaseException as exc:
        report["status"] = "failed"
        report["errors"].append({"error": f"{type(exc).__name__}: {exc}"})
    finally:
        if process is not None:
            try:
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
                report["cleanup"] = {"owned_pid": process.pid, "exited": process.poll() is not None,
                                     "exit_code": process.returncode}
            except Exception as exc:
                report["cleanup"] = {"owned_pid": process.pid, "error": str(exc)}
                report["errors"].append({"error": f"Owned server cleanup failed: {exc}"})
                report["status"] = "failed"
        report["finished_utc"] = utc_now()
        save(output, report)
        emit("finished", status=report["status"], output=str(output), errors=report["errors"])
    return 0 if report["status"] == "complete" else 1


def self_test():
    import threading
    import unittest

    class ContractTests(unittest.TestCase):
        def audio(self, seconds=2):
            pcm = b"\x00\x00" * (16000 * seconds)
            return (struct.pack("<4sI4s4sIHHIIHH4sI", b"RIFF", 36 + len(pcm), b"WAVE", b"fmt ",
                                16, 1, 1, 16000, 32000, 2, 16, b"data", len(pcm)) + pcm)

        def test_eou_tail_is_transcribed_and_words_are_offset(self):
            sizes = []
            responses = iter([
                {"text": "First <EOU>", "words": [{"word": "<EOU>", "start": 0.8, "end": 1.0}]},
                {"text": "tail", "words": [{"word": "tail", "start": 0.2, "end": 0.5}]}])
            def send(wav):
                sizes.append(PcmWave(wav).duration)
                return next(responses)
            result = transcribe_all(self.audio(), send)
            self.assertEqual(result["text"], "First tail")
            self.assertEqual(sizes, [2.0, 1.0])
            self.assertEqual(result["words"][0]["end"], 1.5)

        def test_missing_text_and_non_pcm_rejected(self):
            with self.assertRaises(ValueError):
                parse_segment({"words": []})
            wav = bytearray(self.audio())
            struct.pack_into("<H", wav, 20, 3)
            with self.assertRaises(ValueError):
                PcmWave(wav)

        def test_tiny_eou_stops_without_loop(self):
            result = transcribe_all(self.audio(), lambda wav: {
                "text": "Hi<EOU>", "words": [{"word": "<EOU>", "start": 0, "end": 0.08}]})
            self.assertEqual(result["text"], "Hi")
            self.assertEqual(len(result["segments"]), 1)

        @unittest.skipUnless(os.name == "nt", "Windows ownership API")
        def test_wrong_process_receives_no_http_body(self):
            received = []
            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                listener.listen()
                port = listener.getsockname()[1]
                def accept():
                    with listener.accept()[0] as connection:
                        connection.settimeout(3)
                        received.append(connection.recv(4096))
                thread = threading.Thread(target=accept)
                thread.start()
                connection = OwnedConnection(port, 0, 3)
                try:
                    with self.assertRaisesRegex(RuntimeError, "Another process"):
                        connection.request("POST", "/", b"private audio")
                finally:
                    connection.close()
                thread.join(timeout=4)
            self.assertFalse(thread.is_alive())
            self.assertEqual(received, [b""])

    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(ContractTests))
    return 0 if result.wasSuccessful() else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--settings", type=Path)
    parser.add_argument("--inputs", type=Path)
    parser.add_argument("--corpus", type=Path,
                        help="Optional manifest; file paths are relative to this JSON file, instead of --inputs")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--warm-runs", type=int, default=3)
    parser.add_argument("--startup-timeout", type=float, default=60)
    parser.add_argument("--request-timeout", type=float, default=300)
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if not all((args.settings, args.inputs or args.corpus, args.output)) or args.warm_runs < 1:
        parser.error("--settings, --inputs or --corpus, --output, and at least one --warm-runs are required")
    if args.inputs is not None and args.corpus is not None:
        parser.error("Choose --inputs or --corpus, not both")
    if args.startup_timeout <= 0 or args.request_timeout <= 0:
        parser.error("Timeouts must be positive")
    return run(args)


if __name__ == "__main__":
    sys.exit(main())
