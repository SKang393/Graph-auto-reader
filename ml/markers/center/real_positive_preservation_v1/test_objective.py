# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""GPU-only synthetic gradient checks; no optimizer and no private data."""
from hashlib import sha256
import unittest

import torch

from ..confidence_spatial_v35.model import SpatialCenterNet
from ..real_positive_context_v1.model import RealPositiveCenterNet
from ..real_positive_context_v1.objective import SyntheticLabels
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from .component_source import ComponentNegativeStore
from .objective import preservation_loss


class PreservationObjectiveTests(unittest.TestCase):
    def setUp(self):
        if not torch.version.hip or not torch.cuda.is_available():
            self.skipTest("ROCm RX 6600 XT is required")
        properties = torch.cuda.get_device_properties(torch.device("cuda:0"))
        if properties.name != "AMD Radeon RX 6600 XT" or properties.gcnArchName.split(":")[0] != "gfx1032":
            self.skipTest("RX 6600 XT gfx1032 is required at cuda:0")
        self.model = RealPositiveCenterNet(SpatialCenterNet(ScaleClassifierNet(ModelConfig()))).to("cuda:0")
        self.model.train()
        self.synthetic = torch.zeros((2, 3, 33, 33), dtype=torch.float32, device="cuda:0")
        self.real_fixture = torch.ones((1, 3, 33, 33), dtype=torch.float32, device="cuda:0")
        self.source = ComponentNegativeStore._synthetic_fixture(torch.ones((3, 768), dtype=torch.float32))
        raw = b"\x00\x01"
        self.labels = SyntheticLabels(raw, sha256(raw).hexdigest())

    def _loss(self, **kwargs):
        return preservation_loss(self.model, self.synthetic, self.labels, self.real_fixture,
                                 self.source, (0, 1), _allow_synthetic_fixture=True, **kwargs)

    def test_upward_penalty_has_only_residual_gpu_gradients(self):
        with torch.no_grad():
            self.model.residual.bias.fill_(0.5)
        before = {name: value.detach().clone() for name, value in self.model.local.state_dict().items()}
        result = self._loss()
        self.assertAlmostEqual(float(result.component_upward_penalty.detach().cpu()), 0.25, places=6)
        result.component_upward_penalty.backward()
        self.assertTrue(all(parameter.grad is None for parameter in self.model.local.parameters()))
        self.assertTrue(all(parameter.grad is not None and parameter.grad.device == torch.device("cuda:0")
                            for parameter in self.model.residual.parameters()))
        self.assertGreater(float(self.model.residual.bias.grad.item()), 0)
        self.assertTrue(all(torch.equal(value, before[name]) for name, value in self.model.local.state_dict().items()))

    def test_downward_and_initial_residual_are_not_penalized(self):
        self.assertEqual(float(self._loss().component_upward_penalty.detach().cpu()), 0.0)
        with torch.no_grad():
            self.model.residual.bias.fill_(-0.5)
        self.assertEqual(float(self._loss().component_upward_penalty.detach().cpu()), 0.0)

    def test_fixture_cannot_enter_normal_source_and_bad_indices_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "not an authenticated"):
            preservation_loss(self.model, self.synthetic, self.labels, self.real_fixture, self.source, (0,))
        for bad in ((), (0, 0), (3,), (-1,), (True,)):
            with self.subTest(indices=bad), self.assertRaises(ValueError):
                preservation_loss(self.model, self.synthetic, self.labels, self.real_fixture, self.source, bad,
                                  _allow_synthetic_fixture=True)

    def test_cpu_input_cannot_reach_loss(self):
        with self.assertRaisesRegex(RuntimeError, "CPU training is forbidden"):
            preservation_loss(self.model, self.synthetic.cpu(), self.labels, self.real_fixture, self.source, (0,),
                              _allow_synthetic_fixture=True)


if __name__ == "__main__":
    unittest.main()
