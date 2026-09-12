# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed full-OCR coverage contract for Goal 22 sealed acceptance reserves."""

from __future__ import annotations

from dataclasses import replace
import hashlib
import json
from pathlib import Path
from typing import Any, Mapping, Sequence

from ml.synthetic import sealed_acceptance as base
from ml.synthetic.dataset import CaseSpec
from ml.synthetic.ocr_sealed_coverage import (
    GENERATOR_ROLE_TO_RUNTIME_ROLE,
    OcrSealedCoverageError,
    validate_ocr_sealed_coverage,
)


PROTOCOL_SCHEMA = "graphreader.goal22-ocr-sealed-coverage.v1"
ACCEPTANCE_SCOPE = "goal22.full-ocr.five-axis-family.real-range.v1"
ACCEPTANCE_PRESET = base.ACCEPTANCE_PRESET
PROTOCOL_PATH = Path("ml/policy/goal22-ocr-sealed-coverage-v1.json")
# Filled after the canonical protocol document is written. The protocol does not
# contain this value, so the binding has no self-hash cycle.
PROTOCOL_SHA256 = "26ab0e017ccc6dc17d26effe11b1fb40f440ed4471e896c1afac3c8cc96999c4"

GENERATOR_SOURCE_PATHS = tuple(sorted(
    set(base.GENERATOR_SOURCE_PATHS)
    | {
        PROTOCOL_PATH,
        Path("ml/synthetic/ocr_sealed_acceptance.py"),
        Path("ml/synthetic/ocr_sealed_coverage.py"),
    },
    key=lambda path: path.as_posix(),
))

GENERATOR_ROLES = (
    "x_tick",
    "y_tick",
    "axis_title",
    "phase_heading",
    "legend_text",
    "participant",
    "annotation",
    "condition_label",
)
OCR_X_LABEL_CASE_INDEX = 0
OCR_X_LABEL_VISIBILITY = "visible"

_PROTOCOL_FIELDS = {
    "schema",
    "protocol_id",
    "acceptance_scope",
    "purpose",
    "base_coverage_protocol",
    "ocr_truth_selection",
    "ordered_case_overrides",
    "required_dataset_coverage",
    "generator_source_paths",
    "evidence_use",
}
_CASE_OVERRIDE_FIELDS = {
    "ordered_case_index",
    "field",
    "base_value",
    "required_value",
    "purpose",
    "all_other_case_spec_fields_unchanged",
}
_BASE_PROTOCOL_FIELDS = {"acceptance_scope", "path", "sha256"}
_SELECTION_FIELDS = {
    "annotation_sources",
    "visible",
    "text",
    "rendered_pixel_box",
    "coordinate_space",
    "text_identity",
    "region_identity",
    "identity_uniqueness",
}
_DATASET_COVERAGE_FIELDS = {
    "nonempty_ocr_truth_per_source",
    "generator_roles",
    "runtime_role_mapping",
    "region_denominator",
    "character_denominator",
}
_EVIDENCE_USE_FIELDS = {
    "aggregate_only",
    "synthetic_only",
    "private_data",
    "training_permitted",
    "production_approval",
}


class OcrSealedAcceptanceError(base.SealedAcceptanceError):
    """Raised when full-OCR sealed coverage or identity is incomplete."""


def acceptance_case_specs() -> tuple[CaseSpec, ...]:
    """Return the base corpus with one explicit OCR-only presentation override."""

    specs = base.acceptance_case_specs()
    presentation = dict(specs[OCR_X_LABEL_CASE_INDEX].presentation or {})
    presentation["x_label_visibility"] = OCR_X_LABEL_VISIBILITY
    return (
        replace(specs[OCR_X_LABEL_CASE_INDEX], presentation=presentation),
        *specs[OCR_X_LABEL_CASE_INDEX + 1 :],
    )


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _base_protocol_document() -> dict[str, Any]:
    return {
        "acceptance_scope": base.ACCEPTANCE_SCOPE,
        "path": base.PROTOCOL_PATH.as_posix(),
        "sha256": base.PROTOCOL_SHA256,
    }


def _selection_document() -> dict[str, Any]:
    return {
        "annotation_sources": ["annotation.texts", "annotation.panels[].texts"],
        "visible": "record.visible is not false",
        "text": "record.text is a nonblank string after trimming",
        "rendered_pixel_box": "record.rendered_pixel_box is present",
        "coordinate_space": "original_pixels",
        "text_identity": "trimmed text_id is nonblank",
        "region_identity": "trimmed region_id is nonblank",
        "identity_uniqueness": "text_id and region_id are each unique per source",
    }


def _case_overrides_document() -> list[dict[str, Any]]:
    return [{
        "ordered_case_index": OCR_X_LABEL_CASE_INDEX,
        "field": "presentation.x_label_visibility",
        "base_value": "hidden",
        "required_value": OCR_X_LABEL_VISIBILITY,
        "purpose": "expose_x_tick_truth_for_full_ocr_coverage",
        "all_other_case_spec_fields_unchanged": True,
    }]


def _required_dataset_coverage_document() -> dict[str, Any]:
    return {
        "nonempty_ocr_truth_per_source": True,
        "generator_roles": list(GENERATOR_ROLES),
        "runtime_role_mapping": {
            role: GENERATOR_ROLE_TO_RUNTIME_ROLE[role] for role in GENERATOR_ROLES
        },
        "region_denominator": "count every selected truth record exactly once",
        "character_denominator": (
            "count every Unicode scalar in every selected truth text exactly once"
        ),
    }


def expected_protocol_document() -> dict[str, Any]:
    """Return the only protocol document supported by this implementation."""

    return {
        "schema": PROTOCOL_SCHEMA,
        "protocol_id": "goal22-ocr-sealed-coverage-v1",
        "acceptance_scope": ACCEPTANCE_SCOPE,
        "purpose": "aggregate_only_full_ocr_acceptance",
        "base_coverage_protocol": _base_protocol_document(),
        "ocr_truth_selection": _selection_document(),
        "ordered_case_overrides": _case_overrides_document(),
        "required_dataset_coverage": _required_dataset_coverage_document(),
        "generator_source_paths": [path.as_posix() for path in GENERATOR_SOURCE_PATHS],
        "evidence_use": {
            "aggregate_only": True,
            "synthetic_only": True,
            "private_data": False,
            "training_permitted": False,
            "production_approval": False,
        },
    }


def _object(value: Any, fields: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != fields:
        raise OcrSealedAcceptanceError(f"{label} has an invalid shape")
    return value


def parse_supported_protocol(payload: bytes) -> dict[str, Any]:
    """Parse the exact hash-bound OCR coverage protocol."""

    if _sha256_bytes(payload) != PROTOCOL_SHA256:
        raise OcrSealedAcceptanceError(
            "OCR coverage protocol differs from its supported SHA-256"
        )
    try:
        document = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise OcrSealedAcceptanceError(
            "OCR coverage protocol is not valid JSON"
        ) from error

    _object(document, _PROTOCOL_FIELDS, "OCR coverage protocol")
    _object(
        document["base_coverage_protocol"],
        _BASE_PROTOCOL_FIELDS,
        "OCR coverage base protocol",
    )
    _object(
        document["ocr_truth_selection"],
        _SELECTION_FIELDS,
        "OCR truth selection",
    )
    overrides = document["ordered_case_overrides"]
    if not isinstance(overrides, list) or any(
        not isinstance(override, dict) or set(override) != _CASE_OVERRIDE_FIELDS
        for override in overrides
    ):
        raise OcrSealedAcceptanceError("OCR ordered case overrides are invalid")
    _object(
        document["required_dataset_coverage"],
        _DATASET_COVERAGE_FIELDS,
        "OCR required dataset coverage",
    )
    _object(document["evidence_use"], _EVIDENCE_USE_FIELDS, "OCR evidence use")
    if document != expected_protocol_document():
        raise OcrSealedAcceptanceError(
            "OCR coverage protocol has invalid safety, source, or selection identities"
        )
    return document


def load_supported_protocol(repository_root: Path) -> tuple[dict[str, Any], bytes]:
    """Load the supported OCR protocol and verify its bound base protocol."""

    root = repository_root.resolve()
    path = (root / PROTOCOL_PATH).resolve()
    if root not in path.parents or not path.is_file():
        raise OcrSealedAcceptanceError("supported OCR coverage protocol is missing")
    payload = path.read_bytes()
    protocol = parse_supported_protocol(payload)
    try:
        _base_protocol, base_payload = base.load_supported_protocol(root)
    except base.SealedAcceptanceError as error:
        raise OcrSealedAcceptanceError(str(error)) from error
    if (
        protocol["base_coverage_protocol"] != _base_protocol_document()
        or _sha256_bytes(base_payload) != base.PROTOCOL_SHA256
    ):
        raise OcrSealedAcceptanceError(
            "OCR coverage protocol does not bind the supported base protocol"
        )
    return protocol, payload


def require_supported_identity(
    acceptance_scope: Any,
    coverage_protocol_path: Any,
    coverage_protocol_sha256: Any,
) -> None:
    if (
        acceptance_scope != ACCEPTANCE_SCOPE
        or coverage_protocol_path != PROTOCOL_PATH.as_posix()
        or coverage_protocol_sha256 != PROTOCOL_SHA256
    ):
        raise OcrSealedAcceptanceError(
            "acceptance reserve does not use the supported Goal 22 OCR coverage identity"
        )


def _source_rows(
    rows: Sequence[Mapping[str, str]],
) -> list[Mapping[str, str]]:
    paths = [row.get("path") for row in rows]
    expected_paths = [path.as_posix() for path in GENERATOR_SOURCE_PATHS]
    if paths != expected_paths:
        raise OcrSealedAcceptanceError(
            "OCR acceptance generator source snapshot is incomplete"
        )
    protocol_row = next(
        (row for row in rows if row.get("path") == PROTOCOL_PATH.as_posix()),
        None,
    )
    if protocol_row is None or protocol_row.get("sha256") != PROTOCOL_SHA256:
        raise OcrSealedAcceptanceError(
            "OCR acceptance protocol is absent from the source snapshot"
        )
    return [
        row
        for row in rows
        if row.get("path") in {path.as_posix() for path in base.GENERATOR_SOURCE_PATHS}
    ]


def validate_acceptance_cases(
    config: Mapping[str, Any],
    source_rows: Sequence[Mapping[str, str]],
    protocol_payload: bytes,
    cases: Sequence[Mapping[str, Any]],
    *,
    snapshot_payloads: Mapping[str, bytes],
) -> dict[str, Any]:
    """Validate base geometry plus complete OCR truth denominators."""

    protocol = parse_supported_protocol(protocol_payload)
    base_rows = _source_rows(source_rows)
    require_supported_identity(
        config.get("acceptance_scope"),
        config.get("coverage_protocol_path"),
        config.get("coverage_protocol_sha256"),
    )
    if (
        config.get("purpose") != base.ACCEPTANCE_PURPOSE
        or config.get("preset") != ACCEPTANCE_PRESET
    ):
        raise OcrSealedAcceptanceError(
            "OCR acceptance generation config has the wrong purpose or preset"
        )

    base_payload = snapshot_payloads.get(base.PROTOCOL_PATH.as_posix())
    if base_payload is None or _sha256_bytes(base_payload) != base.PROTOCOL_SHA256:
        raise OcrSealedAcceptanceError(
            "OCR acceptance snapshot lacks the bound base protocol bytes"
        )
    if protocol["base_coverage_protocol"] != _base_protocol_document():
        raise OcrSealedAcceptanceError(
            "OCR acceptance protocol changed its base coverage binding"
        )

    base_config = dict(config)
    base_config.update({
        "acceptance_scope": base.ACCEPTANCE_SCOPE,
        "preset": base.ACCEPTANCE_PRESET,
        "coverage_protocol_path": base.PROTOCOL_PATH.as_posix(),
        "coverage_protocol_sha256": base.PROTOCOL_SHA256,
    })
    try:
        base_coverage = base.validate_acceptance_cases(
            base_config,
            base_rows,
            base_payload,
            cases,
        )
    except base.SealedAcceptanceError as error:
        raise OcrSealedAcceptanceError(str(error)) from error

    first_scene = cases[OCR_X_LABEL_CASE_INDEX].get("scene")
    first_presentation = (
        first_scene.get("presentation") if isinstance(first_scene, Mapping) else None
    )
    if (
        not isinstance(first_presentation, Mapping)
        or first_presentation.get("x_label_visibility") != OCR_X_LABEL_VISIBILITY
    ):
        raise OcrSealedAcceptanceError(
            "OCR acceptance case 0 changed prescribed x-label visibility"
        )

    annotations: list[Mapping[str, Any]] = []
    for ordinal, case in enumerate(cases):
        if not isinstance(case, Mapping) or not isinstance(case.get("annotation"), Mapping):
            raise OcrSealedAcceptanceError(
                f"OCR acceptance case {ordinal} lacks an annotation object"
            )
        annotation = case["annotation"]
        try:
            source_coverage = validate_ocr_sealed_coverage([annotation])
        except OcrSealedCoverageError as error:
            raise OcrSealedAcceptanceError(str(error)) from error
        if source_coverage["truth_region_count"] == 0:
            raise OcrSealedAcceptanceError(
                f"OCR acceptance source {ordinal} has no selected OCR truth"
            )
        annotations.append(annotation)

    try:
        ocr_coverage = validate_ocr_sealed_coverage(annotations)
    except OcrSealedCoverageError as error:
        raise OcrSealedAcceptanceError(str(error)) from error
    if set(ocr_coverage["by_generator_role"]) != set(GENERATOR_ROLES):
        raise OcrSealedAcceptanceError(
            "OCR acceptance corpus does not represent all required generator roles"
        )
    if ocr_coverage["source_count"] != len(cases):
        raise OcrSealedAcceptanceError(
            "OCR acceptance corpus has incomplete source accounting"
        )

    return {
        "base_geometry_coverage": base_coverage,
        "ocr_coverage": ocr_coverage,
    }


__all__ = [
    "ACCEPTANCE_PRESET",
    "ACCEPTANCE_SCOPE",
    "GENERATOR_ROLES",
    "GENERATOR_SOURCE_PATHS",
    "PROTOCOL_PATH",
    "PROTOCOL_SCHEMA",
    "PROTOCOL_SHA256",
    "OcrSealedAcceptanceError",
    "acceptance_case_specs",
    "expected_protocol_document",
    "load_supported_protocol",
    "parse_supported_protocol",
    "require_supported_identity",
    "validate_acceptance_cases",
]
