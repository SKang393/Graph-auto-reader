# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from types import SimpleNamespace

import numpy as np
import torch

from ml.markers.center.line_aware_v1.pipeline import MarkerPrediction, ProposalBatch
from ml.markers.center.localization_confidence_v26.diagnose_suppressor_anchors import (
    _anchor_context,
    _nms_records,
    _suppressor_cross_tabs,
)
from ml.markers.center.plot_domain_v25.diagnose_dev import Candidate, PostprocessTrace, Suppression


def _scene() -> SimpleNamespace:
    return SimpleNamespace(
        centers=((10.0, 10.0), (40.0, 40.0)),
        diameters=(10.0, 18.0),
    )


def test_anchor_context_uses_nearest_truth_training_assignment() -> None:
    scene = _scene()
    proposals = torch.tensor(((12.0, 10.0), (35.0, 40.0)), dtype=torch.float32)
    output = np.asarray(((0.7, -0.5, 0.0, 5.0), (0.8, 0.0, 0.0, 8.0)), dtype=np.float32)

    positive = _anchor_context(
        scene, proposals, output,
        Candidate(0, MarkerPrediction(10.0, 10.0, 5.0, 0.7), 5.0), 0,
    )
    negative = _anchor_context(
        scene, proposals, output,
        Candidate(1, MarkerPrediction(35.0, 40.0, 8.0, 0.8), 8.0), 1,
    )

    assert positive["training_anchor_relation"] == "positive_for_missed_truth"
    assert positive["decoded_offset_px"] == {"x": -2.0, "y": 0.0}
    assert negative["training_anchor_relation"] == "negative"


def test_nms_record_names_actual_first_suppressor_and_prediction_outcome() -> None:
    scene = _scene()
    proposals = ProposalBatch(
        torch.zeros((3, 3, 33, 33)),
        torch.tensor(((10.0, 10.0), (16.0, 10.0), (40.0, 40.0))),
    )
    output = np.asarray(
        ((0.6, 0.0, 0.0, 5.0), (0.9, 0.0, 0.0, 6.0), (0.8, 0.0, 0.0, 8.0)),
        dtype=np.float32,
    )
    suppressed = Candidate(0, MarkerPrediction(10.0, 10.0, 5.0, 0.6), 5.0)
    suppressor = Candidate(1, MarkerPrediction(16.0, 10.0, 6.0, 0.9), 6.0)
    other = Candidate(2, MarkerPrediction(40.0, 40.0, 8.0, 0.8), 8.0)
    trace = PostprocessTrace(
        (suppressed, suppressor, other),
        (suppressor, other),
        (Suppression(suppressed, suppressor, 6.0, 7.5),),
    )

    records, labels, outcomes = _nms_records(
        "fixture", "fixture:1", scene, proposals, output, trace, ((1, 1),)
    )

    assert len(records) == 1
    assert records[0]["lowest_error_candidate"]["training_anchor_relation"] == "positive_for_missed_truth"
    assert records[0]["actual_suppressor"]["training_anchor_relation"] == "negative"
    assert records[0]["actual_suppressor_prediction_outcome"] == "false_positive"
    assert labels == {"negative": 1}
    assert outcomes == {"false_positive": 1}


def test_split_aggregate_crosses_fp_suppressor_relation_with_radius() -> None:
    scene = _scene()
    proposals = ProposalBatch(
        torch.zeros((3, 3, 33, 33)),
        torch.tensor(((10.0, 10.0), (16.0, 10.0), (40.0, 40.0))),
    )
    output = np.asarray(
        ((0.6, 0.0, 0.0, 5.0), (0.9, 0.0, 0.0, 6.0), (0.8, 0.0, 0.0, 8.0)),
        dtype=np.float32,
    )
    suppressed = Candidate(0, MarkerPrediction(10.0, 10.0, 5.0, 0.6), 5.0)
    suppressor = Candidate(1, MarkerPrediction(16.0, 10.0, 6.0, 0.9), 6.0)
    other = Candidate(2, MarkerPrediction(40.0, 40.0, 8.0, 0.8), 8.0)
    trace = PostprocessTrace(
        (suppressed, suppressor, other), (suppressor, other),
        (Suppression(suppressed, suppressor, 6.0, 7.5),),
    )
    records, _, _ = _nms_records(
        "fixture", "fixture:1", scene, proposals, output, trace, ((1, 1),)
    )
    report = _suppressor_cross_tabs(records)

    assert report["false_positive_suppressor_by_anchor_relation_and_missed_truth_radius"] == {
        "negative|within_decoder_range": 1
    }
    assert report["false_positive_suppressor_by_anchor_relation_and_output_radius"] == {
        "negative|not_clamped": 1
    }
