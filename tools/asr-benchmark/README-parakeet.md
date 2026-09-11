# Current Parakeet benchmark

`parakeet_bench.py` reads a saved settings snapshot and starts one separate,
owned `parakeet-server.exe` process from its selected runtime, using its selected
GGUF model. It leaves the app and settings unchanged. The script requires Windows
and Python 3.11+; it has no third-party Python dependencies.

```powershell
python tools/asr-benchmark/parakeet_bench.py --self-test
python tools/asr-benchmark/parakeet_bench.py `
  --settings smoke/asr-comparison-20260911/settings-snapshot.json `
  --inputs smoke/asr-comparison-20260911/inputs `
  --output smoke/asr-comparison-20260911/results/parakeet-current.json `
  --warm-runs 3
```

Use the actual isolated Python executable if `python` is unavailable on PATH.
The input directory must contain `manifest.json` entries with `id`, `file`, and
`sha256`; every WAV hash is verified before use. Results and raw server logs can
contain private dictation text and should stay in the ignored `smoke` directory.
Alternatively, pass `--corpus path/to/corpus.json` instead of `--inputs`. This
accepts the same JSON list, resolving `file` paths relative to the corpus JSON so
it can include nested public, private, silence, and long-audio probe directories.
Optional `kind`, `source`, and `reference` fields are copied into the result inputs.
Each invocation always starts a fresh owned server; use a separate output name to
preserve earlier measurements.

The command line matches `PersistentParakeetServerTranscriber`: `--model`,
`--host 127.0.0.1`, and `--port`. Its multipart requests match the app's
`verbose_json` and word timestamp fields. The harness also follows the app's EOU
continuation loop, offsetting subsequent word timestamps and preserving responses
for every segment. A single raw POST would risk losing words after the first EOU.
No transcript-correction dictionary is applied to these raw model outputs.

Connections use the Win32 TCP ownership table to verify the established server
connection belongs to the owned process before sending HTTP/audio bytes. The
script ignores HTTP proxy environment variables and only connects to IPv4 loopback.
Cleanup targets only the process handle this invocation started.

Reported cold readiness includes process launch to an owned accepting TCP socket.
The first transcription is recorded separately because additional lazy setup may
occur. Disk caches are not flushed, so this is a fresh process rather than a cold
operating-system cache measurement. All warm repetitions run serially after that
first request. Per-recording timing includes WAV read/parse, segment reconstruction,
HTTP transport, all EOU continuation requests, and JSON parsing. It does not measure
the UI hotkey-to-insertion delay. The separate HTTP client uses a new connection
per segment; app connection pooling may have slightly different overhead.

Working-set and private-memory counters describe only this owned server. Peak
working set is the Windows process lifetime peak, not a sampled GPU measurement.
The selected CPU baseline does not use CUDA. Keep other benchmark inference serial
to avoid competition; leave unrelated live applications unchanged and document them.

The script emits JSONL progress to stdout and atomically updates one JSON report
after every run. Failed requests remain in `runs` and `errors`; an unsuccessful run
or cleanup returns a nonzero exit code. Tests exercise EOU tail recovery, timestamp
offsets, malformed response/audio rejection, termination at tiny EOU timestamps,
and refusing a mismatched process before sending private bytes.

Startup has a 60-second default timeout. Each recording has a 300-second request
budget checked before each EOU continuation, and its remaining budget becomes the
HTTP socket timeout. Socket timeouts apply per blocking operation, so this is not a
hard operating-system wall-clock kill deadline. The EOU loop also has a 64-segment
limit. The entire serial corpus has no independent global deadline. Both timeout
values are configurable and retained in the report.
