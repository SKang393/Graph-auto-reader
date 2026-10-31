# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""GPU synthetic tests; no optimizer, real data, development or sealed inputs."""
import unittest
from copy import deepcopy
from hashlib import sha256
from io import BytesIO
import json
from unittest.mock import patch

import numpy as np
import torch

from ..confidence_spatial_v35.model import SpatialCenterNet
from ..real_positive_context_v1.model import RealPositiveCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from .objective import directional_penalties
from .source import SyntheticTrainFeatures
from . import source


def fixture(values, cohort):
    features = torch.zeros((len(values), 768), dtype=torch.float32)
    features[:, 0] = torch.tensor(values, dtype=torch.float32)
    return SyntheticTrainFeatures._synthetic_fixture(features, cohort)


class DirectionalPreservationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not torch.cuda.is_available() or not torch.version.hip:
            raise RuntimeError("The RX 6600 XT GPU is required for these tests")
        properties = torch.cuda.get_device_properties(0)
        if properties.name != "AMD Radeon RX 6600 XT" or properties.gcnArchName.split(":")[0] != "gfx1032":
            raise RuntimeError("Tests require RX 6600 XT gfx1032")

    def setUp(self):
        self.model = RealPositiveCenterNet(SpatialCenterNet(ScaleClassifierNet(ModelConfig()))).to("cuda:0")
        with torch.no_grad():
            self.model.residual.weight.zero_()
            self.model.residual.weight[0, 0] = 1
            self.model.residual.bias.zero_()
        self.component = fixture([-2, 3], "component_positive")
        self.family = fixture([-2, 3], "family_negative")

    def test_loss_penalizes_only_expected_directions_and_has_gpu_gradient(self):
        loss = directional_penalties(self.model, self.component, (0, 1), self.family, (0, 1),
                                     component_weight=2, family_weight=3, _allow_synthetic_fixture=True)
        self.assertAlmostEqual(loss.component_positive_downward.item(), 2.0)
        self.assertAlmostEqual(loss.family_negative_upward.item(), 4.5)
        self.assertAlmostEqual(loss.weighted_total.item(), 17.5)
        loss.weighted_total.backward()
        self.assertEqual(self.model.residual.weight.grad.device.type, "cuda")
        self.assertTrue(all(parameter.grad is None for parameter in self.model.local.parameters()))

    def test_favorable_directions_are_zero(self):
        loss = directional_penalties(self.model, self.component, (1,), self.family, (0,),
                                     component_weight=1, family_weight=1, _allow_synthetic_fixture=True)
        self.assertEqual(loss.weighted_total.item(), 0)

    def test_fixture_and_cohort_and_weights_fail_closed(self):
        with self.assertRaises(RuntimeError):
            directional_penalties(self.model, self.component, (0,), self.family, (0,), component_weight=1, family_weight=1)
        with self.assertRaises(ValueError):
            directional_penalties(self.model, self.family, (0,), self.component, (0,),
                                  component_weight=1, family_weight=1, _allow_synthetic_fixture=True)
        for weight in (0, -1, float("nan"), True):
            with self.subTest(weight=weight), self.assertRaises(ValueError):
                directional_penalties(self.model, self.component, (0,), self.family, (0,),
                                      component_weight=weight, family_weight=1, _allow_synthetic_fixture=True)
        for indices in ((), (0, 0), (2,), (True,)):
            with self.subTest(indices=indices), self.assertRaises(ValueError):
                directional_penalties(self.model, self.component, indices, self.family, (0,),
                                      component_weight=1, family_weight=1, _allow_synthetic_fixture=True)

    def test_bound_train_features_and_manifest_tamper(self):
        component = SyntheticTrainFeatures.load("component_positive", "identity")
        family = SyntheticTrainFeatures.load("family_negative", "identity")
        self.assertEqual((component.count, family.count), (3258, 8230))
        self.assertEqual((component.binding["anchor_label"], family.binding["anchor_label"]), (1, 0))
        self.assertEqual(component.gpu_batch((0,)).device.type, "cuda")
        with self.assertRaises(ValueError):
            SyntheticTrainFeatures.load("private", "identity")
        with self.assertRaises(ValueError):
            SyntheticTrainFeatures.load("component_positive", "development")
        with patch("ml.markers.center.preservation_bidirectional_v1.source._load_bytes", return_value=b"tampered"):
            with self.assertRaises(ValueError):
                SyntheticTrainFeatures.load("component_positive", "identity")

    def test_authenticated_cohorts_accept_v37_and_reject_single_weight_drift(self):
        component = SyntheticTrainFeatures.load("component_positive", "identity")
        family = SyntheticTrainFeatures.load("family_negative", "identity")
        checkpoint = torch.load(BytesIO(source._load_bytes(source.DATA / source.V37, source.V37_CHECKPOINT_SHA256)),
                                map_location="cpu", weights_only=True)
        self.model.local.load_state_dict(checkpoint["state_dict"], strict=True)
        self.model.train()
        self.assertFalse(self.model.local.training)
        self.assertTrue(self.model.residual.training)
        result = directional_penalties(self.model, component, (0,), family, (0,),
                                       component_weight=1, family_weight=1)
        self.assertTrue(torch.isfinite(result.weighted_total).item())
        with torch.no_grad():
            first = next(self.model.local.parameters()).view(-1)
            first[0].add_(1.0)
        with self.assertRaisesRegex(RuntimeError, "frozen local model"):
            directional_penalties(self.model, component, (0,), family, (0,),
                                  component_weight=1, family_weight=1)

    def test_sha_valid_but_structurally_invalid_train_reports_are_rejected(self):
        original_loader = source._load_bytes
        report_path = source.DATA / source.REPORT
        original = json.loads(original_loader(report_path, source.V37_FEATURE_REPORT_SHA256))
        for kind in ("group", "model", "labels", "features"):
            with self.subTest(kind=kind):
                report = deepcopy(original)
                group = next(item for item in report["groups"] if item["scope"] == "component:identity")
                fake_files = {}
                if kind == "group":
                    group["rows"] -= 1
                elif kind == "model":
                    report["model"]["sha256"] = "0" * 64
                else:
                    array = np.zeros((2,), dtype=np.float32) if kind == "labels" else np.zeros((2, 768), dtype=np.float32)
                    buffer = BytesIO()
                    np.save(buffer, array, allow_pickle=False)
                    raw = buffer.getvalue()
                    relative = f"artifacts/goal22-runs/synthetic-invalid-{kind}.npy"
                    digest = sha256(raw).hexdigest()
                    group[kind] = {"path": relative, "sha256": digest}
                    fake_files[source.DATA / relative] = (raw, digest)
                report_bytes = json.dumps(report).encode("utf-8")
                report_hash = sha256(report_bytes).hexdigest()
                def load(path, expected):
                    if path == report_path:
                        self.assertEqual(expected, report_hash)
                        return report_bytes
                    if path in fake_files:
                        raw, digest = fake_files[path]
                        self.assertEqual(expected, digest)
                        return raw
                    return original_loader(path, expected)
                with patch.object(source, "V37_FEATURE_REPORT_SHA256", report_hash), patch.object(source, "_load_bytes", side_effect=load):
                    with self.assertRaises(ValueError):
                        SyntheticTrainFeatures.load("component_positive", "identity")


if __name__ == "__main__":
    unittest.main()
