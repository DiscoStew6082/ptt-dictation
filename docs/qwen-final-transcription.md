# Qwen final transcription

Select **Qwen3-ASR 1.7B (NVIDIA GPU)** under **Final recognition** in Settings to retain Parakeet live preview and use Qwen after stopping. The existing hold/toggle keys, corrections, cancellation, textbox protections, and Session History remain in use. Settings changes apply to the next recording.

Qwen loads in a resident local Python worker when the first dictation starts. Later dictations reuse it. The first load is slower than subsequent recognition. The app remains cancellable during loading and final recognition; cancelling retires the worker, so the next Qwen dictation loads a fresh one. A Qwen error is reported and the existing preview is retained in History rather than silently substituted as a successful Qwen transcript.

Selecting Parakeet restores Parakeet final recognition on the next recording. An already loaded Qwen worker releases its GPU memory when that Parakeet recording requests final recognition, or when the app exits. The Parakeet device selector controls the preview/Parakeet engine; Qwen requires a CUDA GPU with BF16 support regardless of that selector.

Keep the destination textbox available until final recognition finishes. A submitted/rebuilt editor may no longer accept the Qwen revision; the completed transcript is preserved in History if insertion cannot safely finish. The tray remains active during processing, including the Qwen final pass.

## Local setup

Open **Settings**, select Qwen under **Final recognition**, and choose **Download Qwen**. Setup reports progress and supports **Cancel**. Once ready, save Settings to use Qwen for final recognition; installation does not change the selected engine by itself. **Check Qwen setup** validates a registered installation and repairs a missing/broken runtime without modifying that existing Python environment.

Setup first checks the registered model against the pinned file sizes and SHA-256 hashes. A healthy local model is reused directly, including the model downloaded for the isolated comparison. A healthy runtime is also reused after checking Python, Torch, Transformers, CUDA/BF16 support, and the offline model processor. This check does not load the full model into GPU memory.

For a new installation, setup uses a private directory under `%LOCALAPPDATA%\PttDictation\qwen`. It downloads [Astral's portable Python 3.12.14 Windows runtime](https://github.com/astral-sh/python-build-standalone/releases/tag/20260901), checks the published SHA-256, and extracts only ordinary files/directories inside that private location. It does not install global Python or change PATH. The first setup needs several GB of disk space and downloads: the model alone is approximately 4.1 GB, plus the CUDA runtime and Python dependencies.

The packaged setup resources pin all 58 dependency wheels to exact trusted URLs and SHA-256 hashes. Pip installs binary wheels with hash checking and no unpinned dependency resolution, following its [secure-install guidance](https://pip.pypa.io/en/stable/topics/secure-installs/). The selected versions are Python 3.12.14, [PyTorch 2.11.0 with CUDA 12.8](https://pytorch.org/get-started/previous-versions/), Transformers 5.13.0, and `Qwen/Qwen3-ASR-1.7B-hf` at revision `bcd2b5b7f32b480ab5790554cfa8347f246a14f3`. The original `qwen-asr` wrapper is not used.

Completed model/runtime downloads are reused on retry; partial model downloads resume when the server supports ranges. Cancelling stops only setup's owned child processes. A failed or cancelled setup leaves the existing registration unchanged. The new `%LOCALAPPDATA%\PttDictation\qwen-installation.json` is replaced atomically only after validation succeeds. Setup has a two-hour overall deadline, a 45-minute package-install deadline, and a 90-second runtime-validation deadline. Downloads fail after 60 seconds without a response/data rather than hanging indefinitely.

Manual registration remains available for an already provisioned trusted local environment:

```powershell
pwsh -File scripts/Register-QwenInstallation.ps1 -PythonPath 'C:\Qwen\runtime\Scripts\python.exe' -ModelPath 'C:\Qwen\models\qwen3-asr-1.7b-hf'
```

This advanced route downloads nothing and does not change the active engine. `-ValidateOnly` checks the supplied installation without registering it.

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

## Roll back this local Qwen trial

For the prepared local trial, double-click `Rollback-Qwen.cmd` in the main checkout. It validates `publish/rollback-before-qwen` against its saved hashes, restores the previous app and exact saved settings through `Update-LocalApp.ps1`, and restarts the app at the permanent pinned executable path. Finish or cancel any active dictation before running it. The ignored snapshot is stored under the main checkout.

The launcher restores the pre-trial settings, so settings edits made during the trial are replaced. The downloaded Qwen environment and model remain on disk for reuse, but the restored app does not use them. Selecting Parakeet in Settings is the smaller engine-only rollback if the new app itself works.

The snapshot can be checked without stopping the app:

```powershell
pwsh -File scripts/Restore-LocalAppSnapshot.ps1 -SnapshotPath publish/rollback-before-qwen -VerifyOnly
```

`Update-LocalApp.ps1 -SettingsSource <file>` installs a package and settings together. It validates the settings before stopping the app and restores the previous package and settings if installation or startup fails. The destination remains the permanent installation; the supplied settings file cannot redirect it.

## September 11 settings and setup repair

The Parakeet preview device preference is retained across cancellation, temporary GPU failure, asynchronous asset provisioning, and relaunch. A temporary CPU retry no longer saves CPU over a chosen CUDA preference. Changing the device clears the previous runtime override so the new choice resolves the matching runtime. Settings writes use an atomic replacement; explicit and derived writes publish in the same order.

The Qwen setup button remains available for a registered installation as **Check Qwen setup**. A missing installation shows **Download Qwen**. Both states are independent of the Parakeet model download button.

A final insertion can refresh the text pattern on its original accessibility element once after a transient read failure. This never adopts a newly focused editor and never retries selection or paste dispatch. A permanently unavailable editor still ends safely with the final transcript retained in History; the recorded native editor-loss incident has not been proven to be transient.

Validation on September 11, 2026:

- Full Release suite: 358 passed, zero failures/skips; separate Settings layout capture passed.
- Settings regressions cover cancellation, atomic save failure, device changes, concurrent provisioning, UI publication ordering, and caller-thread publication.
- Installer regressions cover integrity checks, cancellation, bounded processes/downloads, registration preservation, repair, and retry.
- Real existing-model/runtime validation passed with all HTTP requests blocked and registration redirected to a scratch location.
- Real portable Python download, hash verification, extraction, and one pinned wheel installation passed. A complete fresh 58-wheel installation was not repeated.
- The cached CUDA runtime transcribed a saved 7.758-second recording successfully; its server log confirmed CUDA0 on the RTX 3060. This does not establish transcription accuracy.
- Exact native tray-menu and hotkey/textbox acceptance remains unverified. Available automation cannot right-click the notification-area icon; isolated form tests and layout images are supporting evidence only.

For rollback of this repair, double-click Rollback-Settings-Changes.cmd. It restores the verified app and settings from immediately before these changes (commit 49008e2), using the snapshot at C:\Users\stewa\projects\par-win-ptt\publish\rollback-before-settings-recovery. Finish or cancel dictation first. Settings edits made after that snapshot are replaced. The earlier pre-Qwen snapshot and Rollback-Qwen.cmd are also available in the main checkout.
