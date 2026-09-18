# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

from ml.markers.center.component_diversity_v27 import (
    diagnose_score_localization as subject,
)


def test_score_offset_attribution_separates_confidence_and_localization() -> None:
    coordinates = np.asarray([
        [0.0, 0.0],
        [1.0, 0.0],
        [10.0, 0.0],
        [20.0, 0.0],
    ], dtype=np.float32)
    centers = np.asarray([[0.0, 0.0], [10.0, 0.0]], dtype=np.float32)
    output = np.asarray([
        [0.10, 0.0, 0.0, 1.0],
        [0.30, 2.0, 0.0, 1.0],
        [0.30, 0.0, 0.0, 1.0],
        [0.40, 0.0, 0.0, 1.0],
    ], dtype=np.float32)

    counts, positive, scores, decoded_error = subject._score_offset_counts(
        coordinates, centers, output
    )

    assert counts == {
        "truths": 2,
        "proposals": 4,
        "positive_anchors": 3,
        "negative_anchors": 1,
        "positive_anchors_below_threshold": 1,
        "negative_anchors_above_threshold": 1,
        "positive_anchors_decoded_beyond_five_pixels": 1,
        "truths_without_localized_above_threshold_positive_anchor": 1,
        "truths_with_localized_above_threshold_positive_anchor": 1,
    }
    assert positive.tolist() == [True, True, True, False]
    assert scores.tolist() == pytest.approx([0.10, 0.30, 0.30, 0.40])
    assert decoded_error.tolist() == pytest.approx([0.0, 9.0, 0.0, 10.0])
    subject._require_score_offset_partition(counts, "fixture")


def test_exact_metrics_bind_full_v26_and_v27_denominators() -> None:
    component_v26 = {
        "scene_count": 167,
        "truth_count": 2004,
        "proposal_count": 224840,
        "true_positive": 1837,
        "false_positive": 224,
        "false_negative": 167,
    }
    component_v27 = {
        "scene_count": 167,
        "truth_count": 2004,
        "proposal_count": 224840,
        "true_positive": 1796,
        "false_positive": 238,
        "false_negative": 208,
    }
    family_v27 = {
        "scene_count": 9,
        "truth_count": 206,
        "proposal_count": 45396,
        "true_positive": 190,
        "false_positive": 39,
        "false_negative": 16,
    }

    subject._require_exact_metrics("component", "v26", component_v26)
    subject._require_exact_metrics("component", "v27", component_v27)
    subject._require_exact_metrics("family", "v27", family_v27)

    with pytest.raises(RuntimeError, match="proposal_count changed"):
        subject._require_exact_metrics(
            "component", "v27", {**component_v27, "proposal_count": 224839}
        )
    with pytest.raises(RuntimeError, match="false_negative changed"):
        subject._require_exact_metrics(
            "family", "v27", {**family_v27, "false_negative": 15}
        )


def test_prediction_cache_keys_require_explicit_model_and_split_identity() -> None:
    assert (
        subject._prediction_key("v27", "component", 4)
        == "component_004_v27_candidate_predictions"
    )
    with pytest.raises(RuntimeError, match="model identity"):
        subject._prediction_key("candidate", "component", 0)
    with pytest.raises(RuntimeError, match="split identity"):
        subject._prediction_key("v27", "sealed", 0)


def test_dev_scenes_use_v27_authenticated_historical_source_handling(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    component = tuple(range(167))
    family = tuple(range(9))
    observed: list[Path] = []

    def prepare(root: Path) -> SimpleNamespace:
        observed.append(root)
        return SimpleNamespace(component_dev=component, family_dev=family)

    monkeypatch.setattr(
        subject.v27, "_prepare_base_with_authenticated_current_sources", prepare
    )
    root = Path("authenticated-root")

    scenes = subject._load_authenticated_dev_scenes(root)

    assert observed == [root]
    assert scenes == {"component": component, "family": family}


def test_decomposition_and_self_test_reject_mutated_totals() -> None:
    subject._require_decomposition(6, {"score": 2, "offset": 1, "nms": 3}, "fixture")
    with pytest.raises(RuntimeError, match="does not conserve"):
        subject._require_decomposition(
            6, {"score": 2, "offset": 1, "nms": 2}, "fixture"
        )
    subject.self_test()
