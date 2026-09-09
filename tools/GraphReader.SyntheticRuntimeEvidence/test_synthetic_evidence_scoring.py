# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from copy import deepcopy
from hashlib import sha256
from io import BytesIO

import numpy as np
import pytest
from PIL import Image

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


def _png_bytes(image):
    payload = BytesIO()
    image.save(payload, format="PNG")
    return payload.getvalue()


def _ocr_result(image_sha, bounds):
    return {
        "contract_version": 1,
        "stage": "ocr",
        "coordinate_space": "original_pixels",
        "succeeded": True,
        "failure": None,
        "input_sha256": image_sha,
        "regions": [{
            "region_id": "region-1",
            "coordinate_space": "original_pixels",
            "polygon": {"bounds": dict(zip(("left", "top", "right", "bottom"), bounds))},
        }],
    }


def _v2_fixture(tmp_path, monkeypatch):
    monkeypatch.setattr(ocr, "REPOSITORY_ROOT", tmp_path)
    input_directory = tmp_path / "artifacts" / "inputs"
    output_directory = tmp_path / "artifacts" / "run"
    input_directory.mkdir(parents=True)
    output_directory.mkdir(parents=True)
    source = Image.fromarray(np.arange(8 * 6 * 3, dtype=np.uint8).reshape(6, 8, 3), "RGB")
    source_bytes = _png_bytes(source)
    source_hash = sha256(source_bytes).hexdigest()
    source_name = "source.png"
    (input_directory / source_name).write_bytes(source_bytes)
    panel_ids = ["11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222"]
    panels = []
    for panel_id, x in zip(panel_ids, (0, 4)):
        crop = {"x": x, "y": 1, "width": 4, "height": 4}
        panel_bytes = _png_bytes(source.crop((x, 1, x + 4, 5)))
        panel_hash = sha256(panel_bytes).hexdigest()
        directory = output_directory / source_hash / panel_id
        directory.mkdir(parents=True)
        (directory / "panel.png").write_bytes(panel_bytes)
        panels.append({
            "panel_id": panel_id,
            "image_sha256": panel_hash,
            "width": 4,
            "height": 4,
            "source_image_sha256": source_hash,
            "source_width": 8,
            "source_height": 6,
            "crop": crop,
            "requested_crop": {"x": x + 0.25, "y": 1.25, "width": 3.5, "height": 3.5},
            "source_to_panel_matrix": [1, 0, -x, 0, 1, -1, 0, 0, 1],
            "panel_to_source_matrix": [1, 0, x, 0, 1, 1, 0, 0, 1],
            "panel_png": {"file": "panel.png", "sha256": panel_hash, "byte_count": len(panel_bytes)},
            "import_warnings": [],
            "status": "seed-completed",
            "ocr": _ocr_result(panel_hash, (0.5, 0.5, 2.5, 2.5)),
        })
    report = {
        "schema": ocr.REPORT_SCHEMA_V2,
        "scope": ocr.REPORT_SCOPE,
        "production_approved": False,
        "training_input_ready": False,
        "input_manifest_sha256": "a" * 64,
        "count": 1,
        "completed": 1,
        "failed": 0,
        "panel_count": 2,
        "completed_panels": 2,
        "failed_panels": 0,
        "cases": [{
            "image_sha256": source_hash,
            "width": 8,
            "height": 6,
            "status": "panels-completed",
            "panels": panels,
        }],
    }
    images = [{"image": source_name, "image_sha256": source_hash, "width": 8, "height": 6}]
    return report, images, output_directory / "report.json", input_directory / "input-manifest.json"


def _validate_v2(fixture):
    report, images, report_path, manifest_path = fixture
    return ocr._validate_report(
        report, "a" * 64, images, report_path=report_path, manifest_path=manifest_path)


def test_v2_accepts_exact_panel_provenance_and_fractional_requested_crop(tmp_path, monkeypatch):
    fixture = _v2_fixture(tmp_path, monkeypatch)
    cases = _validate_v2(fixture)
    assert list(cases) == [fixture[1][0]["image_sha256"]]


@pytest.mark.parametrize("mutation", [
    "source-id", "source-hash", "source-dimensions", "duplicate-source", "missing-source",
    "crop-fractional", "crop-outside", "requested-outside",
    "matrix", "crop-hash", "crop-pixels", "duplicate-panel", "overlap", "source-count",
    "panel-count", "false-source-success", "unbound-failed-panel", "owned-path",
])
def test_v2_rejects_adversarial_source_and_panel_provenance(tmp_path, monkeypatch, mutation):
    fixture = _v2_fixture(tmp_path, monkeypatch)
    report, images, report_path, _ = fixture
    case = report["cases"][0]
    panel = case["panels"][0]
    if mutation == "source-id":
        case["image_sha256"] = "f" * 64
    elif mutation == "source-hash":
        (fixture[3].parent / images[0]["image"]).write_bytes(_png_bytes(Image.new("RGB", (8, 6), "white")))
    elif mutation == "source-dimensions":
        case["width"] = 9
    elif mutation == "duplicate-source":
        report["cases"].append(deepcopy(case))
    elif mutation == "missing-source":
        report["cases"] = []
    elif mutation == "crop-fractional":
        panel["crop"]["x"] = 0.5
    elif mutation == "crop-outside":
        panel["crop"]["y"] = -1
    elif mutation == "requested-outside":
        panel["requested_crop"]["x"] = -0.25
    elif mutation == "matrix":
        panel["panel_to_source_matrix"][2] = 1
    elif mutation == "crop-hash":
        panel["panel_png"]["sha256"] = "f" * 64
    elif mutation == "crop-pixels":
        path = report_path.parent / images[0]["image_sha256"] / panel["panel_id"] / "panel.png"
        altered = _png_bytes(Image.new("RGB", (4, 4), "white"))
        path.write_bytes(altered)
        altered_hash = sha256(altered).hexdigest()
        panel["image_sha256"] = altered_hash
        panel["panel_png"].update(sha256=altered_hash, byte_count=len(altered))
    elif mutation == "duplicate-panel":
        case["panels"][1]["panel_id"] = panel["panel_id"]
    elif mutation == "overlap":
        second = case["panels"][1]
        second["crop"]["x"] = 3
        second["requested_crop"].update(x=3.25, width=3.5)
        second["source_to_panel_matrix"][2] = -3
        second["panel_to_source_matrix"][2] = 3
    elif mutation == "source-count":
        report["completed"] = 0
    elif mutation == "panel-count":
        report["panel_count"] = 1
    elif mutation == "false-source-success":
        case["panels"][1]["status"] = "failed"
        report["completed_panels"], report["failed_panels"] = 1, 1
    elif mutation == "unbound-failed-panel":
        panel["status"] = "failed"
        panel["crop"] = None
    else:
        panel["panel_png"]["file"] = "../panel.png"
    with pytest.raises(ocr.EvidenceError):
        _validate_v2(fixture)


def test_v2_failed_source_keeps_successful_sibling_panel_and_failed_panel_as_miss(tmp_path, monkeypatch):
    fixture = _v2_fixture(tmp_path, monkeypatch)
    report = fixture[0]
    report["cases"][0]["status"] = "failed"
    report["cases"][0]["panels"][1]["status"] = "failed"
    report.update(completed=0, failed=1, completed_panels=1, failed_panels=1)
    case = next(iter(_validate_v2(fixture).values()))
    predictions = ocr._source_prediction_regions(case, fixture[1][0], ocr.REPORT_SCHEMA_V2)
    assert len(predictions) == 1
    assert predictions[0].box == ocr.Box(0.5, 1.5, 2.5, 3.5)


def test_v2_import_failure_with_no_panels_is_valid_and_retains_full_truth_misses(tmp_path, monkeypatch):
    fixture = _v2_fixture(tmp_path, monkeypatch)
    report = fixture[0]
    case = report["cases"][0]
    case.update(status="failed", panels=[])
    report.update(completed=0, failed=1, panel_count=0, completed_panels=0, failed_panels=0)
    validated = next(iter(_validate_v2(fixture).values()))
    truths = (ocr.Box(1, 1, 2, 2), ocr.Box(5, 1, 6, 2))
    assert ocr._source_prediction_regions(validated, fixture[1][0], ocr.REPORT_SCHEMA_V2) == ()
    assert ocr._truth_crop_coverage(truths, validated, ocr.REPORT_SCHEMA_V2, completed_only=False) == 0


def test_v2_prediction_mapping_is_offset_only_and_does_not_mutate_runtime_output(tmp_path, monkeypatch):
    fixture = _v2_fixture(tmp_path, monkeypatch)
    case = report_case = fixture[0]["cases"][0]
    original = deepcopy(report_case)
    predictions = ocr._source_prediction_regions(case, fixture[1][0], ocr.REPORT_SCHEMA_V2)
    assert [prediction.box for prediction in predictions] == [
        ocr.Box(0.5, 1.5, 2.5, 3.5),
        ocr.Box(4.5, 1.5, 6.5, 3.5),
    ]
    assert report_case == original


def test_full_source_truth_denominator_reports_content_omitted_by_crops():
    truths = (ocr.Box(0, 0, 2, 2), ocr.Box(6, 0, 8, 2), ocr.Box(2, 4, 4, 6))
    case = {"panels": [{"status": "seed-completed", "crop": {"x": 0, "y": 0, "width": 4, "height": 3}}]}
    assert ocr._truth_crop_coverage(truths, case, ocr.REPORT_SCHEMA_V2, completed_only=False) == 1
    assert len(truths) == 3


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


def test_structure_proxy_maps_full_source_truth_into_panel_local_pixels(tmp_path, monkeypatch):
    monkeypatch.setattr(structure, "REPOSITORY_ROOT", tmp_path)
    directory = tmp_path / "artifacts" / "source" / "panel"
    directory.mkdir(parents=True)

    def write(name, values):
        payload = values.tobytes()
        (directory / name).write_bytes(payload)
        return {"file": name, "sha256": sha256(payload).hexdigest(), "byte_count": len(payload)}

    gray = np.full(16, 255, dtype="u1")
    gray[5] = 0
    zero_u1 = np.zeros(16, dtype="u1")
    zero_f32 = np.zeros(16, dtype="<f4")
    diagnostic = {
        "source_gray": write("source.bin", gray),
        "geometry_excluded_ink": write("geometry.bin", zero_u1),
        "marker_like": write("marker.bin", zero_f32),
        "thin_connector": write("connector.bin", zero_f32),
        "applied_to_ocr": False,
        "production_approved": False,
    }
    totals = dict(text_box_ink=0, retained_text_box_ink=0, retained_ink=0,
                  suppressed_ink=0, text_box_ink_suppressed=0, geometry_excluded_ink=0)
    structure._accumulate_panel(
        diagnostic, directory, 4, 4, (ocr.Box(5, 2, 6, 3),), 4, 1, totals)
    assert totals["text_box_ink"] == 1
    assert totals["retained_text_box_ink"] == 1
    assert totals["retained_ink"] == 1
