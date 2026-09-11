# Qwen final transcription

Select **Qwen3-ASR 1.7B (NVIDIA GPU)** under **Final recognition** in Settings to retain Parakeet live preview and use Qwen after stopping. The existing hold/toggle keys, corrections, cancellation, textbox protections, and Session History remain in use. Settings changes apply to the next recording.

Qwen loads in a resident local Python worker when the first dictation starts. Later dictations reuse it. The first load is slower than subsequent recognition. The app remains cancellable during loading and final recognition; cancelling retires the worker, so the next Qwen dictation loads a fresh one. A Qwen error is reported and the existing preview is retained in History rather than silently substituted as a successful Qwen transcript.

Selecting Parakeet restores Parakeet final recognition on the next recording. An already loaded Qwen worker releases its GPU memory when that Parakeet recording requests final recognition, or when the app exits. The Parakeet device selector controls the preview/Parakeet engine; Qwen requires a CUDA GPU with BF16 support regardless of that selector.

Keep the destination textbox available until final recognition finishes. A submitted/rebuilt editor may no longer accept the Qwen revision; the completed transcript is preserved in History if insertion cannot safely finish. The tray remains active during processing, including the Qwen final pass.

## Local setup

The app package includes the worker, but does not include Python, PyTorch, or model weights. Reuse a trusted, already provisioned local environment or provision one using the versions and model revision recorded in [the isolated comparison guide](../tools/asr-benchmark/README.md). The validated environment uses Python 3.12, PyTorch 2.11.0 with CUDA 12.8, Transformers 5.13.0, and the native `Qwen/Qwen3-ASR-1.7B-hf` checkpoint at revision `bcd2b5b7f32b480ab5790554cfa8347f246a14f3`. The original `qwen-asr` wrapper has a different dependency route and is not used here.

Register absolute local paths with PowerShell 7:

```powershell
pwsh -File scripts/Register-QwenInstallation.ps1 -PythonPath 'C:\Qwen\runtime\Scripts\python.exe' -ModelPath 'C:\Qwen\models\qwen3-asr-1.7b-hf'
```

This verifies the installed interpreter's Transformers and CUDA support, checks the model family/size, and writes `%LOCALAPPDATA%\PttDictation\qwen-installation.json`. It downloads nothing, moves no model files, and does not change the active engine. `-ValidateOnly` performs validation without registration. Then choose Qwen under **Final recognition** and save Settings. The status label checks registered files; actual model loading checks runtime viability.

Both transcription engines operate locally. The Qwen worker forces offline model loading and does not bind a network port. Its owned Windows job releases the worker even if the app exits abruptly. Worker failure, malformed responses, wrong response IDs, startup timeout, and request timeout do not yield a fabricated successful transcript. Startup has a 90-second deadline and each final request a 120-second deadline. A later request can start a fresh worker.

## Silence and long recordings

The worker accepts only complete PCM16 mono 16 kHz WAV input. Exact or near-digital silence with absolute sample amplitude at most 2 is returned as empty before inference. This addresses the observed near-silent `Okay.` output without suppressing ordinary quiet speech; it is not a general speech-activity detector.

Generation has duration-based headroom from 512 to 4096 new tokens. If it reaches the limit, the request fails explicitly instead of accepting a silently truncated transcript. Long recordings consume more processing time and memory; the timeout and token limit are not promises that arbitrarily long dictation succeeds.

## Verification

```powershell
dotnet restore PttDictation.sln --locked-mode
dotnet test PttDictation.sln --configuration Release --no-restore
python -B -m unittest discover -s tools/qwen-worker -p test_qwen_worker.py -v
pwsh -File scripts/Test-QwenInstallation.ps1
```

The .NET tests exercise real bounded child processes for protocol validation, cancellation, recovery, and job ownership without requiring model downloads. Python worker tests cover PCM validation, near-silence, token-limit errors, persistent request handling, Unicode paths, and separation of protocol stdout from library output.

A real local-model pipeline replay is also available:

```powershell
dotnet run --project tools/PttDictation.Replay -c Release -- --qwen-probe C:\Probes\configuration.json C:\Probes\results
```

The configuration contains `pythonPath`, `qwenModelPath`, `workerPath`, `parakeetRuntimePath`, `parakeetModelPath`, and `samples` (each with `id`, `path`, and optional `expectedFragment`). It opens no microphone or textbox, changes no app settings, and starts its own recognizer workers. It verifies actual Parakeet previews followed by Qwen finals, near-silence, cancelling real inference, and recovery after cancellation and malformed audio. Output includes private transcripts: keep it in an ignored local directory. This is production code-path evidence, not proof of the user's exact native hotkey/textbox interaction.
