# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Checks aggregate rejection attribution against the pinned adapter run."""

from __future__ import annotations

import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[5]
REPORT = ROOT / "artifacts/stroke-seeded-xtick-v1-p2-v5-component-rejection-v1/report.json"


class ComponentRejectionReportTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.report = json.loads(REPORT.read_text(encoding="utf-8"))

    def test_exact_p2_v5_probe_binding_and_scope(self) -> None:
        self.assertEqual(self.report["detector_checkpoint_sha256"],
                         "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351")
        self.assertEqual(self.report["recognizer_onnx_sha256"],
                         "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84")
        self.assertEqual(self.report["probe_manifest_sha256"],
                         "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba")
        self.assertEqual(self.report["score_threshold"], 0.25)
        self.assertFalse(self.report["training_performed"])
        self.assertFalse(self.report["dev_sealed_private_inputs_read"])
        self.assertFalse(self.report["production_approval"])

    def test_rejection_categories_partition_empty_and_returned_text(self) -> None:
        categories = self.report["rejection_categories"]
        self.assertEqual(sum(categories.values()), 165)
        self.assertEqual(categories["component_geometry_or_glyph_count_reject"], 82)
        self.assertEqual(categories["model_reject_class"], 12)
        self.assertEqual(categories["confidence_below_threshold"], 11)
        self.assertEqual(categories["numeric_grammar_reject"], 7)
        self.assertEqual(categories["recognized_exact"], 23)
        self.assertEqual(categories["recognized_wrong"], 30)
        empty_count = sum(categories[key] for key in (
            "component_geometry_or_glyph_count_reject", "model_reject_class",
            "confidence_below_threshold", "numeric_grammar_reject"))
        self.assertEqual(empty_count, 112)
        self.assertEqual(self.report["explicit_failure_codes"], {})

    def test_actual_adapter_logit_decision_parity(self) -> None:
        parity = self.report["adapter_logit_preprocessing_parity"]
        self.assertEqual(parity["sample_count"], 165)
        self.assertEqual(parity["mismatches"], 0)
        self.assertEqual(parity["captured_provider"], "CPUExecutionProvider")
        self.assertEqual(parity["provider_calls"], 1)
        self.assertGreater(parity["captured_output_values"], 0)


if __name__ == "__main__":
    unittest.main()
