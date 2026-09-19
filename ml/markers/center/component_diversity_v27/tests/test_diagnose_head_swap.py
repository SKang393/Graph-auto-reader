# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import numpy as np
import pytest

from ml.markers.center.component_diversity_v27 import diagnose_head_swap as subject


def test_hybrid_outputs_swap_only_confidence_or_geometry() -> None:
    v26 = np.asarray([
        [0.20, 1.0, 2.0, 3.0],
        [0.30, 4.0, 5.0, 6.0],
    ], dtype=np.float32)
    v27 = np.asarray([
        [0.70, 7.0, 8.0, 9.0],
        [0.80, 10.0, 11.0, 12.0],
    ], dtype=np.float32)
    original_v26 = v26.copy()
    original_v27 = v27.copy()

    outputs = subject._hybrid_outputs(v26, v27)

    assert outputs["v26"] is v26
    assert outputs["v27"] is v27
    np.testing.assert_array_equal(
        outputs["v27_confidence_v26_geometry"][:, 0], v27[:, 0]
    )
    np.testing.assert_array_equal(
        outputs["v27_confidence_v26_geometry"][:, 1:], v26[:, 1:]
    )
    np.testing.assert_array_equal(
        outputs["v26_confidence_v27_geometry"][:, 0], v26[:, 0]
    )
    np.testing.assert_array_equal(
        outputs["v26_confidence_v27_geometry"][:, 1:], v27[:, 1:]
    )
    np.testing.assert_array_equal(v26, original_v26)
    np.testing.assert_array_equal(v27, original_v27)


def test_paired_truth_transitions_are_exact_and_conservative() -> None:
    assert subject._paired_transitions({0, 1, 4}, {1, 2, 4}, 6) == {
        "true_positive_to_true_positive": 2,
        "true_positive_to_false_negative": 1,
        "false_negative_to_true_positive": 1,
        "false_negative_to_false_negative": 2,
    }
    with pytest.raises(RuntimeError, match="index is invalid"):
        subject._paired_transitions({6}, set(), 6)


def test_max_positive_margin_uses_nearest_truth_and_fixed_radius() -> None:
    coordinates = np.asarray([
        [0.0, 0.0],
        [2.0, 0.0],
        [10.0, 0.0],
        [14.0, 0.0],
    ], dtype=np.float32)
    centers = np.asarray([
        [0.0, 0.0],
        [10.0, 0.0],
        [30.0, 0.0],
    ], dtype=np.float32)
    output = np.asarray([
        [0.10, 0.0, 0.0, 1.0],
        [0.40, 0.0, 0.0, 1.0],
        [0.20, 0.0, 0.0, 1.0],
        [0.90, 0.0, 0.0, 1.0],
    ], dtype=np.float32)

    margins = subject._max_positive_margins(coordinates, centers, output)

    assert margins[:2].tolist() == pytest.approx([0.15, -0.05])
    assert np.isnan(margins[2])
    assert subject._margin_report(margins) == {
        "truth_count": 3,
        "truths_without_positive_anchor": 1,
        "truths_at_or_above_threshold": 1,
        "truths_below_threshold": 1,
        "max_positive_confidence_minus_0_25": {
            "count": 2,
            "minimum": pytest.approx(-0.05),
            "p05": pytest.approx(-0.04),
            "median": pytest.approx(0.05),
            "p95": pytest.approx(0.14),
            "maximum": pytest.approx(0.15),
        },
    }


def test_exact_original_metrics_bind_both_full_dev_splits() -> None:
    for split in ("component", "family"):
        for model_identity in ("v26", "v27"):
            expected = subject.EXPECTED[split]
            counts = {
                "scene_count": expected["scene_count"],
                "truth_count": expected["truth_count"],
                "proposal_count": expected["proposal_count"],
                **expected[model_identity],
            }
            subject.source_diagnostic._require_exact_metrics(
                split, model_identity, counts
            )


def test_cached_proposals_preserve_coordinates_without_patches() -> None:
    coordinates = np.asarray([[4.0, 8.0], [12.0, 16.0]], dtype=np.float32)

    proposals = subject._cached_proposals(coordinates)

    assert tuple(proposals.patches.shape) == (2, 0)
    np.testing.assert_array_equal(proposals.coordinates.numpy(), coordinates)


def test_self_test() -> None:
    subject.self_test()
