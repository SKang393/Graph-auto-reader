# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic-only ONNX / RX 6600 XT parity; no training or real inputs."""
from pathlib import Path
import tempfile
import unittest

import numpy as np
import torch

from .export_evaluate import GpuEvaluationModel, ROOT, export_model, require_gpu
from .model import OPERATING_THRESHOLD, RealPositiveCenterNet
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet


class ExportEvaluationTest(unittest.TestCase):
    def test_dynamic_synthetic_gpu_onnx_parity_and_retained_geometry(self):
        device = require_gpu()
        torch.manual_seed(482)
        model = RealPositiveCenterNet(SpatialCenterNet(ScaleClassifierNet(ModelConfig()))).eval()
        with torch.no_grad():
            model.residual.bias.fill_(0.25)
        before = {name: value.detach().clone() for name, value in model.state_dict().items()}
        root = ROOT / "artifacts" / "test-temp"
        with tempfile.TemporaryDirectory(prefix="synthetic-export-", dir=root) as temporary:
            path = Path(temporary) / "candidate.onnx"
            session = export_model(model, path, device)
            gpu = GpuEvaluationModel(model, device)
            for count in (1, 4):
                generator = torch.Generator(device="cpu").manual_seed(count)
                samples = torch.rand(count, 3, 33, 33, generator=generator)
                expected = gpu(samples).numpy()
                actual = session.run(["candidate_predictions"], {"candidate_patches": samples.numpy()})[0]
                self.assertEqual(actual.shape, (count, 4))
                self.assertTrue(np.isfinite(actual).all())
                self.assertLessEqual(float(np.max(np.abs(expected - actual))), 1e-5)
                np.testing.assert_array_equal(expected[:, 0] >= OPERATING_THRESHOLD,
                                              actual[:, 0] >= OPERATING_THRESHOLD)
            self.assertEqual(gpu.rows, 5)
            self.assertLessEqual(gpu.geometry_maximum_absolute_change, 1e-5)
            self.assertGreater(path.stat().st_size, 0)
        for name, value in model.state_dict().items():
            self.assertTrue(torch.equal(value.cpu(), before[name]), name)


if __name__ == "__main__":
    unittest.main()
