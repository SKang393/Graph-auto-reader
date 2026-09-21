# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.center.cascade_component_diagnostic import decode, retain
from ml.markers.center.shape_coverage_evaluation import predictions


def sample():
    ink = torch.zeros((3, 40, 40))
    ink[0, 12:19, 12:19] = 1
    ink[0, 25:32, 25:32] = 1
    scene = SimpleNamespace(tensor=ink)
    coordinates = np.array([[15, 15], [16, 15], [28, 28]], np.float32)
    output = np.array([[.9, 0, 0, 3], [.8, -.25, 0, 3], [.15, 0, 0, 3]], np.float32)
    return scene, coordinates, output


def test_lower_score_replay_preserves_geometry_nms_and_historical_decoder():
    scene, coordinates, output = sample()
    assert decode(scene, coordinates, output, None, threshold=.25)[2] == predictions(
        scene, coordinates, output, None, balanced=True)
    decoded, geometric, accepted = decode(scene, coordinates, output, None)
    assert len(decoded) == len(geometric) == 3
    assert [(p.x, p.y) for p in accepted] == [(15, 15), (28, 28)]
    assert accepted[1].confidence == float(output[2, 0])


def test_plot_domain_remains_a_hard_boundary():
    scene, coordinates, output = sample()
    domain = SimpleNamespace(contains=lambda x, y: x < 20)
    assert len(decode(scene, coordinates, output, domain)[2]) == 1


@pytest.mark.parametrize("column,value", [(0, float("nan")), (0, 1.1), (1, float("inf"))])
def test_invalid_cached_values_fail_closed(column, value):
    scene, coordinates, output = sample()
    output[0, column] = value
    with pytest.raises(ValueError):
        decode(scene, coordinates, output, None)


def test_classifier_rejection_uses_packed_artifact_probability_and_exact_boundary():
    output = np.zeros((3, 25), np.float32)
    output[:, 0] = output[:, 9] = 1
    output[:, 12] = [.499, .5, .501]
    assert retain(("a", "b", "c"), output) == ("a",)
    output[0, 12] = float("nan")
    with pytest.raises(ValueError):
        retain(("a", "b", "c"), output)


def test_malformed_classifier_distribution_cannot_be_accepted():
    with pytest.raises(ValueError):
        retain(("a",), np.zeros((1, 25), np.float32))
    assert retain((), np.empty((0, 25), np.float32)) == ()
