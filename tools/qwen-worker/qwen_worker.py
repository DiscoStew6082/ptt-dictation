"""Resident, offline Qwen final-transcription worker. Stdout is JSON-lines only."""

from __future__ import annotations

import argparse
from array import array
import contextlib
from dataclasses import dataclass
import json
import math
import os
from pathlib import Path
import sys
import traceback
from typing import Callable, TextIO
import wave


PROTOCOL_VERSION = 1
SAMPLE_RATE = 16000
NEAR_DIGITAL_SILENCE_PEAK = 2
MAX_NEW_TOKENS = 4096


class WorkerError(Exception):
    """An actionable error safe to return through the local worker protocol."""


@dataclass(frozen=True)
class Recording:
    pcm: bytes
    sample_rate: int
    duration_seconds: float
    peak: int


@dataclass(frozen=True)
class GeneratedTranscript:
    text: str
    token_count: int
    token_limit: int


def local_path(value: object, *, directory: bool = False) -> Path:
    if not isinstance(value, str) or not value or "://" in value or value.startswith(("\\\\", "//")):
        raise WorkerError("An absolute local filesystem path is required; URLs and network paths are not supported.")
    path = Path(value)
    if not path.is_absolute():
        raise WorkerError("An absolute local filesystem path is required.")
    try:
        path = path.resolve(strict=True)
        if str(path).startswith(("\\\\", "//")):
            raise WorkerError("Network paths are not supported.")
        valid = path.is_dir() if directory else path.is_file()
    except (OSError, ValueError) as error:
        raise WorkerError("The local model directory or recording file could not be opened.") from error
    if not valid:
        raise WorkerError("The model path must be a directory." if directory else "The recording path must be a regular file.")
    return path


def read_recording(value: object) -> Recording:
    path = local_path(value)
    try:
        with wave.open(str(path), "rb") as audio:
            channels, width, rate, frames, compression, _ = audio.getparams()
            if (channels, width, rate, compression) != (1, 2, SAMPLE_RATE, "NONE"):
                raise WorkerError("Qwen requires an uncompressed PCM16 mono WAV recording at 16000 Hz.")
            pcm = audio.readframes(frames)
            if len(pcm) != frames * 2:
                raise WorkerError("The WAV recording is truncated.")
    except (OSError, EOFError, wave.Error) as error:
        raise WorkerError("The recording could not be read as a complete PCM WAV file.") from error
    samples = array("h")
    samples.frombytes(pcm)
    if sys.byteorder != "little":
        samples.byteswap()
    peak = max((abs(sample) for sample in samples), default=0)
    return Recording(pcm, rate, frames / rate, peak)


def generation_budget(duration_seconds: float) -> int:
    # Generous headroom for fast English speech; reaching even the maximum is
    # reported as failure, never accepted as a silently shortened transcript.
    return min(MAX_NEW_TOKENS, max(512, math.ceil(duration_seconds * 16) + 128))


def configure_offline_environment() -> None:
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"


class QwenBackend:
    def __init__(self, model_path: Path):
        import torch

        if not torch.cuda.is_available():
            raise WorkerError("Qwen final transcription requires CUDA; no CUDA device is available.")
        if not torch.cuda.is_bf16_supported():
            raise WorkerError("The CUDA device does not support the required BF16 model configuration.")
        from transformers import AutoModelForMultimodalLM, AutoProcessor

        torch.set_num_threads(4)
        torch.manual_seed(0)
        self.torch = torch
        self.processor = AutoProcessor.from_pretrained(model_path, local_files_only=True, trust_remote_code=False)
        self.model = AutoModelForMultimodalLM.from_pretrained(
            model_path, local_files_only=True, trust_remote_code=False,
            dtype=torch.bfloat16, device_map={"": "cuda:0"}, attn_implementation="sdpa",
        ).eval()
        torch.cuda.synchronize()

    def transcribe(self, recording: Recording) -> GeneratedTranscript:
        import numpy as np

        # Pass the bytes already validated above, avoiding a second file read
        # which could otherwise observe a changed recording or path.
        audio = np.frombuffer(recording.pcm, dtype="<i2").astype(np.float32) / 32768.0
        limit = generation_budget(recording.duration_seconds)
        with self.torch.inference_mode():
            inputs = self.processor.apply_transcription_request(
                audio=audio, language="English",
            ).to(self.model.device, self.model.dtype)
            output_ids = self.model.generate(**inputs, max_new_tokens=limit, do_sample=False)
            generated_ids = output_ids[:, inputs["input_ids"].shape[1]:]
            count = generated_ids.shape[-1]
            # A capped result is never decoded/returned as successful text.
            text = "" if count >= limit else self.processor.decode(
                generated_ids, return_format="transcription_only"
            )[0]
        return GeneratedTranscript(text, count, limit)


def emit(destination: TextIO, message: dict) -> None:
    destination.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
    destination.flush()


def process_request(line: str, backend) -> dict:
    request_id = ""
    try:
        try:
            request = json.loads(line)
        except (ValueError, TypeError) as error:
            raise WorkerError("The request must be one JSON object per line.") from error
        if not isinstance(request, dict):
            raise WorkerError("The request must be a JSON object.")
        if isinstance(request.get("id"), str):
            request_id = request["id"]
        if not request_id:
            raise WorkerError("The request id must be a nonempty string.")
        recording = read_recording(request.get("audioPath"))
        if recording.peak <= NEAR_DIGITAL_SILENCE_PEAK:
            text = ""
        else:
            generated = backend.transcribe(recording)
            if generated.token_count >= generated.token_limit:
                raise WorkerError(
                    f"Qwen reached its {generated.token_limit}-token output limit. "
                    "No truncated transcript was returned; use a shorter recording."
                )
            if not isinstance(generated.text, str):
                raise WorkerError("Qwen did not return transcript text.")
            text = generated.text
        return {"id": request_id, "type": "result", "text": text}
    except WorkerError as error:
        return {"id": request_id, "type": "error", "message": str(error)}
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        return {"id": request_id, "type": "error", "message": f"Qwen transcription failed ({type(error).__name__})."}


def run_worker(model: str, source: TextIO, destination: TextIO, backend_factory: Callable = QwenBackend) -> int:
    configure_offline_environment()
    model_path = local_path(model, directory=True)
    # The command-line entrypoint also redirects OS stdout handles, covering
    # native libraries. This context covers Python output for injected tests.
    with contextlib.redirect_stdout(sys.stderr):
        backend = backend_factory(model_path)
        emit(destination, {"type": "ready", "protocol": PROTOCOL_VERSION})
        for line in source:
            emit(destination, process_request(line, backend))
    return 0


def protocol_stdout() -> TextIO:
    """Keep a private protocol pipe while routing library/native stdout to stderr."""
    sys.stdout.flush()
    destination = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", newline="\n", buffering=1)
    try:
        os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
        if os.name == "nt":
            # Some Windows native libraries use GetStdHandle rather than CRT
            # descriptors, so redirect both views of the inherited stdout pipe.
            import ctypes
            from ctypes import wintypes
            import msvcrt

            set_std_handle = ctypes.WinDLL("kernel32", use_last_error=True).SetStdHandle
            set_std_handle.argtypes = [wintypes.DWORD, wintypes.HANDLE]
            set_std_handle.restype = wintypes.BOOL
            if not set_std_handle(0xFFFFFFF5, msvcrt.get_osfhandle(sys.stderr.fileno())):
                raise OSError(ctypes.get_last_error(), "Could not isolate the worker protocol pipe")
        sys.stdout = sys.stderr
        return destination
    except BaseException:
        destination.close()
        raise


def main(argv=None, backend_factory: Callable = QwenBackend) -> int:
    with protocol_stdout() as destination:
        # Redirected Windows stdin otherwise follows the system code page.
        sys.stdin.reconfigure(encoding="utf-8")
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument("--model", required=True, help="Absolute directory containing the local Qwen checkpoint")
        args = parser.parse_args(argv)
        try:
            return run_worker(args.model, sys.stdin, destination, backend_factory)
        except WorkerError as error:
            print(str(error), file=sys.stderr, flush=True)
        except Exception:
            traceback.print_exc(file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
