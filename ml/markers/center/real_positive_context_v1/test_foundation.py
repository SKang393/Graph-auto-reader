# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic inference and GPU gradient checks only; no optimizer is created."""
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import unittest

import torch
from torch.nn import functional as F

from .model import OPERATING_THRESHOLD, RealPositiveCenterNet
from .objective import SyntheticLabels, mixed_confidence_loss
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet


def _gpu() -> torch.device:
    if not torch.cuda.is_available() or not torch.version.hip:
        raise RuntimeError("ROCm RX 6600 XT is required for this test")
    device = torch.device("cuda:0")
    props = torch.cuda.get_device_properties(device)
    if props.name != "AMD Radeon RX 6600 XT" or props.gcnArchName.split(":")[0] != "gfx1032":
        raise RuntimeError("Expected RX 6600 XT gfx1032 on cuda:0")
    return device


def _labels(values: bytes) -> SyntheticLabels:
    return SyntheticLabels(values, sha256(values).hexdigest())


class FoundationTests(unittest.TestCase):
    def test_pinned_synthetic_v37_checkpoint_retains_predictions_when_available(self):
        root = Path(__file__).resolve().parents[4]
        relative_checkpoint = Path("artifacts/goal22-runs/center-confidence-reflection-v37/P1/marker-center.pt")
        candidates = (root / relative_checkpoint,
                      root / "artifacts/goal22-worktrees/resume-20260908" / relative_checkpoint)
        checkpoint = next((path for path in candidates if path.is_file()), None)
        if checkpoint is None:
            self.skipTest("Foundation checkpoint parity only: V37 artifact is absent from both known locations")
        result = root / "ml/markers/center/confidence_reflection_v37/P1_RESULT.json"
        expected_sha256 = json.loads(result.read_text(encoding="utf-8"))["checkpoint_sha256"]
        raw = checkpoint.read_bytes()
        self.assertEqual(sha256(raw).hexdigest(), expected_sha256)
        device = _gpu()
        local = SpatialCenterNet(ScaleClassifierNet(ModelConfig()))
        local.load_state_dict(torch.load(BytesIO(raw), map_location="cpu", weights_only=True)["state_dict"])
        local.eval().to(device)
        model = RealPositiveCenterNet(local).to(device)
        patches = torch.rand(4, 3, 33, 33, device=device)
        with torch.inference_mode():
            self.assertTrue(torch.equal(local(patches), model(patches)))

    def test_zero_initialization_retains_predictions_geometry_and_threshold(self):
        device = _gpu()
        torch.manual_seed(23)
        local = SpatialCenterNet(ScaleClassifierNet(ModelConfig())).eval().to(device)
        model = RealPositiveCenterNet(local).to(device)
        patches = torch.rand(4, 3, 33, 33, device=device)
        with torch.inference_mode():
            baseline = local(patches)
            actual = model(patches)
        self.assertTrue(torch.equal(actual, baseline))
        self.assertTrue(torch.equal(actual[:, 0] >= OPERATING_THRESHOLD,
                                    baseline[:, 0] >= OPERATING_THRESHOLD))
        model.train()
        self.assertFalse(local.training)
        self.assertTrue(all(not parameter.requires_grad for parameter in local.parameters()))
        self.assertTrue(all(parameter.requires_grad for parameter in model.residual.parameters()))
        self.assertEqual(sum(p.numel() for p in model.residual.parameters()), 769)
        with torch.no_grad():
            model.residual.bias.fill_(1)
            altered = model(patches)
        self.assertTrue(torch.equal(altered[:, 1:], baseline[:, 1:]))
        self.assertFalse(torch.equal(altered[:, 0], baseline[:, 0]))

    def test_synthetic_bytes_are_binary_immutable_and_hash_bound(self):
        labels = _labels(b"\x00\x01\x01")
        self.assertEqual(labels.values, b"\x00\x01\x01")
        for raw, digest in ((b"", sha256(b"").hexdigest()),
                            (b"\x02", sha256(b"\x02").hexdigest()),
                            (b"\x01", "0" * 64)):
            with self.subTest(raw=raw):
                with self.assertRaises(ValueError):
                    SyntheticLabels(raw, digest)
        with self.assertRaises(ValueError):
            SyntheticLabels(bytearray(b"\x01"), sha256(b"\x01").hexdigest())

    def test_gpu_loss_uses_only_positive_real_targets_and_no_optimizer(self):
        device = _gpu()
        synthetic = torch.tensor([-1., 2., .5], device=device, requires_grad=True)
        real = torch.tensor([-2., 0., 2.], device=device, requires_grad=True)
        loss = mixed_confidence_loss(synthetic, _labels(b"\x00\x01\x01"), real, real_weight=0.4)
        expected = (F.binary_cross_entropy_with_logits(synthetic, torch.tensor([0., 1., 1.], device=device)) +
                    .4 * F.binary_cross_entropy_with_logits(real, torch.ones_like(real)))
        self.assertEqual(loss.device, device)
        self.assertTrue(torch.allclose(loss, expected))
        loss.backward()
        self.assertEqual(real.grad.device, device)
        self.assertTrue(torch.all(real.grad < 0))
        self.assertTrue(synthetic.grad[0] > 0 and synthetic.grad[1] < 0)

    def test_gpu_gradient_reaches_only_residual_without_an_optimizer(self):
        device = _gpu()
        local = SpatialCenterNet(ScaleClassifierNet(ModelConfig())).to(device)
        model = RealPositiveCenterNet(local).to(device)
        model.train()
        patches = torch.rand(5, 3, 33, 33, device=device)
        logits, geometry = model.logits_and_geometry(patches)
        self.assertFalse(geometry.requires_grad)
        loss = mixed_confidence_loss(logits[:3], _labels(b"\x00\x01\x01"), logits[3:], real_weight=.4)
        loss.backward()
        self.assertTrue(all(p.grad is None for p in local.parameters()))
        self.assertTrue(all(p.grad is not None and p.grad.device == device for p in model.residual.parameters()))
        self.assertTrue(all(torch.isfinite(p.grad).all() for p in model.residual.parameters()))
        self.assertGreater(float(model.residual.weight.grad.abs().sum()), 0.)

    def test_invalid_shapes_weights_and_labels_fail_before_use(self):
        device = _gpu()
        logits = torch.zeros(2, device=device)
        labels = _labels(b"\x00\x01")
        for synthetic, target, real, weight in (
            (logits[:1], labels, logits, 1.),
            (logits.reshape(1, 2), labels, logits, 1.),
            (logits, labels, logits[:0], 1.),
            (logits, labels, logits, 0.),
            (logits, labels, logits, float("nan")),
            (logits, labels, torch.tensor([float("nan")], device=device), 1.),
        ):
            with self.subTest(weight=weight, synthetic_shape=synthetic.shape, real_shape=real.shape):
                with self.assertRaises(ValueError):
                    mixed_confidence_loss(synthetic, target, real, real_weight=weight)
        with self.assertRaises(TypeError):
            mixed_confidence_loss(logits, b"\x00\x01", logits, real_weight=1.)
        with self.assertRaises(RuntimeError):
            mixed_confidence_loss(torch.zeros(2), labels, torch.zeros(2), real_weight=1.)


if __name__ == "__main__":
    unittest.main()
