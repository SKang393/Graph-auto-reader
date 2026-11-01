# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Standard-library-only focused tests for the no-training diagnostic."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import numpy as np

from ml.markers.center.confidence_pairwise_false_positive_diagnostic_v1 import diagnostic


class DiagnosticTests(unittest.TestCase):
    def test_fixed_threshold_and_top_negative_order_are_deterministic(self) -> None:
        labels = np.asarray([1, 0, 0, 1, 0], dtype=np.float32)
        scores = np.asarray([0.1, 0.1, 0.9, 0.09, 0.7], dtype=np.float32)
        metrics = diagnostic._metrics(labels, scores)
        self.assertEqual(metrics, {
            "threshold": 0.1,
            "true_positives": 1,
            "false_positives": 3,
            "false_negatives": 1,
            "true_negatives": 0,
            "precision": 0.25,
            "recall": 0.5,
            "positive_rows": 2,
            "negative_rows": 3,
        })
        top = diagnostic._top_negative_rows("component:identity", labels, scores, scores, 2)
        self.assertEqual([row["row_index_within_scope"] for row in top], [2, 4])
        self.assertTrue(all(row["in_sample"] is True and row["label"] == 0 for row in top))

    def test_metrics_reject_malformed_data(self) -> None:
        invalid = (
            (np.asarray([[0.0]], dtype=np.float32), np.asarray([0.1], dtype=np.float32)),
            (np.asarray([2.0], dtype=np.float32), np.asarray([0.1], dtype=np.float32)),
            (np.asarray([0.0], dtype=np.float32), np.asarray([np.nan], dtype=np.float32)),
            (np.asarray([0.0, 1.0], dtype=np.float32), np.asarray([0.1], dtype=np.float32)),
        )
        for labels, scores in invalid:
            with self.subTest(labels=labels.shape, scores=scores.shape):
                with self.assertRaises(ValueError):
                    diagnostic._metrics(labels, scores)

    def test_numpy_input_is_hash_checked_before_use(self) -> None:
        with tempfile.TemporaryDirectory(dir=diagnostic.PROJECT_ROOT / "artifacts" / "goal22-runs") as temp_name:
            root = Path(temp_name)
            array_path = root / "train.npy"
            np.save(array_path, np.asarray([[1.0, 2.0]], dtype=np.float32), allow_pickle=False)
            expected = hashlib.sha256(array_path.read_bytes()).hexdigest()
            loaded = diagnostic._load_pinned_npy(
                root,
                root,
                {"path": "train.npy", "sha256": expected},
                shape=(1, 2),
                dtype=np.dtype(np.float32),
            )
            np.testing.assert_array_equal(loaded, np.asarray([[1.0, 2.0]], dtype=np.float32))
            with self.assertRaisesRegex(ValueError, "Array hash mismatch"):
                diagnostic._load_pinned_npy(
                    root,
                    root,
                    {"path": "train.npy", "sha256": "0" * 64},
                    shape=(1, 2),
                    dtype=np.dtype(np.float32),
                )

    def test_protocol_is_fixed_train_only_and_does_not_pin_dev_payloads(self) -> None:
        protocol = json.loads(diagnostic.PROTOCOL_PATH.read_text(encoding="utf-8"))
        self.assertEqual(protocol["fixed_threshold"], 0.1)
        self.assertFalse(protocol["data_policy"]["threshold_search"])
        self.assertEqual(protocol["data_policy"]["development_payloads_read"], 0)
        self.assertEqual(protocol["data_policy"]["private_reads"], 0)
        self.assertEqual(protocol["data_policy"]["sealed_reads"], 0)
        self.assertFalse(protocol["train_dev_identity"]["per_scene_overlap_verified"])
        self.assertTrue(all("dev" not in key.lower() for key in protocol["feature_report"]))
        self.assertEqual(protocol["feature_report"]["selected_groups"][-1]["scope"], "crowded_mined_negative:identity")

    def test_actual_pinned_train_preflight_when_artifacts_are_present(self) -> None:
        protocol = json.loads(diagnostic.PROTOCOL_PATH.read_text(encoding="utf-8"))
        required = [
            protocol["feature_report"]["path"],
            protocol["candidate"]["training_report_path"],
            protocol["candidate"]["checkpoint_path"],
            protocol["candidate"]["config_path"],
            protocol["candidate"]["protocol_path"],
            *(item["path"] for item in protocol["scoring_sources"]),
        ]
        absent = [path for path in required if not (diagnostic.PROJECT_ROOT / path).exists()]
        if absent:
            self.skipTest("Exact authenticated train artifacts are absent in this checkout")
        feature_report, training_report, v39_protocol, selected = diagnostic.validate_inputs(diagnostic.PROJECT_ROOT, protocol)
        self.assertEqual(training_report["training"]["optimizer_steps"], protocol["candidate"]["expected_training_steps"])
        self.assertFalse(v39_protocol["train_dev_separation"]["per_scene_overlap_verified"])
        self.assertEqual(set(selected), {item["scope"] for item in protocol["feature_report"]["selected_groups"]})
        self.assertEqual(sum(item["rows"] for item in selected.values()), protocol["feature_report"]["identity_rows_only"])
        self.assertEqual(feature_report["private_reads"], 0)
        self.assertEqual(feature_report["sealed_reads"], 0)

    def test_source_has_no_optimizer_operations(self) -> None:
        source = diagnostic.PROTOCOL_PATH.with_name("diagnostic.py").read_text(encoding="utf-8")
        for forbidden in ("torch.optim", ".backward(", ".step(", "acquire_training_candidate"):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, source)


if __name__ == "__main__":
    unittest.main(verbosity=2)
