# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from contextlib import nullcontext

import pytest
import torch
from torch import nn

from ml.markers.center.confidence_reflection_v37.augmentation import TRANSFORMS, reflect_patches
from ml.markers.center.confidence_residual_v38.features import append_residual_features
from ml.markers.center.confidence_spatial_v35.model import frozen_spatial_features


class Budget:
    def work_block(self):
        return nullcontext()


class Base(nn.Module):
    def __init__(self):
        super().__init__()
        self.ink_tower = nn.Sequential(nn.Flatten(), nn.Linear(1089, 384))
        self.mask_tower = nn.Sequential(nn.Flatten(), nn.Linear(2178, 384))
        self.head = nn.Sequential(nn.Identity())
        self.eval().requires_grad_(False)


def test_retains_rows_and_appends_four_exact_reflection_feature_groups():
    torch.manual_seed(10)
    base = Base()
    patches = torch.rand(2, 3, 33, 33)
    payload = {"patches": patches, "labels": torch.zeros(2), "hard_negative": torch.ones(2)}
    training = (torch.rand(3, 768), torch.tensor([1., 0., 1.]), torch.tensor([0., 1., 0.]))
    result = append_residual_features(training, payload, base, Budget())
    assert result[0].shape == (11, 768)
    for actual, original in zip(result, training, strict=True):
        assert torch.equal(actual[:3], original)
    for index, transform in enumerate(TRANSFORMS):
        expected = frozen_spatial_features(base, reflect_patches(patches, transform))
        assert torch.equal(result[0][3 + index * 2:5 + index * 2], expected)
    assert torch.equal(result[1][3:], torch.zeros(8))
    assert torch.equal(result[2][3:], torch.ones(8))
    assert not result[0].requires_grad


def test_rejects_positive_or_unfrozen_base():
    base = Base()
    training = (torch.zeros(1, 768), torch.zeros(1), torch.zeros(1))
    payload = {"patches": torch.zeros(1, 3, 33, 33), "labels": torch.ones(1), "hard_negative": torch.ones(1)}
    with pytest.raises(ValueError):
        append_residual_features(training, payload, base, Budget())
    payload["labels"] = torch.zeros(1)
    base.train()
    with pytest.raises(ValueError):
        append_residual_features(training, payload, base, Budget())
