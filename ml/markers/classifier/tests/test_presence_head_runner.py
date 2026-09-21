# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from contextlib import nullcontext

import pytest
import torch

from ml.markers.classifier.model import CompactMarkerClassifier
from ml.markers.classifier.presence_head_v6.runner import (
    INITIALIZER_SHA256, RECIPE, configure_head_only, fit_head, frozen_state, validate_config)


def config():
    return {**RECIPE, "source_checkpoint_sha256": INITIALIZER_SHA256,
            "private_reads": 0, "sealed_runs_authorized": 0}


@pytest.mark.parametrize("change", [{"private_reads": 1}, {"sealed_runs_authorized": 1},
                                   {"maximum_iterations": 128}, {"trainable_parameters": "all"}])
def test_authorization_cannot_widen(change):
    with pytest.raises(ValueError):
        validate_config({**config(), **change})


def test_configuration_freezes_encoder_shape_fill_and_embedding():
    model = CompactMarkerClassifier()
    before = frozen_state(model)
    configure_head_only(model)
    assert not model.training and not model.projection.training
    assert {n for n, p in model.named_parameters() if p.requires_grad} == {
        "artifact_head.weight", "artifact_head.bias"}
    with torch.no_grad():
        model.artifact_head.bias.add_(1)
    assert frozen_state(model) == before
    with torch.no_grad():
        model.shape_head.bias.add_(1)
    assert frozen_state(model) != before


def test_linear_fit_uses_only_presence_targets_and_preserves_other_weights():
    # A two-feature toy classifier tests optimization plumbing, not a model run.
    class Toy(torch.nn.Module):
        def __init__(self):
            super().__init__()
            self.artifact_head = torch.nn.Linear(2, 1)
            self.shape_head = torch.nn.Linear(2, 3)
    class Budget:
        def work_block(self):
            return nullcontext()
    model = Toy()
    with torch.no_grad():
        model.artifact_head.weight.zero_(); model.artifact_head.bias.zero_()
    configure_head_only(model)
    before = frozen_state(model)
    features = torch.tensor([[-1., 0.], [-2., 0.], [1., 0.], [2., 0.]])
    labels = torch.tensor([0., 0., 1., 1.])
    fit = fit_head(model, features, labels, Budget())
    assert 0 < fit["iterations"] <= RECIPE["maximum_iterations"]
    assert torch.equal((model.artifact_head(features).flatten() >= 0).float(), labels)
    assert frozen_state(model) == before
    with pytest.raises(ValueError):
        fit_head(model, features, torch.zeros(4), Budget())
