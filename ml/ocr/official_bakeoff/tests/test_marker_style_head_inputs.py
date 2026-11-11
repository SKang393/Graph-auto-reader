# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unit capture records are explicit test doubles, never runtime evidence."""
from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path
from uuid import UUID

import numpy as np
from PIL import Image
import pytest

from ml.ocr.official_bakeoff import marker_style_head_inputs as subject
from ml.synthetic.io import canonical_json_bytes, png_bytes


def _write(path: Path, value) -> str:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = value if isinstance(value, bytes) else canonical_json_bytes(value)
    path.write_bytes(payload)
    return sha256(payload).hexdigest()


@pytest.fixture(scope="module")
def saved(tmp_path_factory):
    root = tmp_path_factory.mktemp("marker-style-capture-unit")
    directory = root / "artifacts" / "fixture"
    fixture = subject.fixture.prepare(directory, root=root)
    fixture_path = directory / "generator.json"
    manifest = json.loads((directory / "input-manifest.json").read_bytes())
    candidate = {"native_scope": "reviewed-source-runtime-local-diagnostic", "native_sha256": "a" * 64,
                 "native_path": "unit-test-only-not-loaded", "license_inputs": [],
                 "detector": {"model_path": "unit-test-only-not-loaded", "model_id": "PP-OCRv5_mobile_det",
                    "model_version": "5.0.0", "model_sha256": "b" * 64}}
    # Safe fixture sibling models shared metadata outside the fake worktree.
    detector_manifest = root.parent / f"{root.name}-metadata-unit" / "detector.json"
    manifest_sha = _write(detector_manifest, {})
    candidate["detector"]["manifest_path"] = str(detector_manifest)
    candidate["detector"]["manifest_sha256"] = manifest_sha
    candidate_path = root / "candidate-unit.json"
    candidate_sha = _write(candidate_path, candidate)
    report_path = root / "artifacts" / "runtime-unit" / "report.json"
    cases = []
    for index, source in enumerate(fixture["sources"]):
        source_sha = source["image"]["sha256"]
        image = Image.open(root / source["image"]["path"]).convert("RGB")
        # Deliberately nonzero source-to-panel translation, actually applied to pixels.
        crop = (1, 2, image.width - 2, image.height - 4)
        encoded = png_bytes(image.crop((crop[0], crop[1], crop[0] + crop[2], crop[1] + crop[3])))
        panel_id = str(UUID(int=index + 1))
        panel_sha = _write(report_path.parent / source_sha / panel_id / "panel.png", encoded)
        gray, _ = subject.head._decode_production_pixels(encoded, crop[2], crop[3])
        panel = {"status": "seed-completed", "panel_id": panel_id, "source_image_sha256": source_sha,
            "image_sha256": panel_sha, "width": crop[2], "height": crop[3],
            "source_width": image.width, "source_height": image.height,
            "crop": dict(zip(("x", "y", "width", "height"), crop)),
            "requested_crop": dict(zip(("x", "y", "width", "height"), crop)),
            "source_to_panel_matrix": [1, 0, -crop[0], 0, 1, -crop[1], 0, 0, 1],
            "panel_to_source_matrix": [1, 0, crop[0], 0, 1, crop[1], 0, 0, 1],
            "panel_png": {"file": "panel.png", "sha256": panel_sha, "byte_count": len(encoded)},
            "ocr_proposal_diagnostic": {"unmasked_input_sha256": sha256(gray.tobytes()).hexdigest()}}
        cases.append({"image_sha256": source_sha, "status": "panels-completed",
                      "width": image.width, "height": image.height, "panels": [panel]})
    report = {"schema": "graphreader.synthetic-runtime-seed-evidence.v2", "scope": "local-synthetic-seed-diagnostic",
        "production_approved": False, "training_input_ready": False,
        "input_manifest_sha256": subject._digest(directory / "input-manifest.json"),
        "candidate_sha256": candidate_sha, "native_scope": candidate["native_scope"],
        "native_sha256": candidate["native_sha256"], "count": 9, "completed": 9, "failed": 0,
        "panel_count": 9, "completed_panels": 9, "failed_panels": 0, "cases": cases}
    report_sha = _write(report_path, report)
    source_path = root / subject.head.CAPTURE_SOURCE_PATH
    _write(source_path, b"// explicit unit test source identity\n")
    binary_root = root / "unit-binaries"
    for name in subject.head.CAPTURE_ASSEMBLY_NAMES:
        _write(binary_root / f"{name}.dll", f"unit test assembly {name}".encode())
    return dict(root=root, directory=directory, fixture_path=fixture_path,
        fixture_sha=subject._digest(fixture_path), fixture=fixture, manifest=manifest,
        candidate=candidate, candidate_path=candidate_path, candidate_sha=candidate_sha,
        report_path=report_path, report_sha=report_sha, report=report, binary_root=binary_root,
        detector_manifest=detector_manifest)


@pytest.fixture
def prepared(saved, monkeypatch):
    # Only fixed external model/native authentication is stubbed. Real fixture,
    # crop/pixel, row, tensor-shape, hash, projection and inverse-target readers run.
    monkeypatch.setattr(subject, "EXPECTED_CANDIDATE_SHA256", saved["candidate_sha"])
    monkeypatch.setattr(subject.base, "_authenticate_candidate", lambda candidate, root:
        (candidate["detector"], saved["detector_manifest"], subject._digest(saved["detector_manifest"])))
    return _prepare(saved)


def _prepare(saved, report_path=None, report_sha=None):
    return subject.prepare_capture_request(saved["fixture_path"], saved["fixture_sha"],
        report_path or saved["report_path"], report_sha or saved["report_sha"],
        saved["candidate_path"], saved["candidate_sha"], capture_binary_root=saved["binary_root"],
        repository_root=saved["root"])


def _capture(saved, preparation, directory):
    request_path = directory / "request-unit.json"
    request_sha = _write(request_path, preparation.request)
    rows = []
    for panel in preparation.panels:
        shape = subject.head._production_tensor_shape(panel.width, panel.height)
        # Explicit finite test tensor, not purported application preprocessing output.
        payload = np.zeros(shape, dtype="<f4").tobytes()
        tensor_file = f"{panel.panel_id}.f32"
        tensor_sha = _write(directory / tensor_file, payload)
        row = subject.shared._capture_panel_record(panel, saved["root"])
        row["detector_configuration_fingerprint"] = subject.head.DETECTOR_CONFIGURATION_FINGERPRINT
        row["tensor"] = {"file": tensor_file, "sha256": tensor_sha, "byte_count": len(payload),
                         "shape": list(shape), "dtype": "float32-le", "input_name": "x", "output_name": "fetch_name_0"}
        rows.append(row)
    request = preparation.request
    report = {"schema": subject.REPORT_SCHEMA, "scope": subject.SCOPE, "synthetic_only": True,
        "private_data": False, "sealed_data": False, "truth_used_by_capture": False,
        "model_inference": False, "training_input_ready": False, "production_approved": False,
        "request": {"path": request_path.relative_to(saved["root"]).as_posix(), "sha256": request_sha},
        "binding_sha256": request["binding"]["sha256"], "panel_count": 9, "failed_panel_count": 0,
        "candidate_sha256": saved["candidate_sha"], "detector_model_sha256": request["detector"]["model_sha256"],
        "detector_manifest_sha256": request["detector"]["manifest_sha256"],
        "native_sha256": request["native"]["sha256"], "capture_source_sha256": request["capture_source"]["sha256"],
        "assemblies": request["assemblies"], "maximum_side_length": 960, "dimension_multiple": 128,
        "detector_configuration_fingerprint": subject.head.DETECTOR_CONFIGURATION_FINGERPRINT, "panels": rows}
    report_path = directory / "report-unit.json"
    report_sha = _write(report_path, report)
    return request_path, request_sha, report_path, report_sha, report


def test_real_fixture_projects_to_captured_grid_and_writes_usable_maps(saved, prepared):
    assert prepared.train.source_count == prepared.train.panel_count == 9
    assert prepared.train.full_source_truth_count == prepared.train.projected_source_truth_count > 0
    assert not saved["detector_manifest"].is_relative_to(saved["root"])
    assert prepared.request["detector"]["manifest_path"] == str(saved["detector_manifest"].resolve())
    encoded = canonical_json_bytes(prepared.request)
    assert all(token not in encoded for token in (b'"text"', b"truth_id", b"annotation"))
    directory = saved["root"] / "artifacts" / "capture-unit"
    request, request_sha, report, report_sha, _ = _capture(saved, prepared, directory)
    loaded = subject.load_model_grid_inputs(request, request_sha, report, report_sha, repository_root=saved["root"])
    for panel in loaded.panels:
        assert not panel.input_values.flags.writeable and not panel.shrink_target.flags.writeable
        sx, sy = panel.tensor_shape[3] / panel.width, panel.tensor_shape[2] / panel.height
        for projection in panel.projections:
            source = next(t for t in loaded.source_truths if t.truth_id == projection.truth_id)
            assert projection.panel_box == (source.source_box[0] - 1, source.source_box[1] - 2,
                                            source.source_box[2] - 1, source.source_box[3] - 2)
            cx = int((projection.panel_box[0] + projection.panel_box[2]) * sx / 2)
            cy = int((projection.panel_box[1] + projection.panel_box[3]) * sy / 2)
            assert panel.shrink_target[0, cy, cx] == panel.supervision_mask[0, cy, cx] == 1
    checked = set()
    for source in saved["fixture"]["sources"]:
        panel = next(p for p in loaded.panels if p.source_sha256 == source["image"]["sha256"])
        annotation = json.loads((saved["root"] / source["annotation"]["path"]).read_bytes())
        gray = np.asarray(Image.open(saved["root"] / source["image"]["path"]).convert("L"))
        text_boxes = [t["rendered_pixel_box"] for t in subject.fixture.source_truths(annotation)]
        for marker in annotation["panels"][0]["markers"]:
            if marker["shape"] not in ("diamond", "star", "asterisk", "cross"):
                continue
            x, y = map(int, marker["center"])
            if any(l - 10 <= x <= l + w + 10 and t - 10 <= y <= t + h + 10 for l, t, w, h in text_boxes):
                continue
            ink = np.argwhere(gray[y - 3:y + 4, x - 3:x + 4] < 160)
            if not len(ink):
                continue
            dy, dx = ink[0]
            tx = int((x - 3 + dx - 1) * panel.tensor_shape[3] / panel.width)
            ty = int((y - 3 + dy - 2) * panel.tensor_shape[2] / panel.height)
            assert panel.shrink_target[0, ty, tx] == 0 and panel.supervision_mask[0, ty, tx] == 1
            checked.add(marker["shape"])
    assert checked == {"diamond", "star", "asterisk", "cross"}
    output = saved["root"] / "artifacts" / "maps-unit"
    manifest = subject.write_model_grid_inputs(output, request, request_sha, report, report_sha,
                                               repository_root=saved["root"])
    assert manifest["source_count"] == manifest["panel_count"] == 9
    for row in manifest["panels"]:
        for kind in ("tensor", "target", "mask"):
            ref = row[kind]
            payload = (saved["root"] / ref["path"]).read_bytes()
            assert sha256(payload).hexdigest() == ref["sha256"]
            values = np.frombuffer(payload, dtype="<f4").reshape(ref["shape"])
            assert np.isfinite(values).all()
        assert row["source_to_tensor_matrix"][2] == -row["panel_to_tensor_matrix"][0]
    with pytest.raises(subject.MarkerStyleHeadInputError, match="new model-grid"):
        subject.write_model_grid_inputs(output, request, request_sha, report, report_sha, repository_root=saved["root"])


@pytest.mark.parametrize("mutation", ["missing_crop", "bad_matrix", "wrong_source", "clipped_text", "wrong_gray", "wrong_count"])
def test_runtime_geometry_or_identity_cannot_be_defaulted(saved, prepared, mutation):
    report = deepcopy(saved["report"])
    panel = report["cases"][0]["panels"][0]
    if mutation == "missing_crop":
        del panel["requested_crop"]
    elif mutation == "bad_matrix":
        panel["source_to_panel_matrix"][2] = 0
    elif mutation == "wrong_source":
        panel["source_image_sha256"] = "0" * 64
    elif mutation == "clipped_text":
        crop = {"x": 200, "y": 200, "width": 50, "height": 50}
        image = Image.open(saved["root"] / saved["fixture"]["sources"][0]["image"]["path"])
        encoded = png_bytes(image.crop((200, 200, 250, 250)))
        panel["panel_id"] = str(UUID(int=100))
        panel["image_sha256"] = _write(saved["report_path"].parent / report["cases"][0]["image_sha256"] /
                                       panel["panel_id"] / "panel.png", encoded)
        panel["width"] = panel["height"] = 50
        panel["crop"] = panel["requested_crop"] = crop
        panel["source_to_panel_matrix"] = [1, 0, -200, 0, 1, -200, 0, 0, 1]
        panel["panel_to_source_matrix"] = [1, 0, 200, 0, 1, 200, 0, 0, 1]
        gray, _ = subject.head._decode_production_pixels(encoded, 50, 50)
        panel["ocr_proposal_diagnostic"]["unmasked_input_sha256"] = sha256(gray.tobytes()).hexdigest()
        panel["panel_png"] = {"file": "panel.png", "sha256": panel["image_sha256"], "byte_count": len(encoded)}
    elif mutation == "wrong_gray":
        panel["ocr_proposal_diagnostic"]["unmasked_input_sha256"] = "0" * 64
    else:
        report["panel_count"] = 6
    path = saved["report_path"].parent / f"{mutation}-unit.json"
    digest = _write(path, report)
    with pytest.raises(ValueError):
        _prepare(saved, path, digest)


def test_shared_geometry_defaults_still_reject_nine_source_population(saved):
    with pytest.raises(subject.shared.LegendCoverageHeadInputError, match="runtime report changed"):
        subject.shared._load_runtime_report({r["image_sha256"]: r for r in saved["manifest"]["images"]},
            saved["directory"] / "input-manifest.json", subject._digest(saved["directory"] / "input-manifest.json"),
            saved["report"], saved["report_path"], saved["report_sha"], saved["candidate"], saved["candidate_sha"],
            saved["manifest"]["seed"], saved["root"])


@pytest.mark.parametrize("mutation", ["dev_scope", "duplicate", "wrong_image", "shape", "fingerprint", "bytes"])
def test_capture_reader_rejects_wrong_scope_population_pixels_and_tensors(saved, prepared, mutation):
    directory = saved["root"] / "artifacts" / "capture-unit"
    request = directory / "request-unit.json"
    report_path = directory / "report-unit.json"
    # The first full consumer test writes the common immutable unit capture once.
    if not report_path.exists():
        _capture(saved, prepared, directory)
    report = json.loads(report_path.read_bytes())
    if mutation == "dev_scope":
        report["scope"] = subject.head.CAPTURE_SCOPE
    elif mutation == "duplicate":
        report["panels"][1] = deepcopy(report["panels"][0])
    elif mutation == "wrong_image":
        report["panels"][0]["panel_sha256"] = "0" * 64
    elif mutation == "shape":
        report["panels"][0]["tensor"]["shape"][3] += 128
    elif mutation == "fingerprint":
        report["panels"][0]["detector_configuration_fingerprint"] = "0" * 64
    else:
        report["panels"][0]["tensor"]["sha256"] = "0" * 64
    path = directory / f"{mutation}-unit.json"
    digest = _write(path, report)
    with pytest.raises(ValueError):
        subject.load_model_grid_inputs(request, subject._digest(request), path, digest, repository_root=saved["root"])


def test_fixture_hash_and_source_binding_are_required(saved, prepared):
    with pytest.raises(ValueError, match="checksum"):
        subject._load_fixture(saved["fixture_path"], "0" * 64, saved["root"])
    document = deepcopy(saved["fixture"])
    document["sources"][0]["split"] = "dev"
    path = saved["directory"] / "bad-fixture-unit.json"
    digest = _write(path, document)
    with pytest.raises(ValueError, match="not train"):
        subject._load_fixture(path, digest, saved["root"])


@pytest.mark.parametrize("mutation", ["native_payload", "detector_payload", "other_setting"])
def test_candidate_whole_file_binding_rejects_payload_and_settings_changes(saved, prepared, mutation):
    candidate = deepcopy(saved["candidate"])
    if mutation == "native_payload":
        candidate["native_sha256"] = "0" * 64
    elif mutation == "detector_payload":
        candidate["detector"]["model_sha256"] = "0" * 64
    else:
        candidate["production_approved"] = True
    path = saved["root"] / f"{mutation}-candidate-unit.json"
    digest = _write(path, candidate)
    with pytest.raises(ValueError, match="frozen artifact"):
        subject.prepare_capture_request(saved["fixture_path"], saved["fixture_sha"],
            saved["report_path"], saved["report_sha"], path, digest,
            capture_binary_root=saved["binary_root"], repository_root=saved["root"])
    with pytest.raises(ValueError, match="checksum"):
        subject.prepare_capture_request(saved["fixture_path"], saved["fixture_sha"],
            saved["report_path"], saved["report_sha"], path, saved["candidate_sha"],
            capture_binary_root=saved["binary_root"], repository_root=saved["root"])


@pytest.mark.parametrize("mutation", ["wrong_path", "wrong_hash", "changed_bytes"])
def test_external_manifest_must_match_exact_pinned_path_and_bytes(saved, prepared, monkeypatch, mutation):
    path = saved["detector_manifest"]
    digest = subject._digest(path)
    if mutation == "wrong_path":
        path = path.parent / "wrong-manifest-unit.json"
        _write(path, {})
    elif mutation == "wrong_hash":
        digest = "0" * 64
    else:
        original = path.read_bytes()
        _write(path, {"changed": True})
    monkeypatch.setattr(subject.base, "_authenticate_candidate", lambda candidate, root:
        (candidate["detector"], path, digest))
    try:
        with pytest.raises(ValueError, match="manifest (path|checksum)"):
            _prepare(saved)
    finally:
        if mutation == "changed_bytes":
            path.write_bytes(original)
