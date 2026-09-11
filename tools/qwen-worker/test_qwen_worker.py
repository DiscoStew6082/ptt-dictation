"""Behavioral tests for the resident worker; no model, CUDA, or network needed."""

import contextlib
import io
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import wave

import qwen_worker as worker


class FakeBackend:
    def __init__(self):
        self.recordings = []
        self.responses = []

    def transcribe(self, recording):
        self.recordings.append(recording)
        response = self.responses.pop(0) if self.responses else worker.GeneratedTranscript("spoken words", 3, 512)
        if isinstance(response, Exception):
            raise response
        return response


class WorkerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.model = self.root / "model"
        self.model.mkdir()
        self.backend = FakeBackend()
        self.audio = self.write_wav("speech.wav", [100, -100] * 8000)

    def write_wav(self, name, samples, *, channels=1, width=2, rate=16000):
        path = self.root / name
        data = struct.pack("<" + "h" * len(samples), *samples) if width == 2 else bytes(samples)
        with wave.open(str(path), "wb") as output:
            output.setnchannels(channels)
            output.setsampwidth(width)
            output.setframerate(rate)
            output.writeframes(data)
        return path

    def request(self, audio=None, request_id="request-1"):
        return json.dumps({"id": request_id, "audioPath": str(audio or self.audio)}, ensure_ascii=False)

    def process(self, audio=None, request_id="request-1"):
        return worker.process_request(self.request(audio, request_id), self.backend)

    def test_ready_follows_single_load_and_requests_are_serial(self):
        destination = io.StringIO()
        loads = []

        def factory(path):
            self.assertEqual(destination.getvalue(), "")
            self.assertEqual(path, self.model)
            self.assertEqual(os.environ["HF_HUB_OFFLINE"], "1")
            self.assertEqual(os.environ["TRANSFORMERS_OFFLINE"], "1")
            loads.append(path)
            return self.backend

        source = io.StringIO(self.request(request_id="one") + "\n" + self.request(request_id="two") + "\n")
        with patch.dict(os.environ):
            result = worker.run_worker(str(self.model), source, destination, factory)
        messages = [json.loads(line) for line in destination.getvalue().splitlines()]
        self.assertEqual(result, 0)
        self.assertEqual(loads, [self.model])
        self.assertEqual(messages, [
            {"type": "ready", "protocol": 1},
            {"id": "one", "type": "result", "text": "spoken words"},
            {"id": "two", "type": "result", "text": "spoken words"},
        ])
        self.assertEqual(len(self.backend.recordings), 2)
        self.assertEqual(self.backend.recordings[0].duration_seconds, 1.0)
        self.assertEqual(self.backend.recordings[0].sample_rate, 16000)
        self.assertEqual(self.backend.recordings[0].pcm, struct.pack("<hh", 100, -100) * 8000)

    def test_only_digital_or_near_digital_silence_skips_inference(self):
        for index, samples in enumerate(([], [0] * 100, [-2, -1, 0, 1, 2] * 20)):
            with self.subTest(samples=samples[:5]):
                path = self.write_wav(f"quiet-{index}.wav", samples)
                self.assertEqual(self.process(path), {"id": "request-1", "type": "result", "text": ""})
        self.assertEqual(self.backend.recordings, [])
        for peak in (3, -3, -32768):
            with self.subTest(peak=peak):
                path = self.write_wav(f"audible-{peak}.wav", [peak])
                self.assertEqual(self.process(path)["text"], "spoken words")
                self.assertEqual(self.backend.recordings[-1].peak, abs(peak))
        self.assertEqual(len(self.backend.recordings), 3)

    def test_wrong_wav_format_is_rejected_before_inference(self):
        variants = (
            {"channels": 2},
            {"width": 1},
            {"rate": 44100},
        )
        for index, options in enumerate(variants):
            with self.subTest(options=options):
                path = self.write_wav(f"wrong-{index}.wav", [20] * 100, **options)
                response = self.process(path)
                self.assertEqual(response["type"], "error")
                self.assertIn("PCM16 mono WAV", response["message"])
        self.assertEqual(self.backend.recordings, [])

    def test_invalid_and_truncated_wav_is_rejected_before_inference(self):
        invalid = self.root / "invalid.wav"
        invalid.write_bytes(b"not a WAV")
        truncated = self.root / "truncated.wav"
        truncated.write_bytes(self.audio.read_bytes()[:-20])
        for path in (invalid, truncated):
            with self.subTest(path=path.name):
                response = self.process(path)
                self.assertEqual(response["type"], "error")
                self.assertNotIn("text", response)
        self.assertEqual(self.backend.recordings, [])

    def test_bad_requests_do_not_prevent_later_valid_request(self):
        malformed = (
            "{", "[]", "null", "1", "\n",
            json.dumps({"audioPath": str(self.audio)}),
            json.dumps({"id": 7, "audioPath": str(self.audio)}),
            json.dumps({"id": "", "audioPath": str(self.audio)}),
            json.dumps({"id": "missing-audio"}),
        )
        for line in malformed:
            with self.subTest(line=line):
                response = worker.process_request(line, self.backend)
                self.assertEqual(response["type"], "error")
                self.assertNotIn("text", response)
        self.assertEqual(self.backend.recordings, [])
        self.assertEqual(self.process()["type"], "result")

    def test_absolute_local_regular_file_is_required(self):
        invalid_paths = (
            "relative.wav", "https://example.invalid/audio.wav", "file:///audio.wav",
            r"\\server\share\audio.wav", "//server/share/audio.wav",
            str(self.root), str(self.root / "missing.wav"),
        )
        for value in invalid_paths:
            with self.subTest(value=value):
                response = worker.process_request(json.dumps({"id": "guard", "audioPath": value}), self.backend)
                self.assertEqual(response["type"], "error")
                self.assertEqual(response["id"], "guard")
        self.assertEqual(self.backend.recordings, [])

    def test_capped_output_is_never_returned_as_success_or_partial_text(self):
        self.backend.responses = [worker.GeneratedTranscript("private partial transcript", 4096, 4096)]
        response = self.process()
        self.assertEqual(response["type"], "error")
        self.assertIn("4096-token output limit", response["message"])
        self.assertNotIn("text", response)
        self.assertNotIn("private partial", json.dumps(response))
        self.assertEqual(self.process(request_id="next")["type"], "result")

    def test_generation_budget_scales_for_long_recording_and_has_explicit_ceiling(self):
        self.assertEqual(worker.generation_budget(1), 512)
        self.assertGreater(worker.generation_budget(73), 512)
        self.assertEqual(worker.generation_budget(300), 4096)
        self.assertEqual(worker.generation_budget(3600), 4096)

    def test_backend_error_has_matching_id_and_worker_can_continue(self):
        self.backend.responses = [RuntimeError("backend failed")]
        errors = io.StringIO()
        with contextlib.redirect_stderr(errors):
            response = self.process(request_id="failed-id")
        self.assertEqual(response, {
            "id": "failed-id", "type": "error", "message": "Qwen transcription failed (RuntimeError).",
        })
        self.assertIn("backend failed", errors.getvalue())
        self.assertEqual(self.process(request_id="next")["type"], "result")

    def test_startup_failure_never_announces_ready(self):
        destination = io.StringIO()

        def factory(path):
            raise worker.WorkerError("model could not load")

        with patch.dict(os.environ), self.assertRaisesRegex(worker.WorkerError, "could not load"):
            worker.run_worker(str(self.model), io.StringIO(""), destination, factory)
        self.assertEqual(destination.getvalue(), "")

    def test_cuda_and_bf16_are_required_before_transformers_loading(self):
        for available, bf16, expected in ((False, False, "no CUDA device"), (True, False, "BF16")):
            with self.subTest(available=available, bf16=bf16):
                torch = SimpleNamespace(cuda=SimpleNamespace(
                    is_available=lambda: available, is_bf16_supported=lambda: bf16,
                ))
                with patch.dict(sys.modules, {"torch": torch}), self.assertRaisesRegex(worker.WorkerError, expected):
                    worker.QwenBackend(self.model)

    def test_cli_protocol_is_utf8_and_isolates_python_crt_and_windows_output(self):
        # An actual child process exercises the same stdout isolation as the app.
        # Only the inference backend is replaced; no torch/model import occurs.
        unicode_audio = self.write_wav("caf\u00e9-\u8bed\u97f3.wav", [100] * 100)
        child = r'''
import os
import sys
sys.path.insert(0, sys.argv[1])
import qwen_worker as worker

class Backend:
    def __init__(self, model):
        print("python-noise", flush=True)
        os.write(1, b"crt-noise\n")
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.GetStdHandle.argtypes = [wintypes.DWORD]
            kernel.GetStdHandle.restype = wintypes.HANDLE
            kernel.WriteFile.argtypes = [wintypes.HANDLE, wintypes.LPCVOID, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID]
            kernel.WriteFile.restype = wintypes.BOOL
            data = b"win32-noise\n"
            written = wintypes.DWORD()
            if not kernel.WriteFile(kernel.GetStdHandle(0xFFFFFFF5), data, len(data), ctypes.byref(written), None):
                raise OSError(ctypes.get_last_error(), "write failed")
    def transcribe(self, recording):
        print("inference-noise", flush=True)
        return worker.GeneratedTranscript("caf\u00e9 \u8bed\u97f3", 4, 512)

raise SystemExit(worker.main(["--model", sys.argv[2]], backend_factory=Backend))
'''
        environment = dict(os.environ, PYTHONUTF8="0", PYTHONIOENCODING="cp1252")
        result = subprocess.run(
            [sys.executable, "-B", "-c", child, str(Path(worker.__file__).parent), str(self.model)],
            input=self.request(unicode_audio, "utf8-\u8bed") + "\n" + self.request(request_id="second") + "\n",
            capture_output=True, text=True, encoding="utf-8", errors="strict", timeout=20, env=environment,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([json.loads(line) for line in result.stdout.splitlines()], [
            {"type": "ready", "protocol": 1},
            {"id": "utf8-\u8bed", "type": "result", "text": "caf\u00e9 \u8bed\u97f3"},
            {"id": "second", "type": "result", "text": "caf\u00e9 \u8bed\u97f3"},
        ])
        self.assertIn("python-noise", result.stderr)
        self.assertIn("crt-noise", result.stderr)
        self.assertEqual(result.stderr.count("inference-noise"), 2)
        if os.name == "nt":
            self.assertIn("win32-noise", result.stderr)

    def test_cli_invalid_model_fails_without_ready_or_model_import(self):
        result = subprocess.run(
            [sys.executable, "-B", worker.__file__, "--model", "relative-model"],
            input="", capture_output=True, text=True, encoding="utf-8", timeout=20,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stdout, "")
        self.assertIn("absolute local filesystem path", result.stderr)


if __name__ == "__main__":
    unittest.main()
