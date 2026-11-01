# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""No-training tests for the fixed-threshold contribution analysis."""
from __future__ import annotations

import json
from pathlib import Path
import unittest

import torch

from ml.markers.center.confidence_pairwise_fp_attribution_v1 import attribution


class AttributionTests(unittest.TestCase):
    def test_per_hidden_contributions_reconstruct_linear_output(self) -> None:
        torch.manual_seed(42)
        hidden = torch.randn(11, 64)
        layer = torch.nn.Linear(64, 1)
        contributions = attribution._contributions(hidden, layer.weight)
        reconstructed = contributions.sum(dim=1) + layer.bias[0]
        expected = layer(hidden).squeeze(1)
        self.assertTrue(torch.allclose(reconstructed, expected, atol=1e-6, rtol=1e-6))

    def test_top_deltas_rank_positive_fp_associations(self) -> None:
        fp = torch.zeros(4).numpy()
        tn = torch.zeros(4).numpy()
        fp[:] = [4.0, 1.0, 3.0, -1.0]
        tn[:] = [1.0, 2.0, 0.0, -3.0]
        top = attribution._top_deltas(fp, 2, tn, 2, 3)
        self.assertEqual([item["hidden_unit"] for item in top], [0, 2, 3])
        self.assertEqual([item["mean_contribution_delta_fp_minus_tn"] for item in top], [1.5, 1.5, 1.0])

    def test_protocol_is_fixed_threshold_train_only_and_group_complete(self) -> None:
        protocol = json.loads(attribution.PROJECT_ROOT.joinpath(*attribution.PROTOCOL_RELATIVE.parts).read_text(encoding="utf-8"))
        self.assertEqual(protocol["analysis"]["fixed_threshold"], 0.1)
        self.assertEqual(protocol["analysis"]["logit_decomposition_tolerance"], {"absolute": 0.00002, "relative": 0.00001})
        self.assertEqual(protocol["analysis"]["group_scopes"], [
            "component:identity", "family:identity", "coverage:identity", "crowded:identity", "crowded_mined_negative:identity",
        ])
        self.assertFalse(protocol["data_policy"]["training"])
        self.assertFalse(protocol["data_policy"]["threshold_search"])
        self.assertEqual(protocol["data_policy"]["development_payloads_read"], 0)
        self.assertEqual(protocol["data_policy"]["private_reads"], 0)
        self.assertEqual(protocol["data_policy"]["sealed_reads"], 0)

    def test_attribution_source_contains_no_optimizer_operations(self) -> None:
        source = attribution.PROTOCOL_RELATIVE.with_name("attribution.py")
        content = (attribution.PROJECT_ROOT / source).read_text(encoding="utf-8")
        for forbidden in ("torch.optim", ".backward(", ".step("):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, content)


if __name__ == "__main__":
    unittest.main(verbosity=2)
