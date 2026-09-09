# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path

import pytest

import score_original_model_input as scorer


def _json_bytes(value):
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")


def _write_json(path: Path, value) -> str:
    payload = _json_bytes(value)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return sha256(payload).hexdigest()


def _polygon(left, top, right, bottom):
    return {
        "points": [
            {"x": left, "y": top, "is_finite": True},
            {"x": right, "y": top, "is_finite": True},
            {"x": right, "y": bottom, "is_finite": True},
            {"x": left, "y": bottom, "is_finite": True},
        ],
        "bounds": {
            "x": left, "y": top, "width": right - left, "height": bottom - top,
            "left": left, "top": top, "right": right, "bottom": bottom,
            "is_valid": True,
        },
    }


def _region(identifier, left, top, right, bottom):
    return {
        "region_id": identifier,
        "polygon": _polygon(left, top, right, bottom),
        "coordinate_space": "original_pixels",
    }


def _candidate_summary():
    return {
        "sha256": "1" * 64,
        "native_sha256": "2" * 64,
        "native_scope": "reviewed-source-runtime-local-diagnostic",
        "detector": {
            "model_id": "detector", "model_version": "1", "model_sha256": "3" * 64,
        },
        "recognizer": {
            "model_id": "recognizer", "model_version": "1", "model_sha256": "4" * 64,
        },
    }


def _model_evidence(task, model, panel_id, panel_sha, warnings):
    return {
        "task": task,
        "envelope": {
            "contract_version": 1,
            "run_id": "00000000-0000-4000-8000-000000000001",
            "project_id": "00000000-0000-4000-8000-000000000002",
            "panel_id": panel_id,
            "stage": "ocr",
            "coordinate_space": "original_pixels",
            "input_sha256": panel_sha,
            "model": {
                "model_id": model["model_id"], "version": model["model_version"],
                "sha256": model["model_sha256"], "provider": "cpu",
            },
            "warnings": list(warnings),
        },
    }


def _panel(mode, candidate, panel_id):
    panel_sha = "5" * 64
    masked_sha = "6" * 64
    original_sha = "7" * 64
    masked_region = _region("masked-region", 1, 1, 5, 5)
    original_region = _region("original-region", 2, 2, 7, 7)
    selected = masked_region if mode == "axis-masked" else original_region
    if mode == "axis-masked":
        warnings = {
            "ocr_detector_axis_geometry_mask_applied",
            f"ocr_detector_input_sha256:{masked_sha}",
        }
    else:
        warnings = {
            "ocr_detector_model_input_original",
            f"ocr_detector_model_input_sha256:{original_sha}",
            "ocr_detector_structure_input_axis_masked",
            f"ocr_detector_structure_input_sha256:{masked_sha}",
        }
    models = [
        {
            "task": task,
            "model_id": candidate[name]["model_id"],
            "version": candidate[name]["model_version"],
            "sha256": candidate[name]["model_sha256"],
            "execution_provider": "cpu",
        }
        for task, name in (("ocr_detection", "detector"), ("ocr_recognition", "recognizer"))
    ]
    return {
        "panel_id": panel_id,
        "image_sha256": panel_sha,
        "width": 10,
        "height": 10,
        "source_image_sha256": "8" * 64,
        "source_width": 30,
        "source_height": 10,
        "crop": {"x": 10, "y": 0, "width": 10, "height": 10},
        "requested_crop": {"x": 10, "y": 0, "width": 10, "height": 10},
        "source_to_panel_matrix": [1, 0, -10, 0, 1, 0, 0, 0, 1],
        "panel_to_source_matrix": [1, 0, 10, 0, 1, 0, 0, 0, 1],
        "panel_png": {"file": "panel.png", "sha256": panel_sha, "byte_count": 42},
        "source_gray": {"file": "source-gray8.bin", "sha256": original_sha, "byte_count": 100},
        "detector_input_sha256": masked_sha,
        "detector_bgr_sha256": "9" * 64,
        "pre_ocr_diagnostic": {"applied_to_ocr": False, "production_approved": False},
        "status": "seed-completed",
        "ocr_proposal_diagnostic": {
            "model_regions": [deepcopy(masked_region)],
            "unmasked_model_regions": [deepcopy(original_region)],
            "component_regions": [_region("component", 1, 1, 4, 4)],
            "detector_input_sha256": masked_sha,
            "unmasked_input_sha256": original_sha,
            "coordinate_space": "original_pixels",
            "used_as_accepted_evidence": False,
        },
        "ocr_configured_models": {
            "models": models, "scope": "unapproved_local_synthetic_candidate",
        },
        "ocr_models": [
            _model_evidence("ocr_detection", candidate["detector"], panel_id, panel_sha, warnings),
            _model_evidence("ocr_recognition", candidate["recognizer"], panel_id, panel_sha, warnings),
        ],
        "ocr": {
            "contract_version": 1,
            "stage": "ocr",
            "coordinate_space": "original_pixels",
            "succeeded": True,
            "failure": None,
            "input_sha256": panel_sha,
            "regions": [deepcopy(selected)],
            "region_failures": [],
            "cache": {"crop_count": 1},
        },
    }


def test_exact_candidate_clone_allows_only_reviewed_input_fields():
    baseline = {
        "schema": "graphreader.local-synthetic-ocr-candidate.v1",
        "production_approved": False,
        "detector": {"model_sha256": "a" * 64},
        "recognizer": {"model_sha256": "b" * 64},
    }
    original = deepcopy(baseline)
    original["ocr_model_input"] = "original"
    original["model_input_protocol"] = {
        "path": scorer.PROTOCOL_PATH.as_posix(),
        "sha256": scorer.EXPECTED_PROTOCOL_SHA256,
    }
    scorer._validate_isolated_candidate(baseline, original, scorer.EXPECTED_PROTOCOL_SHA256)

    original["detector"]["model_sha256"] = "c" * 64
    with pytest.raises(scorer.EvidenceError, match="changes more than"):
        scorer._validate_isolated_candidate(baseline, original, scorer.EXPECTED_PROTOCOL_SHA256)


def test_complete_candidate_verifies_recognizer_manifest_and_license_bytes(tmp_path, monkeypatch):
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    artifacts = tmp_path / "artifacts" / "candidate"
    external = tmp_path / "external"
    external.mkdir()
    native = external / "native.dll"
    license_path = external / "NOTICE.txt"
    native.write_bytes(b"native")
    license_path.write_bytes(b"reviewed notice")
    candidate = {
        "schema": "graphreader.local-synthetic-ocr-candidate.v1",
        "production_approved": False,
        "native_path": str(native),
        "native_sha256": sha256(native.read_bytes()).hexdigest(),
        "native_scope": "reviewed-source-runtime-local-diagnostic",
        "license_inputs": [{
            "path": str(license_path), "sha256": sha256(license_path.read_bytes()).hexdigest(),
        }],
    }
    for name, task in (("detector", "ocr_detection"), ("recognizer", "ocr_recognition")):
        model = external / f"{name}.onnx"
        manifest_path = external / f"{name}.json"
        model.write_bytes(name.encode("ascii"))
        model_sha = sha256(model.read_bytes()).hexdigest()
        manifest = {
            "model_id": name, "model_version": "1", "task": task, "sha256": model_sha,
            "license": {"spdx": "Apache-2.0", "reviewed": True},
            "commercial_use": True, "redistribution": True, "providers": ["cpu"],
        }
        manifest_sha = _write_json(manifest_path, manifest)
        candidate[name] = {
            "model_path": str(model), "model_id": name, "model_version": "1",
            "model_sha256": model_sha, "manifest_path": str(manifest_path),
            "manifest_sha256": manifest_sha,
        }
    candidate_path = artifacts / "candidate.json"
    candidate_sha = _write_json(candidate_path, candidate)
    _, summary = scorer._validate_complete_candidate(candidate_path, candidate_sha)
    assert summary["recognizer"]["model_id"] == "recognizer"

    license_path.write_bytes(b"changed notice")
    with pytest.raises(scorer.EvidenceError, match="license input 0 bytes"):
        scorer._validate_complete_candidate(candidate_path, candidate_sha)


def test_panel_pair_scores_selected_expanded_regions_and_rejects_structural_drift():
    candidate = _candidate_summary()
    baseline = _panel(
        "axis-masked", candidate, "00000000-0000-4000-8000-000000000010")
    original = _panel(
        "original", candidate, "00000000-0000-4000-8000-000000000011")
    baseline_stages, original_stages, inputs_differ = scorer._validate_panel_pair(
        baseline, original, candidate, candidate)
    assert inputs_differ is True
    assert baseline_stages["raw_expanded"][0].box.left == 11
    assert original_stages["raw_expanded"][0].box.left == 12
    assert baseline_stages["consensus"] == baseline_stages["final_ocr_regions"]
    assert original_stages["consensus"] == original_stages["final_ocr_regions"]

    original["ocr_proposal_diagnostic"]["component_regions"][0]["polygon"] = _polygon(3, 3, 8, 8)
    with pytest.raises(scorer.EvidenceError, match="changed a learned or structural"):
        scorer._validate_panel_pair(baseline, original, candidate, candidate)


def test_panel_pair_rejects_unproven_original_input_warning():
    candidate = _candidate_summary()
    baseline = _panel(
        "axis-masked", candidate, "00000000-0000-4000-8000-000000000010")
    original = _panel(
        "original", candidate, "00000000-0000-4000-8000-000000000011")
    original["ocr_models"][0]["envelope"]["warnings"] = []
    with pytest.raises(scorer.EvidenceError, match="do not prove"):
        scorer._validate_panel_pair(baseline, original, candidate, candidate)


def test_score_rejects_malformed_runtime_evidence_before_truth_regeneration(tmp_path, monkeypatch):
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    candidate = _candidate_summary()
    baseline_raw = {"schema": "candidate", "production_approved": False}
    protocol_bytes = b"reviewed protocol bytes"
    protocol_sha = sha256(protocol_bytes).hexdigest()
    original_raw = deepcopy(baseline_raw)
    original_raw.update({
        "ocr_model_input": "original",
        "model_input_protocol": {
            "path": scorer.PROTOCOL_PATH.as_posix(), "sha256": protocol_sha,
        },
    })
    artifacts = tmp_path / "artifacts"
    baseline_path = artifacts / "baseline" / "candidate.json"
    original_path = artifacts / "original" / "candidate.json"
    manifest_path = artifacts / "inputs" / "input-manifest.json"
    report_path = artifacts / "reports" / "report.json"
    execution_path = artifacts / "execution" / "execution-files.json"
    bound = {
        "baseline_candidate": (baseline_path, "a" * 64),
        "train_manifest": (manifest_path, "b" * 64),
        "dev_manifest": (manifest_path, "b" * 64),
        "baseline_train_report": (report_path, "c" * 64),
        "baseline_dev_report": (report_path, "c" * 64),
    }
    monkeypatch.setattr(scorer, "_load_protocol", lambda _: ({
        "hypothesis": "h", "isolated_change": "c", "acceptance_bar": "reference",
    }, protocol_bytes, bound))

    def candidate_loader(path, _):
        return (baseline_raw, candidate) if path == baseline_path else (original_raw, candidate)

    monkeypatch.setattr(scorer, "_validate_complete_candidate", candidate_loader)
    monkeypatch.setattr(
        scorer.db_geometry, "_validate_execution_manifest", lambda *_: (b"execution", ()))
    source_sha = "8" * 64
    images = [{"image_sha256": source_sha}]
    baseline_panel = _panel(
        "axis-masked", candidate, "00000000-0000-4000-8000-000000000010")
    original_panel = _panel(
        "original", candidate, "00000000-0000-4000-8000-000000000011")
    original_panel["ocr_models"][0]["envelope"]["warnings"] = []
    baseline_cases = {source_sha: {"status": "panels-completed", "panels": [baseline_panel]}}
    original_cases = {source_sha: {"status": "panels-completed", "panels": [original_panel]}}

    def report_loader(*arguments):
        mode = arguments[6]
        cases = original_cases if mode == "original" else baseline_cases
        return images, cases, {}, b"report", 393, ()

    monkeypatch.setattr(scorer, "_validate_report", report_loader)
    regenerate_called = False

    def forbidden_regenerate(*_):
        nonlocal regenerate_called
        regenerate_called = True
        raise AssertionError("truth regeneration ran before runtime evidence validation")

    monkeypatch.setattr(scorer.family_ocr, "_regenerate", forbidden_regenerate)
    with pytest.raises(scorer.EvidenceError, match="do not prove"):
        scorer.score(
            tmp_path / scorer.PROTOCOL_PATH,
            original_path,
            "d" * 64,
            execution_path,
            "e" * 64,
            report_path,
            "f" * 64,
            report_path,
            "0" * 64,
            artifacts / "output.json",
        )
    assert regenerate_called is False
