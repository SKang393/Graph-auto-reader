# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
import json
from types import SimpleNamespace

import pytest
import numpy as np

import score_participant_lane_candidate as scorer


def _polygon(left: float, top: float, right: float, bottom: float) -> dict:
    return {
        "points": [
            {"x": left, "y": top, "is_finite": True},
            {"x": right, "y": top, "is_finite": True},
            {"x": right, "y": bottom, "is_finite": True},
            {"x": left, "y": bottom, "is_finite": True},
        ],
    }


def _raw(region_id: str, left: float, right: float) -> dict:
    return {
        "region_id": region_id,
        "coordinate_space": "source_original_pixels",
        "panel_polygon": _polygon(left, 10, right, 20),
        "source_polygon": _polygon(left, 10, right, 20),
    }


def _panel() -> SimpleNamespace:
    return SimpleNamespace(
        panel_id="panel", source_sha256="a" * 64,
        width=100, height=100, source_width=100, source_height=100,
        panel_to_source_matrix=(1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0),
    )


def _completed_record() -> dict:
    members = ["left", "right"]
    merged_id = scorer._merged_id(members)
    merged = {
        "region_id": merged_id,
        "member_raw_region_ids": members,
        "assembly_kind": "participant_lane",
        "coordinate_space": "source_original_pixels",
        "panel_polygon": _polygon(1, 10, 19, 20),
        "source_polygon": _polygon(1, 10, 19, 20),
    }
    recognized = {
        "region_id": merged_id,
        "coordinate_space": "source_original_pixels",
        "panel_polygon": _polygon(1, 10, 19, 20),
        "source_polygon": _polygon(1, 10, 19, 20),
        "text": "Participant",
        "role": "participant",
    }
    return {
        "status": "completed",
        "assembly_context": {
            "composition_version": scorer.ASSEMBLY_COMPOSITION,
            "plot_bounds_panel_ltrb": [50, 0, 90, 90],
        },
        "raw_detector_regions": [_raw("left", 1, 9), _raw("right", 11, 19)],
        "effective_regions": [merged],
        "recognized_regions": [recognized],
        "region_failures": [],
    }


def _inside_plot_record(phase_divider_xs=()) -> dict:
    members = ["left", "right"]
    merged_id = scorer._merged_id(members, scorer.INSIDE_PLOT_PROFILE)
    merged = {
        "region_id": merged_id,
        "member_raw_region_ids": members,
        "assembly_kind": "inside_plot",
        "coordinate_space": "source_original_pixels",
        "panel_polygon": _polygon(1, 10, 19, 20),
        "source_polygon": _polygon(1, 10, 19, 20),
    }
    return {
        "status": "completed",
        "assembly_context": {
            "composition_version": scorer.INSIDE_PLOT_PROFILE.assembly_composition,
            "plot_bounds_panel_ltrb": [0, 0, 90, 90],
            "phase_divider_xs": list(phase_divider_xs),
        },
        "raw_detector_regions": [_raw("left", 1, 9), _raw("right", 11, 19)],
        "effective_regions": [merged],
        "recognized_regions": [],
        "region_failures": [{"region_id": merged_id}],
    }


def _identity_effective(region_id: str, left: float, right: float) -> dict:
    return {
        "region_id": region_id,
        "member_raw_region_ids": [region_id],
        "assembly_kind": "identity",
        "coordinate_space": "source_original_pixels",
        "panel_polygon": _polygon(left, 10, right, 20),
        "source_polygon": _polygon(left, 10, right, 20),
    }


def test_replay_matches_public_sort_membership_union_and_identifier() -> None:
    record = _completed_record()
    raw, effective, predictions, failures = scorer._validate_panel_regions(
        record, _panel(), (50.0, 0.0, 90.0, 90.0))
    assert [row.region_id for row in raw] == ["left", "right"]
    assert len(effective) == 1
    assert effective[0].member_ids == ("left", "right")
    assert effective[0].region_id == (
        "participant-lane:"
        + scorer.sha256(
            f"{scorer.ASSEMBLY_COMPOSITION}\nleft\nright".encode("utf-8")
        ).hexdigest()
    )
    assert predictions[0].text == "Participant"
    assert predictions[0].role == "participant"
    assert failures == 0


def test_default_profile_preserves_participant_lane_replay() -> None:
    record = _completed_record()
    implicit = scorer._validate_panel_regions(
        record, _panel(), (50.0, 0.0, 90.0, 90.0))
    explicit = scorer._validate_panel_regions(
        record, _panel(), (50.0, 0.0, 90.0, 90.0),
        scorer.PARTICIPANT_LANE_PROFILE, ())
    assert implicit == explicit


def test_inside_plot_replay_matches_membership_union_and_identifier() -> None:
    record = _inside_plot_record((50.0,))
    raw, effective, predictions, failures = scorer._validate_panel_regions(
        record, _panel(), (0.0, 0.0, 90.0, 90.0),
        scorer.INSIDE_PLOT_PROFILE, (50.0,))
    assert [row.region_id for row in raw] == ["left", "right"]
    assert len(effective) == 1
    assert effective[0].member_ids == ("left", "right")
    assert effective[0].region_id == (
        "inside-plot:"
        + scorer.sha256(
            f"{scorer.INSIDE_PLOT_PROFILE.assembly_composition}\nleft\nright".encode("utf-8")
        ).hexdigest()
    )
    assert predictions == ()
    assert failures == 1


def test_pixel_bounds_replay_uses_original_pixels_and_preserves_failed_region() -> None:
    record = _inside_plot_record((50.0,))
    record["assembly_context"]["pixel_bounds_composition_version"] = scorer.PIXEL_BOUNDS_COMPOSITION
    gray = np.full((100, 100), 255, dtype=np.uint8)
    gray[12:18, 3:17] = 0
    for key in ("panel_polygon", "source_polygon"):
        record["effective_regions"][0][key] = _polygon(3, 12, 17, 18)
    raw, effective, predictions, failures = scorer._validate_panel_regions(
        record, _panel(), (0.0, 0.0, 90.0, 90.0),
        scorer.PIXEL_BOUNDS_PROFILE, (50.0,), gray)
    assert len(raw) == 2 and len(effective) == 1
    assert effective[0].member_ids == ("left", "right")
    assert predictions == () and failures == 1
    gray[10, 1] = 0  # Claimed bounds now discard an actual foreground pixel.
    with pytest.raises(scorer.EvidenceError, match="deterministic assembly replay"):
        scorer._validate_panel_regions(record, _panel(), (0.0, 0.0, 90.0, 90.0),
            scorer.PIXEL_BOUNDS_PROFILE, (50.0,), gray)


@pytest.mark.parametrize("fault", ["missing_pixels", "wrong_size", "wrong_composition"])
def test_pixel_bounds_requires_pixels_and_distinct_composition(fault) -> None:
    record = _inside_plot_record((50.0,))
    record["assembly_context"]["pixel_bounds_composition_version"] = scorer.PIXEL_BOUNDS_COMPOSITION
    gray = np.full((100, 100), 255, dtype=np.uint8)
    if fault == "missing_pixels":
        gray = None
    elif fault == "wrong_size":
        gray = gray[:50]
    else:
        record["assembly_context"]["pixel_bounds_composition_version"] = "unbound"
    with pytest.raises(scorer.EvidenceError, match="composition or original pixels"):
        scorer._validate_panel_regions(record, _panel(), (0.0, 0.0, 90.0, 90.0),
            scorer.PIXEL_BOUNDS_PROFILE, (50.0,), gray)


def test_pixel_profile_keeps_fixed_request_and_unapproved_candidate_identity() -> None:
    profile = scorer.PIXEL_BOUNDS_PROFILE
    assert profile.requires_phase_dividers and profile.refines_pixel_bounds
    assert profile.candidate_composition != scorer.INSIDE_PLOT_PROFILE.candidate_composition
    with pytest.raises(scorer.EvidenceError, match="exact authenticated"):
        scorer._validate_profile_request({}, "a" * 64, profile)


def test_inside_plot_replay_rejects_union_crossing_authenticated_divider() -> None:
    record = _inside_plot_record((10.0,))
    record["effective_regions"] = [
        _identity_effective("left", 1, 9),
        _identity_effective("right", 11, 19),
    ]
    record["region_failures"] = [{"region_id": "left"}, {"region_id": "right"}]
    _, effective, _, failures = scorer._validate_panel_regions(
        record, _panel(), (0.0, 0.0, 90.0, 90.0),
        scorer.INSIDE_PLOT_PROFILE, (10.0,))
    assert [row.member_ids for row in effective] == [("left",), ("right",)]
    assert failures == 2


@pytest.mark.parametrize(
    ("mutate", "message"),
    [
        (lambda context: context.pop("phase_divider_xs"), "unknown or missing"),
        (lambda context: context.update(phase_divider_xs=[float("nan")]), "finite"),
        (lambda context: context.update(phase_divider_xs=[49.0]), "runtime phase dividers"),
    ],
)
def test_inside_plot_context_fails_closed(mutate, message) -> None:
    record = _inside_plot_record((50.0,))
    mutate(record["assembly_context"])
    with pytest.raises(scorer.EvidenceError, match=message):
        scorer._validate_panel_regions(
            record, _panel(), (0.0, 0.0, 90.0, 90.0),
            scorer.INSIDE_PLOT_PROFILE, (50.0,))


def test_candidate_header_is_profile_bound() -> None:
    candidate = {
        "schema": scorer.geometry.CANDIDATE_SCHEMA,
        "scope": scorer.geometry.CANDIDATE_SCOPE,
        "production_approved": False,
        "training_input_ready": False,
        "composition_version": scorer.INSIDE_PLOT_PROFILE.candidate_composition,
        "native_path": "native",
        "native_sha256": "a" * 64,
        "native_scope": "reviewed-source-runtime-local-diagnostic",
        "license_inputs": [],
        "detector": {},
        "recognizer": {},
        "execution_assemblies": [],
    }
    scorer._validate_candidate_header(candidate, scorer.INSIDE_PLOT_PROFILE)
    with pytest.raises(scorer.EvidenceError, match="participant_lane candidate"):
        scorer._validate_candidate_header(candidate, scorer.PARTICIPANT_LANE_PROFILE)


def test_inside_plot_request_allows_only_exact_archived_capture_source_rebind() -> None:
    request = {"capture_source": dict(scorer.EXPECTED_INSIDE_PLOT_CAPTURE_SOURCE)}
    scorer._validate_profile_request(
        request, scorer.EXPECTED_INSIDE_PLOT_REQUEST_SHA256,
        scorer.INSIDE_PLOT_PROFILE)
    scorer._validate_profile_request(request, "0" * 64, scorer.PARTICIPANT_LANE_PROFILE)

    changed = deepcopy(request)
    changed["capture_source"]["path"] = "artifacts/other-source.cs"
    with pytest.raises(scorer.EvidenceError, match="exact authenticated historical"):
        scorer._validate_profile_request(
            changed, scorer.EXPECTED_INSIDE_PLOT_REQUEST_SHA256,
            scorer.INSIDE_PLOT_PROFILE)
    with pytest.raises(scorer.EvidenceError, match="exact authenticated historical"):
        scorer._validate_profile_request(
            request, "0" * 64, scorer.INSIDE_PLOT_PROFILE)


def test_inside_plot_evaluation_request_diff_is_exactly_one_source_path() -> None:
    root = scorer.ROOT
    evaluation_path = (
        root / "artifacts/goal22-runs/ocr-v45-evaluation-inputs-v1/"
        "historical-evaluation-request.json"
    )
    evaluation = json.loads(evaluation_path.read_text(encoding="utf-8"))
    capture = deepcopy(evaluation)
    capture["capture_source"]["path"] = (
        "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadTensorCapture.cs"
    )
    resolved, rebound, digest = scorer._evaluation_request_binding(
        root, root / "old-request.json", capture, "1" * 64,
        evaluation_path, scorer.EXPECTED_INSIDE_PLOT_REQUEST_SHA256,
        scorer.INSIDE_PLOT_PROFILE)
    assert resolved == evaluation_path.resolve()
    assert rebound == evaluation
    assert digest == scorer.EXPECTED_INSIDE_PLOT_REQUEST_SHA256

    changed_capture = deepcopy(capture)
    changed_capture["maximum_side_length"] += 1
    with pytest.raises(scorer.EvidenceError, match="differs beyond"):
        scorer._evaluation_request_binding(
            root, root / "old-request.json", changed_capture, "1" * 64,
            evaluation_path, scorer.EXPECTED_INSIDE_PLOT_REQUEST_SHA256,
            scorer.INSIDE_PLOT_PROFILE)


@pytest.mark.parametrize(
    ("mutate", "message"),
    [
        (lambda row: row["effective_regions"][0].update(
            member_raw_region_ids=["left", "left"]), "identity or membership"),
        (lambda row: row["effective_regions"][0].update(
            panel_polygon=_polygon(1, 10, 18, 20)), "deterministic assembly replay"),
        (lambda row: row["assembly_context"].update(
            plot_bounds_panel_ltrb=[49, 0, 90, 90]), "authenticated runtime plot bounds"),
        (lambda row: row["recognized_regions"][0].update(
            region_id="left"), "absent from effective output"),
    ],
)
def test_effective_inventory_fails_closed_on_nonreplayed_evidence(mutate, message) -> None:
    record = _completed_record()
    mutate(record)
    with pytest.raises(scorer.EvidenceError, match=message):
        scorer._validate_panel_regions(record, _panel(), (50.0, 0.0, 90.0, 90.0))


def test_recognized_and_failures_partition_effective_inventory() -> None:
    record = _completed_record()
    failure = {"region_id": record["effective_regions"][0]["region_id"]}
    record["recognized_regions"] = []
    record["region_failures"] = [failure]
    _, _, predictions, failures = scorer._validate_panel_regions(
        record, _panel(), (50.0, 0.0, 90.0, 90.0))
    assert predictions == ()
    assert failures == 1

    duplicated = deepcopy(record)
    duplicated["recognized_regions"] = [_completed_record()["recognized_regions"][0]]
    with pytest.raises(scorer.EvidenceError, match="do not partition effective output"):
        scorer._validate_panel_regions(
            duplicated, _panel(), (50.0, 0.0, 90.0, 90.0))


def test_failed_preassembly_panel_keeps_raw_evidence_without_fabricating_effective_output() -> None:
    record = _completed_record()
    record["status"] = "failed"
    record["effective_regions"] = []
    record["recognized_regions"] = []
    record.pop("region_failures")
    raw, effective, predictions, failures = scorer._validate_panel_regions(
        record, _panel(), (50.0, 0.0, 90.0, 90.0))
    assert len(raw) == 2
    assert effective == () and predictions == () and failures == 0


def test_full_text_metric_remains_the_frozen_full_denominator_metric() -> None:
    truth = scorer.metric.FullTextTruth(
        "t" * 64, "a" * 64, scorer.Box(1, 10, 19, 20),
        "Participant", "participant", "participant")
    prediction = scorer.metric.FullTextPrediction(
        "prediction", "a" * 64, scorer.Box(1, 10, 19, 20),
        "Participant", "participant")
    result = scorer.metric._score_split((truth,), {"a" * 64: (prediction,)})
    assert result["truth_region_count"] == 1
    assert result["geometry_matched_region_count"] == 1
    assert result["recognition_exact_accuracy"] == 1.0
    assert result["character_error_rate"] == 0.0
    assert result["role_accuracy"] == 1.0


def test_historical_capture_assemblies_use_exact_preserved_bytes_only(tmp_path) -> None:
    root = tmp_path.resolve()
    backup_root = root / "artifacts" / "historical-runtime"
    backup_root.mkdir(parents=True)
    original_root = root / "tools" / "GraphReader.SyntheticRuntimeEvidence" / "bin" / "Release"
    rows = []
    digests = {}
    for index, name in enumerate(sorted(scorer.HISTORICAL_RUNTIME_FILES)):
        backup = backup_root / name
        backup.write_bytes(f"payload-{index}".encode("ascii"))
        digest = scorer.sha256(backup.read_bytes()).hexdigest()
        original = original_root / name
        rows.append({
            "sha256": digest,
            "backup_path": backup.relative_to(root).as_posix(),
            "original_path": original.relative_to(root).as_posix(),
        })
        digests[name] = digest
    manifest = root / "artifacts" / "historical-manifest.json"
    manifest.write_text(json.dumps(rows), encoding="utf-8")
    manifest_sha = scorer.sha256(manifest.read_bytes()).hexdigest()
    historical = scorer._validate_historical_capture_runtime(root, manifest, manifest_sha)
    requested = [
        {
            "name": name.removesuffix(".dll"),
            "path": (original_root / name).relative_to(root).as_posix(),
            "sha256": digests[name],
        }
        for name in (
            "GraphReader.SyntheticRuntimeEvidence.dll", "GraphReader.App.dll",
            "GraphReader.Ocr.dll", "GraphReader.Inference.dll",
        )
    ]
    validated = scorer._validate_historical_capture_assemblies(root, requested, historical)
    assert {row["name"] for row in validated} == scorer.geometry.EXPECTED_ASSEMBLIES

    changed = deepcopy(requested)
    changed[0]["sha256"] = "0" * 64
    with pytest.raises(scorer.EvidenceError, match="no exact historical backup"):
        scorer._validate_historical_capture_assemblies(root, changed, historical)
