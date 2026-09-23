"""Prepare identical whole/chunk/prefix WAVs and score completed offline runs.

Use ignored output directories. This helper never loads models, uploads audio,
or interacts with the app. Fixed chunks are a context-loss experiment, not a
stateful streaming implementation. Cumulative prefixes sample the growing-context
workload every two seconds; production offers prefixes more frequently.
"""

import argparse
import hashlib
import json
from pathlib import Path
import statistics
import wave

from compare_results import alignment


def ranges(frames, rate, mode):
    if frames <= 0 or rate <= 0:
        raise ValueError("Audio must contain samples at a positive rate")
    if mode == "whole":
        return [(0, frames)]
    if mode == "fixed6":
        size = 6 * rate
        return [(start, min(start + size, frames)) for start in range(0, frames, size)]
    if mode == "cumulative2":
        return [(0, end) for end in range(2 * rate, frames, 2 * rate)] + [(0, frames)]
    raise ValueError("Unknown mode")


def prepare(corpus_path, output):
    source = json.loads(corpus_path.read_text(encoding="utf-8-sig"))
    # Private recordings lack checked references; retain them locally, unscored.
    selected = [x for x in source if x["kind"] in ("public_reference", "user_dictation", "silence")]
    public = [x for x in selected if x["kind"] == "public_reference"]
    longest = max(public, key=lambda x: x["duration_seconds"])["id"] if public else None
    output.mkdir(parents=True, exist_ok=True)
    corpus, groups = [], []
    for item in selected:
        path = corpus_path.parent / item["file"]
        if hashlib.sha256(path.read_bytes()).hexdigest().lower() != item["sha256"].lower():
            raise ValueError(f"Source hash changed: {item['id']}")
        with wave.open(str(path), "rb") as audio:
            params = audio.getparams()
            if (params.nchannels, params.sampwidth, params.framerate, params.comptype) != (1, 2, 16000, "NONE"):
                raise ValueError("Expected 16kHz mono PCM16")
            pcm = audio.readframes(params.nframes)
        if len(pcm) != params.nframes * 2:
            raise ValueError("Truncated WAV")
        modes = ["whole", "fixed6"] + (["cumulative2"] if item["id"] == longest else [])
        for mode in modes:
            group = {"id": item["id"], "kind": item["kind"], "mode": mode,
                     "reference": item.get("reference"), "audio_seconds": params.nframes / 16000,
                     "source_sha256": item["sha256"], "parts": []}
            for index, (start, end) in enumerate(ranges(params.nframes, 16000, mode)):
                sample_id = f"{item['id']}--{mode}--{index:03d}"
                target = output / (sample_id + ".wav")
                with wave.open(str(target), "wb") as audio:
                    audio.setparams(params)
                    audio.writeframes(pcm[start * 2:end * 2])
                metadata = {"id": sample_id, "sha256": hashlib.sha256(target.read_bytes()).hexdigest(),
                            "duration_seconds": (end - start) / 16000}
                corpus.append({**metadata, "kind": item["kind"], "file": target.name})
                group["parts"].append({**metadata, "start_seconds": start / 16000,
                                       "end_seconds": end / 16000})
            groups.append(group)
    (output / "corpus.json").write_text(json.dumps(corpus, indent=2) + "\n", encoding="utf-8")
    (output / "groups.json").write_text(json.dumps(groups, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"prepared_inputs": len(corpus), "groups": len(groups)}))


def summarize(groups, report, expected_repeats):
    if report["status"] != "complete":
        raise ValueError("Cannot score an incomplete or failed benchmark")
    prepared = report.get("corpus", report.get("inputs"))
    if not isinstance(prepared, list) or not prepared:
        raise ValueError("Missing prepared-audio provenance in benchmark report")
    inputs = {}
    for item in prepared:
        if item["id"] in inputs:
            raise ValueError("Duplicate prepared-audio provenance in benchmark report")
        inputs[item["id"]] = item
    expected = {}
    for group in groups:
        for part in group["parts"]:
            match = inputs.get(part["id"])
            valid_hash = (isinstance(part.get("sha256"), str) and len(part["sha256"]) == 64
                          and all(c in "0123456789abcdef" for c in part["sha256"].lower()))
            if (not valid_hash or not isinstance(part.get("duration_seconds"), (int, float))
                    or not part["duration_seconds"] > 0 or match is None
                    or str(match.get("sha256", "")).lower() != part["sha256"].lower()
                    or match.get("duration_seconds") != part["duration_seconds"]):
                raise ValueError(f"Prepared-audio provenance mismatch: {part['id']}")
            expected[part["id"]] = part
    rows = {}
    for row in report["runs"]:
        if row["phase"] == "warm":
            key = (row["id"], row["repeat"])
            if key in rows:
                raise ValueError("Duplicate benchmark run")
            if "corpus" in report and row["id"] in expected:
                part = expected[row["id"]]
                if (str(row.get("audio_sha256", "")).lower() != part["sha256"].lower()
                        or row.get("audio_seconds") != part["duration_seconds"]):
                    raise ValueError(f"Run audio provenance mismatch: {row['id']}")
            rows[key] = row
    results = []
    for group in groups:
        assembled = []
        for repeat in range(1, expected_repeats + 1):
            parts = [rows.get((part["id"], repeat)) for part in group["parts"]]
            if any(p is None or p["status"] != "ok" or p.get("token_limit_reached") for p in parts):
                raise ValueError(f"Missing, failed, or capped run: {group['id']} {group['mode']} {repeat}")
            # No overlap: concatenation must retain genuinely repeated words.
            text = parts[-1]["text"] if group["mode"] == "cumulative2" else " ".join(p["text"] for p in parts)
            assembled.append({"text": text, "wall_seconds": sum(p["wall_seconds"] for p in parts),
                              "max_request_seconds": max(p["wall_seconds"] for p in parts)})
        row = {key: group[key] for key in ("id", "kind", "mode", "audio_seconds")}
        row.update(median_wall_seconds=statistics.median(r["wall_seconds"] for r in assembled),
                   max_request_seconds=max(r["max_request_seconds"] for r in assembled),
                   transcripts_stable=len({r["text"] for r in assembled}) == 1,
                   nonempty_outputs=sum(bool(r["text"].strip()) for r in assembled))
        if group["kind"] == "public_reference":
            row["scores"] = [alignment(group["reference"], r["text"]) for r in assembled]
        if group["mode"] == "cumulative2":
            row["prefix_latency"] = [{"audio_seconds": part["end_seconds"],
                                      "median_seconds": statistics.median(rows[(part["id"], r)]["wall_seconds"]
                                                                          for r in range(1, expected_repeats + 1))}
                                     for part in group["parts"]]
        results.append(row)
    pooled = []
    for mode in ("whole", "fixed6"):
        public = [r for r in results if r["mode"] == mode and r["kind"] == "public_reference"]
        words = sum(r["scores"][0]["reference_words"] for r in public)
        scores = [{key: sum(r["scores"][repeat][key] for r in public)
                   for key in ("errors", "substitutions", "deletions", "insertions")}
                  for repeat in range(expected_repeats)]
        pooled.append({"mode": mode, "clips": len(public), "reference_words": words, "scores": scores,
                       "wer_each_repeat": [s["errors"] / words for s in scores] if words else [],
                       "sum_median_wall_seconds": sum(r["median_wall_seconds"] for r in public),
                       "all_transcripts_stable": all(r["transcripts_stable"] for r in public)})
    return {"engine": report.get("model_id", report["engine"]), "expected_repeats": expected_repeats,
            "public_summary": pooled, "rows": results,
            "peak_torch_allocated_bytes": max((r.get("peak_allocated_bytes", 0) for r in rows.values()), default=0)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    prep.add_argument("--corpus", type=Path, required=True)
    prep.add_argument("--output", type=Path, required=True)
    score = sub.add_parser("score")
    score.add_argument("--groups", type=Path, required=True)
    score.add_argument("--results", nargs="+", type=Path, required=True)
    score.add_argument("--repeats", type=int, default=2)
    score.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args.corpus, args.output)
    else:
        if args.repeats < 1:
            parser.error("--repeats must be positive")
        groups = json.loads(args.groups.read_text(encoding="utf-8"))
        results = [summarize(groups, json.loads(p.read_text(encoding="utf-8")), args.repeats) for p in args.results]
        args.output.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
        print(json.dumps([{k: v for k, v in r.items() if k != "rows"} for r in results], indent=2))


if __name__ == "__main__":
    main()
