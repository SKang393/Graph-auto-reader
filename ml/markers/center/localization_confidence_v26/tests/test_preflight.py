# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from dataclasses import replace
from types import SimpleNamespace

import pytest
import torch

from ml.markers.center.localization_confidence_v26.preflight import (
    SceneInventory,
    assign_unique_nearest_confidence,
    exact_patch_label_collision_report,
    _scene_inventories,
    _tensor_digest,
    summarize_assignments,
)


def _scene(
    coordinates=((8.0, 10.0), (10.0, 10.0), (12.0, 10.0), (20.0, 20.0)),
    selected=(0, 1, 2, 3),
    truths=((10.0, 10.0),),
    labels=(1.0, 1.0, 1.0, 0.0),
) -> SceneInventory:
    count = len(selected)
    return SceneInventory(
        "train:fixture:1",
        torch.tensor(coordinates, dtype=torch.float32),
        tuple(selected),
        tuple(truths),
        torch.arange(count * 3 * 33 * 33, dtype=torch.float32).reshape(count, 3, 33, 33),
        torch.tensor(labels, dtype=torch.float32),
        torch.zeros((count, 2), dtype=torch.float32),
        torch.full((count,), 5.0),
        torch.tensor([False] * count),
    )


def test_unique_nearest_target_keeps_all_regression_rows_and_negatives() -> None:
    assignment = assign_unique_nearest_confidence(_scene())

    assert assignment.confidence_targets.tolist() == [0.0, 1.0, 0.0, 0.0]
    assert assignment.classification_mask.tolist() == [False, True, False, True]
    assert assignment.regression_mask.tolist() == [True, True, True, False]
    assert assignment.reachable_truth_count == 1
    assert assignment.zero_eligible_truth_count == 0


def test_canonical_tie_uses_coordinates_then_proposal_index() -> None:
    scene = _scene(
        coordinates=((11.0, 10.0), (9.0, 10.0), (9.0, 10.0), (30.0, 30.0)),
        labels=(1.0, 1.0, 1.0, 0.0),
    )

    assignment = assign_unique_nearest_confidence(scene)

    assert assignment.confidence_targets.tolist() == [0.0, 1.0, 0.0, 0.0]


def test_positive_boundary_uses_v25_float32_cdist_membership() -> None:
    coordinate = torch.tensor(((16_777_216.0, 10.0),), dtype=torch.float32)
    truth = ((16_777_219.0, 10.0),)
    distance = torch.cdist(coordinate, torch.tensor(truth, dtype=torch.float32))[0, 0]
    expected_positive = bool(distance.le(3.0))
    scene = _scene(
        coordinates=tuple(tuple(value) for value in coordinate.tolist()),
        selected=(0,),
        truths=truth,
        labels=(1.0 if expected_positive else 0.0,),
    )

    assignment = assign_unique_nearest_confidence(scene)

    assert assignment.regression_mask.tolist() == [expected_positive]


def test_unreachable_truths_remain_in_denominator_with_support_categories() -> None:
    scene = _scene(
        coordinates=((10.0, 10.0), (24.0, 20.0), (50.0, 50.0)),
        selected=(0, 1, 2),
        truths=((10.0, 10.0), (20.0, 20.0), (80.0, 80.0)),
        labels=(1.0, 0.0, 0.0),
    )

    report, _ = summarize_assignments((scene,))

    assert report["truth_count"] == 3
    assert report["confidence_positive_row_count"] == 1
    assert report["zero_eligible_truth_count"] == 2
    assert report["proposal_only_3_to_5_truth_count"] == 1
    assert report["outside_5_truth_count"] == 1


def test_marker_free_scene_retains_all_selected_negatives() -> None:
    scene = _scene(
        coordinates=((4.0, 4.0), (8.0, 8.0)),
        selected=(0, 1),
        truths=(),
        labels=(0.0, 0.0),
    )

    report, assignments = summarize_assignments((scene,))

    assert report["truth_count"] == 0
    assert report["selected_row_count"] == 2
    assert report["v25_negative_row_count"] == 2
    assert assignments[0].classification_mask.tolist() == [True, True]
    assert assignments[0].regression_mask.tolist() == [False, False]


def test_sampled_row_hash_binds_source_coordinates() -> None:
    original, _ = summarize_assignments((_scene(),))
    moved, _ = summarize_assignments((_scene(
        coordinates=((8.0, 10.0), (10.0, 10.0), (12.0, 10.0), (21.0, 20.0)),
    ),))

    assert original["sampled_row_identity_sha256"] != moved["sampled_row_identity_sha256"]


def test_missing_or_relabelled_v25_positive_fails_closed() -> None:
    scene = _scene()
    with pytest.raises(ValueError, match="omitted an eligible positive"):
        assign_unique_nearest_confidence(replace(
            scene,
            selected_indices=(1, 2, 3),
            selected_patches=scene.selected_patches[:3],
            selected_labels=torch.tensor((1.0, 1.0, 0.0)),
            selected_offsets=scene.selected_offsets[:3],
            selected_radii=scene.selected_radii[:3],
            selected_hard=scene.selected_hard[:3],
        ))

    with pytest.raises(ValueError, match="labels differ"):
        assign_unique_nearest_confidence(replace(
            scene,
            selected_labels=torch.tensor((1.0, 0.0, 1.0, 0.0)),
        ))


def test_exact_patch_collision_reports_opposing_labels_and_positive_radius() -> None:
    shared = torch.arange(3 * 33 * 33, dtype=torch.float32).reshape(3, 33, 33)
    distinct = torch.zeros((3, 33, 33), dtype=torch.float32)
    patches = torch.stack((shared, shared.clone(), shared.clone(), distinct))
    labels = torch.tensor((1.0, 0.0, 1.0, 0.0))
    radii = torch.tensor((9.0, 4.0, 6.0, 4.0))

    report = exact_patch_label_collision_report(patches, labels, radii)

    assert report == {
        "unique_patch_sha256_count": 2,
        "opposing_label_patch_sha256_count": 1,
        "positive_rows_in_opposing_label_groups": 2,
        "negative_rows_in_opposing_label_groups": 1,
        "positive_rows_by_truth_radius": {
            "above_8px": 1,
            "within_2_5_to_8px": 1,
            "below_2_5px": 0,
        },
    }


def test_tensor_inventory_hash_binds_values_and_metadata() -> None:
    baseline = _tensor_digest((("patches", torch.tensor(((1.0, 2.0),))),))
    changed_value = _tensor_digest((("patches", torch.tensor(((1.0, 3.0),))),))
    changed_name = _tensor_digest((("other", torch.tensor(((1.0, 2.0),))),))

    assert baseline != changed_value
    assert baseline != changed_name


def test_component_inventory_reconstructs_positive_union_with_recorded_negatives(
    monkeypatch,
) -> None:
    coordinates = torch.tensor(((10.0, 10.0), (40.0, 40.0), (12.0, 10.0), (60.0, 60.0)))
    proposals = SimpleNamespace(coordinates=coordinates)
    monkeypatch.setattr(
        "ml.markers.center.localization_confidence_v26.preflight.v24.extract_proposals",
        lambda _: proposals,
    )
    scene = SimpleNamespace(
        split="train",
        family="fixture",
        seed=1,
        tensor=torch.zeros((3, 80, 80)),
        centers=((10.0, 10.0),),
    )
    count = 3
    values = (
        torch.zeros((count, 3, 33, 33)),
        torch.tensor((1.0, 1.0, 0.0)),
        torch.zeros((count, 2)),
        torch.full((count,), 5.0),
        torch.tensor((False, False, False)),
        SimpleNamespace(selections=((3,), (), ())),
    )

    inventories = _scene_inventories((scene,), values, family_domain=False)

    assert inventories[0].selected_indices == (0, 2, 3)


def test_component_inventory_rejects_populated_surplus_sampler_rows(monkeypatch) -> None:
    coordinates = torch.tensor(((10.0, 10.0), (40.0, 40.0)))
    monkeypatch.setattr(
        "ml.markers.center.localization_confidence_v26.preflight.v24.extract_proposals",
        lambda _: SimpleNamespace(coordinates=coordinates),
    )
    scene = SimpleNamespace(
        split="train", family="fixture", seed=1,
        tensor=torch.zeros((3, 50, 50)), centers=((10.0, 10.0),),
    )
    values = (
        torch.zeros((2, 3, 33, 33)), torch.tensor((1.0, 0.0)),
        torch.zeros((2, 2)), torch.full((2,), 5.0),
        torch.tensor((False, False)), SimpleNamespace(selections=((1,), (0,))),
    )

    with pytest.raises(RuntimeError, match="populated trailing"):
        _scene_inventories((scene,), values, family_domain=False)
