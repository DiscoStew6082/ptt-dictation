# Qwen3-ASR runtime research

Follow-up: [September 11 benchmark results](qwen3-asr-benchmark-2026-09-11.md) now provide local runtime measurements. The uncertainty statements below describe the earlier research snapshot.

Research date: 2026-09-10. Scope: speech-to-text transcription for a new local app. This corrects the earlier TTS interpretation. No packages, models, inference tests, or app code were installed or run.

## Recommended starting point

After approval, evaluate **`Qwen/Qwen3-ASR-1.7B-hf` with native Transformers on Windows**, in an isolated Python/CUDA environment, one recording at a time. Keep **`Qwen/Qwen3-ASR-0.6B-hf`** as the candidate to compare if latency or memory becomes limiting. This is a proposed feasibility baseline, not a proven installation or performance result. The required task is transcription; voice cloning, speech synthesis, and forced alignment are unnecessary.

The native Transformers integration was announced June 26, 2026. The checkpoint card requires **Transformers >=5.13.0**. Its recommended flow uses `AutoProcessor.apply_transcription_request`, `AutoModelForMultimodalLM.generate`, and decoding with `return_format="transcription_only"`. The API supports a forced language and contextual vocabulary/hotwords. The `-hf` checkpoints should be paired with this native integration. [Official native model card](https://huggingface.co/Qwen/Qwen3-ASR-1.7B-hf).

Native source declares SDPA attention support. A conservative Windows test can use BF16 CUDA with SDPA before considering extra acceleration packages or compilation. This is a source-informed proposal; Windows inference has not been verified here. [Transformers model source](https://github.com/huggingface/transformers/blob/main/src/transformers/models/qwen3_asr/modeling_qwen3_asr.py#L41-L51).

## Two distinct software paths

| Runtime | Checkpoint family | What is established |
| --- | --- | --- |
| Native Transformers >=5.13.0 | `Qwen3-ASR-1.7B-hf` / `0.6B-hf` | Official complete-input transcription examples and standard Transformers integration. |
| Original `qwen-asr` package | `Qwen3-ASR-1.7B` / `0.6B` | Transformers and vLLM backends, long-audio handling, optional alignment, and explicit incremental-audio API on the vLLM backend. |

The native checkpoint card supplies the first path. The original toolkit documents the second. Treat these as separately pinned environments until interoperability is tested. [Native checkpoint](https://huggingface.co/Qwen/Qwen3-ASR-1.7B-hf), [original toolkit](https://github.com/QwenLM/Qwen3-ASR#python-package-usage).

The original package currently identifies itself as `qwen-asr` 0.0.6 and pins Transformers **4.57.6**, Accelerate **1.12.0**, and optional vLLM **0.14.0**. Installing it into the newer native environment would introduce conflicting Transformers requirements. Its version pins also differ from current vLLM documentation, so do not combine an old package recipe with arbitrary latest packages. [Official package metadata](https://github.com/QwenLM/Qwen3-ASR/blob/main/pyproject.toml).

## Streaming and live dictation

The native model card describes the model family as streaming-capable, but its demonstrated API accepts audio before calling `generate`. That does not establish a supported live-microphone incremental-state interface in native Transformers. This research did not verify such an interface; do not promise live partial dictation merely from the streaming checkbox. [Native usage](https://huggingface.co/Qwen/Qwen3-ASR-1.7B-hf#usage).

The original toolkit's actual streaming API has an explicit **vLLM-only** guard. Its default chunk duration is **2 seconds**; it accepts mono 16 kHz PCM, accumulates audio, and re-feeds all audio received so far. It can revise the previous output's trailing tokens; default rollback is five tokens after the initial two chunks. It does not support batch inference or timestamps in streaming mode. A finish call flushes the remaining audio. Consequently, live text must tolerate revisions, and longer recordings need measured throughput rather than an assumption of constant-cost streaming. [Streaming implementation](https://github.com/QwenLM/Qwen3-ASR/blob/main/qwen_asr/inference/qwen3_asr.py#L534-L757).

A practical first version could transcribe after recording ends. Live interim text is a separate product/runtime decision. Repeated native transcription of growing recordings would be an application-level approximation requiring its own latency and correctness work, not the documented native streaming API.

vLLM does **not** support Windows natively; its official installation guide points Windows users toward WSL with a compatible Linux distribution. Thus the original streaming route adds a Linux runtime to this Windows app. Native forks are outside this recommendation. [vLLM platform requirements](https://docs.vllm.ai/en/latest/getting_started/installation/gpu/#requirements).

## Hardware and approval-stage validation

The coordinating task's read-only inventory found an RTX 3060, **12,288 MiB total VRAM**, **9,246 MiB free** at that moment, driver 610.74. These are inventory observations, not proof that either model loads or meets dictation latency targets. No trustworthy RTX 3060 runtime measurements for these exact configurations were established during this bounded research.

The native model card's compilation result uses **A100 at batch size four** and cannot predict single-recording desktop latency. Its 1.7B versus 0.6B quality comparison is useful for choosing candidates, but the actual user's microphone, accent, names, coding terms, and recording lengths should decide the runtime choice. [Native speed and memory notes](https://huggingface.co/Qwen/Qwen3-ASR-1.7B-hf#speed--memory-improvements).

After approval, measure cold load, warm transcription delay after stop, peak allocated/reserved GPU memory, and transcription accuracy on the same recordings for both sizes. Include silence, background noise, numbers, punctuation, names, coding terms, short utterances, and multi-minute recordings. For live previews, separately measure first partial text, revision frequency, tail flush, and cancellation. Keep the worker resident only if its measured memory impact is acceptable alongside the user's normal GPU workload. No minimum-VRAM or real-time guarantee is justified before this test.
