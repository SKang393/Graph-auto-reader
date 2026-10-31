# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic checks. No optimizer or real data."""
import unittest
import json
from types import SimpleNamespace
from unittest.mock import patch
import torch
from . import probe


class GpuDbHeadProbeTests(unittest.TestCase):
    def test_cpu_fallback_rejected_before_probe(self):
        with patch.object(torch.cuda, "is_available", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "CPU fallback forbidden"):
                probe.run_probe()

    def test_synthetic_head_has_only_expected_tensors(self):
        head = probe.synthetic_head()
        self.assertEqual(len(tuple(head.parameters())), 9)
        self.assertEqual(len(tuple(head.buffers())), 4)
        self.assertTrue(all(torch.isfinite(value).all() for value in head.state_dict().values()))

    def test_rejects_missing_hip_wrong_card_and_wrong_architecture(self):
        for hip, name, arch, message in (
            (None, "AMD Radeon RX 6600 XT", "gfx1032", "CPU fallback forbidden"),
            ("7.16", "AMD Radeon RX 6800", "gfx1032", "must reside"),
            ("7.16", "AMD Radeon RX 6600 XT", "gfx1100", "must reside"),
        ):
            with self.subTest(hip=hip, name=name, arch=arch), \
                 patch.object(torch.cuda, "is_available", return_value=True), \
                 patch.object(torch.version, "hip", hip), \
                 patch.object(torch.cuda, "get_device_properties", return_value=SimpleNamespace(name=name, gcnArchName=arch)):
                with self.assertRaisesRegex(RuntimeError, message):
                    probe.require_gpu()

    def test_rejects_cpu_tensor_when_gpu_identity_is_valid(self):
        with patch.object(torch.cuda, "is_available", return_value=True), \
             patch.object(torch.version, "hip", "7.16"), \
             patch.object(torch.cuda, "get_device_properties", return_value=SimpleNamespace(
                 name="AMD Radeon RX 6600 XT", gcnArchName="gfx1032")):
            with self.assertRaisesRegex(RuntimeError, "must reside"):
                probe.require_gpu(torch.zeros(1))

    def test_synthetic_gpu_forward_backward_has_no_optimizer(self):
        with patch.object(torch.optim.Optimizer, "step", side_effect=AssertionError("optimizer forbidden")):
            result = probe.run_probe()
        self.assertEqual(result["status"], "pass")
        print(json.dumps(result, sort_keys=True))
        # Peak PyTorch allocation exceeded 64 MiB on the first measured run.
        self.assertLess(result["peak_additional_allocated_bytes"], 96 * 1024 * 1024)
        self.assertEqual(result["optimizer_steps"], 0)
        self.assertFalse(result["training_inputs_read"])
        self.assertFalse(result["production_approved"])


if __name__ == "__main__":
    unittest.main()
