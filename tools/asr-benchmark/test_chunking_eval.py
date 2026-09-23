import unittest

from chunking_eval import ranges, summarize


class ChunkingEvaluationTests(unittest.TestCase):
    def test_chunk_partition_keeps_final_sample_and_every_sample_once(self):
        spans = ranges(192449, 16000, "fixed6")
        self.assertEqual(spans, [(0, 96000), (96000, 192000), (192000, 192449)])
        self.assertEqual(sum(end - start for start, end in spans), 192449)

    def test_cumulative_prefixes_keep_short_tail_and_no_duplicate_full_prefix(self):
        self.assertEqual(ranges(64000, 16000, "cumulative2"), [(0, 32000), (0, 64000)])
        self.assertEqual(ranges(64001, 16000, "cumulative2"), [(0, 32000), (0, 64000), (0, 64001)])

    def fixture(self):
        metadata = [{"id": part, "sha256": part * 64, "duration_seconds": 6} for part in ("a", "b")]
        groups = [{"id": "fixture", "kind": "public_reference", "mode": "fixed6",
                   "audio_seconds": 12, "reference": "go go", "parts": [dict(part) for part in metadata]}]
        runs = [{"id": part, "repeat": 1, "phase": "warm", "status": "ok", "text": "go",
                 "wall_seconds": 0.5} for part in ("a", "b")]
        return groups, {"status": "complete", "engine": "test", "runs": runs, "inputs": metadata}

    def test_same_id_from_different_audio_cannot_be_scored(self):
        for schema in ("inputs", "corpus"):
            for field, stale in (("sha256", "f" * 64), ("duration_seconds", 6.001)):
                with self.subTest(schema=schema, field=field):
                    groups, report = self.fixture()
                    metadata = report.pop("inputs")
                    metadata[0][field] = stale
                    report[schema] = metadata
                    with self.assertRaisesRegex(ValueError, "provenance"):
                        summarize(groups, report, 1)

    def test_missing_or_duplicate_prepared_metadata_is_rejected(self):
        for fault in ("missing", "duplicate"):
            with self.subTest(fault=fault):
                groups, report = self.fixture()
                if fault == "missing": report.pop("inputs")
                if fault == "duplicate": report["inputs"].append(report["inputs"][0])
                with self.assertRaises(ValueError): summarize(groups, report, 1)

    def test_qwen_per_run_metadata_must_match_prepared_audio(self):
        for field, stale in (("audio_sha256", "f" * 64), ("audio_seconds", 7)):
            with self.subTest(field=field):
                groups, report = self.fixture()
                report["corpus"] = report.pop("inputs")
                for run, part in zip(report["runs"], report["corpus"]):
                    run.update(audio_sha256=part["sha256"], audio_seconds=part["duration_seconds"])
                self.assertEqual(summarize(groups, report, 1)["rows"][0]["scores"][0]["errors"], 0)
                report["runs"][0][field] = stale
                with self.assertRaisesRegex(ValueError, "provenance"):
                    summarize(groups, report, 1)

    def test_disjoint_chunks_do_not_remove_intentional_repeated_words(self):
        groups, report = self.fixture()
        result = summarize(groups, report, 1)
        self.assertEqual(result["rows"][0]["scores"][0]["errors"], 0)
        self.assertEqual(result["rows"][0]["median_wall_seconds"], 1)

    def test_missing_failed_capped_and_duplicate_runs_never_score_as_success(self):
        for fault in ("missing", "failed", "capped", "duplicate"):
            with self.subTest(fault=fault):
                groups, report = self.fixture()
                if fault == "missing": report["runs"].pop()
                if fault == "failed": report["runs"][0]["status"] = "error"
                if fault == "capped": report["runs"][0]["token_limit_reached"] = True
                if fault == "duplicate": report["runs"].append(report["runs"][0])
                with self.assertRaises(ValueError): summarize(groups, report, 1)


if __name__ == "__main__":
    unittest.main()
