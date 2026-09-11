"""Build labelled omission/silence followups after the smaller model dropped a repeat."""

import argparse
import json
from pathlib import Path

from prepare_corpus import inspect_wav, write_wav


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("inputs", type=Path)
    args = parser.parse_args()
    corpus = json.loads((args.inputs / "corpus.json").read_text(encoding="utf-8"))
    lookup = {item["id"]: item for item in corpus}
    followup = [lookup[key] for key in ("dictation-03", "concatenated-stress", "silence-3s")]
    pieces = [lookup[key] for key in ("librispeech-01", "librispeech-03", "librispeech-08")]
    chunks = []
    for item in pieces:
        actual, pcm = inspect_wav(args.inputs / item["file"])
        if actual["sha256"].lower() != item["sha256"].lower():
            raise ValueError(f"Source audio changed: {item['id']}")
        chunks.append(pcm)
    reference = " ".join(item["reference"] for item in pieces)
    for gap in (1, 10):
        pcm = bytes(32000) + bytes(32000 * gap).join(chunks) + bytes(32000)
        path = args.inputs / "probes" / f"public-mixed-gap{gap}s.wav"
        write_wav(path, pcm)
        item, _ = inspect_wav(path)
        item.update(file="probes/" + path.name, kind="public_reference",
                    reference=reference, reference_status="Concatenated public references, artificial pause stress; not independent new speech",
                    derived_from=[piece["id"] for piece in pieces], gap_seconds=gap)
        followup.append(item)
    (args.inputs / "followup-corpus.json").write_text(json.dumps(followup, indent=2) + "\n", encoding="utf-8")
    print(json.dumps([{k: item[k] for k in ("id", "duration_seconds", "kind")} for item in followup], indent=2))


if __name__ == "__main__":
    main()
