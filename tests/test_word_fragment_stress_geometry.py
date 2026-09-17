# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Handwritten geometry cases, without model inference or corpus access."""

import pytest

from ml.ocr.official_bakeoff.word_fragment_stress_v1 import (
    _best_assigned_pair,
    _missing_assigned_pair_reasons,
    _pair_geometry,
)


TOKENS = ((0.0, 0.0, 10.0, 10.0), (15.0, 0.0, 25.0, 10.0))


@pytest.mark.parametrize(
    ("raw", "reason"),
    [
        ((TOKENS[0], (40.0, 0.0, 50.0, 10.0)), "right_token_without_raw_overlap"),
        (((40.0, 0.0, 50.0, 10.0), TOKENS[1]), "left_token_without_raw_overlap"),
        (((0.0, 0.0, 25.0, 10.0),), "without_two_distinct_assigned_raw_boxes"),
        ((TOKENS[0], (1.0, 0.0, 9.0, 10.0)), "right_token_without_raw_overlap"),
    ],
)
def test_missing_token_or_reused_detection_does_not_become_a_pair(raw, reason):
    assert _best_assigned_pair(raw, TOKENS) is None
    assert reason in _missing_assigned_pair_reasons(raw, TOKENS)


def test_pair_assignment_uses_both_tokens_despite_detection_order():
    raw = (TOKENS[1], (40.0, 0.0, 50.0, 10.0), TOKENS[0])
    pair = _best_assigned_pair(raw, TOKENS)
    assert pair == (2, 0)
    geometry = _pair_geometry(raw[pair[0]], raw[pair[1]])
    assert geometry["horizontal_gap_pixels"] == 5.0
    assert geometry["horizontal_gap_height_ratio"] == 0.5
    assert geometry["vertical_overlap_ratio"] == 1.0
    assert geometry["height_ratio"] == 1.0
