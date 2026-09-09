# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path

import numpy as np
import pytest
from PIL import Image

import score_db_geometry_observation as scorer
import score_family_ocr as family_ocr
from ml.ocr.component_region_detector_v6.dataset import Box


def _json_bytes(value):
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode("utf-8")


def _png_bytes(image):
    payload = BytesIO()
    image.save(payload, format="PNG")
    return payload.getvalue()


def _hash(payload):
    return sha256(payload).hexdigest()


def _write_json(path, value):
    payload = _json_bytes(value)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return _hash(payload)


def _polygon(left, top, right, bottom):
    points = [
        {"x": left, "y": top, "is_finite": True},
        {"x": right, "y": top, "is_finite": True},
        {"x": right, "y": bottom, "is_finite": True},
        {"x": left, "y": bottom, "is_finite": True},
    ]
    return {
        "points": points,
        "bounds": {
            "x": left, "y": top, "width": right - left, "height": bottom - top,
            "left": left, "top": top, "right": right, "bottom": bottom,
            "center": {"x": (left + right) / 2, "y": (top + bottom) / 2, "is_finite": True},
            "is_valid": True,
        },
    }


def _region(identifier, polygon, confidence=0.8, density=0.7):
    return {
        "region_id": identifier,
        "polygon": deepcopy(polygon),
        "orientation_degrees": 0,
        "detection_confidence": confidence,
        "context": None,
        "coordinate_space": "original_pixels",
        "evidence": {
            "component_count": 1, "ink_density": density,
            "text_likelihood": confidence, "structure_likelihood": 1 - confidence,
            "likely_graph_structure": False, "reasons": ["onnx_db_text_probability"],
        },
    }


def _observation(identifier, initial, expanded, input_sha, width=10, height=10):
    return {
        "input_sha256": input_sha,
        "image_width": width,
        "image_height": height,
        "tensor_width": 960,
        "tensor_height": 960,
        "accepted_contours": [{
            "returned_region_id": identifier,
            "initial_polygon": deepcopy(initial),
            "expanded_polygon": deepcopy(expanded),
            "detection_confidence": 0.8,
            "ink_density": 0.7,
        }],
    }


def _fixture(tmp_path, monkeypatch):
    monkeypatch.setattr(scorer, "REPOSITORY_ROOT", tmp_path)
    monkeypatch.setattr(family_ocr, "REPOSITORY_ROOT", tmp_path)
    monkeypatch.setattr(scorer, "EXPECTED_TRUTH_COUNTS", {"train": 3, "validation": 3})
    monkeypatch.setattr(scorer, "EXPECTED_TOTAL_TRUTHS", 6)

    external = tmp_path / "external"
    external.mkdir()
    native = external / "OpenCvSharpExtern.dll"
    model = external / "detector.onnx"
    manifest = external / "detector.json"
    native.write_bytes(b"native-runtime")
    model.write_bytes(b"detector-model")
    manifest.write_bytes(b'{"model":"detector"}\n')
    detector = {
        "model_path": str(model),
        "model_id": "PP-OCRv5_mobile_det",
        "model_version": "5.0.0",
        "model_sha256": family_ocr._sha256_file(model),
        "manifest_path": str(manifest),
        "manifest_sha256": family_ocr._sha256_file(manifest),
    }
    candidate = {
        "schema": "graphreader.local-synthetic-ocr-candidate.v1",
        "production_approved": False,
        "native_path": str(native),
        "native_sha256": family_ocr._sha256_file(native),
        "native_scope": "reviewed-source-runtime-local-diagnostic",
        "license_inputs": [],
        "detector": detector,
        "recognizer": {},
    }
    candidate_path = tmp_path / "artifacts" / "candidate" / "candidate.json"
    candidate_hash = _write_json(candidate_path, candidate)

    source_array = np.arange(30 * 10 * 3, dtype=np.uint8).reshape(10, 30, 3)
    sources = {
        "train": Image.fromarray(source_array, "RGB"),
        "validation": Image.fromarray(np.roll(source_array, 1, axis=1), "RGB"),
    }
    source_hashes = {}
    truth_by_hash = {}
    manifests = {}
    manifest_paths = {}
    manifest_hashes = {}
    for split, seed in (("train", 393), ("validation", 394)):
        source_bytes = _png_bytes(sources[split])
        source_hash = _hash(source_bytes)
        source_hashes[split] = source_hash
        truth_by_hash[source_hash] = (Box(2, 2, 6, 6), Box(12, 2, 16, 6), Box(24, 2, 28, 6))
        directory = tmp_path / "artifacts" / f"inputs-{split}"
        directory.mkdir(parents=True)
        image_name = f"{split}-{seed}-{source_hash[:12]}.png"
        (directory / image_name).write_bytes(source_bytes)
        value = {
            "schema": family_ocr.MANIFEST_SCHEMA,
            "source": family_ocr.MANIFEST_SOURCE,
            "preset": "smoke",
            "seed": seed,
            "split": split,
            "contains_truth": False,
            "contains_precomputed_masks": False,
            "images": [{
                "image": image_name, "image_sha256": source_hash,
                "width": 30, "height": 10, "split": split,
                "family": f"family-{split}", "seed": seed,
            }],
        }
        path = directory / "input-manifest.json"
        manifests[split] = value
        manifest_paths[split] = path
        manifest_hashes[split] = _write_json(path, value)

    protocol = {
        "evidence_policy": "ml/policy/evidence-policy.json",
        "hypothesis": "expanded geometry may be oversized",
        "isolated_change": "observe accepted contours without changing output",
        "split_identities": {
            "train_manifest": {
                "path": manifest_paths["train"].relative_to(tmp_path).as_posix(),
                "sha256": manifest_hashes["train"],
            },
            "dev_manifest": {
                "path": manifest_paths["validation"].relative_to(tmp_path).as_posix(),
                "sha256": manifest_hashes["validation"],
            },
            "runtime_candidate": {
                "path": candidate_path.relative_to(tmp_path).as_posix(),
                "sha256": candidate_hash,
            },
        },
        "metric": "fixed IoU 0.5 maximum-cardinality precision and recall",
        "acceptance_bar": "descriptive shared tier1 reference only",
        "budget": {"train_dev_runs": "unlimited", "sealed_runs": 0},
    }
    protocol_path = tmp_path / scorer.PROTOCOL_PATH
    protocol_hash = _write_json(protocol_path, protocol)
    monkeypatch.setattr(scorer, "EXPECTED_PROTOCOL_SHA256", protocol_hash)

    execution_directory = tmp_path / "artifacts" / "execution"
    execution_directory.mkdir(parents=True)
    assembly_names = (
        "GraphReader.SyntheticRuntimeEvidence", "GraphReader.App", "GraphReader.Axis",
        "GraphReader.Ocr", "GraphReader.Inference", "GraphReader.Pdf",
    )
    execution_entries = []
    assemblies = []
    for index, name in enumerate(assembly_names):
        payload = f"assembly-{index}-{name}".encode("ascii")
        path = execution_directory / f"{name}.dll"
        path.write_bytes(payload)
        file_hash = _hash(payload)
        execution_entries.append({"path": path.name, "sha256": file_hash, "bytes": len(payload)})
        assemblies.append({"name": name, "sha256": file_hash})
    execution_manifest_path = execution_directory / "execution-files.json"
    execution_manifest_hash = _write_json(execution_manifest_path, execution_entries)
    reports = {}
    report_paths = {}
    sidecar_paths = []
    for split in ("train", "validation"):
        source = sources[split]
        source_hash = source_hashes[split]
        output = tmp_path / "artifacts" / f"run-{split}"
        panels = []
        for panel_index, crop_x in enumerate((0, 10)):
            identity_index = panel_index + 1 + (0 if split == "train" else 2)
            panel_id = f"{identity_index:08d}-1111-4111-8111-{identity_index:012d}"
            panel_image = source.crop((crop_x, 0, crop_x + 10, 10))
            panel_bytes = _png_bytes(panel_image)
            panel_hash = _hash(panel_bytes)
            panel_directory = output / source_hash / panel_id
            panel_directory.mkdir(parents=True)
            (panel_directory / "panel.png").write_bytes(panel_bytes)
            crop = {"x": crop_x, "y": 0, "width": 10, "height": 10}
            source_to_panel = [1, 0, -crop_x, 0, 1, 0, 0, 0, 1]
            panel_to_source = [1, 0, crop_x, 0, 1, 0, 0, 0, 1]
            panel = {
                "panel_id": panel_id,
                "image_sha256": panel_hash,
                "width": 10,
                "height": 10,
                "source_image_sha256": source_hash,
                "source_width": 30,
                "source_height": 10,
                "crop": crop,
                "requested_crop": deepcopy(crop),
                "source_to_panel_matrix": source_to_panel,
                "panel_to_source_matrix": panel_to_source,
                "panel_png": {"file": "panel.png", "sha256": panel_hash, "byte_count": len(panel_bytes)},
                "import_warnings": [],
                "status": "seed-completed",
            }
            initial = _polygon(2, 2, 6, 6)
            expanded = _polygon(1, 1, 8, 8)
            model_regions = {}
            invocations = []
            for kind, stage, byte in (("axis-masked", "masked", "a"), ("unmasked", "unmasked", "b")):
                identifier = f"{stage}-{panel_id}"
                gray_sha = byte * 64
                bgr_sha = ("c" if stage == "masked" else "d") * 64
                model_regions[stage] = [_region(identifier, expanded)]
                invocations.append({
                    "kind": kind,
                    "canonical_gray_sha256": gray_sha,
                    "detector_bgr_sha256": bgr_sha,
                    "observation": _observation(identifier, initial, expanded, bgr_sha),
                })
            sidecar = {
                "schema": scorer.SIDECAR_SCHEMA,
                "scope": scorer.SIDECAR_SCOPE,
                "production_approved": False,
                "training_input_ready": False,
                "truth_used_by_runtime": False,
                "protocol": {"path": scorer.PROTOCOL_PATH.as_posix(), "sha256": protocol_hash},
                "input_manifest_sha256": manifest_hashes[split],
                "candidate_sha256": candidate_hash,
                "source": {"image_sha256": source_hash, "width": 30, "height": 10},
                "panel": {
                    "panel_id": panel_id, "image_sha256": panel_hash,
                    "width": 10, "height": 10, "crop": crop,
                    "requested_crop": deepcopy(crop),
                    "source_to_panel_matrix": source_to_panel,
                    "panel_to_source_matrix": panel_to_source,
                },
                "detector_model": {
                    "model_id": detector["model_id"], "model_version": detector["model_version"],
                    "model_sha256": detector["model_sha256"],
                    "manifest_path": detector["manifest_path"], "manifest_sha256": detector["manifest_sha256"],
                },
                "native_sha256": candidate["native_sha256"],
                "native_scope": candidate["native_scope"],
                "runtime_assemblies": deepcopy(assemblies),
                "invocations": invocations,
            }
            sidecar_path = panel_directory / "ocr-db-geometry-observations.json"
            sidecar_hash = _write_json(sidecar_path, sidecar)
            sidecar_paths.append(sidecar_path)
            panel["ocr_proposal_diagnostic"] = {
                "model_regions": model_regions["masked"],
                "component_regions": [],
                "detector_input_sha256": "a" * 64,
                "unmasked_model_regions": model_regions["unmasked"],
                "unmasked_input_sha256": "b" * 64,
                "coordinate_space": "original_pixels",
                "used_as_accepted_evidence": False,
                "db_geometry_diagnostic_sidecar": {
                    "file": sidecar_path.name,
                    "sha256": sidecar_hash,
                    "byte_count": sidecar_path.stat().st_size,
                },
            }
            panel["ocr_configured_models"] = {
                "models": [{
                    "task": "ocr_detection", "model_id": detector["model_id"],
                    "version": detector["model_version"], "sha256": detector["model_sha256"],
                    "execution_provider": "cpu",
                }],
                "scope": "unapproved_local_synthetic_candidate",
            }
            panels.append(panel)
        report = {
            "schema": family_ocr.REPORT_SCHEMA_V2,
            "scope": family_ocr.REPORT_SCOPE,
            "production_approved": False,
            "training_input_ready": False,
            "input_manifest_sha256": manifest_hashes[split],
            "candidate_sha256": candidate_hash,
            "native_sha256": candidate["native_sha256"],
            "native_scope": candidate["native_scope"],
            "runtime_assemblies": deepcopy(assemblies),
            "count": 1, "completed": 1, "failed": 0,
            "panel_count": 2, "completed_panels": 2, "failed_panels": 0,
            "cases": [{
                "image_sha256": source_hash, "width": 30, "height": 10,
                "status": "panels-completed", "panels": panels,
            }],
        }
        path = output / "report.json"
        _write_json(path, report)
        reports[split] = report
        report_paths[split] = path

    def regenerate(split, dataset_seed, images):
        del split, dataset_seed
        return {
            image["image_sha256"]: ({}, {"truths": truth_by_hash[image["image_sha256"]]})
            for image in images
        }

    def truth_regions(annotation):
        truths = annotation["truths"]
        return truths, tuple("tick" for _ in truths)

    monkeypatch.setattr(family_ocr, "_regenerate", regenerate)
    monkeypatch.setattr(family_ocr, "_truth_regions", truth_regions)
    output_path = tmp_path / "artifacts" / "score" / "result.json"
    return {
        "protocol_path": protocol_path,
        "protocol": protocol,
        "candidate_path": candidate_path,
        "model_path": model,
        "execution_manifest_path": execution_manifest_path,
        "execution_manifest_hash": execution_manifest_hash,
        "reports": reports,
        "report_paths": report_paths,
        "sidecar_paths": sidecar_paths,
        "output_path": output_path,
    }


def _rewrite_report(fixture, split):
    _write_json(fixture["report_paths"][split], fixture["reports"][split])


def _rewrite_sidecar_and_descriptor(fixture, index):
    path = fixture["sidecar_paths"][index]
    value = json.loads(path.read_text(encoding="utf-8"))
    payload = _json_bytes(value)
    path.write_bytes(payload)
    split = "train" if index < 2 else "validation"
    panel_index = index % 2
    descriptor = fixture["reports"][split]["cases"][0]["panels"][panel_index]["ocr_proposal_diagnostic"]["db_geometry_diagnostic_sidecar"]
    descriptor["sha256"] = _hash(payload)
    descriptor["byte_count"] = len(payload)
    _rewrite_report(fixture, split)


def _run(fixture):
    return scorer.score(
        fixture["protocol_path"], fixture["execution_manifest_path"],
        fixture["execution_manifest_hash"], fixture["report_paths"]["train"],
        fixture["report_paths"]["validation"], fixture["output_path"])


def test_scores_identical_contours_and_retains_full_source_truth_once(tmp_path, monkeypatch):
    fixture = _fixture(tmp_path, monkeypatch)
    result = _run(fixture)

    assert result["integrity"]["full_source_truth_count"] == 6
    assert result["integrity"]["expanded_point_sequences_equal_model_regions"] is True
    for stage in ("masked", "unmasked"):
        initial = result["geometry_metrics"][stage]["initial"]
        expanded = result["geometry_metrics"][stage]["expanded"]
        assert initial["train"] == {
            "truth_region_count": 3, "predicted_region_count": 2,
            "true_positives": 2, "false_positives": 0, "false_negatives": 1,
            "precision": 1.0, "recall": 2 / 3,
        }
        assert expanded["train"]["predicted_region_count"] == 2
        assert expanded["train"]["true_positives"] == 0
        assert initial["combined"]["truth_region_count"] == 6
        assert initial["combined"]["predicted_region_count"] == 4
    assert result["production_approval"] is False


@pytest.mark.parametrize("mutation", [
    "protocol-bytes", "candidate-bytes", "model-bytes", "execution-manifest-bytes",
    "execution-file-bytes", "sidecar-bytes",
    "sidecar-source", "sidecar-crop", "sidecar-native", "sidecar-model",
    "sidecar-assemblies", "input-hash", "contour-count", "expanded-points",
    "expanded-id", "confidence", "missing-sidecar", "report-assemblies", "overlap",
])
def test_rejects_mixed_tampered_or_incomplete_evidence(tmp_path, monkeypatch, mutation):
    fixture = _fixture(tmp_path, monkeypatch)
    first_sidecar_path = fixture["sidecar_paths"][0]
    sidecar = json.loads(first_sidecar_path.read_text(encoding="utf-8"))
    if mutation == "protocol-bytes":
        fixture["protocol_path"].write_bytes(fixture["protocol_path"].read_bytes() + b" ")
    elif mutation == "candidate-bytes":
        fixture["candidate_path"].write_bytes(fixture["candidate_path"].read_bytes() + b" ")
    elif mutation == "model-bytes":
        fixture["model_path"].write_bytes(b"changed-model")
    elif mutation == "execution-manifest-bytes":
        fixture["execution_manifest_path"].write_bytes(
            fixture["execution_manifest_path"].read_bytes() + b" ")
    elif mutation == "execution-file-bytes":
        (fixture["execution_manifest_path"].parent / "GraphReader.Ocr.dll").write_bytes(b"changed")
    elif mutation == "sidecar-bytes":
        first_sidecar_path.write_bytes(first_sidecar_path.read_bytes() + b" ")
    elif mutation == "sidecar-source":
        sidecar["source"]["width"] = 31
    elif mutation == "sidecar-crop":
        sidecar["panel"]["crop"]["x"] = 1
    elif mutation == "sidecar-native":
        sidecar["native_sha256"] = "f" * 64
    elif mutation == "sidecar-model":
        sidecar["detector_model"]["model_id"] = "foreign"
    elif mutation == "sidecar-assemblies":
        sidecar["runtime_assemblies"][0]["sha256"] = "f" * 64
    elif mutation == "input-hash":
        sidecar["invocations"][0]["observation"]["input_sha256"] = "f" * 64
    elif mutation == "contour-count":
        sidecar["invocations"][0]["observation"]["accepted_contours"] = []
    elif mutation == "expanded-points":
        polygon = sidecar["invocations"][0]["observation"]["accepted_contours"][0]["expanded_polygon"]
        polygon["points"] = _polygon(2, 1, 8, 8)["points"]
        polygon["bounds"] = _polygon(2, 1, 8, 8)["bounds"]
    elif mutation == "expanded-id":
        sidecar["invocations"][0]["observation"]["accepted_contours"][0]["returned_region_id"] = "foreign"
    elif mutation == "confidence":
        sidecar["invocations"][0]["observation"]["accepted_contours"][0]["detection_confidence"] = 0.81
    elif mutation == "missing-sidecar":
        first_sidecar_path.unlink()
    elif mutation == "report-assemblies":
        fixture["reports"]["train"]["runtime_assemblies"][0]["sha256"] = "f" * 64
        _rewrite_report(fixture, "train")
    elif mutation == "overlap":
        second = fixture["reports"]["train"]["cases"][0]["panels"][1]
        second["crop"]["x"] = 9
        second["requested_crop"]["x"] = 9
        second["source_to_panel_matrix"][2] = -9
        second["panel_to_source_matrix"][2] = 9
        _rewrite_report(fixture, "train")
    if mutation in {
        "sidecar-source", "sidecar-crop", "sidecar-native", "sidecar-model",
        "sidecar-assemblies", "input-hash", "contour-count", "expanded-points",
        "expanded-id", "confidence",
    }:
        _write_json(first_sidecar_path, sidecar)
        _rewrite_sidecar_and_descriptor(fixture, 0)

    with pytest.raises((scorer.EvidenceError, OSError)):
        _run(fixture)


def test_rejects_denominator_drift_even_when_all_observations_are_valid(tmp_path, monkeypatch):
    fixture = _fixture(tmp_path, monkeypatch)
    monkeypatch.setattr(scorer, "EXPECTED_TRUTH_COUNTS", {"train": 2, "validation": 3})
    with pytest.raises(scorer.EvidenceError, match="fixed denominator"):
        _run(fixture)


def test_rejects_mixed_train_dev_executed_assemblies_even_when_sidecars_agree(tmp_path, monkeypatch):
    fixture = _fixture(tmp_path, monkeypatch)
    report = fixture["reports"]["validation"]
    report["runtime_assemblies"][0]["sha256"] = "f" * 64
    for index in (2, 3):
        path = fixture["sidecar_paths"][index]
        sidecar = json.loads(path.read_text(encoding="utf-8"))
        sidecar["runtime_assemblies"][0]["sha256"] = "f" * 64
        _write_json(path, sidecar)
        _rewrite_sidecar_and_descriptor(fixture, index)
    _rewrite_report(fixture, "validation")

    with pytest.raises(scorer.EvidenceError, match="authenticated execution files"):
        _run(fixture)


def test_invalid_sidecar_is_rejected_before_truth_regeneration(tmp_path, monkeypatch):
    fixture = _fixture(tmp_path, monkeypatch)
    calls = 0
    regenerate = family_ocr._regenerate

    def counted_regenerate(*args, **kwargs):
        nonlocal calls
        calls += 1
        return regenerate(*args, **kwargs)

    monkeypatch.setattr(family_ocr, "_regenerate", counted_regenerate)
    # Corrupt development after the complete train exchange. No train truth may
    # be regenerated until the development sidecars also pass validation.
    path = fixture["sidecar_paths"][2]
    sidecar = json.loads(path.read_text(encoding="utf-8"))
    sidecar["source"]["width"] = 31
    _write_json(path, sidecar)
    _rewrite_sidecar_and_descriptor(fixture, 2)

    with pytest.raises(scorer.EvidenceError, match="source identity"):
        _run(fixture)
    assert calls == 0
