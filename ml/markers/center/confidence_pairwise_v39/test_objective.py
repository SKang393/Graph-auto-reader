# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from __future__ import annotations

import pytest
import torch
from torch.nn import functional as F

from .objective import PAIRWISE_MARGIN_LOGITS, synthetic_confidence_loss


def test_pairwise_term_rewards_correct_ordering_and_keeps_bce_calibration() -> None:
    labels = torch.tensor([1, 0], dtype=torch.int64)
    groups = torch.tensor([4, 4])
    ordered = torch.tensor([1.0, -1.0], requires_grad=True)
    reversed_logits = torch.tensor([-1.0, 1.0], requires_grad=True)

    ordered_loss = synthetic_confidence_loss(ordered, labels, groups)
    reversed_loss = synthetic_confidence_loss(reversed_logits, labels, groups)
    ordered_pair = F.softplus(PAIRWISE_MARGIN_LOGITS - (ordered[0] - ordered[1]))
    expected = F.binary_cross_entropy_with_logits(ordered, labels.float()) + ordered_pair

    assert torch.allclose(ordered_loss, expected)
    assert ordered_loss < reversed_loss


def test_pairwise_gradient_pushes_positive_up_and_negative_down() -> None:
    logits = torch.tensor([0.0, 0.0], requires_grad=True)
    loss = synthetic_confidence_loss(logits, torch.tensor([1, 0]), torch.tensor([1, 1]))
    loss.backward()

    assert logits.grad is not None
    assert logits.grad[0] < 0
    assert logits.grad[1] > 0


@pytest.mark.parametrize("labels", [torch.tensor([1, 1]), torch.tensor([0, 0])])
def test_single_class_batch_falls_back_to_bce(labels: torch.Tensor) -> None:
    logits = torch.tensor([-0.5, 0.75])
    actual = synthetic_confidence_loss(logits, labels, torch.tensor([1, 1]))
    expected = F.binary_cross_entropy_with_logits(logits, labels.float())

    assert torch.allclose(actual, expected)


@pytest.mark.parametrize(
    ("logits", "labels"),
    [
        (torch.tensor([[0.0, 1.0]]), torch.tensor([1, 0])),
        (torch.tensor([0.0, 1.0]), torch.tensor([1])),
        (torch.tensor([0.0, float("nan")]), torch.tensor([1, 0])),
        (torch.tensor([0.0, 1.0]), torch.tensor([1, 2])),
        (torch.tensor([0.0, 1.0]), torch.tensor([1 + 0j, 0 + 0j])),
    ],
)
def test_invalid_batch_is_rejected(logits: torch.Tensor, labels: torch.Tensor) -> None:
    with pytest.raises(ValueError):
        synthetic_confidence_loss(logits, labels, torch.zeros(labels.numel(), dtype=torch.int64))


def test_pairwise_terms_never_cross_authenticated_report_groups() -> None:
    logits = torch.tensor([5.0, -5.0])
    labels = torch.tensor([1, 0])
    groups = torch.tensor([0, 1])

    actual = synthetic_confidence_loss(logits, labels, groups)
    expected = F.binary_cross_entropy_with_logits(logits, labels.float())

    assert torch.allclose(actual, expected)


def test_groupwise_pairwise_ignores_cross_group_extremes() -> None:
    logits = torch.tensor([1.0, -1.0, -20.0, 20.0])
    labels = torch.tensor([1, 0, 1, 0])
    groups = torch.tensor([0, 0, 1, 1])
    within_group = torch.stack((
        F.softplus(PAIRWISE_MARGIN_LOGITS - (logits[0] - logits[1])),
        F.softplus(PAIRWISE_MARGIN_LOGITS - (logits[2] - logits[3])),
    )).mean()

    actual = synthetic_confidence_loss(logits, labels, groups)
    expected = F.binary_cross_entropy_with_logits(logits, labels.float()) + within_group

    assert torch.allclose(actual, expected)
