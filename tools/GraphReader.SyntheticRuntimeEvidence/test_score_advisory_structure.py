# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path

import pytest

import score_advisory_structure as scorer


MODEL_ID = "11111111-2222-4333-8444-555555555555"
COMPONENT_ID = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"


def _polygon(left=1.25, top=2.5, right=5.0, bottom=7.75):
    return {
        "points": [
            {"x": left, "y": top},
            {"x": right, "y": top},
            {"x": right, "y": bottom},
            {"x": left, "y": bottom},
        ],
    }


def _raw_region():
    return {
        "region_id": MODEL_ID,
        "polygon": _polygon(1, 1, 9, 9),
        "detection_confidence": 0.875,
        "coordinate_space": "original_pixels",
    }


def _contours():
    return {
        MODEL_ID: {
            "returned_region_id": MODEL_ID,
            "initial_polygon": _polygon(),
            "expanded_polygon": _polygon(1, 1, 9, 9),
            "detection_confidence": 0.875,
        },
    }


def _require_local_artifacts(*relative_paths):
    missing = [
        path for path in relative_paths
        if not (scorer.REPOSITORY_ROOT / path).is_file()
    ]
    if missing:
        pytest.skip("local frozen evidence artifacts are not present in this checkout")


def test_reviewed_protocol_binds_full_denominator_and_no_approval():
    protocol, digest, identities = scorer._load_protocol(
        scorer.REPOSITORY_ROOT / scorer.PROTOCOL_PATH)
    assert digest == scorer.EXPECTED_PROTOCOL_SHA256
    assert identities["train_source_count"] == 4
    assert identities["train_panel_count"] == 4
    assert identities["train_truth_count"] == 146
    assert identities["dev_source_count"] == 3
    assert identities["dev_panel_count"] == 9
    assert identities["dev_truth_count"] == 183
    assert protocol["budget"]["optimizer_steps"] == 0
    assert protocol["budget"]["private_reads"] == 0
    assert protocol["budget"]["sealed_runs"] == 0
    assert protocol["budget"]["production_approval"] is False
    assert protocol["budget"]["release_eligible"] is False


def test_actual_candidate_changes_only_admission_and_protocol():
    candidate_relative = Path(
        "artifacts/synthetic-runtime-evidence/advisory-structure-source-c96/candidate.json")
    _require_local_artifacts(scorer.BASELINE_CANDIDATE_PATH, candidate_relative)
    baseline_path = scorer.REPOSITORY_ROOT / scorer.BASELINE_CANDIDATE_PATH
    candidate_path = scorer.REPOSITORY_ROOT / candidate_relative
    baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    candidate_bytes = candidate_path.read_bytes()
    assert sha256(candidate_bytes).hexdigest() == (
        "eed14ce421d56b8251213043337b2867ab7f96410404544341e512b27ffabbdd"
    )
    candidate = json.loads(candidate_bytes)
    scorer._validate_isolated_candidate(
        baseline, candidate, scorer.EXPECTED_PROTOCOL_SHA256)

    changed = deepcopy(candidate)
    changed["production_approved"] = True
    with pytest.raises(scorer.EvidenceError, match="changes more than"):
        scorer._validate_isolated_candidate(
            baseline, changed, scorer.EXPECTED_PROTOCOL_SHA256)


def test_real_candidate_ancestry_reaches_original_axis_baseline():
    _require_local_artifacts(
        scorer.BASELINE_CANDIDATE_PATH,
        scorer.initial_scorer.ORIGINAL_CANDIDATE_PATH,
        scorer.initial_scorer.DB_PROTOCOL_PATH,
        Path("artifacts/synthetic-runtime-evidence/official-metadata-source-c96/candidate.json"),
    )
    evaluator_sha = sha256(Path(scorer.__file__).read_bytes()).hexdigest()
    scorer._validate_evaluator_sources(evaluator_sha)
    baseline_raw, _ = scorer.original_scorer._validate_complete_candidate(
        scorer.REPOSITORY_ROOT / scorer.BASELINE_CANDIDATE_PATH,
        scorer.EXPECTED_BASELINE_CANDIDATE_SHA256)
    axis_candidate, _, _ = scorer._validate_baseline_ancestry(
        baseline_raw, scorer.initial_scorer.EXPECTED_PROTOCOL_SHA256)
    _, _, bound = scorer.db_geometry._load_protocol(
        scorer.REPOSITORY_ROOT / scorer.initial_scorer.DB_PROTOCOL_PATH)
    db_candidate = scorer.db_geometry._validate_candidate(*bound["runtime_candidate"])
    assert db_candidate["sha256"] == axis_candidate["sha256"]
    assert db_candidate["sha256"] != scorer.EXPECTED_BASELINE_CANDIDATE_SHA256

    changed = deepcopy(baseline_raw)
    changed["ocr_model_input"] = "axis_masked"
    with pytest.raises(scorer.EvidenceError, match="changes more than"):
        scorer._validate_baseline_ancestry(
            changed, scorer.initial_scorer.EXPECTED_PROTOCOL_SHA256)


def test_advisory_region_id_matches_binarywriter_guid_contract():
    assert scorer._advisory_region_id(MODEL_ID, _polygon()) == (
        "cd954bcc-f2f2-7bcc-6a3c-f7850f3aa768"
    )
    changed = _polygon()
    changed["points"][0]["x"] += 0.25
    assert scorer._advisory_region_id(MODEL_ID, changed) != (
        "cd954bcc-f2f2-7bcc-6a3c-f7850f3aa768"
    )


def test_mapping_accepts_visible_recognition_failure_without_losing_detector_region():
    raw = _raw_region()
    contours = _contours()
    output_id = scorer._advisory_region_id(MODEL_ID, contours[MODEL_ID]["initial_polygon"])
    by_raw, detector_regions, failures = scorer._validate_advisory_mapping(
        [raw], contours, [], [{"region_id": output_id, "failure_code": "recognition-failed"}])
    assert by_raw == {}
    assert failures == 1
    assert detector_regions == [{
        **raw,
        "region_id": output_id,
        "polygon": contours[MODEL_ID]["initial_polygon"],
    }]


def test_mapping_requires_successful_regions_in_sorted_detector_subsequence():
    first = _raw_region()
    second = deepcopy(first)
    second["region_id"] = "22222222-3333-4444-8555-666666666666"
    second["polygon"] = _polygon(10, 10, 18, 18)
    contours = _contours()
    contours[second["region_id"]] = {
        "returned_region_id": second["region_id"],
        "initial_polygon": _polygon(10, 20, 14, 24),
        "expanded_polygon": deepcopy(second["polygon"]),
        "detection_confidence": second["detection_confidence"],
    }
    first_id = scorer._advisory_region_id(
        MODEL_ID, contours[MODEL_ID]["initial_polygon"])
    second_id = scorer._advisory_region_id(
        second["region_id"], contours[second["region_id"]]["initial_polygon"])
    final_by_id = {
        first_id: {
            "region_id": first_id,
            "polygon": contours[MODEL_ID]["initial_polygon"],
            "coordinate_space": "original_pixels",
        },
        second_id: {
            "region_id": second_id,
            "polygon": contours[second["region_id"]]["initial_polygon"],
            "coordinate_space": "original_pixels",
        },
    }
    final = [final_by_id[identifier] for identifier in sorted(final_by_id)]
    _, detector, failures = scorer._validate_advisory_mapping(
        [second, first], contours, final, [])
    assert [region["region_id"] for region in detector] == [first_id, second_id]
    assert failures == 0

    with pytest.raises(scorer.EvidenceError, match="ordered detector subsequence"):
        scorer._validate_advisory_mapping(
            [second, first], contours, list(reversed(final)), [])


@pytest.mark.parametrize(
    "final_regions,failures,error",
    [
        ([], [], "omit or add"),
        ([{"region_id": "00000000-0000-0000-0000-000000000000"}], [], "omit or add"),
    ],
)
def test_mapping_rejects_omitted_or_foreign_detector_identity(final_regions, failures, error):
    with pytest.raises(scorer.EvidenceError, match=error):
        scorer._validate_advisory_mapping(
            [_raw_region()], _contours(), final_regions, failures)


def test_mapping_rejects_region_reported_as_success_and_failure():
    contours = _contours()
    output_id = scorer._advisory_region_id(MODEL_ID, contours[MODEL_ID]["initial_polygon"])
    final = {"region_id": output_id, "polygon": contours[MODEL_ID]["initial_polygon"]}
    with pytest.raises(scorer.EvidenceError, match="both recognized and failed"):
        scorer._validate_advisory_mapping(
            [_raw_region()], contours, [final], [{"region_id": output_id}])


def test_descriptive_changes_counts_current_recognition_failure_without_rejecting_baseline(
    monkeypatch,
):
    raw = _raw_region()
    component = {"region_id": COMPONENT_ID}
    contours = _contours()
    baseline_id = "bbbbbbbb-1111-4222-8333-cccccccccccc"
    monkeypatch.setattr(
        scorer.initial_scorer, "_consensus_pairs", lambda *_: [(raw, component)])
    monkeypatch.setattr(
        scorer.initial_scorer, "_initial_output_region_id", lambda *_: baseline_id)
    baseline_panel = {
        "ocr_proposal_diagnostic": {"component_regions": [component]},
        "ocr": {"regions": [{"region_id": baseline_id, "text": "one"}]},
    }
    result = scorer._descriptive_changes(baseline_panel, [raw], contours, {})
    assert result["baseline_selected_compared"] == 0
    assert result["baseline_selected_recognition_failures"] == 1
    assert result["newly_admitted_recognized"] == 0


def test_report_identity_requires_advisory_fields_and_preserved_geometry():
    baseline = {
        "ocr_adapter_id": (
            "prefix:" + scorer.initial_scorer.INITIAL_COMPOSITION + ":suffix"),
        "geometry_protocol_sha256": scorer.initial_scorer.EXPECTED_PROTOCOL_SHA256,
    }
    current = {
        "ocr_adapter_id": scorer._expected_adapter_id(baseline["ocr_adapter_id"]),
        "geometry_protocol_sha256": scorer.initial_scorer.EXPECTED_PROTOCOL_SHA256,
        "structure_admission": "advisory",
        "admission_protocol_sha256": scorer.EXPECTED_PROTOCOL_SHA256,
    }
    scorer._validate_report_identity(current, baseline, scorer.EXPECTED_PROTOCOL_SHA256)
    current["structure_admission"] = "required"
    with pytest.raises(scorer.EvidenceError, match="identity is invalid"):
        scorer._validate_report_identity(current, baseline, scorer.EXPECTED_PROTOCOL_SHA256)


def test_output_guard_accepts_only_new_repository_artifact_path(monkeypatch, tmp_path):
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    valid = tmp_path / "artifacts" / "advisory" / "score.json"
    assert scorer._validate_new_output_path(valid) == valid.resolve()

    existing = tmp_path / "artifacts" / "existing.json"
    existing.parent.mkdir(parents=True)
    existing.write_text("prior evidence", encoding="utf-8")
    with pytest.raises(scorer.EvidenceError, match="new destination"):
        scorer._validate_new_output_path(existing)
    with pytest.raises(scorer.EvidenceError, match="repository artifacts"):
        scorer._validate_new_output_path(tmp_path / "outside.json")


def test_unreviewed_scorer_fails_before_protocol_or_truth(monkeypatch, tmp_path):
    protocol_called = False
    regenerated = False

    def forbidden_protocol(*_):
        nonlocal protocol_called
        protocol_called = True
        raise AssertionError("protocol read preceded evaluator authentication")

    def forbidden_regenerate(*_):
        nonlocal regenerated
        regenerated = True
        raise AssertionError("truth regeneration preceded evaluator authentication")

    monkeypatch.setattr(scorer, "_load_protocol", forbidden_protocol)
    monkeypatch.setattr(scorer.family_ocr, "_regenerate", forbidden_regenerate)
    with pytest.raises(scorer.EvidenceError, match="current advisory evaluator bytes"):
        scorer.score(
            tmp_path / "protocol.json", tmp_path / "candidate.json", "1" * 64,
            tmp_path / "execution.json", "2" * 64,
            tmp_path / "train.json", "3" * 64,
            tmp_path / "dev.json", "4" * 64,
            tmp_path / "output.json", evaluator_sha256="0" * 64)
    assert protocol_called is False
    assert regenerated is False
    assert not (tmp_path / "output.json").exists()


def test_changed_imported_scorer_fails_before_protocol_or_truth(monkeypatch, tmp_path):
    source = tmp_path / scorer.INITIAL_SCORER_PATH
    source.parent.mkdir(parents=True)
    source.write_bytes(b"changed imported scorer")
    protocol_called = False
    regenerated = False

    def forbidden_protocol(*_):
        nonlocal protocol_called
        protocol_called = True
        raise AssertionError("protocol read preceded imported-source authentication")

    def forbidden_regenerate(*_):
        nonlocal regenerated
        regenerated = True
        raise AssertionError("truth regeneration preceded imported-source authentication")

    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    monkeypatch.setattr(scorer, "_load_protocol", forbidden_protocol)
    monkeypatch.setattr(scorer.family_ocr, "_regenerate", forbidden_regenerate)
    evaluator_sha = sha256(Path(scorer.__file__).read_bytes()).hexdigest()
    with pytest.raises(scorer.EvidenceError, match="initial-contour scorer bytes"):
        scorer.score(
            tmp_path / "protocol.json", tmp_path / "candidate.json", "1" * 64,
            tmp_path / "execution.json", "2" * 64,
            tmp_path / "train.json", "3" * 64,
            tmp_path / "dev.json", "4" * 64,
            tmp_path / "output.json", evaluator_sha256=evaluator_sha)
    assert protocol_called is False
    assert regenerated is False
    assert not (tmp_path / "output.json").exists()


@pytest.mark.parametrize("relative", sorted(scorer.DIRECT_HELPER_BINDINGS, key=str))
def test_changed_direct_helper_fails_before_helper_validation_or_truth(
    relative, monkeypatch, tmp_path,
):
    initial = tmp_path / scorer.INITIAL_SCORER_PATH
    initial.parent.mkdir(parents=True)
    initial_bytes = b"reviewed initial scorer fixture"
    initial.write_bytes(initial_bytes)
    helper = tmp_path / relative
    helper.parent.mkdir(parents=True, exist_ok=True)
    helper.write_bytes(b"changed direct helper")
    expected_helper = sha256(b"reviewed direct helper").hexdigest()
    helper_called = False
    regenerated = False

    def forbidden_helper(*_):
        nonlocal helper_called
        helper_called = True
        raise AssertionError("helper validation preceded direct source authentication")

    def forbidden_regenerate(*_):
        nonlocal regenerated
        regenerated = True
        raise AssertionError("truth regeneration preceded direct source authentication")

    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    monkeypatch.setattr(
        scorer, "EXPECTED_INITIAL_SCORER_SHA256", sha256(initial_bytes).hexdigest())
    monkeypatch.setattr(scorer, "DIRECT_HELPER_BINDINGS", {relative: expected_helper})
    monkeypatch.setattr(scorer, "_load_protocol", forbidden_helper)
    monkeypatch.setattr(scorer.original_scorer, "_validate_report", forbidden_helper)
    monkeypatch.setattr(scorer.db_geometry, "_validate_run", forbidden_helper)
    monkeypatch.setattr(scorer.family_ocr, "_regenerate", forbidden_regenerate)
    evaluator_sha = sha256(Path(scorer.__file__).read_bytes()).hexdigest()
    with pytest.raises(scorer.EvidenceError, match="direct helper .* bytes"):
        scorer.score(
            tmp_path / "protocol.json", tmp_path / "candidate.json", "1" * 64,
            tmp_path / "execution.json", "2" * 64,
            tmp_path / "train.json", "3" * 64,
            tmp_path / "dev.json", "4" * 64,
            tmp_path / "output.json", evaluator_sha256=evaluator_sha)
    assert helper_called is False
    assert regenerated is False
    assert not (tmp_path / "output.json").exists()
