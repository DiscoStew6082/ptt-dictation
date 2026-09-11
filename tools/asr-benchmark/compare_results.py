"""Score only corpus-provided references; keep private dictations explicitly unscored."""

import argparse
import collections
import json
import re
import statistics
import unicodedata
from pathlib import Path


def words(text):
    text = unicodedata.normalize("NFKC", text).lower().replace("\u2019", "'")
    tokens = re.findall(r"[a-z0-9]+(?:'[a-z0-9]+)*", text)
    titles = {"mr": "mister", "mrs": "missus", "dr": "doctor", "ms": "miss"}
    return [titles.get(token, token) for token in tokens]


def alignment(reference, hypothesis):
    ref, hyp = words(reference), words(hypothesis)
    dp = [[(0, 0, 0, 0)] * (len(hyp) + 1) for _ in range(len(ref) + 1)]
    for i in range(1, len(ref) + 1):
        dp[i][0] = (i, 0, i, 0)
    for j in range(1, len(hyp) + 1):
        dp[0][j] = (j, 0, 0, j)
    for i in range(1, len(ref) + 1):
        for j in range(1, len(hyp) + 1):
            if ref[i - 1] == hyp[j - 1]:
                dp[i][j] = dp[i - 1][j - 1]
            else:
                e, s, d, n = dp[i - 1][j - 1]
                sub = (e + 1, s + 1, d, n)
                e, s, d, n = dp[i - 1][j]
                delete = (e + 1, s, d + 1, n)
                e, s, d, n = dp[i][j - 1]
                insert = (e + 1, s, d, n + 1)
                dp[i][j] = min((sub, delete, insert), key=lambda x: x[0])
    e, s, d, n = dp[-1][-1]
    return {"reference_words": len(ref), "errors": e, "substitutions": s,
            "deletions": d, "insertions": n, "wer": e / len(ref) if ref else None}


def summarize(corpus, source):
    report = json.loads(source.read_text(encoding="utf-8-sig"))
    groups = collections.defaultdict(list)
    for run in report["runs"]:
        if run["phase"] == "warm":
            groups[run["id"]].append(run)
    rows = []
    scores = []
    for item in corpus:
        runs = groups[item["id"]]
        good = [run for run in runs if run["status"] == "ok"]
        texts = [run["text"] for run in good]
        row = {"id": item["id"], "kind": item["kind"], "audio_seconds": item["duration_seconds"],
               "successful_warm_runs": len(good), "failed_warm_runs": len(runs) - len(good),
               "median_seconds": statistics.median(r["wall_seconds"] for r in good) if good else None,
               "min_seconds": min((r["wall_seconds"] for r in good), default=None),
               "max_seconds": max((r["wall_seconds"] for r in good), default=None),
               "transcripts_identical": len(set(texts)) == 1 if texts else None,
               "text": texts[0] if texts else None,
               "token_limit_reached": any(r.get("token_limit_reached", False) for r in good),
               "reference": item.get("reference")}
        if item["kind"] == "public_reference" and texts:
            row["score"] = alignment(item["reference"], texts[0])
            scores.append(row["score"])
        if item["kind"] == "silence" or item.get("near_silent"):
            row["nonempty_outputs"] = sum(bool(t.strip()) for t in texts)
        rows.append(row)
    public_rows = [r for r in rows if r["kind"] == "public_reference"]
    total_words = sum(score["reference_words"] for score in scores)
    total_errors = sum(score["errors"] for score in scores)
    return {
        "source": str(source), "engine": report.get("model_id", report["engine"]),
        "status": report["status"], "rows": rows,
        "public_reference_summary": {
            "scored_clips": len(scores), "expected_clips": len(public_rows),
            "reference_words": total_words, "errors": total_errors,
            "pooled_wer": total_errors / total_words if total_words else None,
            "sum_median_wall_seconds": sum(row["median_seconds"] or 0 for row in public_rows),
            "total_audio_seconds": sum(row["audio_seconds"] for row in public_rows),
        },
        "model_load_seconds": report.get("model_load_seconds"),
        "python_import_seconds": report.get("import_seconds"),
        "parakeet_process_readiness_seconds": report.get("cold_process_readiness_seconds"),
        "first_request_seconds": next((r["wall_seconds"] for r in report["runs"]
                                       if r["phase"] == "first_request" and r["status"] == "ok"), None),
        "peak_torch_allocated_bytes": max((r.get("peak_allocated_bytes", 0) for r in report["runs"]), default=0),
        "peak_torch_reserved_bytes": max((r.get("peak_reserved_bytes", 0) for r in report["runs"]), default=0),
    }


def self_test():
    assert alignment("a b c", "a x c")["substitutions"] == 1
    assert alignment("a b c", "a c")["deletions"] == 1
    assert alignment("a c", "a b c")["insertions"] == 1
    assert alignment("MISTER QUILTER'S", "Mr. Quilter's!")["errors"] == 0
    assert alignment("", "invented")["wer"] is None
    print("5 scorer checks passed")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--corpus", type=Path)
    parser.add_argument("--results", nargs="+", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    corpus = json.loads(args.corpus.read_text(encoding="utf-8-sig"))
    result = {
        "normalization": "Unicode NFKC, lowercase, punctuation removed except internal apostrophes; mr/mrs/dr/ms expanded consistently; no number/contraction/semantic rewriting",
        "limits": "Public references are a small familiar fixture from one speaker/passage; derived probes reuse that speech and are not independent accuracy examples; private recording has no independent reference; current Parakeet CPU vs Qwen GPU; no app/hotkey/insertion acceptance",
        "engines": [summarize(corpus, source) for source in args.results],
    }
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(json.dumps([{k: v for k, v in engine.items() if k != "rows"} for engine in result["engines"]], indent=2))


if __name__ == "__main__":
    main()
