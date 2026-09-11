# Qwen versus the installed Parakeet setup

Date: 2026-09-11. Scope: an authorized, isolated inference benchmark. No PTT app integration or deployment was performed.

## Finding

Both Qwen sizes run locally on this computer. On this small sample, they return cleaner recognition than the selected Parakeet engine, but take longer to transcribe completed recordings. The 1.7B model is the stronger candidate for a future optional final-recognition pass; its silence behavior needs a guard and broader natural-use testing. The 0.6B model saves substantial memory but did not provide a meaningful short-recording speed advantage here, and consistently omitted one repeated spoken section in a long probe.

This supports further evaluation, not replacing the current default. The current Parakeet path remains much faster for short dictation. No claim about live streaming, cancellation, or hotkey-to-textbox responsiveness is established by these offline runs.

## Exact configurations

- **Current Parakeet:** `realtime-eou-120m-v1-f16`, F16 GGUF, CPU, `parakeet.cpp` v0.4.0 persistent server. These are the actual selected settings, not a larger NVIDIA Parakeet leaderboard model.
- **Qwen 1.7B:** `Qwen/Qwen3-ASR-1.7B-hf`, BF16, CUDA/SDPA, revision `bcd2b5b7f32b480ab5790554cfa8347f246a14f3`.
- **Qwen 0.6B:** `Qwen/Qwen3-ASR-0.6B-hf`, BF16, CUDA/SDPA, revision `7f1569a48a89f3e3f4dc3a5c9d28bddd903bc76c`.
- **Machine/runtime:** Windows 11, RTX 3060 12 GB, driver 610.74, Python 3.12.14, Torch 2.11.0+cu128, Transformers 5.13.0. Qwen uses batch one, greedy decoding, English, no vocabulary hints, no compilation, four CPU threads, and a 512-token output budget.

Qwen uses the documented native Transformers model path. This avoids installing the original `qwen-asr` package with its older Transformers dependency pins. [Native model documentation](https://huggingface.co/Qwen/Qwen3-ASR-1.7B-hf)

## Initial comparison

Values below are warm medians from three repetitions of each original/public recording. Loading/import costs are separate. The public time is the sum of per-recording medians for 86.345 seconds of audio, not a single batch request.

| Measurement | Current Parakeet | Qwen 1.7B | Qwen 0.6B |
| --- | ---: | ---: | ---: |
| Saved speech recording, 12.028 s | 0.424 s | 1.567 s | 1.494 s |
| Eight public clips, summed warm time | 3.123 s | 20.169 s | 20.335 s |
| Public reference word errors | 12 / 200 | 6 / 200 | 6 / 200 |
| Custom normalized public WER | 6.0% | 3.0% | 3.0% |
| Peak PyTorch allocated memory, initial corpus | Not applicable | 4.32 GiB | 1.98 GiB |
| Peak PyTorch reserved memory, initial corpus | Not applicable | 4.99 GiB | 2.66 GiB |

The Parakeet server's peak working set across the expanded corpus was approximately 867 MiB of system RAM. PyTorch allocation/reservation is not total GPU usage: it excludes other applications and some CUDA overhead, and reserved blocks can persist across requests. It is not a per-recording minimum-memory claim.

The saved speech output differed mainly at its opening phrase and formatting. Both Qwen versions produced a plausible opening where Parakeet produced a different word. The complete private transcript comparison is kept in the ignored local report; it is not committed here. No independently checked gold transcript exists for that recording, so it has no numerical accuracy score.

## Startup costs

| Measurement | Current Parakeet | Qwen 1.7B | Qwen 0.6B |
| --- | ---: | ---: | ---: |
| Fresh server process to owned listening socket | 0.565 s | Not applicable | Not applicable |
| Python library import time | Not applicable | 9.384 s | 9.249 s |
| Processor/model loading after imports | Not separately measured | 3.367 s | 2.049 s |
| First request on the saved speech | 0.427 s | 4.352 s | 3.081 s |

These are different startup stages, not equivalent cold-start benchmarks. OS disk caches were not purged. A future Qwen adapter should reuse a resident worker rather than import Python libraries and reload the model for every dictation.

## Silence and long-input behavior

Only one of the three preserved files has appreciable signal. The other two contain approximately plus/minus one PCM16 unit of noise, and are treated as near-silence controls. A separate three-second WAV contains exact digital silence.

In the initial corpus, Parakeet and Qwen 0.6B returned empty text for all silence controls. Qwen 1.7B returned `Okay.` for the 4.638-second near-silent clip on all three warm runs, while producing no text for the other near-silent clip and digital silence. This is a concrete reason to test a speech-activity gate before using it in the app; no such guard was added during the benchmark.

The 70.933-second stress input repeats the same preserved speech three times with quiet files and extra gaps. Parakeet recovered all three sections using its production-style EOU continuation. Qwen 1.7B recovered all three; Qwen 0.6B recovered only two in the first run. The Qwen outputs were well below the token cap. The installed processor does not truncate audio by default.

Follow-up runs started fresh workers and repeated each probe three times per engine. All three warm outputs agreed for every engine/input pair:

| Follow-up input | Current Parakeet | Qwen 1.7B | Qwen 0.6B |
| --- | --- | --- | --- |
| 4.638 s near-silence | Empty | `Okay.` | Empty |
| 3 s digital silence | Empty | Empty | Empty |
| 70.933 s repeated-speech probe | All three sections | All three sections | Only two of three sections |
| Three different public excerpts, 1 s gaps, 31.58 s total | All three sections | All three sections | All three sections |
| Same excerpts, 10 s gaps, 49.58 s total | All three sections | All three sections | All three sections |

Both Qwen versions matched the normalized concatenated public references on those two follow-up inputs. Parakeet made two substitutions on the 1-second-gap version and three on the 10-second-gap version. These synthetic inputs reuse existing speech, so their results are not pooled into the initial 200-word score. The smaller model's omission is reproducible on the repeated-speech input; the different-sentence results do not establish a general failure at long pauses. Conversely, two successful pause probes are not proof of robust long-form recognition.

## Limits of the accuracy result

The eight public clips are the first eight rows of `hf-internal-testing/librispeech_asr_dummy`, `clean/validation`: one speaker reading one passage. The first clip is also used in Qwen's documentation. This is a familiar public sanity check, not a held-out or representative benchmark. [Fixture dataset](https://huggingface.co/datasets/hf-internal-testing/librispeech_asr_dummy)

The local scorer applies Unicode normalization, lowercase, punctuation removal except internal apostrophes, and consistent expansion of common titles such as `mr` to `mister`. It does not rewrite numbers, contractions, names, or semantic equivalents. These custom scores are not directly interchangeable with leaderboard WER. Qwen's six errors occur in the single 29.4-second public clip; the six-word gap is too small and narrow to establish broadly halved dictation errors.

Original/public transcripts were stable across all three warm repetitions. The initial long/digital-silence probes ran once per Qwen model and three times for Parakeet; follow-up probes use three repetitions for each engine. Raw totals across unequal run counts should not be compared.

Timing compares the user's actual Parakeet CPU configuration to proposed Qwen GPU configurations. It is not an equal-hardware model ranking. Parakeet includes local HTTP requests and EOU continuation; Qwen calls its Python model directly. Both include WAV input processing and returned text. Neither includes the app's recording, correction, or insertion path.

## Reproduction and evidence

Reusable harnesses and exact Python package versions are in `tools/asr-benchmark/`. Nine behavior checks passed, covering EOU continuation, malformed data, wrong loopback process ownership, and WER scoring. An independent review checked the harnesses and reproduced the public edit-distance totals. The expanded and follow-up comparisons completed 160 inference calls without runtime errors or Qwen token-cap hits; a separate 10-call Parakeet pilot also completed. Successful execution does not imply correct transcription, as the silence and omission findings show.

Private audio, snapshots, per-run outputs, raw logs, exact hashes, and the detailed local report live under Git-ignored `smoke/asr-comparison-20260911/`. Model downloads were pinned to the revisions above, and all preserved WAVs were hashed before inference. Qwen inference used local files in HF offline mode. Parakeet contacted only its separately started, verified, owned loopback server. No audio was submitted to an external transcription API.

Closeout verified identical installed-executable and settings SHA256 hashes, the same original app/server process identities and command lines, and no remaining benchmark workers. Benchmark code, environment setup, and documentation were created; application source was not edited, and no app deployment, restart, or UI acceptance test was performed. The downloaded models and isolated Python environment remain available locally for reuse. App integration requires a separate decision.
