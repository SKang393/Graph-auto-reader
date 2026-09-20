# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import pytest
import torch

from ml.markers.center.real_range_generator_v1.generator import Scene
from ml.markers.center.plot_domain_v25.proposal_domain import PlotDomain
from ml.markers.center.shape_coverage_cache import pack_scene, unpack_scene, validate_rows


def test_dev_roundtrip_preserves_pixels_truth_and_plot_domain(tmp_path):
    scene = Scene("dev", "unit-fixture", 7, torch.zeros(3, 32, 32),
                  ((12.25, 13.5),), (8.,), (8.,), (("axis", 2., 3.),))
    domain = PlotDomain("runtime_plot_polygon", 32, 32,
                        ((2., 2.), (30., 2.), (30., 30.), (2., 30.)), "test")
    packed = pack_scene(scene, domain)
    path = tmp_path/"dev.pt"
    torch.save(packed, path)
    restored = unpack_scene(torch.load(path, weights_only=True))
    assert restored.scene.centers == [[12.25, 13.5]]
    assert restored.scene.hard_negatives == [["axis", 2., 3.]]
    assert restored.panel_domain.domain == domain
    assert torch.equal(restored.scene.tensor, scene.tensor)
    packed["tensor"][0, 0, 0] = 1
    with pytest.raises(ValueError, match="tensor changed"):
        unpack_scene(packed)


def test_train_scene_cannot_become_dev():
    scene = Scene("train", "unit-fixture", 7, torch.zeros(3, 32, 32), (), (), (), ())
    with pytest.raises(ValueError, match="development split"):
        unpack_scene(pack_scene(scene))


def test_historical_validation_identity_is_preserved_only_when_explicitly_expected():
    scene = Scene("validation", "unit-fixture", 7, torch.zeros(3, 32, 32), (), (), (), ())
    packed = pack_scene(scene)
    with pytest.raises(ValueError, match="development split"):
        unpack_scene(packed)
    assert unpack_scene(packed, expected_split="validation").split == "validation"


@pytest.mark.parametrize("defect", ["offset", "nan", "label", "count"])
def test_malformed_training_population_is_rejected(defect):
    values = (torch.ones(2, 3, 33, 33), torch.tensor([1., 0.]),
              torch.zeros(2, 2), torch.ones(2), torch.zeros(2))
    if defect == "offset":
        values[2][0, 0] = .76
    elif defect == "nan":
        values[0][1, 0, 2, 2] = float("nan")
    elif defect == "label":
        values[1][1] = .2
    with pytest.raises(ValueError):
        validate_rows(values, expected_count=3 if defect == "count" else 2)
