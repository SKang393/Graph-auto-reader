# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused P3 provenance, recipe-diff, and GPU-binding tests."""

from __future__ import annotations

import hashlib
from pathlib import Path
import unittest

import train_classifier_p3 as p3
import correct_report_p3 as correction


class P3Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.p3_hash = hashlib.sha256(Path(__file__).with_name("train_classifier_p3.py").read_bytes()).hexdigest()
        p3._RUNNER_SHA = cls.p3_hash
        cls.p2, cls.sources = p3.load_pinned_stack()
        cls.p2_config, _ = cls.p2.validate_config()

    def test_source_loaded_from_pinned_bytes_without_bytecode_dependency(self):
        self.assertEqual(self.sources["p2_runner_sha256"], p3.P2_RUNNER_SHA)
        self.assertEqual(self.sources["p2_config_sha256"], p3.P2_CONFIG_SHA)
        self.assertEqual(self.sources["helper_source_sha256"], "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5")
        self.assertEqual(self.sources["p1_trainer_source_sha256"], "3e8617809584dadb9c6b35cb52b95136fe766212a2f0bb65f4863b0d17ebc594")
        self.assertEqual(self.sources["train_admission_source_sha256"], "72947560a68324ca2a6b470898755334e2f6ef98dd7491552e3618a615b30ede")
        for module in (self.p2, self.p2.p1, self.p2.admission, self.p2.helpers):
            self.assertTrue(Path(module.__file__).is_file())

    def test_recipe_changes_only_duration_identity_and_output(self):
        config, _ = p3.load_and_validate_config(self.p2_config, self.sources)
        changed = {key for key in self.p2_config if config[key] != self.p2_config[key]}
        self.assertEqual(changed, {"recipe_id", "epochs", "output_relative", "expected_runner_sha256"})
        self.assertEqual(config["epochs"], 60)
        self.assertEqual(config["steps_per_epoch"] * config["epochs"], 1380)
        self.assertEqual(config["model_seed"], 27104152)
        self.assertEqual(config["sampler_seed"], 27104153)
        self.assertEqual(config["training_data"], {"positive": 81, "negative": 365, "unique_total": 446})
        self.assertEqual(config["loss"], "binary_cross_entropy_with_logits")
        self.assertEqual(config["optimizer"], "AdamW")

    def test_wrong_epoch_or_dependency_is_rejected(self):
        config, _ = p3.load_and_validate_config(self.p2_config, self.sources)
        config["epochs"] = 61
        with self.assertRaises(p3.P3Error):
            p3._validate_recipe_config(config, self.p2_config)

    def test_rx_gpu_binding_is_required(self):
        import torch
        runtime = self.p2.helpers.require_rx_6600_xt(torch)
        self.assertEqual(runtime.get("device"), "cuda:0")
        self.assertFalse(runtime.get("cpu_fallback", True))

    def test_report_correction_only_renames_active_p3_fields(self):
        import json
        report_path = (Path(__file__).parents[5] / "artifacts" /
            "stroke-seeded-xtick-v1-ink-stroke-classifier-p3-sourceuniform-60ep-rx6600xt" /
            "report.json")
        original_bytes = report_path.read_bytes()
        self.assertEqual(hashlib.sha256(original_bytes).hexdigest(), correction.ORIGINAL_SHA256)
        report = json.loads(original_bytes.decode("utf-8"))
        self.assertNotIn("report_correction", report)
        fixed = correction.correct_report(report)
        before = report["train_only_metrics"]["p1_train_only_comparison"]
        after = fixed["train_only_metrics"]["p1_train_only_comparison"]
        self.assertIn("p2_train_metrics", before)
        self.assertNotIn("p2_train_metrics", after)
        self.assertEqual(after["p3_train_metrics"], report["train_only_comparison"]["P3"])
        self.assertAlmostEqual(after["metric_deltas_p3_minus_p1"]["recall"],
            report["train_only_comparison"]["P3"]["recall"] - report["train_only_comparison"]["P1"]["recall"])
        self.assertIn("no holdout images", after["p1_report_scope_note"])
        self.assertFalse(fixed["dependencies"]["all_local_source_dependencies_executed_from_exact_bytes"])
        unresolved = fixed["dependencies"]["unbound_transitive_source_dependency"]
        self.assertEqual(unresolved["path"], "ml/ocr/fragmented_xtick_detector_v2/materialize.py")
        self.assertFalse(unresolved["executed_from_exact_bytes_proven"])


if __name__ == "__main__":
    unittest.main()
