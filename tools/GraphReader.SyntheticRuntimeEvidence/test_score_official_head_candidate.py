# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
from types import SimpleNamespace

import pytest
import numpy as np

import score_official_head_candidate as scorer


def _write(root: Path, relative: str, payload: bytes) -> dict[str, str]:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return {"path": relative, "sha256": sha256(payload).hexdigest()}


def _assemblies(root: Path):
    return [
        {"name": name, **_write(root, f"snapshot/{name}.dll", name.encode())}
        for name in sorted(scorer.EXPECTED_ASSEMBLIES)
    ]


def _polygon(left=1.0, top=2.0, right=9.0, bottom=8.0):
    return {"points": [
        {"x": left, "y": top, "is_finite": True},
        {"x": right, "y": top, "is_finite": True},
        {"x": right, "y": bottom, "is_finite": True},
        {"x": left, "y": bottom, "is_finite": True},
    ]}


def _panel():
    return scorer._PanelIdentity(
        "train", "a" * 64, 100, 80, "panel-1", "b" * 64, 20, 10,
        (30, 11, 20, 10), (30, 11, 20, 10),
        (1.0, 0.0, -30.0, 0.0, 1.0, -11.0, 0.0, 0.0, 1.0),
        (1.0, 0.0, 30.0, 0.0, 1.0, 11.0, 0.0, 0.0, 1.0),
        "c" * 64, "d" * 64)


def _candidate(root: Path, assemblies):
    detector_hash = sha256(b"det").hexdigest()
    recognizer_hash = sha256(b"rec").hexdigest()
    detector_manifest = {
        "model_id": "det", "model_version": "1", "task": "ocr_detection",
        "sha256": detector_hash, "providers": ["cpu"],
    }
    recognizer_manifest = {
        "model_id": "rec", "model_version": "1", "task": "ocr_recognition",
        "sha256": recognizer_hash, "providers": ["cpu"],
    }
    det_manifest = _write(root, "models/det.json", json.dumps(detector_manifest).encode())
    rec_manifest = _write(root, "models/rec.json", json.dumps(recognizer_manifest).encode())
    _write(root, "models/det.onnx", b"det")
    _write(root, "models/rec.onnx", b"rec")
    native = _write(root, "native/native.dll", b"native")
    license_record = _write(root, "licenses/model.txt", b"Apache-2.0")
    return {
        "schema": scorer.CANDIDATE_SCHEMA, "scope": scorer.CANDIDATE_SCOPE,
        "production_approved": False, "training_input_ready": False,
        "composition_version": scorer.COMPOSITION_VERSION,
        "native_path": native["path"], "native_sha256": native["sha256"],
        "native_scope": "reviewed-source-runtime-local-diagnostic",
        "license_inputs": [license_record], "execution_assemblies": assemblies,
        "detector": {
            "model_path": "models/det.onnx", "model_id": "det", "model_version": "1",
            "model_sha256": detector_hash,
            "manifest_path": det_manifest["path"], "manifest_sha256": det_manifest["sha256"],
        },
        "recognizer": {
            "model_path": "models/rec.onnx", "model_id": "rec", "model_version": "1",
            "model_sha256": recognizer_hash,
            "manifest_path": rec_manifest["path"], "manifest_sha256": rec_manifest["sha256"],
        },
    }


def _summary(candidate, root):
    def model(role, task):
        value = candidate[role]
        return {
            "task": task, "model_id": value["model_id"], "version": value["model_version"],
            "sha256": value["model_sha256"], "manifest_path": value["manifest_path"],
            "manifest_sha256": value["manifest_sha256"],
        }
    return {
        "path": "candidate.json", "sha256": "e" * 64,
        "composition_version": scorer.COMPOSITION_VERSION,
        "adapter_id": (
            f"graphreader-ocr:{scorer.COMPOSITION_VERSION}:"
            f"{candidate['detector']['model_sha256'][:12]}:"
            f"{candidate['recognizer']['model_sha256'][:12]}:"
            f"{candidate['native_sha256'][:12]}"),
        "configuration_scope": "unapproved_frozen_candidate",
        "detector": model("detector", "ocr_detection"),
        "recognizer": model("recognizer", "ocr_recognition"),
        "native_sha256": candidate["native_sha256"],
    }


def _evaluation(root: Path, candidate, assemblies, *, failed=False):
    panel = _panel()
    raw_panel = _polygon()
    raw_source = _polygon(31, 13, 39, 19)
    raw = {
        "region_id": "r1", "panel_polygon": raw_panel, "source_polygon": raw_source,
        "orientation_degrees": 0.0, "detection_confidence": 0.9, "context": None,
        "coordinate_space": "source_original_pixels", "evidence": None,
    }
    common = {
        "split": "train", "source_sha256": panel.source_sha256,
        "source_width": panel.source_width, "source_height": panel.source_height,
        "panel_id": panel.panel_id, "panel_sha256": panel.panel_sha256,
        "width": panel.width, "height": panel.height,
        "crop": {"x": 30, "y": 11, "width": 20, "height": 10},
        "requested_crop": {"x": 30, "y": 11, "width": 20, "height": 10},
        "source_to_panel_matrix": list(panel.source_to_panel_matrix),
        "panel_to_source_matrix": list(panel.panel_to_source_matrix),
        "status": "failed" if failed else "completed",
        "raw_detector_regions": [raw], "recognized_regions": [],
        "elapsed_milliseconds": 1.0,
    }
    if failed:
        common.update({"stage": "ocr", "error": "sanitized"})
    else:
        common.update({
            "original_gray_sha256": panel.gray_sha256,
            "original_bgr_sha256": panel.bgr_sha256,
            "region_failures": [{"region_id": "r1", "source_image": "original",
                                 "failure": {"code": "recognition_failed"}}],
            "ocr": {}, "configured_models": [], "executed_models": [],
        })
    return {
        "schema": scorer.EVALUATION_SCHEMA,
        "status": "failed" if failed else "panels_completed", "scope": scorer.CANDIDATE_SCOPE,
        "synthetic_only": True, "private_data": False, "sealed_data": False,
        "truth_used_by_runtime": False, "optimizer_steps": 0, "production_approved": False,
        "training_input_ready": False, "execution_assemblies": assemblies,
        "request": {"path": "request.json", "sha256": "f" * 64},
        "candidate": _summary(candidate, root),
        "input_mode": "production_decoded_original_bgr_db",
        "detector_postprocess": "manifest_bound_unchanged_db_postprocess",
        "maximum_logical_detector_requests_per_panel": 2,
        "detector_requests_share_exact_runtime_input_and_stage_cache_key": True,
        "graph_structure_consensus_applied": False, "axis_mask_applied_to_detector": False,
        "axis_bounds_used_for_role_classification": True,
        "panel_count": 1, "completed_panel_count": 0 if failed else 1,
        "failed_panel_count": 1 if failed else 0, "model_inference": True,
        "elapsed_milliseconds": 1.0, "panels": [common],
    }


def _write_evaluation(root, value):
    payload = json.dumps(value).encode()
    path = root / "evaluation.json"
    path.write_bytes(payload)
    return path, sha256(payload).hexdigest()


def test_maximum_cardinality_scoring_keeps_full_source_denominator():
    source_truth = scorer.production_head_inputs.production_tiled_inputs.SourceTextTruth
    truths = (
        source_truth("t1", "train", 1, "family", 1, "a", "text1", "other",
                     scorer.Box(0.0, 0.0, 10.0, 10.0), (), "outside_runtime_crops"),
        SimpleNamespace(source_sha256="a", source_box=(20.0, 0.0, 30.0, 10.0)),
        SimpleNamespace(source_sha256="b", source_box=(0.0, 0.0, 10.0, 10.0)),
    )
    predictions = {
        "a": (scorer._Prediction(scorer.Box(0, 0, 10, 10)),
              scorer._Prediction(scorer.Box(40, 0, 50, 10))),
    }
    result = scorer._score_predictions(truths, predictions)
    assert result == {
        "truth_region_count": 3, "predicted_region_count": 2,
        "true_positives": 1, "false_positives": 1, "false_negatives": 2,
        "precision": 0.5, "recall": 1 / 3, "intersection_over_union_minimum": 0.5,
    }


@pytest.mark.parametrize("failed", [False, True])
def test_evaluation_retains_raw_geometry_on_recognition_or_panel_failure(tmp_path, failed):
    assemblies = _assemblies(tmp_path)
    candidate = _candidate(tmp_path, assemblies)
    (tmp_path / "candidate.json").write_text(json.dumps(candidate), encoding="utf-8")
    (tmp_path / "request.json").write_text("{}", encoding="utf-8")
    report, digest = _write_evaluation(tmp_path, _evaluation(tmp_path, candidate, assemblies, failed=failed))
    evidence = scorer._validate_evaluation(
        tmp_path, report, digest, tmp_path / "request.json", "f" * 64,
        tmp_path / "candidate.json", "e" * 64, {"panel-1": _panel()}, candidate)
    assert len(evidence.raw_by_source["a" * 64]) == 1
    assert evidence.recognized_by_source == {}
    if failed:
        assert evidence.failed_panel_raw_regions["train"] == 1
    else:
        assert evidence.explicit_region_failures["train"] == 1


def test_evaluation_rejects_changed_matrix_pixels_and_geometry(tmp_path):
    assemblies = _assemblies(tmp_path)
    candidate = _candidate(tmp_path, assemblies)
    (tmp_path / "candidate.json").write_text(json.dumps(candidate), encoding="utf-8")
    (tmp_path / "request.json").write_text("{}", encoding="utf-8")
    for mutate, match in (
        (lambda value: value["panels"][0].update(original_gray_sha256="0" * 64), "pixel identity"),
        (lambda value: value["panels"][0].update(panel_to_source_matrix=[1, 0, 31, 0, 1, 11, 0, 0, 1]), "provenance"),
        (lambda value: value["panels"][0]["raw_detector_regions"][0]["source_polygon"]["points"][0].update(x=32), "source geometry"),
    ):
        value = _evaluation(tmp_path, candidate, assemblies)
        mutate(value)
        report, digest = _write_evaluation(tmp_path, value)
        with pytest.raises(scorer.EvidenceError, match=match):
            scorer._validate_evaluation(
                tmp_path, report, digest, tmp_path / "request.json", "f" * 64,
                tmp_path / "candidate.json", "e" * 64, {"panel-1": _panel()}, candidate)
        report.unlink()


def test_capture_report_tensor_bytes_are_authenticated_before_truth(tmp_path):
    panel = _panel()
    tensor = _write(tmp_path, "capture/train/panel.f32", b"\0" * 48)
    request = {
        "assemblies": [],
        "binding": {"sha256": "1" * 64},
        "candidate": {"sha256": "2" * 64},
        "detector": {"model_sha256": "3" * 64, "manifest_sha256": "4" * 64},
        "native": {"sha256": "5" * 64},
        "capture_source": {"sha256": "6" * 64},
    }
    request_path = tmp_path / "request.json"
    request_path.write_text("{}", encoding="utf-8")
    report = {
        "schema": scorer.CAPTURE_REPORT_SCHEMA,
        "scope": scorer.production_head_inputs.CAPTURE_SCOPE,
        "synthetic_only": True, "private_data": False, "sealed_data": False,
        "truth_used_by_capture": False, "model_inference": False,
        "production_approved": False, "training_input_ready": False,
        "panel_count": 1, "failed_panel_count": 0,
        "binding_sha256": "1" * 64, "candidate_sha256": "2" * 64,
        "detector_model_sha256": "3" * 64, "detector_manifest_sha256": "4" * 64,
        "native_sha256": "5" * 64, "capture_source_sha256": "6" * 64,
        "maximum_side_length": scorer.production_head_inputs.MAXIMUM_SIDE_LENGTH,
        "dimension_multiple": scorer.production_head_inputs.DIMENSION_MULTIPLE,
        "detector_configuration_fingerprint": scorer.production_head_inputs.DETECTOR_CONFIGURATION_FINGERPRINT,
        "request": {"path": "request.json", "sha256": "f" * 64},
        "assemblies": [],
        "panels": [{
            "split": panel.split, "source_sha256": panel.source_sha256,
            "panel_id": panel.panel_id, "panel_sha256": panel.panel_sha256,
            "width": panel.width, "height": panel.height, "crop": list(panel.crop),
            "recorded_unmasked_gray_sha256": panel.gray_sha256,
            "reconstructed_bgr_sha256": panel.bgr_sha256,
            "bgr_identity_kind": "reconstructed_from_authenticated_panel_png",
            "detector_configuration_fingerprint": scorer.production_head_inputs.DETECTOR_CONFIGURATION_FINGERPRINT,
            "tensor": {"file": "train/panel.f32", "sha256": tensor["sha256"],
                       "byte_count": 48, "shape": [1, 3, 2, 2], "dtype": "float32-le"},
        }],
    }
    report_path = tmp_path / "capture" / "report.json"
    report_path.parent.mkdir(exist_ok=True)
    payload = json.dumps(report).encode()
    report_path.write_bytes(payload)
    scorer._prevalidate_capture_report(
        tmp_path, report_path, sha256(payload).hexdigest(), request_path, "f" * 64,
        request, {panel.panel_id: panel})
    (tmp_path / "capture/train/panel.f32").write_bytes(b"changed")
    with pytest.raises(scorer.EvidenceError, match="tensor bytes changed"):
        scorer._prevalidate_capture_report(
            tmp_path, report_path, sha256(payload).hexdigest(), request_path, "f" * 64,
            request, {panel.panel_id: panel})


def test_historical_capture_model_uses_only_exact_path_and_reviewed_mirror(monkeypatch, tmp_path):
    root = tmp_path / "artifacts/goal22-worktrees/resume"
    root.mkdir(parents=True)
    payload = b"reviewed-parent"
    digest = sha256(payload).hexdigest()
    monkeypatch.setattr(scorer, "PINNED_CAPTURE_MODEL_SHA256", digest)
    reviewed = root / scorer.REVIEWED_CAPTURE_MODEL_PATH
    reviewed.parent.mkdir(parents=True)
    reviewed.write_bytes(payload)
    historical = tmp_path / scorer.HISTORICAL_CAPTURE_MODEL_PATH

    assert scorer._validate_capture_detector_model(root, historical, digest) == reviewed.resolve()
    assert not historical.exists()

    with pytest.raises(scorer.EvidenceError, match="not an authenticated historical"):
        scorer._validate_capture_detector_model(root, tmp_path / "elsewhere/parent.onnx", digest)

    reviewed.write_bytes(b"changed")
    with pytest.raises(scorer.EvidenceError, match="authenticated identity"):
        scorer._validate_capture_detector_model(root, historical, digest)


def test_capture_model_inside_scoring_root_still_authenticates_exact_bytes(monkeypatch, tmp_path):
    payload = b"parent"
    digest = sha256(payload).hexdigest()
    monkeypatch.setattr(scorer, "PINNED_CAPTURE_MODEL_SHA256", digest)
    model = tmp_path / "models/parent.onnx"
    model.parent.mkdir()
    model.write_bytes(payload)

    assert scorer._validate_capture_detector_model(tmp_path, model, digest) == model.resolve()
    model.write_bytes(b"changed")
    with pytest.raises(scorer.EvidenceError, match="authenticated identity"):
        scorer._validate_capture_detector_model(tmp_path, model, digest)


def test_bad_runtime_evidence_fails_before_truth_regeneration(monkeypatch, tmp_path):
    truth_called = False
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    monkeypatch.setattr(scorer, "_validate_source_bindings", lambda _: ())
    monkeypatch.setattr(scorer, "_load_acceptance_bar", lambda _: ("a" * 64, 0.95, 0.95))
    monkeypatch.setattr(scorer, "_validate_before_truth",
                        lambda *_: (_ for _ in ()).throw(scorer.EvidenceError("bad runtime")))

    def forbidden(*_args, **_kwargs):
        nonlocal truth_called
        truth_called = True
        raise AssertionError("truth regenerated")

    monkeypatch.setattr(scorer.production_head_inputs, "load_production_head_inputs", forbidden)
    with pytest.raises(scorer.EvidenceError, match="bad runtime"):
        scorer.score(
            tmp_path / "binding", "1" * 64, tmp_path / "capture", "2" * 64,
            tmp_path / "request", "3" * 64, tmp_path / "candidate", "4" * 64,
            tmp_path / "evaluation", "5" * 64, tmp_path / "artifacts/out.json",
            evaluator_sha256=sha256(Path(scorer.__file__).read_bytes()).hexdigest(),
            repository_root=tmp_path)
    assert truth_called is False
    assert not (tmp_path / "artifacts/out.json").exists()


def test_unreviewed_evaluator_fails_before_evidence_or_truth(monkeypatch, tmp_path):
    called = False

    def forbidden(*_args, **_kwargs):
        nonlocal called
        called = True
        raise AssertionError("evidence or truth touched")

    monkeypatch.setattr(scorer, "_validate_source_bindings", forbidden)
    monkeypatch.setattr(scorer.production_head_inputs, "load_production_head_inputs", forbidden)
    with pytest.raises(scorer.EvidenceError, match="reviewed identity"):
        scorer.score(
            tmp_path / "binding", "1" * 64, tmp_path / "capture", "2" * 64,
            tmp_path / "request", "3" * 64, tmp_path / "candidate", "4" * 64,
            tmp_path / "evaluation", "5" * 64, tmp_path / "artifacts/out.json",
            evaluator_sha256="0" * 64, repository_root=tmp_path)
    assert called is False


def test_mocked_score_keeps_failed_panel_truth_and_writes_new_output(monkeypatch, tmp_path):
    monkeypatch.setattr(scorer, "EXPECTED_COUNTS", {
        "train": {"sources": 1, "panels": 1, "truths": 1},
        "validation": {"sources": 1, "panels": 1, "truths": 1},
    })
    monkeypatch.setattr(scorer, "_validate_source_bindings", lambda _: ())
    monkeypatch.setattr(scorer, "_load_acceptance_bar", lambda _: ("a" * 64, 0.95, 0.95))
    train_panel = _panel()
    dev_panel = scorer._PanelIdentity(
        "validation", "b" * 64, 100, 80, "panel-2", "c" * 64, 20, 10,
        (0, 0, 20, 10), (0, 0, 20, 10),
        (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0),
        (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0), "d" * 64, "e" * 64)
    evidence = scorer._ValidatedEvidence(
        {"execution_assemblies": [], "panels": [
            {"panel_id": "panel-1", "status": "failed"},
            {"panel_id": "panel-2", "status": "completed"}]},
        {}, {"panel-1": train_panel, "panel-2": dev_panel},
        {"a" * 64: (scorer._Prediction(scorer.Box(0, 0, 10, 10)),)}, {},
        {"train": 0, "validation": 0}, {"train": 1, "validation": 0})
    monkeypatch.setattr(scorer, "_validate_before_truth", lambda *_: evidence)
    truth_type = scorer.production_head_inputs.production_tiled_inputs.SourceTextTruth
    train_truth = truth_type("t1", "train", 1, "f", 1, "a" * 64, "x", "other",
                             scorer.Box(0, 0, 10, 10), (), "outside_runtime_crops")
    dev_truth = truth_type("t2", "validation", 2, "f", 2, "b" * 64, "y", "other",
                           scorer.Box(0, 0, 10, 10), (), "outside_runtime_crops")
    split = lambda name, truth: SimpleNamespace(
        name=name, source_count=1, panel_count=1, full_source_truth_count=1,
        source_truths=(truth,))
    inputs = SimpleNamespace(
        binding_sha256="1" * 64, capture_report_sha256="2" * 64,
        train=split("train", train_truth), dev=split("validation", dev_truth))
    monkeypatch.setattr(scorer.production_head_inputs, "load_production_head_inputs",
                        lambda *_args, **_kwargs: inputs)
    output = tmp_path / "artifacts/out.json"
    result = scorer.score(
        tmp_path / "binding", "1" * 64, tmp_path / "capture", "2" * 64,
        tmp_path / "request", "3" * 64, tmp_path / "candidate", "4" * 64,
        tmp_path / "evaluation", "5" * 64, output,
        evaluator_sha256=sha256(Path(scorer.__file__).read_bytes()).hexdigest(),
        repository_root=tmp_path)
    assert result["raw_detector_geometry"]["train"]["true_positives"] == 1
    assert result["raw_detector_geometry"]["validation"]["false_negatives"] == 1
    assert result["recognition_failures"]["train"]["failed_panel_count"] == 1
    assert json.loads(output.read_text(encoding="utf-8"))["status"] == "diagnostic_only_unapproved"
    with pytest.raises(scorer.EvidenceError, match="new file"):
        scorer.score(
            tmp_path / "binding", "1" * 64, tmp_path / "capture", "2" * 64,
            tmp_path / "request", "3" * 64, tmp_path / "candidate", "4" * 64,
            tmp_path / "evaluation", "5" * 64, output,
            evaluator_sha256=sha256(Path(scorer.__file__).read_bytes()).hexdigest(),
            repository_root=tmp_path)


def test_candidate_rejects_changed_executable_and_unknown_fields(tmp_path):
    assemblies = _assemblies(tmp_path)
    candidate = _candidate(tmp_path, assemblies)
    path = tmp_path / "candidate.json"
    payload = json.dumps(candidate).encode()
    path.write_bytes(payload)
    scorer._validate_candidate(tmp_path, path, sha256(payload).hexdigest())
    candidate["approval"] = True
    payload = json.dumps(candidate).encode()
    path.write_bytes(payload)
    with pytest.raises(scorer.EvidenceError, match="unknown or missing"):
        scorer._validate_candidate(tmp_path, path, sha256(payload).hexdigest())
    del candidate["approval"]
    path.write_text(json.dumps(candidate), encoding="utf-8")
    (tmp_path / "snapshot/GraphReader.Ocr.dll").write_bytes(b"tampered")
    with pytest.raises(scorer.EvidenceError, match="authenticated identity"):
        scorer._validate_candidate(tmp_path, path, sha256(path.read_bytes()).hexdigest())


def _runtime_request_fixture(root: Path):
    reports = []
    request_panels = []
    gray = np.zeros((10, 20), dtype=np.uint8)
    bgr = np.zeros((10, 20, 3), dtype=np.uint8)
    for ordinal in range(6):
        split = "train" if ordinal < 5 else "validation"
        source_payload = f"source-{ordinal}".encode()
        source_sha = sha256(source_payload).hexdigest()
        source_path = root / f"run{ordinal}/source.png"
        source_path.parent.mkdir(parents=True)
        source_path.write_bytes(source_payload)
        manifest = {
            "images": [{"split": split, "image": "source.png", "image_sha256": source_sha,
                        "width": 100, "height": 80}],
        }
        manifest_path = root / f"run{ordinal}/input-manifest.json"
        manifest_payload = json.dumps(manifest).encode()
        manifest_path.write_bytes(manifest_payload)
        manifest_sha = sha256(manifest_payload).hexdigest()
        png_payload = f"panel-{ordinal}".encode()
        png_sha = sha256(png_payload).hexdigest()
        panel_id = f"panel-{ordinal}"
        png_path = root / f"run{ordinal}/{source_sha}/{panel_id}/panel.png"
        png_path.parent.mkdir(parents=True)
        png_path.write_bytes(png_payload)
        panel = {
            "panel_id": panel_id, "image_sha256": png_sha, "width": 20, "height": 10,
            "source_image_sha256": source_sha, "source_width": 100, "source_height": 80,
            "crop": {"x": 30, "y": 11, "width": 20, "height": 10},
            "requested_crop": {"x": 30, "y": 11, "width": 20, "height": 10},
            "source_to_panel_matrix": [1, 0, -30, 0, 1, -11, 0, 0, 1],
            "panel_to_source_matrix": [1, 0, 30, 0, 1, 11, 0, 0, 1],
            "status": "seed-completed",
            "panel_png": {"file": "panel.png", "sha256": png_sha,
                          "byte_count": len(png_payload)},
            "ocr_proposal_diagnostic": {
                "unmasked_input_sha256": sha256(gray.tobytes()).hexdigest()},
        }
        report = {
            "schema": scorer.RUNTIME_REPORT_SCHEMA, "scope": scorer.RUNTIME_SCOPE,
            "production_approved": False, "input_manifest_sha256": manifest_sha,
            "count": 1, "completed": 1, "failed": 0, "panel_count": 1,
            "completed_panels": 1, "failed_panels": 0,
            "cases": [{"image_sha256": source_sha, "width": 100, "height": 80,
                       "status": "panels-completed", "panels": [panel]}],
        }
        report_path = root / f"run{ordinal}/report.json"
        report_payload = json.dumps(report).encode()
        report_path.write_bytes(report_payload)
        report_sha = sha256(report_payload).hexdigest()
        reports.append({"split": split, "manifest_path": manifest_path.relative_to(root).as_posix(),
                        "manifest_sha256": manifest_sha,
                        "report_path": report_path.relative_to(root).as_posix(),
                        "report_sha256": report_sha})
        request_panels.append({
            "split": split, "source_sha256": source_sha, "panel_id": panel_id,
            "panel_sha256": png_sha, "width": 20, "height": 10,
            "crop": [30, 11, 20, 10], "report_path": report_path.relative_to(root).as_posix(),
            "report_sha256": report_sha,
            "panel_png": {"path": png_path.relative_to(root).as_posix(), "sha256": png_sha,
                          "byte_count": len(png_payload)},
            "recorded_unmasked_gray_sha256": sha256(gray.tobytes()).hexdigest(),
            "reconstructed_bgr_sha256": sha256(bgr.tobytes()).hexdigest(),
            "bgr_identity_kind": "reconstructed_from_authenticated_panel_png",
        })
    return {"reports": reports, "panels": request_panels}, gray, bgr


def test_runtime_request_authenticates_inventory_crop_matrices_and_pixels(monkeypatch, tmp_path):
    request, gray, bgr = _runtime_request_fixture(tmp_path)
    monkeypatch.setattr(scorer, "EXPECTED_COUNTS", {
        "train": {"sources": 5, "panels": 5, "truths": 5},
        "validation": {"sources": 1, "panels": 1, "truths": 1},
    })
    monkeypatch.setattr(scorer.production_head_inputs, "_decode_production_pixels",
                        lambda *_: (gray, bgr))
    assert len(scorer._runtime_panels(tmp_path, request)) == 6

    request["panels"][0]["reconstructed_bgr_sha256"] = "0" * 64
    with pytest.raises(scorer.EvidenceError, match="Gray8 or reconstructed BGR24"):
        scorer._runtime_panels(tmp_path, request)
    request["panels"][0]["reconstructed_bgr_sha256"] = sha256(bgr.tobytes()).hexdigest()

    report_descriptor = request["reports"][0]
    report_path = tmp_path / report_descriptor["report_path"]
    report = json.loads(report_path.read_text(encoding="utf-8"))
    report["cases"][0]["panels"][0]["panel_to_source_matrix"][2] = 31
    payload = json.dumps(report).encode()
    report_path.write_bytes(payload)
    digest = sha256(payload).hexdigest()
    report_descriptor["report_sha256"] = digest
    request["panels"][0]["report_sha256"] = digest
    with pytest.raises(scorer.EvidenceError, match="mutual inverses|exact integer crop offsets"):
        scorer._runtime_panels(tmp_path, request)
