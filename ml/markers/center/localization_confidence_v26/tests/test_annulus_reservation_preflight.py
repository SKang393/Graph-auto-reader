# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import numpy as np
import pytest

from ml.markers.center.localization_confidence_v26.annulus_reservation_preflight import (
    annulus_band,
    apply_reservations,
    choose_reservations,
)


def test_annulus_boundaries_are_fixed_and_half_open() -> None:
    assert [annulus_band(value) for value in (3.0, 3.01, 5.0, 5.01, 8.0, 8.01, 12.0, 12.01)] == [
        None,
        "negative_gt3_le5",
        "negative_gt3_le5",
        "negative_gt5_le8",
        "negative_gt5_le8",
        "negative_gt8_le12",
        "negative_gt8_le12",
        None,
    ]


def test_reservation_uses_distance_then_coordinates_then_proposal_index() -> None:
    scene = np.asarray((0, 0, 0, 0, 1), dtype=np.int32)
    proposal = np.asarray((9, 8, 7, 6, 5), dtype=np.int32)
    coordinates = np.asarray(
        ((8, 8), (7, 8), (7, 8), (6, 8), (0, 0)), dtype=np.float32
    )
    truth = np.asarray((2, 2, 2, 2, 2), dtype=np.int32)
    distance = np.asarray((4, 4, 4, 6, 4), dtype=np.float32)

    reserved = choose_reservations(scene, proposal, coordinates, truth, distance)

    assert np.flatnonzero(reserved).tolist() == [2, 3, 4]


def test_same_stratum_replacement_preserves_budget_and_protected_rows() -> None:
    selected = np.asarray((True, True, True, False, False), dtype=np.bool_)
    positive = np.asarray((True, False, False, False, False), dtype=np.bool_)
    protected = np.asarray((False, True, False, False, False), dtype=np.bool_)
    reserved = np.asarray((False, False, False, True, False), dtype=np.bool_)
    strata = np.asarray((0, 1, 2, 2, 1), dtype=np.uint8)
    scene = np.asarray((0, 0, 1, 1, 2), dtype=np.int32)
    proposal = np.asarray((1, 2, 3, 4, 5), dtype=np.int32)

    outcome = apply_reservations(
        "component",
        selected, positive, protected, reserved, strata, scene, proposal
    )

    assert outcome.selected.tolist() == [True, True, False, True, False]
    assert outcome.displaced.tolist() == [False, False, True, False, False]
    assert outcome.fallback_additions == ()
    assert outcome.replacement_tiers == ("same_scene_and_stratum",)


def test_cross_stratum_fallback_is_explicit_and_preserves_total() -> None:
    selected = np.asarray((True, True, False), dtype=np.bool_)
    positive = np.asarray((True, False, False), dtype=np.bool_)
    protected = np.zeros(3, dtype=np.bool_)
    reserved = np.asarray((False, False, True), dtype=np.bool_)
    strata = np.asarray((0, 1, 2), dtype=np.uint8)
    scene = np.asarray((0, 0, 0), dtype=np.int32)
    proposal = np.asarray((0, 1, 2), dtype=np.int32)

    outcome = apply_reservations(
        "family",
        selected, positive, protected, reserved, strata, scene, proposal
    )

    assert outcome.selected.tolist() == [True, False, True]
    assert outcome.fallback_additions == (2,)
    assert outcome.replacement_tiers == ("cross_stratum_global_fallback",)


def test_replacement_fails_closed_when_only_preserved_rows_are_selected() -> None:
    selected = np.asarray((True, True, False), dtype=np.bool_)
    positive = np.asarray((True, False, False), dtype=np.bool_)
    protected = np.asarray((False, True, False), dtype=np.bool_)
    reserved = np.asarray((False, False, True), dtype=np.bool_)
    values = np.asarray((0, 1, 1), dtype=np.int32)

    with pytest.raises(ValueError, match="no unprotected"):
        apply_reservations(
            "component",
            selected, positive, protected, reserved, values, values, values
        )
