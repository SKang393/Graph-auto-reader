# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Goal 22 sealed synthetic coverage identity and semantic safeguards."""

from __future__ import annotations

from copy import deepcopy
from dataclasses import replace
from functools import lru_cache
import hashlib
from pathlib import Path

import pytest

from ml.synthetic.dataset import PRESETS, _build_scenes
from ml.synthetic.sealed_acceptance import (
    ACCEPTANCE_PRESET,
    ACCEPTANCE_PURPOSE,
    ACCEPTANCE_SCOPE,
    GENERATOR_SOURCE_PATHS,
    HELD_OUT_FAMILIES,
    PROTOCOL_PATH,
    PROTOCOL_SHA256,
    SealedAcceptanceError,
    acceptance_case_specs,
    expected_coverage_document,
    load_supported_protocol,
    parse_supported_protocol,
    validate_acceptance_cases,
    verify_acceptance_font_dependencies,
)


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]


@lru_cache(maxsize=2)
def _semantic_cases(dataset_seed: int) -> tuple[dict, ...]:
    specs = acceptance_case_specs()
    scenes = _build_scenes(specs, dataset_seed, require_complete_style_catalog=True)
    cases = []
    for scene, spec in zip(scenes, specs, strict=True):
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
        cases.append({
            "scene": scene,
            "annotation": {
                "scene_id": scene["scene_id"],
                "seed": scene["seed"],
                "design": scene["design"],
                "canvas": {
                    "width": scene["canvas"]["width"],
                    "height": scene["canvas"]["height"],
                },
                "panels": [
                    {"markers": [{} for _point in panel["points"]]}
                    for panel in scene["panels"]
                ],
                "font": font,
                "fonts": [font],
            },
            "image_mode": spec.output_mode,
        })
    return tuple(cases)


def _config(dataset_seed: int) -> dict[str, object]:
    return {
        "dataset_seed": dataset_seed,
        "preset": ACCEPTANCE_PRESET,
        "purpose": ACCEPTANCE_PURPOSE,
        "acceptance_scope": ACCEPTANCE_SCOPE,
        "coverage_protocol_path": PROTOCOL_PATH.as_posix(),
        "coverage_protocol_sha256": PROTOCOL_SHA256,
    }


def _source_rows() -> list[dict[str, str]]:
    return [
        {
            "path": path.as_posix(),
            "sha256": PROTOCOL_SHA256 if path == PROTOCOL_PATH else "0" * 64,
        }
        for path in GENERATOR_SOURCE_PATHS
    ]


def test_fixed_protocol_preserves_every_case_dimension_except_renderer_family() -> None:
    protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    originals = (*PRESETS["smoke"], *PRESETS["real_range"])
    transformed = acceptance_case_specs()

    assert len(transformed) == 21
    assert PROTOCOL_SHA256 == hashlib.sha256(payload).hexdigest()
    assert protocol["required_coverage"] == expected_coverage_document()
    assert protocol["held_out_families"] == HELD_OUT_FAMILIES
    for original, changed in zip(originals, transformed, strict=True):
        assert changed == replace(original, renderer_family="hand_drawn")


def test_semantic_coverage_has_all_27_styles_and_five_held_out_axes() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    cases = _semantic_cases(7)

    observed = validate_acceptance_cases(
        _config(7), _source_rows(), payload, cases
    )

    assert observed == expected_coverage_document()
    assert len(observed["marker_style_catalog"]) == 27
    assert all(case["scene"]["families"] == HELD_OUT_FAMILIES for case in cases)


def test_distinct_feasible_dataset_seeds_have_disjoint_case_identities() -> None:
    first = {case["scene"]["scene_id"] for case in _semantic_cases(7)}
    second = {case["scene"]["scene_id"] for case in _semantic_cases(8)}

    assert len(first) == len(second) == 21
    assert first.isdisjoint(second)


def test_substituted_protocol_is_rejected_before_semantic_use() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    substituted = payload.replace(
        b"aggregate_only_marker_center_acceptance",
        b"aggregate_only_marker_centre_acceptance",
    )

    with pytest.raises(SealedAcceptanceError, match="SHA-256"):
        parse_supported_protocol(substituted)


@pytest.mark.parametrize("mutation", ["missing_case", "wrong_family", "annotation_gap"])
def test_semantic_coverage_rejects_incomplete_or_inconsistent_cases(
    mutation: str,
) -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    cases = list(deepcopy(_semantic_cases(7)))
    if mutation == "missing_case":
        cases.pop()
    elif mutation == "wrong_family":
        cases[0]["scene"]["families"]["marker"] = {
            "key": "geometric_basic", "split": "train",
        }
    else:
        cases[0]["annotation"]["panels"][0]["markers"].pop()

    with pytest.raises(
        SealedAcceptanceError,
        match="incomplete case|ordered identity|annotation marker",
    ):
        validate_acceptance_cases(_config(7), _source_rows(), payload, cases)


def test_acceptance_source_snapshot_must_include_exact_protocol_and_generator_paths() -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    rows = _source_rows()
    rows.pop()

    with pytest.raises(SealedAcceptanceError, match="source snapshot is incomplete"):
        validate_acceptance_cases(_config(7), rows, payload, _semantic_cases(7))


@pytest.mark.parametrize("mutation", ["shared_fallback", "unrecorded_sha", "mixed_dependency"])
def test_acceptance_font_provenance_fails_closed(mutation: str) -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    cases = list(deepcopy(_semantic_cases(7)))
    if mutation == "shared_fallback":
        cases[0]["annotation"]["font"]["resolved_file"] = "arial.ttf"
        cases[0]["annotation"]["fonts"][0]["resolved_file"] = "arial.ttf"
    elif mutation == "unrecorded_sha":
        cases[0]["annotation"]["font"]["sha256"] = "not-a-sha"
        cases[0]["annotation"]["fonts"][0]["sha256"] = "not-a-sha"
    else:
        cases[0]["annotation"]["font"]["sha256"] = "2" * 64
        cases[0]["annotation"]["fonts"][0]["sha256"] = "2" * 64

    with pytest.raises(SealedAcceptanceError, match="font provenance|one exact"):
        validate_acceptance_cases(_config(7), _source_rows(), payload, cases)


def test_acceptance_font_dependency_hash_is_verified_before_archive(
    tmp_path: Path,
) -> None:
    font_path = tmp_path / "segoepr.ttf"
    font_payload = b"synthetic font dependency fixture"
    font_path.write_bytes(font_payload)
    cases = list(deepcopy(_semantic_cases(7)))
    digest = hashlib.sha256(font_payload).hexdigest()
    for case in cases:
        case["annotation"]["font"]["resolved_path"] = str(font_path)
        case["annotation"]["font"]["sha256"] = digest
        case["annotation"]["fonts"][0]["resolved_path"] = str(font_path)
        case["annotation"]["fonts"][0]["sha256"] = digest

    assert verify_acceptance_font_dependencies(cases) == ({
        "resolved_file": "segoepr.ttf",
        "sha256": digest,
    },)

    font_path.write_bytes(b"changed dependency bytes")
    with pytest.raises(SealedAcceptanceError, match="recorded SHA-256"):
        verify_acceptance_font_dependencies(cases)


def test_archived_protocol_remains_interpretable_after_live_preset_edit(
    monkeypatch,
) -> None:
    _protocol, payload = load_supported_protocol(REPOSITORY_ROOT)
    archived_cases = _semantic_cases(7)
    changed_smoke = (
        replace(PRESETS["smoke"][0], session_count=3),
        *PRESETS["smoke"][1:],
    )
    monkeypatch.setitem(PRESETS, "smoke", changed_smoke)

    observed = validate_acceptance_cases(
        _config(7), _source_rows(), payload, archived_cases
    )

    assert observed["marker_count"] == 838
    with pytest.raises(SealedAcceptanceError, match="live synthetic presets differ"):
        load_supported_protocol(REPOSITORY_ROOT)
