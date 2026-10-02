# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.center.confidence_head_v32 import adaptation as subject
from ml.markers.center.focal_confidence_v21.focal_loss import binary_focal_loss_with_logits
from ml.markers.center.scale_classifier_v16.model import ScaleClassifierNet
from ml.markers.center import shape_coverage_evaluation as evaluation


def patches():
    return torch.rand(4, 3, 33, 33, generator=torch.Generator().manual_seed(20261002))


def snapshot(model):
    return {name: value.detach().clone() for name, value in model.state_dict().items()}


def test_confidence_adaptation_changes_only_65_parameters_and_preserves_geometry_outputs():
    model = ScaleClassifierNet()
    original = snapshot(model)
    head = subject.freeze_for_confidence_adaptation(model)
    assert sum(p.numel() for p in head.parameters()) == 65
    assert not any(p.requires_grad for p in model.parameters())
    assert not any(module.training for module in model.modules())
    inputs = patches()
    features = subject.frozen_features(model, inputs)
    assert not features.requires_grad
    with torch.no_grad():
        before = model(inputs)
        raw = model.forward_raw(inputs)
    torch.testing.assert_close(head(features), raw[:, 0], rtol=1e-5, atol=1e-6)
    optimizer = torch.optim.AdamW(head.parameters(), lr=.0003, weight_decay=.0001)
    labels = torch.tensor([0., 1., 0., 1.])
    loss = binary_focal_loss_with_logits(head(features), labels).mean()
    optimizer.zero_grad(set_to_none=True)
    loss.backward()
    assert all(p.grad is None for p in model.parameters())
    optimizer.step()
    assert all(torch.equal(original[k], v) for k, v in model.state_dict().items())
    subject.install_confidence_head(model, head)
    subject.verify_frozen_geometry(model, original)
    with torch.no_grad():
        after = model(inputs)
    assert torch.equal(before[:, 1:], after[:, 1:])
    assert not torch.equal(before[:, 0], after[:, 0])
    assert torch.equal(subject.frozen_features(model, inputs), features)


@pytest.mark.parametrize("defect", ["model_mode", "child_mode", "parameter"])
def test_feature_extraction_rejects_any_unfrozen_layer_or_normalization(defect):
    model = ScaleClassifierNet()
    subject.freeze_for_confidence_adaptation(model)
    if defect == "model_mode":
        model.train()
    elif defect == "child_mode":
        model.ink_tower[1].train()
    else:
        model.head[1].weight.requires_grad_(True)
    with pytest.raises(ValueError, match="must remain frozen"):
        subject.frozen_features(model, patches())


@pytest.mark.parametrize("name,index", [
    ("head.3.weight", (1, 0)), ("head.3.bias", (3,)),
    ("ink_tower.0.weight", (0, 0, 0, 0)),
    ("ink_tower.1.running_mean", (0,)), ("ink_tower.1.num_batches_tracked", ()),
])
def test_state_audit_detects_geometry_feature_or_normalization_mutation(name, index):
    model = ScaleClassifierNet()
    subject.freeze_for_confidence_adaptation(model)
    original = snapshot(model)
    with torch.no_grad():
        model.state_dict()[name][index].add_(1)
    with pytest.raises(ValueError, match="Frozen feature or geometry state changed"):
        subject.verify_frozen_geometry(model, original)


@pytest.mark.parametrize("defect", ["nan", "infinite", "dimensions", "dtype"])
def test_bad_confidence_parameters_are_rejected_without_modifying_model(defect):
    model = ScaleClassifierNet()
    head = subject.freeze_for_confidence_adaptation(model)
    original = snapshot(model)
    if defect in ("nan", "infinite"):
        with torch.no_grad():
            head.bias.fill_(float('nan') if defect == 'nan' else float('inf'))
    elif defect == "dimensions":
        head.weight = torch.nn.Parameter(torch.zeros(2, 64))
    else:
        head.double()
    with pytest.raises(ValueError, match="finite head contract"):
        subject.install_confidence_head(model, head)
    assert all(torch.equal(original[k], v) for k, v in model.state_dict().items())


def test_current_threshold_includes_low_confidence_proposals_without_changing_legacy_result(monkeypatch):
    scene = SimpleNamespace(tensor=torch.zeros(3, 48, 48))
    coordinates = np.array([[10., 10.], [11., 10.], [30., 30.]], dtype=np.float32)
    values = np.array([[.8, 0, 0, 2.5], [.15, 0, 0, 2.5], [.15, 0, 0, 2.5]], dtype=np.float32)
    monkeypatch.setattr(evaluation, 'balanced_center_support', lambda *args: True)
    legacy = evaluation.predictions(scene, coordinates, values, None, balanced=True)
    current = evaluation.predictions(scene, coordinates, values, None, balanced=True, threshold=.1)
    assert len(legacy) == 1 and len(current) == 2
    assert tuple(p for p in current if p.confidence >= .25) == legacy
    assert [(p.x, p.y) for p in current] == [(10., 10.), (30., 30.)]


@pytest.mark.parametrize("threshold", [0, -1, 1.1, float('nan'), float('inf')])
def test_invalid_operating_point_is_rejected_before_loading_inputs(threshold):
    with pytest.raises(ValueError, match="Operating threshold"):
        evaluation.evaluate(None, None, None, None, None, None, operating_threshold=threshold)
