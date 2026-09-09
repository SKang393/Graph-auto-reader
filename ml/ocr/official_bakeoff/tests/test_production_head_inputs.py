# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from dataclasses import replace
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
from PIL import Image
import pytest

from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    RuntimeEvidenceBinding,
    ValidatedRuntimePanelInput,
)
from ml.ocr import production_tiled_inputs
from ml.ocr.official_bakeoff import production_head_inputs as inputs


def _png_bytes(
    pixels: list[tuple[int, int, int, int]], width: int, height: int
) -> bytes:
    image = Image.new("RGBA", (width, height))
    image.putdata(pixels)
    stream = BytesIO()
    image.save(stream, format="PNG")
    return stream.getvalue()


def _runtime_panel(
    panel_id: str,
    width: int,
    height: int,
    gray: np.ndarray,
    panel_sha: str,
    *,
    split: str = "train",
) -> ValidatedRuntimePanelInput:
    zero = np.zeros((height, width), dtype=np.float32)
    return ValidatedRuntimePanelInput(
        split,
        393,
        "fixture-family",
        39301,
        "a" * 64,
        panel_id,
        panel_sha,
        width,
        height,
        (0, 0, width, height),
        (0.0, 0.0, float(width), float(height)),
        gray,
        zero,
        zero,
        zero,
    )


def _tiled_panel(
    projections: tuple[production_tiled_inputs.PanelTextProjection, ...],
    *,
    panel_id: str = "11111111-1111-1111-1111-111111111111",
    width: int = 64,
    height: int = 32,
) -> production_tiled_inputs.ProductionTiledPanel:
    return production_tiled_inputs.ProductionTiledPanel(
        "train",
        393,
        "fixture-family",
        39301,
        "a" * 64,
        panel_id,
        "b" * 64,
        width,
        height,
        (0, 0, width, height),
        (0.0, 0.0, float(width), float(height)),
        (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0),
        (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0),
        np.full((height, width), 255, dtype=np.uint8),
        projections,
        (),
    )


def _projection(box: tuple[float, float, float, float], identity: str = "truth-1"):
    return production_tiled_inputs.PanelTextProjection(
        identity,
        "text-1",
        "11111111-1111-1111-1111-111111111111",
        box,
        box,
        "full",
    )


def test_python_reconstruction_matches_production_alpha_composite_and_luminance() -> None:
    encoded = _png_bytes(
        [
            (0, 0, 0, 0),
            (0, 0, 0, 128),
            (255, 0, 0, 255),
            (0, 255, 0, 255),
            (0, 0, 255, 255),
        ],
        5,
        1,
    )

    gray, bgr = inputs._decode_production_pixels(encoded, 5, 1)

    np.testing.assert_array_equal(gray, np.array([[255, 127, 76, 150, 29]], dtype=np.uint8))
    np.testing.assert_array_equal(
        bgr.reshape(-1, 3),
        np.array(
            [[255, 255, 255], [127, 127, 127], [0, 0, 255], [0, 255, 0], [255, 0, 0]],
            dtype=np.uint8,
        ),
    )
    assert not gray.flags.writeable and not bgr.flags.writeable


def test_capture_panel_binds_recorded_unmasked_gray_and_reconstructed_bgr(tmp_path: Path) -> None:
    panel_id = "11111111-1111-1111-1111-111111111111"
    encoded = _png_bytes([(10, 20, 30, 255), (100, 120, 140, 255)], 2, 1)
    gray, bgr = inputs._decode_production_pixels(encoded, 2, 1)
    panel_sha = sha256(encoded).hexdigest()
    report_path = tmp_path / "artifacts" / "run" / "report.json"
    png_path = report_path.parent / ("a" * 64) / panel_id / "panel.png"
    png_path.parent.mkdir(parents=True)
    png_path.write_bytes(encoded)
    report = {
        "cases": [
            {
                "image_sha256": "a" * 64,
                "panels": [
                    {
                        "panel_id": panel_id,
                        "status": "seed-completed",
                        "panel_png": {
                            "file": "panel.png",
                            "sha256": panel_sha,
                            "byte_count": len(encoded),
                        },
                        "detector_bgr_sha256": "f" * 64,
                        "ocr_proposal_diagnostic": {
                            "unmasked_input_sha256": sha256(gray.tobytes()).hexdigest()
                        },
                    }
                ],
            }
        ]
    }
    report_payload = json.dumps(report).encode()
    report_path.write_bytes(report_payload)
    runtime = _runtime_panel(panel_id, 2, 1, gray, panel_sha, split="validation")
    descriptor = RuntimeEvidenceBinding(
        "validation", tmp_path / "unused.json", "c" * 64,
        report_path, sha256(report_payload).hexdigest()
    )
    binding = SimpleNamespace(
        train=(),
        dev=(SimpleNamespace(runtime_input=runtime),),
        train_bindings=(),
        dev_binding=descriptor,
    )

    result = inputs._capture_panels(binding, tmp_path)

    assert len(result) == 1
    assert result[0].recorded_unmasked_gray_sha256 == sha256(gray.tobytes()).hexdigest()
    assert result[0].reconstructed_bgr_sha256 == sha256(bgr.tobytes()).hexdigest()
    assert result[0].reconstructed_bgr_sha256 != "f" * 64


def test_capture_panel_rejects_changed_unmasked_gray_before_tensor_request(tmp_path: Path) -> None:
    panel_id = "11111111-1111-1111-1111-111111111111"
    encoded = _png_bytes([(10, 20, 30, 255)], 1, 1)
    gray, _ = inputs._decode_production_pixels(encoded, 1, 1)
    panel_sha = sha256(encoded).hexdigest()
    report_path = tmp_path / "artifacts" / "run" / "report.json"
    png_path = report_path.parent / ("a" * 64) / panel_id / "panel.png"
    png_path.parent.mkdir(parents=True)
    png_path.write_bytes(encoded)
    report = {
        "cases": [{"image_sha256": "a" * 64, "panels": [{
            "panel_id": panel_id,
            "status": "seed-completed",
            "panel_png": {"file": "panel.png", "sha256": panel_sha, "byte_count": len(encoded)},
            "ocr_proposal_diagnostic": {"unmasked_input_sha256": "0" * 64},
        }]}]
    }
    payload = json.dumps(report).encode()
    report_path.write_bytes(payload)
    binding = SimpleNamespace(
        train=(),
        dev=(SimpleNamespace(runtime_input=_runtime_panel(
            panel_id, 1, 1, gray, panel_sha, split="validation"
        )),),
        train_bindings=(),
        dev_binding=RuntimeEvidenceBinding(
            "validation", tmp_path / "unused", "c" * 64,
            report_path, sha256(payload).hexdigest()
        ),
    )

    with pytest.raises(inputs.ProductionHeadInputError, match="recorded OCR invocation"):
        inputs._capture_panels(binding, tmp_path)


def test_db_shrink_target_uses_fixed_ratio_and_is_immutable() -> None:
    panel = _tiled_panel((_projection((10.0, 10.0, 30.0, 20.0)),))

    target, mask, degenerate = inputs._build_supervision(panel, 64, 32)

    assert inputs.DB_SHRINK_RATIO == 0.4
    assert target.sum() == 56
    assert np.all(target[0, 13:17, 13:27] == 1)
    assert mask.min() == 1
    assert degenerate == ()
    assert not target.flags.writeable and not mask.flags.writeable


def test_collapsed_small_text_is_reported_and_ignored_instead_of_labeled_background() -> None:
    panel = _tiled_panel((_projection((4.0, 5.0, 5.0, 6.0)),))

    target, mask, degenerate = inputs._build_supervision(panel, 64, 32)

    assert target.sum() == 0
    assert mask[0, 5, 4] == 0
    assert len(degenerate) == 1
    assert degenerate[0].truth_id == "truth-1"
    assert degenerate[0].reason == "db_shrink_collapsed_after_production_resize"


@pytest.mark.parametrize(
    ("width", "height", "expected"),
    [
        (1200, 350, (1, 3, 384, 1024)),
        (350, 1200, (1, 3, 1024, 384)),
        (10, 20, (1, 3, 1024, 1024)),
    ],
)
def test_production_tensor_shape_matches_db_floor_then_ceiling_alignment(
    width: int, height: int, expected: tuple[int, int, int, int]
) -> None:
    assert inputs._production_tensor_shape(width, height) == expected


def test_captured_tensor_load_validates_hash_shape_and_preserves_full_denominator(
    tmp_path: Path,
) -> None:
    panel = _tiled_panel((_projection((10.0, 10.0, 30.0, 20.0)),), width=64, height=32)
    shape = inputs._production_tensor_shape(panel.width, panel.height)
    values = np.linspace(-1.0, 1.0, num=np.prod(shape), dtype=np.float32).reshape(shape)
    payload = values.astype("<f4").tobytes()
    (tmp_path / "train").mkdir()
    tensor_path = tmp_path / "train" / f"{panel.panel_id}.f32"
    tensor_path.write_bytes(payload)
    record = {
        "split": "train",
        "source_sha256": panel.source_sha256,
        "panel_id": panel.panel_id,
        "panel_sha256": panel.panel_sha256,
        "width": panel.width,
        "height": panel.height,
        "crop": list(panel.crop),
        "recorded_unmasked_gray_sha256": "c" * 64,
        "reconstructed_bgr_sha256": "d" * 64,
        "bgr_identity_kind": "reconstructed_from_authenticated_panel_png",
        "tensor": {
            "file": f"train/{panel.panel_id}.f32",
            "sha256": sha256(payload).hexdigest(),
            "byte_count": len(payload),
            "shape": list(shape),
            "dtype": "float32-le",
            "input_name": "x",
            "output_name": "sigmoid_0.tmp_0",
        },
    }
    truth = production_tiled_inputs.SourceTextTruth(
        "truth-1", "train", 393, "fixture-family", 39301, "a" * 64,
        "text-1", "y_tick", (10.0, 10.0, 30.0, 20.0), panel.projections,
        "single_full_projection",
    )
    split = production_tiled_inputs.ProductionTiledSplit(
        "train", 1, 1, 2, 1, 1, 0, 0, (truth,), (panel,)
    )
    requested = inputs.CapturePanelInput(
        "train", panel.source_sha256, panel.panel_id, panel.panel_sha256,
        panel.width, panel.height, panel.crop, tmp_path / "source.json", "e" * 64,
        tmp_path / "panel.png", panel.panel_sha256, 1, "c" * 64, "d" * 64,
    )

    loaded = inputs._load_split(
        split, {panel.panel_id: record}, {panel.panel_id: requested},
        tmp_path / "report.json", tmp_path
    )

    assert loaded.full_source_truth_count == 2
    assert loaded.projected_source_truth_count == 1
    assert loaded.outside_runtime_crop_truth_count == 1
    np.testing.assert_array_equal(loaded.panels[0].input_values, values)
    assert loaded.panels[0].shrink_target.sum() > 0
    assert not loaded.panels[0].input_values.flags.writeable

    record["tensor"]["sha256"] = "0" * 64
    with pytest.raises(inputs.ProductionHeadInputError, match="checksum"):
        inputs._load_split(
            split, {panel.panel_id: record}, {panel.panel_id: requested},
            tmp_path / "report.json", tmp_path
        )


def test_binding_rejection_precedes_truth_regeneration(monkeypatch, tmp_path: Path) -> None:
    events: list[str] = []

    def reject(*args, **kwargs):
        events.append("binding")
        raise RuntimeError("binding rejected")

    def forbidden(*args, **kwargs):
        events.append("truth")
        raise AssertionError("truth regenerated before binding validation")

    monkeypatch.setattr(inputs.runtime_domain_binding_v3, "load_runtime_domain_binding_v3", reject)
    monkeypatch.setattr(inputs.production_tiled_inputs, "_build_split", forbidden)

    with pytest.raises(RuntimeError, match="binding rejected"):
        inputs.prepare_capture_request(tmp_path / "binding.json", inputs.V3_BINDING_SHA256,
                                       repository_root=tmp_path)
    assert events == ["binding"]


def test_capture_request_writer_is_exclusive_and_contains_no_truth(tmp_path: Path) -> None:
    request = {
        "schema": inputs.CAPTURE_REQUEST_SCHEMA,
        "truth_included": False,
        "panels": [{"panel_id": "fixture"}],
    }
    preparation = inputs.CapturePreparation(
        inputs.V3_BINDING_SHA256, None, (), request  # type: ignore[arg-type]
    )
    path = tmp_path / "capture.json"

    digest = inputs.write_capture_request(path, preparation)

    assert digest == sha256(path.read_bytes()).hexdigest()
    assert b"truth_id" not in path.read_bytes()
    with pytest.raises(FileExistsError):
        inputs.write_capture_request(path, preparation)


def test_report_header_rejects_fingerprint_and_execution_snapshot_drift(tmp_path: Path) -> None:
    artifacts = tmp_path / "artifacts"
    artifacts.mkdir()
    request = {
        "capture_source": {"path": "artifacts/source.cs", "sha256": "1" * 64},
        "assemblies": [{"name": "fixture", "path": "artifacts/fixture.dll", "sha256": "2" * 64}],
        "candidate": {"sha256": "3" * 64},
        "detector": {"model_sha256": "4" * 64, "manifest_sha256": "5" * 64},
        "native": {"sha256": "6" * 64},
    }
    request_path = artifacts / "request.json"
    request_payload = json.dumps(request).encode()
    request_path.write_bytes(request_payload)
    preparation = inputs.CapturePreparation(
        "7" * 64, None, (), request  # type: ignore[arg-type]
    )
    report = {
        "schema": inputs.CAPTURE_REPORT_SCHEMA,
        "scope": inputs.CAPTURE_SCOPE,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "truth_used_by_capture": False,
        "model_inference": False,
        "training_input_ready": False,
        "production_approved": False,
        "binding_sha256": "7" * 64,
        "candidate_sha256": "3" * 64,
        "detector_model_sha256": "4" * 64,
        "detector_manifest_sha256": "5" * 64,
        "native_sha256": "6" * 64,
        "capture_source_sha256": "1" * 64,
        "maximum_side_length": inputs.MAXIMUM_SIDE_LENGTH,
        "dimension_multiple": inputs.DIMENSION_MULTIPLE,
        "detector_configuration_fingerprint": inputs.DETECTOR_CONFIGURATION_FINGERPRINT,
        "panel_count": 37,
        "failed_panel_count": 0,
        "assemblies": request["assemblies"],
        "request": {
            "path": "artifacts/request.json",
            "sha256": sha256(request_payload).hexdigest(),
        },
        "panels": [{
            "detector_configuration_fingerprint": inputs.DETECTOR_CONFIGURATION_FINGERPRINT
        }],
    }

    inputs._validate_report_header(report, preparation, tmp_path)
    changed_fingerprint = dict(report)
    changed_fingerprint["detector_configuration_fingerprint"] = "8" * 64
    with pytest.raises(inputs.ProductionHeadInputError, match="fingerprint"):
        inputs._validate_report_header(changed_fingerprint, preparation, tmp_path)
    changed_assemblies = dict(report)
    changed_assemblies["assemblies"] = [{**request["assemblies"][0], "sha256": "9" * 64}]
    with pytest.raises(inputs.ProductionHeadInputError, match="assemblies"):
        inputs._validate_report_header(changed_assemblies, preparation, tmp_path)


def test_public_loader_derives_historical_snapshot_paths_before_truth_join(
    monkeypatch, tmp_path: Path
) -> None:
    snapshot = tmp_path / "artifacts" / "snapshot"
    snapshot.mkdir(parents=True)
    source = snapshot / "OfficialHeadTensorCapture.cs"
    source.write_text("fixture", encoding="utf-8")
    assemblies = []
    for name in inputs.CAPTURE_ASSEMBLY_NAMES:
        path = snapshot / f"{name}.dll"
        path.write_bytes(name.encode())
        assemblies.append({
            "name": name,
            "path": path.relative_to(tmp_path).as_posix(),
            "sha256": sha256(path.read_bytes()).hexdigest(),
        })
    request = {
        "capture_source": {
            "path": source.relative_to(tmp_path).as_posix(),
            "sha256": sha256(source.read_bytes()).hexdigest(),
        },
        "assemblies": assemblies,
    }
    request_path = tmp_path / "artifacts" / "request.json"
    request_payload = json.dumps(request).encode()
    request_path.write_bytes(request_payload)
    report = {
        "request": {
            "path": request_path.relative_to(tmp_path).as_posix(),
            "sha256": sha256(request_payload).hexdigest(),
        }
    }
    report_payload = json.dumps(report).encode()

    def bound_artifact(*args, **kwargs):
        return tmp_path / "artifacts" / "report.json", report_payload

    def observe_prepare(*args, **kwargs):
        assert kwargs["capture_binary_root"] == snapshot
        assert kwargs["capture_source_path"] == source
        raise RuntimeError("snapshot derived before truth")

    monkeypatch.setattr(inputs.runtime_inputs, "_read_bound_artifact", bound_artifact)
    monkeypatch.setattr(inputs, "prepare_capture_request", observe_prepare)

    with pytest.raises(RuntimeError, match="snapshot derived"):
        inputs.load_production_head_inputs(
            tmp_path / "artifacts" / "binding.json",
            inputs.V3_BINDING_SHA256,
            tmp_path / "artifacts" / "report.json",
            "a" * 64,
            repository_root=tmp_path,
        )
