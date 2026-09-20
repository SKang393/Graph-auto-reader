# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from contextlib import nullcontext
from dataclasses import replace
from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.center.shape_coverage_v28.runner import train_epochs, RECIPE
from ml.markers.center.shape_coverage_evaluation import predictions, match_predictions
from ml.markers.center.line_aware_v1.pipeline import ProposalBatch, MarkerPrediction
from ml.markers.center.mask_preserving_v24.mask_preserving import postprocess


class TinyHead(torch.nn.Module):
    def __init__(self):
        super().__init__()
        self.head = torch.nn.Linear(2, 4)

    def forward_raw(self, value):
        return self.head(value)


def test_runtime_predictions_use_the_frozen_one_to_one_matcher():
    items = (MarkerPrediction(10., 10., 4., .9), MarkerPrediction(12., 10., 4., .8))
    assert match_predictions(items, ((10., 10.), (40., 40.))) == ((0, 0),)
    assert match_predictions((), ((10., 10.),)) == ()


def test_epoch_recovery_matches_uninterrupted_training(tmp_path):
    torch.manual_seed(901)
    first = TinyHead()
    state = {k: v.clone() for k, v in first.state_dict().items()}
    training = (torch.rand(6, 2), torch.tensor([1., 0., 1., 0., 1., 0.]),
                torch.zeros(6, 2), torch.ones(6)*4, torch.zeros(6))
    recipe = RECIPE | {"epochs": 2, "batch_size": 2}
    binding = {"fixed": "test-recovery"}
    full, part, resumed = (tmp_path/name for name in ("full", "part", "resumed"))
    for path in (full, part, resumed):
        path.mkdir()
    budget = SimpleNamespace(work_block=nullcontext)
    optimizer = lambda m: torch.optim.AdamW(m.parameters(), lr=.001)
    expected = train_epochs(first, optimizer(first), training, recipe, binding, full, budget)
    second = TinyHead(); second.load_state_dict(state)
    train_epochs(second, optimizer(second), training, recipe | {"epochs": 1}, binding, part, budget)
    third = TinyHead()
    actual = train_epochs(third, optimizer(third), training, recipe, binding, resumed, budget, resume=part/"recovery.pt")
    assert actual["resumed_from_epoch"] == 1
    assert actual["optimizer_steps"] == expected["optimizer_steps"] == 6
    assert actual["history"] == expected["history"]
    for name, value in first.state_dict().items():
        assert torch.equal(value, third.state_dict()[name])
    with pytest.raises(ValueError, match="different training"):
        train_epochs(third, optimizer(third), training, recipe, {"fixed": "changed"}, resumed, budget, resume=part/"recovery.pt")


@pytest.mark.parametrize("seed", [19, 71, 101])
def test_frozen_postprocess_is_reproduced(seed):
    rng = np.random.default_rng(seed)
    tensor = torch.from_numpy(rng.random((3, 64, 64), dtype=np.float32))
    coordinates = rng.integers(4, 60, (40, 2)).astype(np.float32)
    outputs = rng.random((40, 4), dtype=np.float32)
    outputs[:, 1:3] -= .5
    outputs[:, 3] = outputs[:, 3]*5.5+2.5
    scene = SimpleNamespace(tensor=tensor)
    proposals = ProposalBatch(torch.empty(40, 3, 33, 33), torch.from_numpy(coordinates))
    expected = postprocess(scene, proposals, outputs)
    actual = predictions(scene, coordinates, outputs, None, balanced=False)
    np.testing.assert_allclose([[p.x, p.y, p.radius, p.confidence] for p in actual],
                               [[p.x, p.y, p.radius, p.confidence] for p in expected], atol=5e-6, rtol=0)
