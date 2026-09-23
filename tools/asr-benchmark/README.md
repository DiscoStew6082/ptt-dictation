# Isolated speech-recognition comparison

These scripts measure complete-recording recognition separately from PTT Dictation.
They do not modify application code/settings, launch the application, or test
hotkeys, textbox insertion, cancellation, or live microphone streaming.

Keep audio, raw transcripts, runtime/model files, settings snapshots, and generated
results under the Git-ignored `smoke/` directory. Review generated report contents
before copying anything into tracked documentation. Model outputs are not a gold
reference for the user's speech.

## Environment used

Windows 11, Python 3.12.14, PyTorch 2.11.0+cu128, Transformers 5.13.0, RTX 3060 12 GB.
`environment-win-cu128.txt` records all resolved package versions for this run.
Install Torch/Torchaudio from the official CUDA 12.8 wheel index before installing
the remaining packages. Do not install the original `qwen-asr` wrapper in this
native Transformers environment: its older Transformers pin is incompatible.

The benchmark directory used in this investigation is
`smoke/asr-comparison-20260911`. In the commands below, `$bench` represents its
resolved absolute path and `$python` its `.venv/Scripts/python.exe` executable.
Set `UV_CACHE_DIR`, `HF_HOME`, and `NUMBA_CACHE_DIR` beneath `$bench/cache` for setup
and runtime commands. The benchmark itself sets HF offline mode and requires
pre-downloaded local model directories. It sends no recordings to a cloud API.

Models downloaded using `hf download --revision ... --local-dir ...`:

| Model | Revision |
| --- | --- |
| Qwen/Qwen3-ASR-1.7B-hf | bcd2b5b7f32b480ab5790554cfa8347f246a14f3 |
| Qwen/Qwen3-ASR-0.6B-hf | 7f1569a48a89f3e3f4dc3a5c9d28bddd903bc76c |

## Inputs and execution

1. Preserve recordings into `$bench/inputs`, recording original location, file name,
   byte length, and SHA256 in `manifest.json`. Each item must have `id`, `file`, and
   `sha256`. Copy the app settings to an ignored snapshot for Parakeet; never point
   a benchmark at a mutable settings file when comparing repeatable configurations.
2. `prepare_corpus.py INPUTS --public-parquet PARQUET` verifies the preserved hashes,
   inventories PCM16 audio, and produces `corpus.json`. It extracts the first eight
   entries from the supplied public parquet, plus clearly labelled silence and
   concatenated-audio probes. Default `--public-count` is eight.
3. Run `parakeet_bench.py --settings SNAPSHOT --corpus CORPUS --output RESULT
   --warm-runs 3`. See [Parakeet details](README-parakeet.md).
4. Run `qwen_bench.py --model LOCAL_DIRECTORY --model-id HUB_ID --revision REVISION
   --corpus CORPUS --output RESULT`. Repeat with the other model. Inference jobs
   must run serially. Defaults are three warm repeats for original/public clips,
   one for stress/digital silence, greedy decoding, forced English, no hotwords,
   BF16/SDPA, batch one, four CPU threads, and a 512-token output limit.
5. `compare_results.py --corpus CORPUS --results RESULT... --output COMPARISON`
   calculates per-input warm medians and pooled WER on public references only.
6. `prepare_followup.py INPUTS` creates `followup-corpus.json` for investigating the
   observed omission/silence behavior. Run both Qwen models with `--probe-repeats 3`
   and Parakeet with `--warm-runs 3`. Keep these artificial stress results separate
   from the original public score; they reuse speech rather than adding independent
   accuracy examples.

Example Qwen invocation (PowerShell):

```powershell
& $python tools/asr-benchmark/qwen_bench.py `
  --model "$bench/models/qwen3-asr-1.7b-hf" `
  --model-id Qwen/Qwen3-ASR-1.7B-hf `
  --revision bcd2b5b7f32b480ab5790554cfa8347f246a14f3 `
  --corpus "$bench/inputs/corpus.json" `
  --output "$bench/results/qwen-1.7b.json"
```

The public fixture used here is `hf-internal-testing/librispeech_asr_dummy`,
configuration `clean`, split `validation`, first eight rows. The Dataset Viewer
`/parquet` endpoint provides its 9 MB parquet URL. `corpus.json` records reference
text, speaker, source row/ID, parquet hash, and each decoded WAV hash. This is one
speaker reading a familiar passage, including Qwen's model-card example. It is a
small sanity check, not a held-out or representative accuracy benchmark.

## Measurement limits and checks

WAV read/preprocessing, inference, and returned text are included in request wall
time. Qwen's synchronized GPU timing separates preprocessing and generation. Model
loading and Python imports are reported separately; first requests are not pooled
into warm medians. OS caches are not purged. Parakeet includes local HTTP/EOU
continuations; Qwen is called inside its Python process. Neither includes app UI
or textbox insertion overhead.

PyTorch peak allocated/reserved figures describe its allocator. Reserved memory can
persist from earlier requests and is not total device usage or a per-clip minimum.
The September 11 Parakeet baseline ran on CPU. Those historical results compare
the proposed setup with the setup installed then, not equal-hardware model
performance. The September 23 comparison verified both engines on CUDA; see
[updated chunking results](../../docs/research/qwen-chunking-evaluation-2026-09-23.md).

Every result persists errors and completion state. Inspect token-limit flags and
transcript stability before accepting a score. The Qwen runner has a token budget
but no independent wall-clock watchdog; supervise its process and only terminate
the owned benchmark process if needed. The scorer reports observed successful and
failed warm-run counts; compare them with the planned repeat count to spot missing
runs. It scores the first successful warm transcript; if repeats differ, inspect each
output rather than treating the displayed score as stable.

```powershell
& $python tools/asr-benchmark/parakeet_bench.py --self-test
& $python tools/asr-benchmark/compare_results.py --self-test
```

The nine checks cover EOU continuation, malformed responses/audio, refusing the
wrong loopback owner before audio transfer, and scoring substitutions, insertions,
deletions, abbreviations, and empty references. The actual saved-audio runs provide
the runtime evidence. Application acceptance would require separate approved work.

## Whole recordings, independent chunks, and cumulative previews

`chunking_eval.py prepare --corpus EXISTING_CORPUS --output IGNORED_DIRECTORY`
hash-verifies existing PCM16 inputs and writes three labelled variants:

- `whole`: the complete recording.
- `fixed6`: disjoint six-second windows, concatenated without deleting repeated
  boundary words. This is a context-loss probe, not stateful streaming.
- `cumulative2`: every two-second prefix of the longest public clip, plus its
  exact full tail. This samples the recorder's growing-context workload at a
  slower cadence than production: the first live prefix covers two seconds,
  then the two-second window with 1.2-second overlap advances by 0.8 seconds
  per emitted chunk, subject to audio callback timing.

Run both existing benchmark runners serially against the resulting `corpus.json`,
using two warm repeats (`--repeats 2 --probe-repeats 2` for Qwen and
`--warm-runs 2` for Parakeet). Then run:

```powershell
& $python tools/asr-benchmark/chunking_eval.py score `
  --groups "$output/inputs/groups.json" `
  --results "$output/qwen.json" "$output/parakeet.json" `
  --repeats 2 --output "$output/comparison.json"
& $python -B -m unittest discover -s tools/asr-benchmark -p test_chunking_eval.py
```

Scores verify each prepared part's SHA256 and duration against the runner's input
metadata (and Qwen's per-run metadata), rejecting stale same-ID results. They also
reject missing, failed, duplicated, or token-capped runs and report every repeat's
public WER. Private dictation remains unscored; the summary never includes
transcript text. Prefix latency is measured offline, not an app responsiveness or
cancellation test. Independent chunk scores must not be called the current app's
preview score: its recorder currently emits cumulative audio. Keep raw model output
and prepared audio in Git-ignored `publish/` or `smoke/` directories. Qwen's existing
runner prints raw text, so redirect its console output to an ignored local log.
