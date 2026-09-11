"""Isolated complete-recording Qwen benchmark; never starts or edits the PTT app."""

import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import sys
import time
import traceback


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    pending = path.with_suffix(".pending.json")
    pending.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    pending.replace(path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--model-id", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--corpus", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--probe-repeats", type=int, default=1)
    parser.add_argument("--max-new-tokens", type=int, default=512)
    parser.add_argument("--ids", nargs="*")
    args = parser.parse_args()
    # Loading must never fetch models or upload audio during measured inference.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"
    report = {
        "schema": 1, "engine": "qwen_native_transformers",
        "model_id": args.model_id, "revision": args.revision,
        "model_path": str(args.model.resolve()),
        "settings": {"device": "cuda:0", "dtype": "bfloat16", "attention": "sdpa",
                     "do_sample": False, "language": "English", "prompt": None,
                     "max_new_tokens": args.max_new_tokens, "batch_size": 1,
                     "torch_cpu_threads": 4, "compile": False, "offline": True},
        "python": sys.version, "platform": platform.platform(),
        "started_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "runs": [], "status": "starting",
    }
    save(args.output, report)
    model = processor = None
    try:
        before_import = time.perf_counter()
        import torch
        import psutil
        from transformers import AutoModelForMultimodalLM, AutoProcessor
        report["import_seconds"] = time.perf_counter() - before_import
        report["packages"] = {name: importlib.metadata.version(name) for name in
                              ("torch", "transformers", "accelerate", "soundfile", "librosa", "numpy", "huggingface-hub")}
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA unavailable; no silent CPU fallback allowed in this comparison")
        torch.set_num_threads(4)
        torch.manual_seed(0)
        report["gpu"] = torch.cuda.get_device_name(0)
        report["gpu_total_bytes"] = torch.cuda.get_device_properties(0).total_memory
        report["cuda_version"] = torch.version.cuda
        items = json.loads(args.corpus.read_text(encoding="utf-8-sig"))
        if args.ids:
            items = [item for item in items if item["id"] in args.ids]
        if not items:
            raise ValueError("No corpus samples selected")
        for item in items:
            path = args.corpus.parent / item["file"]
            if hashlib.sha256(path.read_bytes()).hexdigest().lower() != item["sha256"].lower():
                raise ValueError(f"Audio hash changed: {item['id']}")
        report["corpus"] = items
        before_load = time.perf_counter()
        processor = AutoProcessor.from_pretrained(args.model, local_files_only=True)
        model = AutoModelForMultimodalLM.from_pretrained(
            args.model, local_files_only=True, dtype=torch.bfloat16,
            device_map={"": "cuda:0"}, attn_implementation="sdpa",
        ).eval()
        torch.cuda.synchronize()
        report["model_load_seconds"] = time.perf_counter() - before_load
        report["model_allocated_bytes"] = torch.cuda.memory_allocated()
        report["status"] = "running"
        save(args.output, report)
        print(json.dumps({"stage": "loaded", "model": args.model_id,
                          "seconds": report["model_load_seconds"],
                          "allocated_bytes": report["model_allocated_bytes"]}), flush=True)
        process = psutil.Process()

        def run(item, phase, repeat):
            record = {"id": item["id"], "kind": item["kind"], "phase": phase, "repeat": repeat,
                      "audio_seconds": item["duration_seconds"], "audio_sha256": item["sha256"]}
            inputs = output_ids = generated_ids = None
            try:
                torch.cuda.synchronize()
                torch.cuda.reset_peak_memory_stats()
                start = time.perf_counter()
                inputs = processor.apply_transcription_request(
                    audio=str(args.corpus.parent / item["file"]), language="English",
                ).to(model.device, model.dtype)
                torch.cuda.synchronize()
                preprocessed = time.perf_counter()
                with torch.inference_mode():
                    output_ids = model.generate(**inputs, max_new_tokens=args.max_new_tokens,
                                                do_sample=False)
                torch.cuda.synchronize()
                generated = time.perf_counter()
                generated_ids = output_ids[:, inputs["input_ids"].shape[1]:]
                text = processor.decode(generated_ids, return_format="transcription_only")[0]
                finished = time.perf_counter()
                token_count = generated_ids.shape[-1]
                record.update(status="ok", text=text,
                              wall_seconds=finished - start,
                              preprocess_seconds=preprocessed - start,
                              generate_seconds=generated - preprocessed,
                              decode_seconds=finished - generated,
                              generated_tokens=token_count,
                              token_limit_reached=token_count >= args.max_new_tokens,
                              peak_allocated_bytes=torch.cuda.max_memory_allocated(),
                              peak_reserved_bytes=torch.cuda.max_memory_reserved(),
                              process_rss_bytes=process.memory_info().rss)
            except Exception as error:
                record.update(status="error", error=repr(error), traceback=traceback.format_exc())
            finally:
                del inputs, output_ids, generated_ids
                report["runs"].append(record)
                save(args.output, report)
                print(json.dumps({key: record[key] for key in
                                  ("id", "phase", "repeat", "status", "wall_seconds", "text", "error")
                                  if key in record}, ensure_ascii=False), flush=True)
            return record

        cold = run(items[0], "first_request", 0)
        if cold["status"] != "ok":
            raise RuntimeError("First inference failed; inspect the recorded error before continuing")
        for repeat in range(1, args.repeats + 1):
            for item in items:
                if item["kind"] in ("derived_stress", "silence") and repeat > args.probe_repeats:
                    continue
                run(item, "warm", repeat)
        report["status"] = "complete" if all(r["status"] == "ok" for r in report["runs"]) else "complete_with_errors"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = repr(error)
        report["traceback"] = traceback.format_exc()
        print(report["traceback"], file=sys.stderr, flush=True)
    finally:
        report["finished_utc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        save(args.output, report)
        del model, processor
        if "torch" in locals() and torch.cuda.is_available():
            torch.cuda.empty_cache()
    return 0 if report["status"] == "complete" else 1


if __name__ == "__main__":
    raise SystemExit(main())
