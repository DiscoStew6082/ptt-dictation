# Qwen ASR latency investigation and optimization

This work preserves Qwen3-ASR 1.7B final recognition and Parakeet live preview.
The originally suggested [qwen3_tts_rs](https://github.com/badlogic/qwen3_tts_rs)
produces speech from text; it cannot replace a speech recognizer. The native
recognition candidate uses [llama.cpp b11669](https://github.com/ggml-org/llama.cpp/releases/tag/b11669)
and the [ggml-org Qwen3-ASR GGUF conversion](https://huggingface.co/ggml-org/Qwen3-ASR-1.7B-GGUF).

## Baseline and decision

The measured machine has an RTX 3060 with 12 GB VRAM. The original resident
worker uses Python 3.12.14, PyTorch 2.11.0+cu128, Transformers 5.13.0, BF16,
SDPA, and greedy English decoding. It loads the model once. More than 99% of
warm worker time was model generation; WAV preparation took about 9–45 ms.
Changing the pipe protocol or moving WAV preparation to C# would address little
of the observed delay. Python coordinates native GPU operations already, but
many small operations and repeated framework dispatch still cost time.

Two implementations were evaluated in isolated branches with serialized GPU
access and the daily app restored between runs:

- Native CUDA RMS normalization in the existing worker. Weight multiplication
  remains separate to preserve the original BF16 rounding boundary. This adds
  no dependency and leaves CPU execution unchanged. Exact numerical tests cover
  specific inputs and shapes, not every possible BF16 input or future kernel.
- Native BF16 recognition through an owned, resident llama.cpp server. The
  Python adapter uses only the standard library for transport and lifecycle;
  ordinary inference runs in the native server. The model remains Qwen 1.7B,
  with no Q4/Q8 weight downgrade. All 311 decoder tensors matched the original
  learned weights; 397 of 398 projector tensors matched after documented
  dtype conversions. The remaining tensor is a generated sinusoidal table.

Prioritizing cuDNN attention was rejected: its warm kernel was faster, but new
input shapes incurred 7–44 second initialization delays. Combining it with
normalization also changed an initial fixture transcript. It is not enabled.

## Initial measurements

Qwen3-ASR 1.7B, BF16 weights; resident processes; public 16 kHz mono speech.
Each original result below is a median of three runs. These are worker/adapter
measurements, not microphone-to-editor latency or a reliable tail percentile.

| Recording | Original Transformers | Native adapter prototype |
| --- | ---: | ---: |
| 5.855 seconds | 1.705 s | 0.372 s |
| 29.4 seconds | 5.800 s | 1.295 s |

The initial broader corpus has 30 cases: 28 public rows representing 26 distinct
utterances from ten speakers, plus silence and a multi-speaker pause fixture.
The duplicate utterances had different WAV metadata and were caught by checking
decoded PCM. The corpus generator now deduplicates PCM and selects subsequent
utterances; the corrected corpus has 28 distinct public utterances and two
synthetic cases. Original evidence is retained rather than relabeled.

On the initial corpus, native recognition and the baseline had the same
normalized words in every case and the same 12 edit errors over 650 reference
words. Two exact-text differences were capitalization only: “rocky Ithaca”
and “Bird's nest salad.” Native repeats were stable and speech cases were
3.55–4.75 times faster. This is bounded corpus evidence, not a claim of universal
accuracy equivalence. Silence in the production adapter uses the existing
near-digital-silence guard before inference.

The normalization-only candidate passed all 30 per-case error checks. Its one
text difference was “half ripe” versus “half-ripe.” The initial ordered
eight-clip comparison showed a 29.3% reduction in the sum of clip medians;
ten alternating warm runs per variant subsequently measured 24.0% lower latency
on the short clip (1.776 s to 1.349 s) and 24.3% on the long clip (5.712 s to
4.323 s). The real Parakeet-preview/Qwen-final pipeline passed silence,
cancellation during inference, fresh-worker recovery, malformed-audio rejection,
and subsequent recovery. Cancellation returned in 5.1 ms in that run.

## Reproducibility and evidence

`tools/qwen-benchmark/` provides hash-checked fixture preparation, per-case
reference scoring, timing, transcript hashes, and bounded child-process cleanup.
Windows workers start suspended, enter their owned job, then resume. Native
workers additionally identify their actual interpreter PID before loading any
children; the host establishes ownership before acknowledging startup.

Scoring normalizes punctuation, case and common titles but retains contractions
and number spellings. Scores are custom comparison measurements, not leaderboard
WER. A worse case or unstable candidate fails even when aggregate errors improve.
Equal-error changed text requires direct inspection. Three-run “p95” is merely
the maximum of those observations and must not be represented as a reliable
population percentile.

Local, ignored evidence is under `smoke/qwen-optimization-20261026/` and the
isolated worktrees beneath `smoke/worktrees/`. Corpus provenance records the
OpenSLR dev-clean checksum and CC BY 4.0 license. Benchmarks use public or
synthetic audio only. Raw recordings, transcripts, downloaded binaries and model
weights are not source or release-package contents.

## Production adapter validation

The corrected 30-case corpus was run three times per case through the production
adapter and compared with the original worker on the same files. All per-case
error/stability gates passed: 15 errors over 677 reference words for each engine.
The sum of speech-case medians fell from 60.189 s to 14.768 s (4.08 times faster).
Every speech case improved by 3.51–4.75 times. Exact-text changes were inspected
directly and preserved the same normalized words.

| Recording | Original worker | Production native adapter |
| --- | ---: | ---: |
| 5.855 seconds, median of three | 1.582 s | 0.376 s |
| 29.4 seconds, median of three | 5.497 s | 1.312 s |
| 182.99 seconds, single long-input check | 28.890 s | 7.495 s |

Resident native startup was 3.476 s. The three-minute output had the same eight
reference errors over 383 words; direct inspection verified that changed text
preserved normalized words. A 369.715-second recording exercised whole-audio
fallback and exactly matched the original transcript (20 errors over 793 words).
It took 74.598 s including the cold fallback load, compared with 72.229 s for
the already loaded original worker. **The native speedup does not apply to
recordings over five minutes.** The next short request reloaded native inference
in 3.687 s and returned the same text as before fallback.

Process observations verified that native and HF model workers never overlapped,
that native returned after fallback, and that cancelling an active fallback
returned no late result. Every observed owned process exited. The real C#
Parakeet-preview/native-final pipeline passed silence, cancellation, fresh-worker
recovery and malformed-audio recovery; native cancellation returned in 4.9 ms.

Final source validation passed 639 .NET tests, 31 worker Python tests (including
the CUDA checks), 21 benchmark/corpus tests, one native-registration rejection test,
and seven existing Qwen-registration checks. The 34 opt-in visible-UI tests were
not run on the user's desktop. Independent review found no blocking issues.

## Installed verification

Source commit `5397792` was staged, then installed with `Update-LocalApp.ps1` at
this machine's existing stable `publish/ptt-dictation-win-x64/PttDictation.exe`
path. The updater launched through Explorer normally with no arguments and
verified all 605 package hashes; a separate `-VerifyOnly` pass confirmed the
receipt and one canonical app process. Native assets were independently pinned,
copied and verified under the permanent local application-data directory.

Public-fixture requests through that running app produced these medians:

| Recording | Before, worker | Installed, worker | Installed, whole endpoint |
| --- | ---: | ---: | ---: |
| 5.855 seconds | 1.680 s | 0.389 s | 0.469 s |
| 29.4 seconds | 5.496 s | 1.320 s | 1.391 s |

Each installed result exactly matched its original transcript; three silence
requests remained empty. No request-audio directories remained afterward. The
first short request after restart took 0.748 s in the worker; the table reports
the median of all three requests, not a claim that startup has no cost.

The automation input check initially reported zero signal from the Wireless
microphone, so physical microphone/hotkey/editor acceptance was left open at
deployment. No microphone setting was changed. On October 27, the user responded
“It's working” after the request to try normal dictation, closing ordinary live
dictation acceptance for this installed build. This is user-confirmed interaction
evidence, separate from the source, pipeline replay and endpoint checks.
