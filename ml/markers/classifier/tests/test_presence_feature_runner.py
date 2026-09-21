# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import numpy as np
import pytest
import torch

from ml.markers.classifier.native_context_v3.runner import loss_terms as previous_loss
from ml.markers.classifier.presence_feature_v7.runner import (
    INITIALIZER_SHA256, RECIPE, loss_terms, training_tensors, validate_config)


@pytest.mark.parametrize("change", [{"private_reads": 1}, {"sealed_runs_authorized": 1},
                                   {"epochs": 240}, {"checkpoint_selection": "best_dev"}])
def test_authorization_cannot_widen(change):
    with pytest.raises(ValueError):
        validate_config({**RECIPE, "source_checkpoint_sha256": INITIALIZER_SHA256,
                         "private_reads": 0, "sealed_runs_authorized": 0, **change})


def outputs(count=3):
    generator = torch.Generator().manual_seed(7)
    return tuple(torch.randn(count, width, generator=generator, requires_grad=True) for width in (9, 3, 1, 16))


def targets():
    return (torch.eye(9)[:3], torch.tensor([0, 1, 2]), torch.tensor([0., 0., 1.]),
            torch.tensor([True, True, False]))


def test_all_known_batch_exactly_preserves_previous_loss():
    predicted = outputs()
    current, terms = loss_terms(predicted, *targets(), torch.ones(3, dtype=torch.bool))
    old, old_terms = previous_loss(predicted, *targets())
    assert torch.equal(current, old)
    assert all(torch.equal(a, b) for a, b in zip(terms, old_terms, strict=True))


def test_presence_only_rows_never_supervise_shape_fill_or_embedding():
    predicted = outputs()
    shape, fill, artifact, unambiguous = targets()
    fill[:] = -1
    loss, terms = loss_terms(predicted, shape, fill, artifact, unambiguous, torch.zeros(3, dtype=torch.bool))
    loss.backward()
    assert torch.isfinite(loss)
    assert all(float(terms[i].detach()) == 0 for i in (0, 1, 3))
    for index in (0, 1, 3):
        assert predicted[index].grad is not None
        assert torch.count_nonzero(predicted[index].grad) == 0
    assert torch.count_nonzero(predicted[2].grad) == 3


def test_mixed_batch_masks_unknown_targets_but_keeps_presence_gradient():
    predicted = outputs()
    shape, fill, artifact, unambiguous = targets()
    fill[1] = -1
    loss, _ = loss_terms(predicted, shape, fill, artifact, unambiguous, torch.tensor([True, False, True]))
    loss.backward()
    assert torch.count_nonzero(predicted[0].grad[0]) > 0
    assert torch.count_nonzero(predicted[0].grad[1]) == 0
    assert torch.count_nonzero(predicted[1].grad[1]) == 0
    assert torch.count_nonzero(predicted[3].grad[1]) == 0
    assert predicted[2].grad[1] != 0


def test_tensor_extension_keeps_existing_targets_and_marks_unknowns():
    known = [{"artifact": False, "shape_target_indices": [0], "fill_target_index": 1, "shape_label_ambiguous": False}]
    presence = [{"artifact": False, "split": "train", "target_kind": "presence_only", "shape": None, "fill": None}]
    pixels = np.zeros((1, 1, 32, 32), dtype=np.float32)
    result = training_tensors(pixels, known, pixels, presence)
    assert len(result[0]) == 2
    assert result[1][0, 0] == 1 and result[1][1].sum() == 0
    assert result[2].tolist() == [1, -1]
    assert result[-1].tolist() == [True, False]
    assert result[-2].tolist() == [True, False]
    for mutation in ({"split": "dev"}, {"shape": "square"}, {"fill_target_index": 0}):
        with pytest.raises(ValueError):
            training_tensors(pixels, known, pixels, [{**presence[0], **mutation}])
