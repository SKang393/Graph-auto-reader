# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
from PIL import Image
import pytest

from ml.ocr.official_bakeoff import text_extent_head_inputs as inputs


def _domain(
    source: str,
    panel_id: str,
    *,
    split: str = "train",
    crop: tuple[int, int, int, int] = (0, 0, 60, 40),
):
    x, y, width, height = crop
    runtime = SimpleNamespace(
        split=split,
        dataset_seed=393,
        family="fixture-family",
        scene_seed=39301,
        source_sha256=source,
        panel_id=panel_id,
        panel_sha256="b" * 64,
        width=width,
        height=height,
        crop=crop,
        requested_crop=tuple(float(value) for value in crop),
        gray8=np.full((height, width), 255, dtype=np.uint8),
    )
    return SimpleNamespace(
        runtime_input=runtime,
        source_width=100,
        source_height=80,
        source_to_panel_matrix=(1.0, 0.0, -x, 0.0, 1.0, -y, 0.0, 0.0, 1.0),
        panel_to_source_matrix=(1.0, 0.0, x, 0.0, 1.0, y, 0.0, 0.0, 1.0),
    )


def _truth(truth_id: str, text_id: str, box: list[float]) -> dict[str, object]:
    return {
        "truth_id": truth_id,
        "source_id": "a" * 64,
        "source_sha256": "a" * 64,
        "text_id": text_id,
        "region_id": "region-" + text_id,
        "panel_id": "provisional-and-ignored",
        "role": "legend_text",
        "text": "saved exact text",
        "source_box_ltrb": box,
        "coordinate_space": "original_pixels",
    }


def _truth_document(records):
    return {
        "schema": inputs.TRAIN_TRUTH_SCHEMA, "split": "train",
        "source_count": 1, "truth_count": len(records),
        "coordinate_space": "original_pixels", "synthetic_only": True,
        "private_data": False, "sealed_data": False, "truths": records,
    }


def test_saved_train_truth_is_projected_against_actual_crops_and_keeps_denominator(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(inputs, "EXPECTED_TRAIN_TRUTHS", 3)
    monkeypatch.setattr(inputs, "EXPECTED_TRAIN_SOURCES", 1)
    source = "a" * 64
    document = _truth_document([
            _truth("1" * 64, "full", [10.0, 10.0, 20.0, 20.0]),
            _truth("2" * 64, "partial", [55.0, 10.0, 65.0, 20.0]),
            _truth("3" * 64, "outside", [70.0, 10.0, 80.0, 20.0]),
        ])
    sources = {source: {"width": 100, "height": 80, "text_truth_count": 3}}

    split = inputs._build_train_split(document, sources, (_domain(source, "panel-1"),))

    assert split.full_source_truth_count == 3
    assert split.projected_source_truth_count == 2
    assert split.outside_runtime_crop_truth_count == 1
    assert split.partial_source_truth_count == 1
    assert [item.status for item in split.panels[0].projections] == ["full", "partial"]
    assert {item.source_text_id for item in split.source_truths} == {"full", "partial", "outside"}


def test_train_truth_rejects_missing_saved_string_before_projection(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(inputs, "EXPECTED_TRAIN_TRUTHS", 1)
    monkeypatch.setattr(inputs, "EXPECTED_TRAIN_SOURCES", 1)
    record = _truth("1" * 64, "text-1", [1.0, 1.0, 5.0, 5.0])
    record["text"] = ""

    with pytest.raises(inputs.TextExtentHeadInputError, match="text"):
        inputs._build_train_split(
            _truth_document([record]),
            {"a" * 64: {"width": 100, "height": 80}},
            (_domain("a" * 64, "panel-1"),),
        )


def test_historical_dev_oracle_is_loaded_without_renderer_truth_regeneration(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(inputs, "EXPECTED_DEV_PANELS", 1)
    monkeypatch.setattr(inputs, "EXPECTED_DEV_TRUTHS", 1)
    monkeypatch.setattr(inputs, "EXPECTED_DEV_SOURCES", 1)
    domain = _domain("d" * 64, "dev-panel", split="validation", crop=(10, 20, 60, 40))
    oracle = {
        "panels": [{
            "split": "validation",
            "panel_id": "dev-panel",
            "source_sha256": "d" * 64,
            "width": 60,
            "height": 40,
            "panel_to_source_matrix": list(domain.panel_to_source_matrix),
            "projections": [{"truth_id": "e" * 64, "panel_box": [5, 6, 15, 16]}],
        }]
    }

    split = inputs._build_dev_split(oracle, (domain,))

    assert split.full_source_truth_count == 1
    assert split.source_truths[0].source_box == (15.0, 26.0, 25.0, 36.0)
    assert split.source_truths[0].role == "other"
    assert split.panels[0].projections[0].panel_box == (5.0, 6.0, 15.0, 16.0)


def _png() -> bytes:
    stream = BytesIO()
    Image.new("RGB", (2, 1), (10, 20, 30)).save(stream, format="PNG")
    return stream.getvalue()


def _write_json(path: Path, value: object) -> str:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(value, sort_keys=True).encode()
    path.write_bytes(payload)
    return sha256(payload).hexdigest()


def test_runtime_report_authenticates_panel_png_gray_and_split_inventory(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr(inputs, "EXPECTED_TRAIN_REPORTS", 1)
    monkeypatch.setattr(inputs, "EXPECTED_DEV_REPORTS", 0)
    monkeypatch.setattr(inputs, "EXPECTED_TRAIN_PANELS", 1)
    monkeypatch.setattr(inputs, "EXPECTED_DEV_PANELS", 0)
    monkeypatch.setattr(inputs, "EXPECTED_DEV_SOURCES", 0)
    encoded = _png()
    source_sha = sha256(encoded).hexdigest()
    (tmp_path / "source.png").write_bytes(encoded)
    panel_id = "11111111-1111-1111-1111-111111111111"
    manifest_path = tmp_path / "manifest.json"
    manifest_sha = _write_json(manifest_path, {
        "schema": "graphreader.synthetic-runtime-raster-inputs.v1",
        "split": "train", "contains_truth": False, "seed": 393,
        "images": [{"split": "train", "image": "source.png", "image_sha256": source_sha, "width": 2, "height": 1}],
    })
    encoded = _png()
    panel_sha = sha256(encoded).hexdigest()
    panel_path = tmp_path / "run" / source_sha / panel_id / "panel.png"
    panel_path.parent.mkdir(parents=True)
    panel_path.write_bytes(encoded)
    gray, _ = inputs.head._decode_production_pixels(encoded, 2, 1)
    report_path = tmp_path / "run" / "report.json"
    report_sha = _write_json(report_path, {
        "schema": "graphreader.synthetic-runtime-seed-evidence.v2",
        "scope": "local-synthetic-seed-diagnostic",
        "production_approved": False, "training_input_ready": False,
        "failed": 0, "completed": 1, "count": 1,
        "input_manifest_sha256": manifest_sha,
        "candidate_sha256": "c" * 64,
        "native_sha256": "n" * 64,
        "panel_count": 1, "completed_panels": 1, "failed_panels": 0,
        "cases": [{
            "image_sha256": source_sha, "width": 2, "height": 1, "status": "panels-completed",
            "panels": [{
                "panel_id": panel_id, "image_sha256": panel_sha,
                "source_image_sha256": source_sha, "width": 2, "height": 1,
                "source_width": 2, "source_height": 1,
                "crop": {"x": 0, "y": 0, "width": 2, "height": 1},
                "requested_crop": {"x": 0, "y": 0, "width": 2, "height": 1},
                "source_to_panel_matrix": [1, 0, 0, 0, 1, 0, 0, 0, 1],
                "panel_to_source_matrix": [1, 0, 0, 0, 1, 0, 0, 0, 1],
                "panel_png": {"file": "panel.png", "sha256": panel_sha,
                              "byte_count": len(encoded)},
                "ocr_proposal_diagnostic": {
                    "unmasked_input_sha256": sha256(gray.tobytes()).hexdigest()
                },
                "status": "seed-completed",
            }],
        }],
    })
    descriptor = inputs.RuntimeReportEvidence(
        "train", manifest_path, manifest_sha, report_path, report_sha
    )
    source_info = {
        source_sha: {"family": "fixture", "seed": 39301, "width": 2, "height": 1,
                     "manifest": "manifest.json", "manifest_sha256": manifest_sha}
    }

    _, panels, domains = inputs._load_runtime_reports(
        (descriptor,), source_info, None,
        {"native_sha256": "n" * 64}, "c" * 64, tmp_path,
    )

    assert len(panels) == len(domains) == 1
    assert panels[0].panel_png_sha256 == panel_sha
    assert panels[0].recorded_unmasked_gray_sha256 == sha256(gray.tobytes()).hexdigest()

    panel_path.write_bytes(encoded + b"changed")
    with pytest.raises(inputs.TextExtentHeadInputError, match="checksum|byte count"):
        inputs._load_runtime_reports(
            (descriptor,), source_info, None,
            {"native_sha256": "n" * 64}, "c" * 64, tmp_path,
        )


def test_preflight_rejects_training_or_inference_activity() -> None:
    baseline = {
        "schema": inputs.PREFLIGHT_SCHEMA,
        "candidate_opened": False,
        "optimizer_steps": 0,
        "model_inference": False,
        "private_data": False,
        "sealed_data": False,
    }
    inputs._require_preflight(baseline)
    for changed in (
        {"optimizer_steps": 1}, {"model_inference": True}, {"candidate_opened": True},
        {"private_data": True}, {"sealed_data": True},
        {"private_data": None}, {"sealed_data": None}, {"optimizer_steps": False},
    ):
        with pytest.raises(inputs.TextExtentHeadInputError, match="scope or counters"):
            inputs._require_preflight({**baseline, **changed})


@pytest.mark.parametrize("invalid", [float("nan"), float("inf"), -float("inf"), True])
def test_projection_boundaries_reject_nonfinite_or_boolean_coordinates(invalid):
    with pytest.raises(inputs.TextExtentHeadInputError):
        inputs._box([invalid, 0, 10, 10], "truth")
    with pytest.raises(inputs.TextExtentHeadInputError):
        inputs._box_object({"x": invalid, "y": 0, "width": 10, "height": 10}, "crop")


@pytest.mark.parametrize("payload", [b'{"x":1,"x":2}', b'{"x":NaN}'])
def test_authenticated_json_rejects_ambiguous_or_nonfinite_fields(tmp_path, payload):
    path = tmp_path / "record.json"
    path.write_bytes(payload)
    with pytest.raises(inputs.TextExtentHeadInputError):
        inputs._read_json(path, sha256(payload).hexdigest(), tmp_path, "fixture")
