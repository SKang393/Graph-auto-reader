# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from types import SimpleNamespace

import torch

from ml.markers.center.localization_confidence_v26.measure_negative_coverage import (
    component_original_strata,
    component_retention_roles,
    distance_band,
    family_original_strata,
    radius_category,
    summarize_negative_rows,
)


def test_distance_and_radius_categories_have_exact_boundaries() -> None:
    assert [distance_band(value) for value in (3.0, 3.01, 5.0, 5.01, 8.0, 8.01, 12.0, 12.01)] == [
        "positive_le3", "negative_gt3_le5", "negative_gt3_le5",
        "negative_gt5_le8", "negative_gt5_le8", "negative_gt8_le12",
        "negative_gt8_le12", "negative_gt12",
    ]
    assert radius_category(2.49) == "below_2_5px"
    assert radius_category(2.5) == "within_2_5_to_8px"
    assert radius_category(8.0) == "within_2_5_to_8px"
    assert radius_category(8.01) == "above_8px"


def test_component_stratum_precedence_matches_sampler(monkeypatch) -> None:
    count = 8
    proposals = SimpleNamespace(
        patches=torch.zeros((count, 3, 33, 33)),
        coordinates=torch.arange(count * 2, dtype=torch.float32).reshape(count, 2),
    )
    labels = torch.tensor((1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0))
    features = {
        "faint_low": torch.tensor((False, False, False, True, False, False, False, False)),
        "faint_p05": torch.tensor((False, False, False, False, True, False, False, False)),
        "ocr_heavy": torch.tensor((False, False, False, False, False, True, False, False)),
        "artifact": torch.tensor((False, False, False, False, False, False, True, False)),
    }
    prefix = "ml.markers.center.localization_confidence_v26.measure_negative_coverage.negative_sampler"
    monkeypatch.setattr(f"{prefix}._features", lambda _: features)
    monkeypatch.setattr(f"{prefix}._hard_indices", lambda *_: torch.tensor((1,)))
    monkeypatch.setattr(f"{prefix}._topology_indices", lambda *_args, **_kwargs: {"topology_junction": {2}, "topology_fragment": set()})
    monkeypatch.setattr(f"{prefix}._connector_anchor_indices", lambda *_: set())
    monkeypatch.setattr(f"{prefix}._sparse_fragment_indices", lambda *_: {7})
    monkeypatch.setattr(f"{prefix}._generic_connector_band_indices", lambda *_: {7})

    strata = component_original_strata(SimpleNamespace(), proposals, labels)

    assert strata == (
        "positive", "hard_existing", "generic", "faint_low",
        "faint_p05", "ocr_heavy", "artifact", "generic_connector_band",
    )
    assert component_retention_roles(SimpleNamespace(), proposals, labels) == (
        "positive", "quota_ranked", "topology_reserved", "quota_ranked",
        "quota_ranked", "quota_ranked", "quota_ranked", "sparse_fragment_reserved",
    )


def test_family_strata_keep_hard_negative_separate_from_bins(monkeypatch) -> None:
    proposals = SimpleNamespace(
        patches=torch.zeros((3, 3, 33, 33)),
        coordinates=torch.tensor(((0.0, 0.0), (10.0, 0.0), (20.0, 0.0))),
    )
    scene = SimpleNamespace(hard_negatives=(("text", 10.0, 0.0),))
    labels = torch.tensor((1.0, 0.0, 0.0))
    monkeypatch.setattr(
        "ml.markers.center.localization_confidence_v26.measure_negative_coverage.stratified_background._bin_ids",
        lambda _: torch.tensor((1, 2, 3)),
    )

    assert family_original_strata(scene, proposals, labels) == (
        "positive", "hard_negative", "stratified_bin_03",
    )


def test_summary_preserves_eligible_and_selected_cross_cells() -> None:
    rows = (
        {"distance_band": "negative_gt3_le5", "stratum": "generic", "radius_category": "within", "selected": True},
        {"distance_band": "negative_gt3_le5", "stratum": "generic", "radius_category": "within", "selected": False},
        {"distance_band": "negative_gt8_le12", "stratum": "hard", "radius_category": "above", "selected": True},
        {"distance_band": "positive_le3", "stratum": "positive", "radius_category": "within", "selected": True},
    )

    report = summarize_negative_rows(rows)

    assert report["eligible_negative_count"] == 3
    assert report["selected_negative_count"] == 2
    assert report["by_distance"]["negative_gt3_le5"] == {
        "eligible": 2, "selected": 1, "selection_fraction": 0.5,
    }
