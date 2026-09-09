# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from types import SimpleNamespace

import numpy as np
import torch

from ml.markers.center.line_aware_v1.pipeline import MarkerPrediction, ProposalBatch
from ml.markers.center.plot_domain_v25.diagnose_dev import (
    Candidate,
    Primitive,
    categorize_missed_truths,
    distance_bin,
    greedy_matches,
    nearest_primitive,
)


def _scene() -> SimpleNamespace:
    tensor = torch.zeros((3, 40, 40), dtype=torch.float32)
    tensor[0, 8:14, 8:14] = 1.0
    return SimpleNamespace(tensor=tensor, centers=((10.0, 10.0),), hard_negatives=())


def test_greedy_matching_uses_one_prediction_and_truth_once() -> None:
    predictions = (
        Candidate(0, MarkerPrediction(10.0, 10.0, 4.0, 0.9)),
        Candidate(1, MarkerPrediction(11.0, 10.0, 4.0, 0.8)),
    )

    used_predictions, used_truths = greedy_matches(predictions, ((10.0, 10.0),))

    assert used_predictions == {0}
    assert used_truths == {0}


def test_missed_truth_distinguishes_classification_from_offset_and_nms() -> None:
    scene = _scene()
    proposals = ProposalBatch(
        torch.zeros((2, 3, 33, 33)),
        torch.tensor(((8.0, 8.0), (12.0, 12.0))),
    )
    below = np.asarray(((0.24, 0.0, 0.0, 4.0), (0.10, 0.0, 0.0, 4.0)), dtype=np.float32)
    causes, *_ = categorize_missed_truths(scene, proposals, below, (), set())
    assert causes == {"classification_below_threshold": 1}

    shifted = np.asarray(((0.8, -0.75, -0.75, 4.0), (0.1, 0.0, 0.0, 4.0)), dtype=np.float32)
    causes, *_ = categorize_missed_truths(scene, proposals, shifted, (), set())
    assert causes == {"offset_error": 1}

    accepted_elsewhere = (
        Candidate(1, MarkerPrediction(10.0, 10.0, 4.0, 0.9)),
    )
    near = np.asarray(((0.8, 0.5, 0.5, 4.0), (0.1, 0.0, 0.0, 4.0)), dtype=np.float32)
    causes, *_ = categorize_missed_truths(
        scene, proposals, near, accepted_elsewhere, set()
    )
    assert causes == {"greedy_assignment_competition": 1}


def test_primitive_proximity_and_bins_are_deterministic() -> None:
    primitives = (
        Primitive("axis", "x", ((0.0, 10.0), (30.0, 10.0)), False),
        Primitive("text", "tick", ((20.0, 20.0),), False),
    )

    assert nearest_primitive((8.0, 12.0), primitives) == ("axis", "x", 2.0)
    assert distance_bin(3.0) == "le3"
    assert distance_bin(5.0) == "gt3_le5"
    assert distance_bin(16.0) == "gt8_le16"
    assert distance_bin(float("inf")) == "gt16_or_unavailable"
