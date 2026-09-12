# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Goal 22 full-OCR sealed coverage identity and semantic safeguards."""

from __future__ import annotations

from copy import deepcopy
from dataclasses import replace
from functools import lru_cache
import hashlib
from pathlib import Path

import pytest

from ml.synthetic.dataset import _build_scenes
from ml.synthetic import sealed_acceptance as base
from ml.synthetic.ocr_sealed_acceptance import (
    ACCEPTANCE_PRESET,
    ACCEPTANCE_SCOPE,
    GENERATOR_ROLES,
    GENERATOR_SOURCE_PATHS,
    PROTOCOL_PATH,
    PROTOCOL_SHA256,
    OcrSealedAcceptanceError,
    acceptance_case_specs,
    expected_protocol_document,
    load_supported_protocol,
    parse_supported_protocol,
    validate_acceptance_cases,
)


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]


def _text(identity: str, role: str, *, text: str = "A") -> dict[str, object]:
    return {
        "text_id": identity,
        "region_id": identity,
        "text": text,
        "role": role,
        "visible": True,
        "rendered_pixel_box": [1.0, 2.0, 4.0, 5.0],
    }


@lru_cache(maxsize=1)
def _semantic_cases() -> tuple[dict, ...]:
    specs = acceptance_case_specs()
    scenes = _build_scenes(specs, 7, require_complete_style_catalog=True)
    cases = []
    for ordinal, (scene, spec) in enumerate(zip(scenes, specs, strict=True)):
        font = {
            "requested": "handwritten",
            "resolved_file": "segoepr.ttf",
            "resolved_path": "C:/Windows/Fonts/segoepr.ttf",
            "family": "Segoe Print",
            "style": "Regular",
            "size_px": scene["presentation"].get(
                "font_size_px", scene["presentation"].get("font_size", 14)
            ),
            "source": "system",
            "sha256": "1" * 64,
            "bundled": False,
        }
        texts = (
            [_text(f"role-{index}", role) for index, role in enumerate(GENERATOR_ROLES)]
            if ordinal == 0
            else [_text(f"source-{ordinal}", "annotation")]
        )
        cases.append({
            "scene": scene,
            "annotation": {
                "scene_id": scene["scene_id"],
                "seed": scene["seed"],
                "design": scene["design"],
                "coordinate_space": "original_pixels",
                "canvas": {
                    "width": scene["canvas"]["width"],
                    "height": scene["canvas"]["height"],
                },
                "texts": texts,
                "panels": [
                    {"markers": [{} for _point in panel["points"]], "texts": []}
                    for panel in scene["panels"]
                ],
                "font": font,
                "fonts": [font],
            },
            "image_mode": spec.output_mode,
        })
    return tuple(cases)


def _config(**changes: object) -> dict[str, object]:
    config = {
        "dataset_seed": 7,
        "preset": ACCEPTANCE_PRESET,
        "purpose": base.ACCEPTANCE_PURPOSE,
        "acceptance_scope": ACCEPTANCE_SCOPE,
        "coverage_protocol_path": PROTOCOL_PATH.as_posix(),
        "coverage_protocol_sha256": PROTOCOL_SHA256,
    }
    config.update(changes)
    return config


def _source_rows() -> list[dict[str, str]]:
    return [
        {
            "path": path.as_posix(),
            "sha256": (
                PROTOCOL_SHA256
                if path == PROTOCOL_PATH
                else base.PROTOCOL_SHA256
                if path == base.PROTOCOL_PATH
                else "0" * 64
            ),
        }
        for path in GENERATOR_SOURCE_PATHS
    ]


def _snapshots(protocol_payload: bytes) -> dict[str, bytes]:
    return {
        PROTOCOL_PATH.as_posix(): protocol_payload,
        base.PROTOCOL_PATH.as_posix(): (
            REPOSITORY_ROOT / base.PROTOCOL_PATH
        ).read_bytes(),
    }


def test_protocol_binds_exact_base_coverage_and_source_inventory() -> None:
    protocol, payload = load_supported_protocol(REPOSITORY_ROOT)

    assert protocol == expected_protocol_document()
    assert hashlib.sha256(payload).hexdigest() == PROTOCOL_SHA256
    assert protocol["base_coverage_protocol"] == {
        "acceptance_scope": base.ACCEPTANCE_SCOPE,
        "path": base.PROTOCOL_PATH.as_posix(),
        "sha256": base.PROTOCOL_SHA256,
    }
    assert protocol["ordered_case_overrides"] == [{
        "ordered_case_index": 0,
        "field": "presentation.x_label_visibility",
        "base_value": "hidden",
        "required_value": "visible",
        "purpose": "expose_x_tick_truth_for_full_ocr_coverage",
        "all_other_case_spec_fields_unchanged": True,
    }]
    assert set(GENERATOR_SOURCE_PATHS) == set(base.GENERATOR_SOURCE_PATHS) | {
        PROTOCOL_PATH,
        Path("ml/synthetic/ocr_sealed_acceptance.py"),
        Path("ml/synthetic/ocr_sealed_coverage.py"),
    }


def test_ocr_case_zero_alone_exposes_x_tick_truth() -> None:
    originals = base.acceptance_case_specs()
    specs = acceptance_case_specs()

    assert specs[0] == replace(
        originals[0], presentation={"x_label_visibility": "visible"}
    )
    assert specs[1:] == originals[1:]
    scenes = _build_scenes(specs, 7, require_complete_style_catalog=True)
    x_tick_truth = [
        text
        for text in scenes[0]["annotations"]["text_regions"]
        if text["role"] == "x_tick"
    ]
    assert x_tick_truth
    assert all(text["visible"] and text["text"].strip() for text in x_tick_truth)


def test_hidden_ocr_case_zero_x_labels_are_rejected() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    cases = deepcopy(_semantic_cases())
    cases[0]["scene"]["presentation"]["x_label_visibility"] = "hidden"

    with pytest.raises(OcrSealedAcceptanceError, match="prescribed x-label visibility"):
        validate_acceptance_cases(
            _config(), _source_rows(), payload, cases,
            snapshot_payloads=_snapshots(payload),
        )


def test_validation_returns_base_geometry_and_full_ocr_denominators() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)

    observed = validate_acceptance_cases(
        _config(),
        _source_rows(),
        payload,
        _semantic_cases(),
        snapshot_payloads=_snapshots(payload),
    )

    assert observed["base_geometry_coverage"] == base.expected_coverage_document()
    assert observed["ocr_coverage"]["source_count"] == 21
    assert observed["ocr_coverage"]["truth_region_count"] == 28
    assert observed["ocr_coverage"]["truth_character_count"] == 28
    assert set(observed["ocr_coverage"]["by_generator_role"]) == set(GENERATOR_ROLES)


def test_protocol_hash_is_checked_before_source_inventory() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    substituted = payload.replace(b"full_ocr", b"whole_ocr")

    with pytest.raises(OcrSealedAcceptanceError, match="SHA-256"):
        validate_acceptance_cases(
            _config(),
            _source_rows()[:-1],
            substituted,
            _semantic_cases(),
            snapshot_payloads=_snapshots(payload),
        )


@pytest.mark.parametrize("mutation", ["missing_path", "wrong_protocol_hash"])
def test_exact_ocr_source_inventory_is_required(mutation: str) -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    rows = _source_rows()
    if mutation == "missing_path":
        rows.pop()
        message = "source snapshot is incomplete"
    else:
        rows[0]["sha256"] = "0" * 64
        message = "protocol is absent"

    with pytest.raises(OcrSealedAcceptanceError, match=message):
        validate_acceptance_cases(
            _config(), rows, payload, _semantic_cases(),
            snapshot_payloads=_snapshots(payload),
        )


@pytest.mark.parametrize("field", ["purpose", "preset"])
def test_ocr_config_rejects_wrong_purpose_or_preset(field: str) -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)

    with pytest.raises(OcrSealedAcceptanceError, match="wrong purpose or preset"):
        validate_acceptance_cases(
            _config(**{field: "wrong"}),
            _source_rows(),
            payload,
            _semantic_cases(),
            snapshot_payloads=_snapshots(payload),
        )


def test_each_source_requires_selected_ocr_truth() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    cases = deepcopy(_semantic_cases())
    cases[1]["annotation"]["texts"] = []

    with pytest.raises(OcrSealedAcceptanceError, match="has no selected OCR truth"):
        validate_acceptance_cases(
            _config(), _source_rows(), payload, cases,
            snapshot_payloads=_snapshots(payload),
        )


def test_full_corpus_requires_all_eight_generator_roles() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    cases = deepcopy(_semantic_cases())
    cases[0]["annotation"]["texts"][0]["role"] = "annotation"

    with pytest.raises(OcrSealedAcceptanceError, match="all required generator roles"):
        validate_acceptance_cases(
            _config(), _source_rows(), payload, cases,
            snapshot_payloads=_snapshots(payload),
        )


@pytest.mark.parametrize("mutation", ["coordinate_space", "duplicate_text_id"])
def test_selected_truth_identity_and_original_coordinates_are_enforced(
    mutation: str,
) -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    cases = deepcopy(_semantic_cases())
    if mutation == "coordinate_space":
        cases[0]["annotation"]["coordinate_space"] = "panel_pixels"
        message = "original pixel"
    else:
        cases[0]["annotation"]["texts"][1]["text_id"] = " role-0 "
        message = "text identities repeat"

    with pytest.raises(OcrSealedAcceptanceError, match=message):
        validate_acceptance_cases(
            _config(), _source_rows(), payload, cases,
            snapshot_payloads=_snapshots(payload),
        )


def test_bound_base_protocol_bytes_and_geometry_are_required() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    missing_base = _snapshots(payload)
    del missing_base[base.PROTOCOL_PATH.as_posix()]
    with pytest.raises(OcrSealedAcceptanceError, match="base protocol bytes"):
        validate_acceptance_cases(
            _config(), _source_rows(), payload, _semantic_cases(),
            snapshot_payloads=missing_base,
        )

    cases = deepcopy(_semantic_cases())
    cases[0]["scene"]["canvas"]["width"] += 1
    with pytest.raises(OcrSealedAcceptanceError, match="range dimensions"):
        validate_acceptance_cases(
            _config(), _source_rows(), payload, cases,
            snapshot_payloads=_snapshots(payload),
        )


def test_substituted_protocol_is_rejected() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    substituted = payload.replace(b"original_pixels", b"panel_pixels")

    with pytest.raises(OcrSealedAcceptanceError, match="SHA-256"):
        parse_supported_protocol(substituted)
