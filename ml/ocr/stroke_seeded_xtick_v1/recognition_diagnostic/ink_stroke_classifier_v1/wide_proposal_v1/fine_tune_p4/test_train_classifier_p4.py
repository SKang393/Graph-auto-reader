# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused pure checks for P4 admission and preflight report contracts."""

from __future__ import annotations

import importlib.util
import inspect
from pathlib import Path
import unittest

SCRIPT = Path(__file__).with_name("train_classifier_p4.py")
spec = importlib.util.spec_from_file_location("p4_train_tests", SCRIPT)
assert spec and spec.loader
p4 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p4)


class P4AdmissionTests(unittest.TestCase):
    def test_ambiguous_matching_state_is_not_a_class(self) -> None:
        self.assertIsNone(p4.strict_label("ambiguous", "excluded"))
        self.assertIsNone(p4.strict_label("ambiguous", "positive"))
        self.assertEqual(p4.strict_label("unique_visible_tick", "positive"), 1)
        self.assertEqual(p4.strict_label("no_ground_truth_tick_nearby", "background_only"), 0)

    def test_hash_dedup_merges_source_groups(self) -> None:
        image_hash = "a" * 64
        merged = p4.merge_hash_labels([
            {"image_sha256": image_hash, "label": 1, "source_group_ids": ["g2"]},
            {"image_sha256": image_hash, "label": 1, "source_group_ids": ["g1", "g2"]},
        ])
        self.assertEqual(merged, {image_hash: {"label": 1, "source_group_ids": ["g1", "g2"]}})

    def test_cross_manifest_semantic_collision_fails_closed(self) -> None:
        image_hash = "b" * 64
        with self.assertRaisesRegex(p4.P4Error, "semantic label conflict"):
            p4.merge_hash_labels([
                {"image_sha256": image_hash, "label": 1, "source_group_ids": ["baseline"]},
                {"image_sha256": image_hash, "label": 0, "source_group_ids": ["candidate"]},
            ])

    def test_preflight_report_cannot_claim_optimizer_work(self) -> None:
        cfg = {"recipe_id": "p4-test", "architecture": "frozen", "model_seed": 1,
               "sampler_seed": 2, "epochs": 20, "batch_size": 32, "steps_per_epoch": 23,
               "optimizer_steps": 460, "learning_rate": 0.001, "weight_decay": 0.01,
               "optimizer": "AdamW", "loss": "BCEWithLogits", "class_draws_per_batch": 16,
               "per_class_draws_per_epoch": 368, "sampler": "round-robin",
               "threshold_for_train_only_metrics": 0.5, "threshold_tuning": False,
               "capturable": True, "torch_rng_seed": 1, "torch_rng_seed_policy": "test"}
        data = {"input_manifest_hashes": {}, "row_bindings_sha256": "c" * 64,
                "baseline_unique_images": 446, "candidate_unique_images": 19,
                "candidate_quarantined_rows": 268, "rows": [None] * 465,
                "class_counts": {1: 89, 0: 376}, "groups": [], "candidate_dependency_sha256": {}}
        gpu = {"optimizer_created": False, "training_or_backward_performed": False}
        report = p4.build_preflight_report(cfg, "d" * 64, data, {}, gpu, "e" * 64,
                                          {"p2_runner_sha256": "f" * 64}, {"candidate_probe_hash_overlap": 0})
        self.assertEqual(report["status"], "preflight-passed-no-optimizer")
        self.assertIs(report["training_performed"], False)
        self.assertIs(report["gpu_preflight"]["optimizer_created"], False)
        self.assertEqual(report["data"]["unique_total"], 465)
        self.assertEqual(report["data"]["class_counts"], {"positive": 89, "negative": 376})
        self.assertEqual(report["executed_dependency_sha256"]["p2_runner_sha256"], "f" * 64)

    def test_capturable_adamw_support_is_required(self) -> None:
        class Supported:
            def __init__(self, params, *, capturable=False):
                del params, capturable

        class Unsupported:
            def __init__(self, params):
                del params

        p4.require_capturable_adamw(Supported)
        self.assertIn("capturable", inspect.signature(Supported).parameters)
        with self.assertRaisesRegex(p4.P4Error, "does not expose capturable"):
            p4.require_capturable_adamw(Unsupported)

    def test_optimizer_state_residency_checks_nested_tensors_and_gradients(self) -> None:
        class Tensor:
            def __init__(self, device):
                self.device = device

        class Parameter(Tensor):
            def __init__(self, device, grad=None):
                super().__init__(device)
                self.grad = grad

        class Model:
            def __init__(self, parameters):
                self._parameters = parameters

            def parameters(self):
                return iter(self._parameters)

        class Optimizer:
            def __init__(self, parameter, state):
                self.param_groups = [{"params": [parameter], "capturable": True}]
                self.state = {parameter: state}

        Torch = type("Torch", (), {"Tensor": Tensor})

        param = Parameter("cuda:0", Tensor("cuda:0"))
        opt = Optimizer(param, {"step": Tensor("cuda:0"), "nested": [Tensor("cuda:0")]})
        counts = p4.assert_optimizer_gpu_residency(Torch, Model([param]), opt, "cuda:0")
        self.assertEqual(counts, {"model_parameters": 1, "gradients": 1,
                                  "optimizer_parameters": 1, "optimizer_state_tensors": 2})

        opt.state[param]["nested"].append(Tensor("cpu"))
        with self.assertRaisesRegex(p4.P4Error, "state tensor left"):
            p4.assert_optimizer_gpu_residency(Torch, Model([param]), opt, "cuda:0")

    def test_rng_seeding_covers_cpu_and_cuda_generators(self) -> None:
        class Cuda:
            seed = None

            @classmethod
            def manual_seed_all(cls, seed):
                cls.seed = seed

        class Torch:
            cuda = Cuda
            seed = None

            @classmethod
            def manual_seed(cls, seed):
                cls.seed = seed

        p4.seed_torch_rng(Torch, 27104152)
        self.assertEqual(Torch.seed, 27104152)
        self.assertEqual(Torch.cuda.seed, 27104152)


if __name__ == "__main__":
    unittest.main()
