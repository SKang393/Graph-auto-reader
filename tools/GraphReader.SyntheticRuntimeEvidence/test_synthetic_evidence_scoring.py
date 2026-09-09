# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from copy import deepcopy
from hashlib import sha256

import numpy as np
import pytest

import score_family_ocr as ocr
import score_family_structure as structure


def _manifest():
    return {
        "schema": ocr.MANIFEST_SCHEMA, "source": ocr.MANIFEST_SOURCE,
        "preset": "smoke", "seed": 393, "split": "validation",
        "contains_truth": False, "contains_precomputed_masks": False,
        "images": [],
    }


@pytest.mark.parametrize("field,value", [
    ("split", "sealed"), ("contains_truth", True),
    ("contains_precomputed_masks", True), ("source", "private"),
    ("truth_rows", []),
])
def test_forbidden_inputs_rejected_before_image_or_truth_read(tmp_path, field, value):
    manifest = _manifest()
    manifest[field] = value
    with pytest.raises(ocr.EvidenceError):
        ocr._validate_manifest(manifest, tmp_path / "manifest.json")


def _report():
    return {
        "schema": ocr.REPORT_SCHEMA, "scope": ocr.REPORT_SCOPE,
        "production_approved": False, "training_input_ready": False,
        "input_manifest_sha256": "a" * 64,
        "count": 2, "completed": 1, "failed": 1,
        "cases": [
            {"image_sha256": "b" * 64, "status": "seed-completed"},
            {"image_sha256": "c" * 64, "status": "failed"},
        ],
    }


@pytest.mark.parametrize("mutation", ["subset", "duplicate", "foreign", "wrong-manifest", "hidden-failure", "approval"])
def test_report_cannot_drop_duplicate_rebind_or_approve_cases(mutation):
    report = deepcopy(_report())
    if mutation == "subset":
        report["cases"].pop()
    elif mutation == "duplicate":
        report["cases"][1] = deepcopy(report["cases"][0])
    elif mutation == "foreign":
        report["cases"][1]["image_sha256"] = "d" * 64
    elif mutation == "wrong-manifest":
        report["input_manifest_sha256"] = "d" * 64
    elif mutation == "hidden-failure":
        report["completed"], report["failed"] = 2, 0
    else:
        report["production_approved"] = True
    with pytest.raises(ocr.EvidenceError):
        ocr._validate_report(report, "a" * 64, [
            {"image_sha256": "b" * 64}, {"image_sha256": "c" * 64}])


def test_failed_case_produces_zero_predictions_without_reading_fake_regions():
    assert ocr._prediction_regions({"status": "failed", "ocr": "must not use"}, {}) == ()


@pytest.mark.parametrize("mutation", ["hash", "size", "nan", "range", "path"])
def test_structure_planes_reject_tampered_or_invalid_evidence(tmp_path, monkeypatch, mutation):
    monkeypatch.setattr(structure, "REPOSITORY_ROOT", tmp_path)
    directory = tmp_path / "artifacts" / "case"
    directory.mkdir(parents=True)
    values = np.array([0.0, 0.5, 1.0], dtype="<f4")
    if mutation == "nan":
        values[0] = np.nan
    if mutation == "range":
        values[0] = 1.5
    payload = values.tobytes()
    (directory / "plane.f32").write_bytes(payload)
    record = {"file": "plane.f32", "sha256": sha256(payload).hexdigest(), "byte_count": len(payload)}
    if mutation == "hash":
        record["sha256"] = "a" * 64
    if mutation == "size":
        record["byte_count"] -= 1
    if mutation == "path":
        record["file"] = "../plane.f32"
    with pytest.raises(ocr.EvidenceError):
        structure._plane(directory, record, "<f4", 3)
