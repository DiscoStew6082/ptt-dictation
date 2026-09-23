# Empty Parakeet preview investigation, September 23, 2026

## Live observations

After a local app restart, a 4.55-second recording took 7.68 seconds from Stop to completed insertion. Approximately 5.93 seconds was remaining Qwen cold startup, 1.39 seconds was the final request, and 0.34 seconds was insertion. The app had been idle for about eleven minutes before recording; the earlier implementation did not preload during that interval. Background Qwen preload addresses this avoidable first-use wait. It does not make final inference instantaneous, and recording immediately after launch can still overlap loading.

The next 9.45-second recording had ten successful Parakeet HTTP responses with empty text and zero words, spanning cumulative audio from 2.0 through 9.2 seconds. There was no preview parse error, cancellation, target rejection, or CPU retry. Qwen produced 58 characters in 1.13 seconds. A later recording produced normal Parakeet previews and completed Qwen in 1.35 seconds. Thus the absent preview was an empty recognition response, not evidence that the engine failed to start or that insertion rejected its text.

That recording was deleted through normal temporary-audio cleanup, so an exact same-audio reproduction is unavailable. These observations contain no retained transcript text.

## Controlled replay

Used the installed Parakeet `realtime-eou-120m-v1-f16` model with the CUDA parakeet.cpp v0.4.0 runtime and the existing `tools/asr-benchmark/parakeet_bench.py` harness. The harness starts and cleans up its own hidden server and follows the app's inference request and end-of-utterance continuation protocol. The running app and its workers remained available; no audio was played and no visible test windows were opened.

Three existing public LibriSpeech fixtures (5.855, 4.815, and 5.640 seconds) each received seven conditions: original audio; 0.5, 1, 2, or 4 seconds of leading digital silence; and amplitude multipliers of 0.1 or 0.01 without added silence. This yielded 21 variants. Each full variant was requested twice after a first-request probe. All 43 requests succeeded, every full variant produced nonempty text, and repeats were stable within each condition.

Each variant was also split into cumulative prefixes beginning at two seconds and growing by 0.8 seconds, including its exact final tail. Across 150 prefixes plus one first-request probe, all 151 requests succeeded. Eighteen early prefixes returned empty text in the two- and four-second leading-silence conditions; every one of the 21 sequences subsequently produced text and ended with a nonempty result. Both owned benchmark servers exited during cleanup.

Artifacts remain ignored under `publish/parakeet-empty-preview-probe-20260923`: corpus manifests, generated WAVs, `results.json`, and `prefix-results.json`. These are diagnostic experiments, not release package contents. Requests were serialized offline; this is recognition evidence, not a measurement of real-time microphone, queue, or textbox interaction.

## Conclusion and remaining evidence

The controlled fixtures did not reproduce a complete empty-preview sequence. They demonstrate that these particular amounts of leading silence and reduced amplitude alone are insufficient to reproduce the reported incident on these recordings. They do not rule out acoustic conditions, captured-audio problems, or intermittent backend state in the lost recording.

The new metadata-only `recognition.preview_empty_warning` explicitly indexes repeated empty previews followed by nonempty final recognition. A future occurrence can now be found without scanning ordinary successful requests. This warning does not claim that Parakeet's underlying empty-response cause is fixed and does not automatically retain microphone recordings.
