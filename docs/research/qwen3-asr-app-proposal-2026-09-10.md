# Qwen speech-to-text app research and proposal

Follow-up: the user subsequently approved an isolated benchmark. See [measured results from September 11](qwen3-asr-benchmark-2026-09-11.md). The proposal below is the earlier research snapshot, not authorization for app integration.

Date: 2026-09-10. Status: research only; awaiting approval before coding, installation, model downloads, or benchmarking.

## Correct model

The intended task is speech-to-text. The appropriate family is **Qwen3-ASR**, particularly **Qwen/Qwen3-ASR-1.7B-hf** for the newer native Hugging Face Transformers integration. The originally linked Qwen3-TTS checkpoint generates speech and is unsuitable for this task. No voice cloning or speech synthesis is proposed.

Qwen's native model card reports these Open ASR Leaderboard results dated **June 26, 2026**:

| Model | Mean word error rate, lower is better |
| --- | ---: |
| Qwen3-ASR-1.7B-hf | 5.59% |
| Qwen3-ASR-0.6B-hf | 6.31% |

These are published benchmark results, not a verified current leaderboard rank or measurements on this computer. [Official model and evaluation](https://huggingface.co/Qwen/Qwen3-ASR-1.7B-hf#evaluation)

The live leaderboard's source computes rank from the selected datasets and supports versioned results. An exact ordinal rank needs a stated date/version, dataset selection, and inclusion/exclusion of proprietary models. The browser-readable Space page did not expose the rendered table during this research, and a read-only configuration request did not yield usable table data. Therefore this report does not claim Qwen is currently number one or assign a current rank. [Leaderboard](https://huggingface.co/spaces/hf-audio/open_asr_leaderboard), [ranking source](https://huggingface.co/spaces/hf-audio/open_asr_leaderboard/blob/main/app.py), [version definitions](https://huggingface.co/spaces/hf-audio/open_asr_leaderboard/blob/main/init.py)

Word error rate counts substitutions, deletions, and insertions relative to a reference transcript. It is useful evidence for choosing candidates, but does not predict the error rate of one person's microphone dictation. The important local comparison is accuracy on the same recordings together with delay after recording stops.

## Recommended app proposal

Assume a new local Windows transcription app, as requested earlier, with a dark interface. Proposed initial workflow:

1. Choose a microphone and press Record.
2. Speak, then press Stop.
3. See an editable transcript; copy it or save it as text.
4. Optionally open an existing WAV recording and transcribe it through the same worker.

Use a small .NET desktop shell and one persistent Python/CUDA worker for the model. Keep the model loaded between recordings if measured memory use permits. Model inference runs outside the UI process so recording, cancellation, and editing remain responsive. This architecture and the UI are recommendations, not approved implementation.

Start by testing native **1.7B-hf** for quality, with **0.6B-hf** as a comparison if response time or memory is limiting. Use the exact native checkpoint/runtime pairing documented in the [runtime research](qwen3-asr-runtime-research-2026-09-10.md). Do not mix the original toolkit's pinned Transformers environment with the newer native integration.

## Live text is a separate decision

The original Qwen toolkit exposes streaming on its vLLM backend, with revisable partial text. Its documentation does not establish that the native Transformers examples provide the same incremental microphone interface. vLLM's official platform route on Windows is WSL/Linux. [Qwen streaming documentation](https://github.com/QwenLM/Qwen3-ASR#streaming-inference), [vLLM requirements](https://docs.vllm.ai/en/latest/getting_started/installation/gpu/#requirements)

For the simplest first version, finish recording and then transcribe. If words must appear during speech, select and validate that runtime/workflow explicitly. Repeatedly transcribing an expanding recording is an approximation, and may increase delay as the recording grows. Do not mistake text-token streaming after submitting a complete recording for streaming microphone recognition.

Timestamps require additional alignment work; speaker identification is a different feature. Neither is necessary for straightforward dictation. Keep these outside the initial proposal unless requested.

## Hardware and validation before committing to the full UI

A read-only `nvidia-smi` query found **RTX 3060, 12,288 MiB VRAM**, with **9,246 MiB free** at that instant and driver **610.74**. This supports evaluating local GPU inference, but proves neither model fit nor responsiveness. No model was loaded.

After approval, the first stage should:

- Create an isolated environment and download a pinned checkpoint revision.
- Transcribe a small agreed set of recordings: short dictation, a paragraph, pauses/silence, background noise, names, numbers, and technical terms.
- Measure cold loading, warm delay after Stop, peak VRAM, omissions, repetitions, and incorrect words.
- Compare the same recordings against the current Parakeet engine where useful, keeping model accuracy separate from text insertion behavior.
- Check cancellation, long-recording handling, and local operation without network access after initial setup.

Use those results to choose the model and whether the complete-input workflow is sufficient. If the model is too slow, report the evidence before changing to another model or adding WSL. GPU-server throughput claims are not single-user RTX 3060 dictation measurements.

## Relationship to existing PTT Dictation

Current source inspection confirms that this repository already isolates recognition behind `ITranscriber.TranscribeAsync(wavPath, cancellationToken)` in `src/PttDictation.Core/Ports.cs`. Its README identifies Parakeet as the current backend and documents 16 kHz mono recording.

If the intended product is eventually the existing hotkey-to-textbox workflow with better recognition, adding a Qwen adapter is a credible alternative to rebuilding the entire app. It would also need model provisioning, warmup, cancellation, and UI configuration work; changing a model filename is insufficient. This is an option for approval, not authorization to alter PTT Dictation or its live installation.

## Approval boundary

Proposed next step: an isolated Qwen3-ASR feasibility comparison on this PC, followed by the new app only after the chosen workflow is clear. The remaining product choice is whether transcription after Stop is sufficient or live text during speech is required, and whether the destination is a new app or the existing PTT workflow.

Completed: primary-source research, hardware inventory, and a small read-only inspection of the existing transcription boundary. Written: these two research notes. Not performed: application edits, package installs, model downloads, inference tests, deployment, commit, or push. Earlier TTS research artifacts were removed after the user clarified STT.
