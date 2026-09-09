# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from types import SimpleNamespace

import numpy as np
import torch

from ml.markers.center.line_aware_v1.pipeline import MarkerPrediction, ProposalBatch
from ml.markers.center.plot_domain_v25.diagnose_dev import (
    Candidate,
    PostprocessTrace,
    Primitive,
    Suppression,
    _postprocess_trace,
    categorize_missed_truths,
    decoded_radius_bin,
    distance_bin,
    greedy_match_pairs,
    greedy_matches,
    nearest_primitive,
    radius_bin,
    summarize_radius_and_nms,
)
from ml.markers.center.plot_domain_v25 import diagnose_dev as subject


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


def test_radius_strata_preserve_truth_denominator_and_label_false_positive_by_nearest_truth() -> None:
    scene = SimpleNamespace(
        centers=((10.0, 10.0), (30.0, 10.0), (50.0, 10.0)),
        diameters=(4.0, 10.0, 20.0),
    )
    accepted = (
        Candidate(0, MarkerPrediction(10.0, 10.0, 2.5, 0.9), 1.5),
        Candidate(1, MarkerPrediction(30.0, 10.0, 5.0, 0.8), 5.0),
        Candidate(2, MarkerPrediction(70.0, 10.0, 8.0, 0.7), 9.0),
    )
    trace = PostprocessTrace(accepted, accepted, ())
    pairs = greedy_match_pairs(accepted, scene.centers)

    report = summarize_radius_and_nms(scene, trace, pairs)

    assert report["truth_outcomes_by_ground_truth_radius"] == {
        "false_negative|above_decoder_maximum": 1,
        "true_positive|below_decoder_minimum": 1,
        "true_positive|within_decoder_range": 1,
    }
    assert report["prediction_outcomes_by_nearest_truth_and_decoded_radius"] == {
        "false_positive|nearest_truth_above_decoder_maximum|clamped_to_maximum": 1,
        "true_positive|nearest_truth_below_decoder_minimum|clamped_to_minimum": 1,
        "true_positive|nearest_truth_within_decoder_range|not_clamped": 1,
    }
    assert radius_bin(8.01) == "above_decoder_maximum"
    assert decoded_radius_bin(8.01) == "clamped_to_maximum"


def test_trace_records_actual_nms_suppressor_and_compares_confidence_to_localization(
    monkeypatch,
) -> None:
    scene = SimpleNamespace(
        tensor=torch.zeros((3, 40, 40), dtype=torch.float32),
        centers=((10.0, 10.0),),
        diameters=(20.0,),
    )
    proposals = ProposalBatch(
        torch.zeros((3, 3, 33, 33)),
        torch.tensor(((10.0, 10.0), (12.0, 10.0), (16.0, 10.0))),
    )
    output = np.asarray(
        ((0.60, 0.0, 0.0, 12.0), (0.70, 0.0, 0.0, 10.0), (0.90, 0.0, 0.0, 9.0)),
        dtype=np.float32,
    )
    monkeypatch.setattr(subject, "_consensus", lambda *_: True)
    monkeypatch.setattr(subject.v24, "_consensus", lambda *_: True)

    trace = _postprocess_trace(scene, proposals, output, None)
    report = summarize_radius_and_nms(scene, trace, ())
    nms = report["nms_false_negatives"]

    assert [item.source_proposal_index for item in trace.accepted] == [2]
    assert {
        (item.suppressed.source_proposal_index, item.suppressor.source_proposal_index)
        for item in trace.suppressions
    } == {(0, 2), (1, 2)}
    assert nms["counts"] == {
        "false_negative_truths": 1,
        "highest_and_lowest_share_actual_suppressor": 1,
        "highest_confidence_candidate_suppressed": 1,
        "lowest_error_candidate_suppressed": 1,
    }
    assert nms["actual_suppressor_prediction_outcome"] == {"false_positive": 1}
    assert nms["ground_truth_radius_bins"] == {"above_decoder_maximum": 1}
    assert nms["lowest_error_candidate_distance_px"]["maximum"] == 0.0
    assert nms["highest_confidence_candidate_distance_px"]["maximum"] == 2.0
    assert nms["actual_suppressor_distance_to_truth_px"]["minimum"] == 6.0
    assert nms["actual_suppressor_error_penalty_px"]["minimum"] == 6.0
    assert nms["suppressed_to_actual_suppressor_distance_px"]["minimum"] == 6.0
    assert nms["applicable_nms_exclusion_distance_px"]["minimum"] == 10.0
