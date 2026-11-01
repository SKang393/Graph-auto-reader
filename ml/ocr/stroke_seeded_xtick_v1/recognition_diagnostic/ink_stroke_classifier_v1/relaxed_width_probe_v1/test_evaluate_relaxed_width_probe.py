# SPDX-License-Identifier: Apache-2.0
from __future__ import annotations

import importlib.util
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import numpy as np
from PIL import Image
import torch

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1.relaxed_width_probe_v1 import evaluate_relaxed_width_probe as subject


class RelaxedWidthTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        scanner_path = subject.ROOT / subject.DIAG_REL
        spec = importlib.util.spec_from_file_location("test_relaxed_width_scanner", scanner_path)
        cls.scanner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.scanner)

    def test_relaxed_width_recovers_wide_ink_cluster_without_truth(self):
        frame = np.full((36, 48), 255, dtype=np.uint8)
        frame[18:24, 10:21] = 20
        axis = [2.0, 15.0, 46.0, 15.0]
        baseline, baseline_trace = self.scanner.scan(frame, axis)
        relaxed, relaxed_trace = self.scanner.scan(frame, axis, max_cluster_width=subject.WIDE_CLUSTER_LIMIT)
        self.assertEqual([], baseline)
        self.assertEqual("cluster_width_over_limit", baseline_trace["clusters"][0]["rejection"])
        self.assertEqual(1, len(relaxed))
        self.assertIsNone(relaxed_trace["clusters"][0]["rejection"])

    def test_baseline_parity_rejects_geometry_drift(self):
        p = {"center": [2.0, 3.0], "line": [2.0, 1.0, 2.0, 5.0],
             "dark_pixel_count": 6, "cluster_width_pixels": 1, "longest_dark_run": 4}
        subject.assert_baseline_parity([p], [dict(p)])
        bad = dict(p, center=[2.1, 3.0])
        with self.assertRaises(subject.RelaxedWidthError):
            subject.assert_baseline_parity([p], [bad])

    def test_quarantine_uses_union_of_p3_p5_and_holdout_hashes_and_keeps_duplicates(self):
        rows = [{"proposal_id": "a", "crop_sha256": "train"},
                {"proposal_id": "b", "crop_sha256": "repeat"},
                {"proposal_id": "c", "crop_sha256": "repeat"},
                {"proposal_id": "d", "crop_sha256": "holdout"}]
        keep, excluded = subject.quarantine(rows, {"train"}, {"train"}, {"holdout"})
        self.assertEqual(["b", "c"], [r["proposal_id"] for r in keep])
        self.assertEqual(["a", "d"], [r["proposal_id"] for r in excluded])
        self.assertIn("exact_P3_training_crop_hash_overlap", excluded[0]["reasons"])
        self.assertIn("exact_P5_training_crop_hash_overlap", excluded[0]["reasons"])

    def test_runtime_guard_rejects_cpu_fallback_and_wrong_gpu(self):
        good = {"device": "cuda:0", "device_name": "AMD Radeon RX 6600 XT", "cpu_fallback": False}
        subject.validate_runtime(good)
        with self.assertRaises(subject.RelaxedWidthError):
            subject.validate_runtime({**good, "cpu_fallback": True})
        with self.assertRaises(subject.RelaxedWidthError):
            subject.validate_runtime({**good, "device_name": "other GPU"})

    def test_globalized_geometry_preserves_segment_length_and_shifts_frame(self):
        local = {"center": [1.5, 2.0], "line": [1.0, 0.0, 2.0, 4.0], "cluster_width_pixels": 2}
        global_prop = subject.globalize(local, 10, 20)
        self.assertEqual([11.5, 22.0], global_prop["center"])
        self.assertEqual([11.0, 20.0, 12.0, 24.0], global_prop["line"])
        self.assertEqual(local["cluster_width_pixels"], global_prop["cluster_width_pixels"])

    def test_bounded_fixture_builds_hash_bound_wide_row_without_truth_labels(self):
        image = Image.new("L", (48, 36), 255)
        for y in range(18, 24):
            for x in range(10, 21):
                image.putpixel((x, y), 20)
        source_id = "fixture-source"
        panel_id = "fixture-panel"
        source_hash = subject.digest(image.tobytes())
        panel = {"panel_id": panel_id, "bounds": [0, 0, 48, 36],
                 "selected_x_axis_line": [2.0, 15.0, 46.0, 15.0],
                 "ink_proposals": [], "needs_review": False}
        scanner_result = {"source_id": source_id, "expected_source_pixel_sha256": source_hash,
                          "panels": [panel]}
        context = {"scanner": {"source_results": [scanner_result]},
                   "probe_manifest": {"sources": [{"source_id": source_id, "source_pixel_sha256": source_hash}]},
                   "source_images": {source_id: image}}

        def crop_bytes(_image, center):
            return f"fixture-crop:{center[0]}:{center[1]}".encode(), np.zeros((96, 256), dtype=np.float32)

        rows, summary = subject._candidate_rows(context, self.scanner, type("P3", (), {"crop_bytes": staticmethod(crop_bytes)}),
            expected_counts=(0, 1, 1))
        self.assertEqual(1, len(rows))
        self.assertEqual(source_id, rows[0]["source_id"])
        self.assertEqual(panel_id, rows[0]["panel_id"])
        self.assertEqual(subject.digest(f"fixture-crop:{rows[0]['proposal']['center'][0]}:{rows[0]['proposal']['center'][1]}".encode()),
                         rows[0]["crop_sha256"])
        self.assertEqual(0, summary["baseline_parity_proposal_count"])
        self.assertEqual(1, summary["relaxed_width_proposal_count"])
        serialized = subject.serializable_candidate_rows(rows)
        self.assertNotIn("pixels", serialized[0])
        self.assertFalse({"label", "truth", "matching_state", "target"} & set(serialized[0]))

    def test_holdout_hash_admission_uses_manifest_metadata_only(self):
        manifest = {"records": [
            {"source_group_id": "holdout", "image_sha256": "h1"},
            {"source_group_id": "train", "image_sha256": "t1"},
            {"source_group_id": "holdout", "image_sha256": "h2"},
        ]}
        self.assertEqual({"h1", "h2"}, subject.holdout_hashes_from_metadata(manifest, {"holdout"}))

    def test_train_hash_subset_and_source_overlap_bindings(self):
        subject.validate_training_hash_sets({"a", "b"}, {"a", "b", "c"}, 2, 3)
        with self.assertRaises(subject.RelaxedWidthError):
            subject.validate_training_hash_sets({"a", "x"}, {"a", "b", "c"}, 2, 3)
        train = [{"source_id": "g1", "source_identity_sha256": "ti", "reconstructed_full_panel_pixel_sha256": "tp"},
                 {"source_id": "h1", "source_identity_sha256": "hi", "reconstructed_full_panel_pixel_sha256": "hp"}]
        probe = [{"source_id": "probe", "source_identity_sha256": "pi", "source_pixel_sha256": "pp"}]
        self.assertTrue(all(not values for values in subject.source_overlap_sets({"g1"}, {"h1"}, train, probe).values()))
        overlapping = probe + [{"source_id": "g1", "source_identity_sha256": "ti", "source_pixel_sha256": "tp"}]
        found = subject.source_overlap_sets({"g1"}, {"h1"}, train, overlapping)
        self.assertEqual(["g1"], found["p5_train_source_id_overlap"])
        self.assertEqual(["ti"], found["p5_train_source_identity_overlap"])

    def test_pinned_p3_adapter_batches_synthetic_candidate_pixels(self):
        p3 = subject.load_exact(
            "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/evaluate_p3_probe.py",
            "263506da81caaa40f75cf6cb8c8b06e55c77b9a19e7a2b88af420a311e87337f",
            "test_pinned_p3_batch_adapter")

        class Model(torch.nn.Module):
            def forward(self, batch):
                return batch.new_zeros((batch.shape[0], 1))

        class P1:
            @staticmethod
            def _build_classifier(_torch):
                return Model()

        class Helpers:
            @staticmethod
            def require_rx_6600_xt(_torch):
                return {"device": "cpu", "device_name": "test stub", "cpu_fallback": False}

        fake_p2 = type("P2", (), {"helpers": Helpers(), "p1": P1()})
        expected = {"model_identity": "stroke-seeded-ink-validity-rf31-p3-sourceuniform-60ep",
            "recipe_id": "ink-stroke-validity-source-uniform-p3-60ep", "epochs": 60,
            "p3_config_sha256": p3.P3_CONFIG_SHA, "p3_runner_sha256": p3.P3_RUNNER_SHA,
            "p2_base_config_sha256": "b230e743cadb3d057aef443b3da1dc5821e2da2ce88ecc20ae3175725069aa42",
            "p2_base_runner_sha256": "dc86c5ee004794519c6c1ac7fa21699b06a31637914de3ab827353c828e3aa5d",
            "manifest_sha256": p3.TRAIN_MANIFEST_SHA, "model_seed": 27104152, "sampler_seed": 27104153,
            "state_dict": {}}
        with patch.object(p3, "_load_exact_v2_materializer"), \
             patch.object(p3, "_load_p3_stack", return_value=(fake_p2, {"test": "pinned"})), \
             patch.object(p3, "_assert_runtime"), \
             patch.object(p3, "read_project", return_value=b"test-checkpoint"), \
             patch.object(torch, "load", return_value=expected), \
             patch.object(torch.cuda, "synchronize"):
            scores, runtime = p3._infer_p3([{"pixels": np.zeros((96, 256), dtype=np.float32)}])
        self.assertEqual([0.5], scores)
        self.assertEqual(1, runtime["inference_rows"])
        self.assertEqual(1, runtime["inference_batches"])

    def test_pinned_p5_adapter_batches_same_synthetic_candidate_pixels(self):
        p5_eval = subject.load_exact(subject.P5_EVALUATOR_REL, subject.P5_EVALUATOR_SHA,
                                     "test_pinned_p5_batch_adapter")

        class Model(torch.nn.Module):
            def forward(self, batch):
                return batch.new_zeros((batch.shape[0], 1))

        class P1:
            @staticmethod
            def _build_classifier(_torch):
                return Model()

        class Helpers:
            GPU_PYTHON = Path(sys.executable)

            @staticmethod
            def require_rx_6600_xt(_torch):
                return {"device": "cpu", "device_name": "AMD Radeon RX 6600 XT", "cpu_fallback": False}

        fake_p2 = type("P2", (), {"helpers": Helpers(), "p1": P1(),
                                    "_versions": staticmethod(lambda runtime: dict(runtime))})
        class Trainer:
            @staticmethod
            def prepare_stack():
                return None, fake_p2, {}, {"test": "pinned"}

        cfg_bytes = b"test-p5-config"
        row_binding_sha = "test-p4-row-bindings"
        payload = {"model_identity": "stroke-seeded-ink-validity-rf31-p5-train-hard-negative-weighted",
            "p4_teacher_checkpoint_sha256": "9b924cafd94c079ad866d1b6e79961fe3abab61c3e96fe5efac7c9796b836df9",
            "p5_config_sha256": subject.digest(cfg_bytes),
            "p5_teacher_receipt_sha256": "cd5e51f4634a60a08e8fb2357c4723209346996cc1b42ab9e2ced8c077cc1f91",
            "p4_train_row_bindings_sha256": row_binding_sha, "state_dict": {}}
        fake_data = {"row_bindings_sha256": row_binding_sha}
        with patch.object(torch, "load", return_value=payload), \
             patch.object(p5_eval, "read_project", return_value=cfg_bytes), \
             patch.object(torch.cuda, "synchronize"):
            scores, runtime = p5_eval.p5_scores(Trainer(), {}, {}, b"test-checkpoint", fake_data,
                [{"pixels": np.zeros((96, 256), dtype=np.float32)}])
        self.assertEqual([0.5], scores)
        self.assertEqual(1, runtime["inference_rows"])
        self.assertEqual(1, runtime["inference_batches"])


if __name__ == "__main__":
    unittest.main()
