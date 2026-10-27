# Offline Qwen latency and quality check

This tool runs one owned, resident JSON-lines worker against hash-checked public
or synthetic 16 kHz mono PCM16 fixtures. It does not change app settings, use the
LAN service, or send audio over the network. Run candidates serially with no
other model competing for GPU memory. Model-free tests:

```powershell
python -B -m unittest discover -s tools/qwen-benchmark -p 'test_*.py'
```

Windows workers start suspended and join an owned process-tree job before they
execute. Legacy workers may send `{"type":"ready","protocol":1}` directly.
Workers that must confirm ownership before loading may first send
`{"type":"bootstrap","protocol":1,"pid":123}` with their actual process ID.
The harness verifies membership in its owned job (or its process group on POSIX),
then sends exactly `{"type":"start","protocol":1}`. The worker must wait for
that acknowledgment before loading or spawning inference children, then send its
normal ready message. Both messages share the startup deadline. Unrelated process
IDs and malformed bootstrap messages fail before acknowledgment.

Create a JSON command array containing the Python executable, `-u`, the worker
path, `--model`, and the model directory (all absolute paths). Then run:

```powershell
python -B tools/qwen-benchmark/benchmark.py --command command.json --manifest manifest.json --output results.json --repeats 3
```

The manifest is a list of `id`, `path`, `sha256`, `reference`, and `kind` values;
`kind` must be `public` or `synthetic`. References are independently supplied,
never generated from candidate output. Optional `speaker` identifies public
corpus speakers. Startup and excluded warmup are reported separately. Every
measured request includes wall time, word errors/reference-word count, and a
transcript SHA-256; transcript content is not saved. Word scoring normalizes
Unicode, case, punctuation, and Mr./Mrs./Dr. titles, retaining contractions and
number spellings. These custom scores are not leaderboard WER. Synthetic cases
that reuse speech should be reported separately from the independent corpus.

`p95_seconds` uses the nearest-rank percentile; with three repetitions it is the
maximum, not a reliable tail-latency estimate. Use at least 20 repetitions for
latency gates. Compare per-case speed and text/error results, not only averages.
The `completed` flag is written only after every requested case succeeds. A
failed run retains its partial report and exits nonzero.

Add `--baseline baseline.json` when measuring a candidate on the exact same
manifest. This records each case's latency ratio, changed-text indicator, and
word-error difference. It exits nonzero if any case has more errors than its
baseline's best repetition or if the candidate's transcript varies across runs.
An improvement on one recording cannot hide a regression on another. Text
changes with equal error count still require review; a passing gate alone is
not proof of equivalent natural-speech quality.

To prepare a broader public corpus, `prepare_corpus.py` downloads the 337 MB
OpenSLR LibriSpeech dev-clean archive, checks its published MD5, and prepares two
distinct utterances from each of ten speakers plus silence and a multi-speaker pause
probe. Supply `--output` (an ignored local directory), `--ffmpeg` (an existing
FFmpeg executable), and optionally `--existing-public` pointing at the older
public-fixture manifest. Selection compares decoded PCM hashes against existing
fixtures and prior selections, skipping duplicates even when WAV metadata differs.
Pass `--archive` to reuse a cached archive, and choose a new output directory when
preserving an earlier benchmark manifest. The public corpus is CC BY 4.0; provenance and source
checksums remain with its local manifest. No archive files are extracted to
paths supplied by the archive. Recordings, result files, and runtime commands
belong in ignored `smoke/`, never in the published product.
