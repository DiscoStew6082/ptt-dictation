# Same-audio Qwen and Parakeet chunking evaluation

Date: 2026-09-23. This was a local offline comparison using already installed
models. No audio was uploaded, no models downloaded, and no app settings,
textbox, clipboard, or live app processes were changed by this evaluation.

## Finding and integration decision

Qwen's advantage on this small reference set persists when **both engines receive
the complete recording**. Whole-recording Qwen made six word errors and
whole-recording Parakeet made twelve out of 200 reference words. Independent
six-second chunks slightly worsened both models. This supports a model-quality
contribution beyond the difference between preview and final context, but the
small familiar fixture does not establish accuracy on the user's natural speech.

Keep Qwen's whole-recording final pass. Do not automatically replace current
previews with independently decoded Qwen chunks or attempt every-two-second Qwen
retranscription: measured growing-prefix calls exceed even that sampled cadence
after about ten seconds of speech on this fixture. Production offers prefixes
more frequently, as explained below. A future Qwen preview option needs its own
bounded scheduling/cancellation and natural-speech acceptance, while retaining the
complete-recording final result. No Qwen preview change was deployed by this work.

An important correction to the earlier discussion: the current
`WasapiAudioRecorder` already emits **cumulative audio**. Its first prefix covers
two seconds; `PcmChunkBuffer` then advances by **0.8 seconds** per emitted chunk
(two-second window minus 1.2-second overlap), subject to audio callback timing.
`ChunkedTranscribingDictationSession` replaces the cumulative preview. Parakeet
therefore receives increasing context. Independent six-second slices below are
a controlled context-loss experiment, not a reproduction of the current preview
pipeline or evidence that current Parakeet previews discard all previous context.

## Configuration and evidence

- Qwen: `Qwen/Qwen3-ASR-1.7B-hf`, revision
  `bcd2b5b7f32b480ab5790554cfa8347f246a14f3`, BF16, CUDA/SDPA, greedy English
  decoding, batch one, four CPU threads, no vocabulary hints, 1,024-token limit.
- Parakeet: current selected `realtime-eou-120m-v1-f16` GGUF and installed
  `parakeet.cpp` v0.4.0 CUDA server. Its log confirms `Backend using device: CUDA0`.
  Production-style EOU continuation and owned-loopback checks were retained.
- Hardware/runtime: RTX 3060 12 GB; existing Windows Python environment with
  PyTorch 2.11.0+cu128 and Transformers 5.13.0. Both engines used the GPU in this
  run; the older September 11 Parakeet timing used the CPU and is not the current
  speed baseline.
- Inputs: the same eight existing LibriSpeech dummy public clips, 86.345 seconds
  and 200 normalized reference words; three retained private WAVs without checked
  reference transcripts; one digital-silence control. Input hashes were verified.
- Two warm repeats per prepared WAV, after a separate first request. Fifty WAV
  inputs covered whole recordings, disjoint six-second chunks, and every-two-second
  growing prefix of the longest public recording, including its 29.4-second tail.
- Models ran serially in separately owned benchmark processes. Live app workers
  stayed running. Total GPU usage temporarily reached approximately 11.2 GiB;
  Qwen peak PyTorch allocation was 4.03 GiB. Allocator figures do not represent
  total GPU memory or guarantee headroom during simultaneous dictation.

## Complete-reference scores

The two repeats produced identical text for each public engine/condition pair.
Times are summed per-recording warm medians, including preprocessing and returned
text, excluding imports/loading and the app's UI/insertion work.

| Input treatment | Qwen errors / words | Qwen WER | Parakeet errors / words | Parakeet WER |
| --- | ---: | ---: | ---: | ---: |
| Complete recording | 6 / 200 | 3.0% | 12 / 200 | 6.0% |
| Independent six-second chunks | 7 / 200 | 3.5% | 15 / 200 | 7.5% |

| Input treatment | Qwen summed inference | Parakeet summed inference |
| --- | ---: | ---: |
| Complete recording | 20.200 s | 0.877 s |
| Independent six-second chunks | 23.943 s | 0.998 s |

Chunks contain every original PCM sample exactly once, including very short tails.
Their texts are concatenated without overlap or removal of repeated words.
This deliberately tests plain independent chunking; it does not evaluate tuned
silence boundaries, overlapping-window alignment, or stateful streaming inference.

Qwen's complete-recording errors were four substitutions and two deletions;
Parakeet's were ten substitutions and two deletions. With six-second chunks they
became six substitutions/one deletion and eleven substitutions/four deletions,
respectively. Neither condition introduced scored insertions on these examples.

## Sampled cumulative preview workload

The longest public clip was tested as identical increasing prefixes for each
engine, sampled every two seconds. This uses the production growing-context
shape at a slower cadence than its 0.8-second advance after the initial prefix.
Each call was measured independently in a resident process. These values measure
inference cost, not delivered app preview latency or coalesced scheduling.

| Available audio | Qwen warm median | Parakeet warm median |
| --- | ---: | ---: |
| 2 s | 0.757 s | 0.042 s |
| 6 s | 1.511 s | 0.079 s |
| 10 s | 3.460 s | 0.084 s |
| 20 s | 4.212 s | 0.176 s |
| 28 s | 6.403 s | 0.228 s |
| Complete 29.4 s tail | 6.620 s | 0.231 s |

Processing all fifteen prefixes cost a median 55.938 seconds for Qwen and 2.053
seconds for Parakeet. The app's existing coalescing can skip superseded pending
prefixes, so this is **not** a prediction that the live app accumulates a
55-second queue. It does show that processing every sampled two-second Qwen
prefix cannot keep up on this sample; it does not reproduce or quantify the
more frequent production schedule. The tail is included to check complete
coverage; its extra call is not claimed to be the app's exact Stop-time schedule.

No timestamped gold reference exists for arbitrary partial prefixes, so there is
no invented partial-prefix WER. At the complete 29.4-second prefix, Qwen made six
errors in 68 words and Parakeet five. Qwen did not win every individual example;
its aggregate advantage comes from the eight-clip result.

## Controls, limits, and cleanup

The retained 12.028-second speech recording took 1.810 seconds whole / 2.385
seconds chunked with Qwen, and 0.090 / 0.148 seconds with Parakeet. Its transcript
has no independently checked reference, so no private-speech accuracy score is
reported. The benchmark never prints private transcripts in this document.

Both models returned empty text for digital silence. Raw Qwen inference again
returned nonempty text for one near-digital-silence retained file. This runner
deliberately benchmarks the model directly; the application's existing Qwen worker
has a `peak <= 2` PCM16 silence guard and would skip inference on that control.
The direct-run result must not be presented as a new failure of the guarded app.

The public fixture is one speaker reading a familiar passage, includes the Qwen
documentation example, and has only 200 words. Repeated calls are repeatability
checks, not additional independent accuracy data. Scoring uses the existing local
normalizer (NFKC, lowercase, punctuation removal except internal apostrophes,
consistent title expansion). It is not a leaderboard WER calculation. Live app
workers remained resident and could contend for resources if used during a run.
Cancellation, user speech over long sessions, natural pauses, active editing, and
actual preview delivery were not tested by this offline benchmark.

Both runners finished successfully: **202/202 requests**, including two first
requests and 200 warm requests, with no runtime failures or Qwen token-cap hits.
Both Python benchmark processes exited. The Parakeet harness verified termination
of its owned PID 30592 (Windows termination exit code 1; harness status complete).
No owned benchmark workers remained, and GPU free memory returned to 5,237 MiB.
The live app and its workers were not stopped by this evaluation.

## Reproduction

The reusable additions are `tools/asr-benchmark/chunking_eval.py` and
`test_chunking_eval.py`; usage is in the benchmark README. Seven behavior tests
verify exact sample coverage, cumulative-tail handling, preservation of repeated
words, rejection of missing/failed/capped/duplicate result rows, and rejection of
stale same-ID audio hashes/durations or missing/duplicated provenance metadata.

Local, Git-ignored evidence is under
`publish/qwen-chunking-evaluation-20260923/`: prepared WAVs and source hashes,
`inputs/groups.json`, `inputs/corpus.json`, the current selected-runtime settings
snapshot, `qwen.json`, `parakeet.json`, `comparison.json`, and console/server logs.
Those raw results may contain private transcripts and must remain untracked.

The execution sequence was: prepare the verified existing corpus; run Qwen with
two warm repeats and two probe repeats; run Parakeet with two warm repeats;
score with `--repeats 2`; verify worker cleanup and GPU release. The benchmark
helper verifies each prepared WAV's SHA256 and duration against the runner's input
metadata and Qwen's per-run metadata. After this provenance check was added, groups
were regenerated from the verified source audio and both existing raw reports
rescored successfully with unchanged results; inference was not rerun. The helper
also refuses to score partial, failed, or token-capped runs as a successful
comparison. Nothing from this evaluation establishes textbox interaction acceptance.
