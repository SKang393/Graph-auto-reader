# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import pytest
import torch

from ml.markers.center.confidence_reflection_v37.augmentation import TRANSFORMS, reflect_patches


@pytest.mark.parametrize('transform', TRANSFORMS)
def test_reflection_keeps_planes_aligned_and_is_its_own_inverse(transform):
    patches = torch.zeros(2, 3, 33, 33)
    patches[:, 0, 9, 21] = .2
    patches[:, 1, 9, 21] = .6
    patches[:, 2, 9, 21] = 1.
    patches[:, :, 16, 16] = .4
    original = patches.clone()
    result = reflect_patches(patches, transform)
    y = 32-9 if transform in ('vertical', 'both') else 9
    x = 32-21 if transform in ('horizontal', 'both') else 21
    torch.testing.assert_close(result[:, :, y, x], patches[:, :, 9, 21], rtol=0, atol=0)
    torch.testing.assert_close(result[:, :, 16, 16], patches[:, :, 16, 16], rtol=0, atol=0)
    assert torch.equal(reflect_patches(result, transform), original)
    result.zero_()
    assert torch.equal(patches, original)


@pytest.mark.parametrize('transform', TRANSFORMS)
def test_reflection_preserves_anchor_distance_and_confidence_label(transform):
    # Every point, including neighbors, receives the same reflection. Therefore
    # both nearest-truth selection and the existing three-pixel label survive.
    centers = torch.tensor([[16., 16.], [18., 18.], [19., 17.], [3., 27.]])
    reflected = centers.clone()
    if transform in ('horizontal', 'both'):
        reflected[:, 0] = 32-reflected[:, 0]
    if transform in ('vertical', 'both'):
        reflected[:, 1] = 32-reflected[:, 1]
    before = torch.linalg.vector_norm(centers-16, dim=1)
    after = torch.linalg.vector_norm(reflected-16, dim=1)
    assert torch.equal(before, after)
    assert torch.equal(before <= 3., after <= 3.)


@pytest.mark.parametrize('defect', ['shape', 'dtype', 'nan', 'below_zero', 'above_one', 'transform'])
def test_invalid_reflection_input_is_rejected(defect):
    value = torch.zeros(2, 3, 33, 33)
    transform = 'horizontal'
    if defect == 'shape':
        value = value[:, :, :, :-1]
    elif defect == 'dtype':
        value = value.double()
    elif defect == 'transform':
        transform = 'random'
    else:
        value[0, 0, 0, 0] = {'nan': float('nan'), 'below_zero': -.1, 'above_one': 1.1}[defect]
    with pytest.raises(ValueError):
        reflect_patches(value, transform)
