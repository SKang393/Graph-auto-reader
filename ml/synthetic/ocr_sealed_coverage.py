# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Validate synthetic OCR truth coverage before a reserve is sealed."""

from __future__ import annotations

from collections import Counter
import math
from numbers import Real
from typing import Any, Mapping, Sequence


GENERATOR_ROLE_TO_RUNTIME_ROLE = {
    "x_tick": "xtick",
    "y_tick": "ytick",
    "axis_title": "axistitle",
    "phase_heading": "phaseheading",
    "legend_text": "legendtext",
    "participant": "participant",
    "annotation": "annotation",
    "condition_label": "phaseheading",
}
ALLOWED_GENERATOR_ROLES = frozenset(GENERATOR_ROLE_TO_RUNTIME_ROLE)


class OcrSealedCoverageError(ValueError):
    """Raised when a synthetic annotation cannot supply valid OCR truth."""


def _sequence(value: Any, label: str) -> Sequence[Any]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)):
        raise OcrSealedCoverageError(f"{label} must be an array")
    return value


def _positive_finite(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, Real):
        raise OcrSealedCoverageError(f"{label} must be a finite positive number")
    result = float(value)
    if not math.isfinite(result) or result <= 0:
        raise OcrSealedCoverageError(f"{label} must be a finite positive number")
    return result


def _records(annotation: Mapping[str, Any], source_index: int) -> list[Mapping[str, Any]]:
    groups = [
        _sequence(annotation.get("texts", ()), f"source {source_index} texts")
    ]
    for panel_index, panel in enumerate(
        _sequence(annotation.get("panels", ()), f"source {source_index} panels")
    ):
        if not isinstance(panel, Mapping):
            raise OcrSealedCoverageError(
                f"source {source_index} panel {panel_index} must be an object"
            )
        groups.append(
            _sequence(
                panel.get("texts", ()),
                f"source {source_index} panel {panel_index} texts",
            )
        )

    records: list[Mapping[str, Any]] = []
    for group in groups:
        for record in group:
            if not isinstance(record, Mapping):
                raise OcrSealedCoverageError(
                    f"source {source_index} text record must be an object"
                )
            records.append(record)
    return records


def _box(
    value: Any,
    canvas_width: float,
    canvas_height: float,
    source_index: int,
) -> None:
    raw = _sequence(value, f"source {source_index} rendered pixel box")
    if len(raw) != 4 or any(isinstance(item, bool) or not isinstance(item, Real) for item in raw):
        raise OcrSealedCoverageError(
            f"source {source_index} rendered pixel box must contain four numbers"
        )
    left, top, width, height = (float(item) for item in raw)
    if (
        not all(math.isfinite(item) for item in (left, top, width, height))
        or left < 0
        or top < 0
        or width <= 0
        or height <= 0
        or left + width > canvas_width
        or top + height > canvas_height
    ):
        raise OcrSealedCoverageError(
            f"source {source_index} rendered pixel box is outside its canvas"
        )


def _scalar_count(text: str, source_index: int) -> int:
    if any(0xD800 <= ord(character) <= 0xDFFF for character in text):
        raise OcrSealedCoverageError(
            f"source {source_index} OCR truth text contains invalid Unicode"
        )
    return len(text)


def validate_ocr_sealed_coverage(
    annotations: Sequence[Mapping[str, Any]],
) -> dict[str, Any]:
    """Return aggregate full-OCR denominators for unsealed annotations.

    Selection matches the V2 full OCR scorer: only visible, nonblank string
    records with an observed ``rendered_pixel_box`` become truths.
    """

    sources = _sequence(annotations, "annotations")
    generator_regions: Counter[str] = Counter()
    generator_characters: Counter[str] = Counter()
    runtime_regions: Counter[str] = Counter()
    runtime_characters: Counter[str] = Counter()

    for source_index, annotation in enumerate(sources):
        if not isinstance(annotation, Mapping):
            raise OcrSealedCoverageError(
                f"source {source_index} annotation must be an object"
            )
        if annotation.get("coordinate_space") != "original_pixels":
            raise OcrSealedCoverageError("OCR truth must use original pixel coordinates")
        canvas = annotation.get("canvas")
        if not isinstance(canvas, Mapping):
            raise OcrSealedCoverageError(f"source {source_index} canvas must be an object")
        canvas_width = _positive_finite(canvas.get("width"), f"source {source_index} canvas width")
        canvas_height = _positive_finite(canvas.get("height"), f"source {source_index} canvas height")
        region_ids: set[str] = set()
        text_ids: set[str] = set()

        for record in _records(annotation, source_index):
            text = record.get("text")
            rendered_box = record.get("rendered_pixel_box")
            if record.get("visible", True) is False or (isinstance(text, str) and not text.strip()):
                continue
            if rendered_box is None:
                continue
            if not isinstance(text, str):
                raise OcrSealedCoverageError(
                    f"source {source_index} rendered OCR truth text must be a string"
                )

            # The frozen source-truth scorer identifies records by trimmed text_id.
            text_id = record.get("text_id")
            if not isinstance(text_id, str) or not text_id.strip():
                raise OcrSealedCoverageError("OCR truth lacks a text identity")
            text_id = text_id.strip()
            if text_id in text_ids:
                raise OcrSealedCoverageError("OCR truth text identities repeat")
            text_ids.add(text_id)

            raw_region_id = record.get("region_id")
            if not isinstance(raw_region_id, str) or not raw_region_id.strip():
                raise OcrSealedCoverageError(
                    f"source {source_index} OCR truth lacks a region identity"
                )
            region_id = raw_region_id.strip()
            if region_id in region_ids:
                raise OcrSealedCoverageError(
                    f"source {source_index} OCR truth region identities repeat"
                )
            region_ids.add(region_id)

            role = record.get("role")
            if not isinstance(role, str) or role not in ALLOWED_GENERATOR_ROLES:
                raise OcrSealedCoverageError(
                    f"source {source_index} OCR truth has an unknown generator role"
                )
            _box(rendered_box, canvas_width, canvas_height, source_index)
            character_count = _scalar_count(text, source_index)
            runtime_role = GENERATOR_ROLE_TO_RUNTIME_ROLE[role]
            generator_regions[role] += 1
            generator_characters[role] += character_count
            runtime_regions[runtime_role] += 1
            runtime_characters[runtime_role] += character_count

    return {
        "source_count": len(sources),
        "truth_region_count": sum(generator_regions.values()),
        "truth_character_count": sum(generator_characters.values()),
        "by_generator_role": {
            role: {
                "truth_region_count": generator_regions[role],
                "truth_character_count": generator_characters[role],
            }
            for role in sorted(generator_regions)
        },
        "by_expected_runtime_role": {
            role: {
                "truth_region_count": runtime_regions[role],
                "truth_character_count": runtime_characters[role],
            }
            for role in sorted(runtime_regions)
        },
    }


__all__ = [
    "ALLOWED_GENERATOR_ROLES",
    "GENERATOR_ROLE_TO_RUNTIME_ROLE",
    "OcrSealedCoverageError",
    "validate_ocr_sealed_coverage",
]
