# Automatic native Qwen setup: October 27, 2026

The production Qwen installer now provisions the optional native CUDA backend
through the existing Download Qwen / Check Qwen setup action. It first establishes
a verified standard runtime, then checks hardware, downloads pinned native
assets, validates every extracted file and warms the actual native model before
committing its registration. The shared recognizer gate covers worker retirement,
validation and registration, preserving in-flight recognition.

## Fresh-install evidence

The replay tool's `--qwen-setup-probe` ran the production installer without
injected hardware, HTTP, process or asset-plan substitutes. Its app-data and
report directories were initially empty. It did not copy registrations, models
or runtimes from the daily installation.

- Windows x64; NVIDIA GeForce RTX 3060, 12 GiB, compute capability 8.6; driver
  API version 13030. The daily app was paused for serialized model checks.
- Fresh standard HF model and managed Python/CPU PyTorch setup, followed by
  pinned native runtime/model downloads and real native warmup: **388.74 s**.
- Second setup: **22.63 s**, with the same runtime directories, model paths,
  native asset paths and native registration path. Both checks reported ready.
- Fresh setup reported `native_cuda`; every registered asset remained under
  the isolated app-data root. Native inference worked with the newly provisioned
  CPU PyTorch fallback environment; no separately installed CUDA PyTorch was used.

Earlier attempts exposed a deep Torch wheel path exceeding the legacy Windows
limit in the longer test directory and a transient HTTP 429. The final candidate
uses shorter private runtime identifiers and bounded respectful HTTP retries.
An attempted extended-path Python workaround was rejected after a real Numba
wheel failed on its relative script paths; that workaround is not shipped.
The successful run restarted from an empty root after these fixes.

## Same-audio speed and quality

Qwen3-ASR 1.7B, BF16 native decoder/projector; RTX 3060. Resident-worker timings
exclude one-time setup and initial model load. The existing independently
transcribed public/synthetic corpus contains 30 cases, 28 distinct speech
utterances, ten speakers, silence and a multi-speaker sequence. Each case ran
three times. The original CUDA worker baseline was recorded on October 26;
this is a comparison with that saved baseline, not an interleaved rerun.

| Measurement | Original CUDA worker | Fresh automatic native setup |
| --- | ---: | ---: |
| Sum of per-case median recognition times | 60.192 s | 15.781 s |
| Word errors across 677 reference words | 15 | 15 |
| Cases with worse word error count | — | 0 |
| Cases with unstable repeated output | 0 | 0 |

The aggregate speedup was **3.81×**. Native medians were **0.398 s** for the
5.855-second sample and **1.428 s** for the 29.4-second sample. Every per-case
accuracy/stability gate passed. This small public corpus does not establish
accuracy for every accent, microphone, language or workload. See the
[original evaluation](qwen-latency-2026-10-26.md) for corpus construction and the
unchanged long-recording boundary.

## Acceptance boundaries

The fresh installation also passed the real Parakeet preview followed by Qwen
final pipeline for short and long public samples, exact/near silence,
cancellation during native inference, recovery after cancellation and malformed
audio, and owned-process cleanup. Cancellation completed in 0.010 s; a new
worker transcribed successfully afterward. The independently installed standard
CPU fallback loaded in 10.43 s and transcribed the 5.855-second public sample in
14.04 s with zero word errors. That single-sample fallback check does not repeat
the earlier long-recording stress test.

Local validation passed 705 .NET tests, with 34 opt-in interactive tests skipped,
plus the model-free worker, native registration, benchmark and installer checks.
Independent code review found no remaining blockers. The daily app was restored
normally at its unchanged stable installation path afterward; its receipt, all
605 package hashes and one process without arguments were verified. No fresh
validation workers remained.

The clean setup check exercises the real installer code path, not a tray or
Settings gesture. An earlier isolated Settings Download Qwen click reached the
installer but failed on the original long path. Computer Use was stopped by the
user; no later successful UI interaction is claimed. Unit/layout checks and CI
visible-UI checks are separate from that desktop gesture.

Recordings over five minutes retain the unchanged whole-recording fallback;
they do not receive the native speedup. Hardware eligibility tests cover the
unsupported paths, but the fresh native download/load run above was performed
on one RTX 3060, not every eligible GPU.

Raw reports and public audio fixtures remain in ignored local validation
directories. No private recordings, settings, credentials, runtime binaries or
model weights are included in the source or application release archive.
