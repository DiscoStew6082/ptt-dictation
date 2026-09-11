"""Inventory preserved WAVs and create explicitly labelled robustness probes."""

import argparse
import hashlib
import json
import math
import struct
import wave
import io
from pathlib import Path


def inspect_wav(path):
    with wave.open(str(path), "rb") as audio:
        channels, width, rate, frames, compression, _ = audio.getparams()
        pcm = audio.readframes(frames)
    if (channels, width, rate, compression) != (1, 2, 16000, "NONE"):
        raise ValueError(f"Expected 16 kHz mono PCM16: {path}")
    samples = struct.unpack(f"<{len(pcm) // 2}h", pcm)
    rms = math.sqrt(sum(x * x for x in samples) / max(1, len(samples)))
    return {
        "id": path.stem, "file": path.name,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "duration_seconds": frames / rate, "sample_rate": rate,
        "channels": channels, "sample_width": width,
        "peak_pcm16": max(map(abs, samples), default=0), "rms_pcm16": rms,
    }, pcm


def write_wav(path, pcm):
    with wave.open(str(path), "wb") as audio:
        audio.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
        audio.writeframes(pcm)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("inputs", type=Path)
    parser.add_argument("--public-parquet", type=Path)
    parser.add_argument("--public-count", type=int, default=8)
    args = parser.parse_args()
    preserved = json.loads((args.inputs / "manifest.json").read_text(encoding="utf-8-sig"))
    corpus, chunks = [], []
    for original in preserved:
        item, pcm = inspect_wav(args.inputs / original["file"])
        if item["sha256"].lower() != original["sha256"].lower():
            raise ValueError(f"Preserved hash mismatch: {item['id']}")
        item.update(kind="user_dictation", reference=None,
                    reference_status="No independently checked transcript; do not report WER")
        item["near_silent"] = item["peak_pcm16"] <= 1
        corpus.append(item)
        chunks.append(pcm)
    if args.public_parquet:
        import pyarrow.parquet as pq
        import soundfile as sf
        public_dir = args.inputs / "public"
        public_dir.mkdir(exist_ok=True)
        for index, row in enumerate(pq.read_table(args.public_parquet).to_pylist()[:args.public_count]):
            pcm, rate = sf.read(io.BytesIO(row["audio"]["bytes"]), dtype="int16")
            if rate != 16000 or pcm.ndim != 1:
                raise ValueError("Unexpected public reference format")
            path = public_dir / f"librispeech-{index + 1:02d}.wav"
            write_wav(path, pcm.astype("<i2").tobytes())
            item, _ = inspect_wav(path)
            item.update(file="public/" + path.name, kind="public_reference",
                        reference=row["text"], reference_status="Dataset-provided reference; no normalization beyond scoring rules",
                        source_dataset="hf-internal-testing/librispeech_asr_dummy",
                        source_config="clean", source_split="validation", source_row=index,
                        source_id=row["id"], source_speaker_id=row.get("speaker_id"),
                        source_parquet_sha256=hashlib.sha256(args.public_parquet.read_bytes()).hexdigest())
            corpus.append(item)
    probes = args.inputs / "probes"
    probes.mkdir(exist_ok=True)
    write_wav(probes / "silence-3s.wav", bytes(16000 * 2 * 3))
    # Repeated existing speech exercises long input and pause/tail handling only.
    # It is not a new independent natural recording or an accuracy reference.
    long_pcm = (bytes(32000) + bytes(32000).join(chunks) + bytes(32000)) * 3
    write_wav(probes / "concatenated-stress.wav", long_pcm)
    for path in sorted(probes.glob("*.wav")):
        item, _ = inspect_wav(path)
        item["file"] = "probes/" + path.name
        item["kind"] = "silence" if path.stem.startswith("silence") else "derived_stress"
        item["reference"] = "" if item["kind"] == "silence" else None
        item["reference_status"] = "Known digital silence" if item["kind"] == "silence" else "Three repetitions of preserved clips separated by silence; unscored"
        corpus.append(item)
    (args.inputs / "corpus.json").write_text(json.dumps(corpus, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(corpus, indent=2))


if __name__ == "__main__":
    main()
