# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Pre-seal synthetic OCR annotation coverage checks."""

from __future__ import annotations

from copy import deepcopy
import math

import pytest

from ml.synthetic.ocr_sealed_coverage import (
    ALLOWED_GENERATOR_ROLES,
    OcrSealedCoverageError,
    validate_ocr_sealed_coverage,
)


def _text(
    region_id: str,
    text: object,
    role: str,
    box: object = (1.0, 2.0, 10.0, 5.0),
    *,
    visible: object = True,
) -> dict[str, object]:
    return {
        "region_id": region_id,
        "text_id": region_id,
        "text": text,
        "role": role,
        "rendered_pixel_box": box,
        "visible": visible,
    }


def _annotation(*texts: dict[str, object]) -> dict[str, object]:
    return {
        "coordinate_space": "original_pixels",
        "canvas": {"width": 100, "height": 50},
        "texts": list(texts),
        "panels": [],
    }


def test_coverage_matches_full_ocr_truth_selection_and_unicode_denominators() -> None:
    first = _annotation(
        _text("shared-id", "10", "x_tick"),
        _text("condition", "A\U0001f600", "condition_label"),
        _text("hidden", "ignored", "not_a_role", visible=False),
        _text("blank", "  ", "not_a_role"),
        _text("not-rendered", "ignored", "not_a_role", box=None),
    )
    second = _annotation()
    second["panels"] = [{"texts": [_text("shared-id", "note", "annotation")]}]

    coverage = validate_ocr_sealed_coverage([first, second])

    assert coverage == {
        "source_count": 2,
        "truth_region_count": 3,
        "truth_character_count": 8,
        "by_generator_role": {
            "annotation": {"truth_region_count": 1, "truth_character_count": 4},
            "condition_label": {"truth_region_count": 1, "truth_character_count": 2},
            "x_tick": {"truth_region_count": 1, "truth_character_count": 2},
        },
        "by_expected_runtime_role": {
            "annotation": {"truth_region_count": 1, "truth_character_count": 4},
            "phaseheading": {"truth_region_count": 1, "truth_character_count": 2},
            "xtick": {"truth_region_count": 1, "truth_character_count": 2},
        },
    }


def test_allowed_generator_roles_match_the_full_ocr_scorers() -> None:
    assert ALLOWED_GENERATOR_ROLES == {
        "x_tick",
        "y_tick",
        "axis_title",
        "phase_heading",
        "legend_text",
        "participant",
        "annotation",
        "condition_label",
    }


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        ("duplicate", "identities repeat"),
        ("missing_id", "lacks a region identity"),
        ("unknown_role", "unknown generator role"),
        ("zero_width", "outside its canvas"),
        ("nonfinite", "outside its canvas"),
        ("outside", "outside its canvas"),
    ],
)
def test_invalid_selected_truths_fail_closed(mutation: str, message: str) -> None:
    annotation = _annotation(_text("one", "text", "annotation"))
    record = annotation["texts"][0]
    if mutation == "duplicate":
        annotation["panels"] = [{"texts": [_text(" one ", "other", "participant")]}]
    elif mutation == "missing_id":
        del record["region_id"]
    elif mutation == "unknown_role":
        record["role"] = "other"
    elif mutation == "zero_width":
        record["rendered_pixel_box"] = [1, 2, 0, 5]
    elif mutation == "nonfinite":
        record["rendered_pixel_box"] = [1, 2, math.inf, 5]
    else:
        record["rendered_pixel_box"] = [95, 2, 10, 5]

    with pytest.raises(OcrSealedCoverageError, match=message):
        validate_ocr_sealed_coverage([annotation])


@pytest.mark.parametrize(
    ("text", "message"),
    [
        (123, "must be a string"),
        ("bad\ud800", "invalid Unicode"),
    ],
)
def test_truth_text_must_be_unicode_scalar_valid(text: object, message: str) -> None:
    annotation = _annotation(_text("one", text, "annotation"))

    with pytest.raises(OcrSealedCoverageError, match=message):
        validate_ocr_sealed_coverage([annotation])


def test_validation_does_not_mutate_annotations() -> None:
    annotations = [_annotation(_text("one", "text", "annotation"))]
    original = deepcopy(annotations)

    validate_ocr_sealed_coverage(annotations)

    assert annotations == original


@pytest.mark.parametrize("text_id", ["one", " one "])
def test_duplicate_text_identity_rejected_even_with_distinct_region_ids(text_id: str) -> None:
    annotation = _annotation(_text("one", "A", "annotation"), _text("two", "B", "annotation"))
    annotation["texts"][1]["text_id"] = text_id
    with pytest.raises(OcrSealedCoverageError, match="text identities repeat"):
        validate_ocr_sealed_coverage([annotation])


@pytest.mark.parametrize("role", [[], {}])
def test_malformed_role_is_structured_failure(role: object) -> None:
    annotation = _annotation(_text("one", "A", "annotation"))
    annotation["texts"][0]["role"] = role
    with pytest.raises(OcrSealedCoverageError, match="unknown generator role"):
        validate_ocr_sealed_coverage([annotation])


@pytest.mark.parametrize("space", [None, "panel_pixels", "graph_units"])
def test_foreign_coordinate_spaces_rejected(space: object) -> None:
    annotation = _annotation(_text("one", "A", "annotation"))
    annotation["coordinate_space"] = space
    with pytest.raises(OcrSealedCoverageError, match="original pixel"):
        validate_ocr_sealed_coverage([annotation])
