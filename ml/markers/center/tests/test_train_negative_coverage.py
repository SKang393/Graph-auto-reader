# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.center.train_negative_coverage import select_negative_indices, selected_rows


def scene(split="train"):
    return SimpleNamespace(split=split, centers=((16.,16.),),tensor=torch.ones(3,48,48))


def test_selection_keeps_all_clear_errors_but_not_near_truth_or_low_confidence():
    coordinates=np.array([[16,16],[24,16],[25,16],[28,16],[36,16],[22,22]],np.float32)
    outputs=np.array([[.9,0,0,3],[.9,0,0,3],[.25,0,0,3],[.1,0,0,3],[.8,0,0,3],[.9,-.75,-.75,3]],np.float32)
    # Last row decodes onto truth despite a far anchor and must never become a negative.
    assert select_negative_indices(scene(),coordinates,outputs).tolist()==[2,4]


@pytest.mark.parametrize("split",["dev","validation","sealed","real-dev","real-sealed"])
def test_non_training_pixels_are_rejected_before_mining(split):
    with pytest.raises(ValueError,match="train scenes only"):
        select_negative_indices(scene(split),np.array([[36,16]],np.float32),np.array([[.8,0,0,3]],np.float32))
    with pytest.raises(ValueError,match="Only train pixels"):
        selected_rows(scene(split),np.array([[36,16]],np.float32),np.array([0]))


@pytest.mark.parametrize("invalid",[float("nan"),float("inf"),-.1,1.1])
def test_non_finite_or_non_probability_scores_are_rejected(invalid):
    with pytest.raises(ValueError):
        select_negative_indices(scene(),np.array([[36,16]],np.float32),np.array([[invalid,0,0,3]],np.float32))


def test_selected_patch_preserves_all_channels_and_labels_only_background():
    source=scene();source.tensor=torch.arange(3*48*48,dtype=torch.float32).reshape(3,48,48)/(3*48*48)
    original=source.tensor.clone()
    values=selected_rows(source,np.array([[32,24]],np.float32),np.array([0]))
    expected=torch.zeros(3,33,33)
    expected[:,:,:32]=source.tensor[:,8:41,16:48]
    assert torch.equal(values[0][0],expected)
    assert values[1].tolist()==[0.] and values[4].tolist()==[1.]
    assert torch.equal(source.tensor,original)


@pytest.mark.parametrize("indices",[np.array([0,0]),np.array([-1]),np.array([1])])
def test_invalid_selection_indices_fail(indices):
    with pytest.raises(ValueError):
        selected_rows(scene(),np.array([[32,24]],np.float32),indices)


def test_independent_row_boundary_rejects_relabeling_a_real_marker():
    with pytest.raises(ValueError,match="cannot touch a truth anchor"):
        selected_rows(scene(),np.array([[16,16]],np.float32),np.array([0]))
