# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from ml.ocr.official_bakeoff import legend_coverage_head_inputs as subject
from ml.synthetic.io import canonical_json_bytes


def _write(path: Path, payload: bytes) -> dict[str, str]:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return {
        "path": path.as_posix(),
        "sha256": sha256(payload).hexdigest(),
    }


def _preflight(root: Path) -> tuple[Path, str]:
    generator = root / "generator.py"
    generator.write_text("# bound generator\n", encoding="utf-8")
    role_counts = {
        "annotation": 2,
        "axis_title": 2,
        "condition_label": 2,
        "legend_text": 3,
        "participant": 1,
        "phase_heading": 2,
        "x_tick": 8,
    }
    sources = []
    for source_index in range(6):
        source_sha = sha256(f"image-{source_index}".encode()).hexdigest()
        image = root / "artifacts" / f"source-{source_index}.png"
        image.parent.mkdir(parents=True, exist_ok=True)
        image.write_bytes(f"image-{source_index}".encode())
        records = []
        ordinal = 0
        current = dict(role_counts)
        current["y_tick"] = 6 if source_index < 3 else 5
        for role, count in current.items():
            for _ in range(count):
                text_id = f"text-{source_index}-{ordinal}"
                records.append({
                    "visible": True,
                    "panel_id": f"panel-{source_index}",
                    "text_id": text_id,
                    "region_id": text_id,
                    "role": role,
                    "text": f"{role} {ordinal}",
                    "rendered_pixel_box": [10.0, 10.0 + ordinal, 8.0, 1.0],
                })
                ordinal += 1
        annotation = {
            "canvas": {"width": 1200, "height": 350, "box": [0, 0, 1200, 350]},
            "texts": [],
            "panels": [{"panel_id": f"panel-{source_index}", "texts": records}],
        }
        annotation_bytes = canonical_json_bytes(annotation)
        scene_bytes = canonical_json_bytes({"seed": 91700 + source_index})
        annotation_path = root / "artifacts" / f"annotation-{source_index}.json"
        scene_path = root / "artifacts" / f"scene-{source_index}.json"
        annotation_path.write_bytes(annotation_bytes)
        scene_path.write_bytes(scene_bytes)
        families = {
            "renderer": "vector_clean",
            "font": "system_sans",
            "degradation": "none",
            "template": "classic_single",
            "marker": "geometric_basic",
        }
        sources.append({
            "index": source_index,
            "seed": 91700 + source_index,
            "families": families,
            "legend_truth_count": 3,
            "image": {
                "path": image.relative_to(root).as_posix(),
                "sha256": source_sha,
            },
            "scene": {
                "path": scene_path.relative_to(root).as_posix(),
                "sha256": sha256(scene_bytes).hexdigest(),
            },
            "annotation": {
                "path": annotation_path.relative_to(root).as_posix(),
                "sha256": sha256(annotation_bytes).hexdigest(),
            },
        })
    preflight = {
        "schema": subject.PREFLIGHT_SCHEMA,
        "status": "supplemental_train_legend_coverage_preflight_execution_complete",
        "execution_complete": True,
        "coverage_ready": True,
        "train_only": True,
        "synthetic_only": True,
        "dev_truth_rows_parsed": 0,
        "dev_pixels_read": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "model_loads": 0,
        "model_inference_runs": 0,
        "optimizer_steps": 0,
        "ocr_revision_opened": False,
        "training_authorized": False,
        "production_approved": False,
        "coverage": {
            "source_count": 6,
            "panel_count": 6,
            "legend_truth_count": 18,
            "inside_plot_legend_truth_count": 18,
            "new_raster_hashes_disjoint_from_historical_train": True,
            "new_raster_hashes_disjoint_from_fixed_dev": True,
        },
        "sources": sources,
        "generator_sources": [{
            "path": generator.relative_to(root).as_posix(),
            "sha256": sha256(generator.read_bytes()).hexdigest(),
        }],
    }
    path = root / "artifacts" / "preflight.json"
    payload = canonical_json_bytes(preflight)
    path.write_bytes(payload)
    return path, sha256(payload).hexdigest()


def test_saved_truth_is_deterministic_and_preserves_all_153_roles(tmp_path) -> None:
    path, digest = _preflight(tmp_path)

    first = subject.build_supplemental_truth_document(
        path, digest, repository_root=tmp_path
    )
    second = subject.build_supplemental_truth_document(
        path, digest, repository_root=tmp_path
    )

    assert first == second
    assert first["source_count"] == 6
    assert first["truth_count"] == 153
    assert first["role_counts"] == subject.EXPECTED_ROLE_COUNTS
    assert len({row["truth_id"] for row in first["truths"]}) == 153
    assert all(row["coordinate_space"] == "original_pixels" for row in first["truths"])


def test_saved_truth_rejects_a_duplicate_text_identity(tmp_path) -> None:
    path, _digest = _preflight(tmp_path)
    preflight = json.loads(path.read_bytes())
    descriptor = preflight["sources"][0]["annotation"]
    annotation_path = tmp_path / descriptor["path"]
    annotation = json.loads(annotation_path.read_bytes())
    annotation["panels"][0]["texts"][1]["text_id"] = annotation["panels"][0]["texts"][0]["text_id"]
    annotation["panels"][0]["texts"][1]["region_id"] = annotation["panels"][0]["texts"][0]["region_id"]
    annotation_payload = canonical_json_bytes(annotation)
    annotation_path.write_bytes(annotation_payload)
    descriptor["sha256"] = sha256(annotation_payload).hexdigest()
    preflight_payload = canonical_json_bytes(preflight)
    path.write_bytes(preflight_payload)

    with pytest.raises(subject.LegendCoverageHeadInputError, match="identity repeats"):
        subject.build_supplemental_truth_document(
            path, sha256(preflight_payload).hexdigest(), repository_root=tmp_path
        )


def test_historical_prepare_restores_only_saved_descriptor_paths(tmp_path, monkeypatch) -> None:
    binary_root = tmp_path / "backup"
    binary_root.mkdir()
    rows = []
    filenames = [
        *(f"{name}.dll" for name in subject.head.CAPTURE_ASSEMBLY_NAMES),
        "GraphReader.SyntheticRuntimeEvidence.exe",
        "GraphReader.SyntheticRuntimeEvidence.pdb",
        "GraphReader.SyntheticRuntimeEvidence.deps.json",
        "GraphReader.SyntheticRuntimeEvidence.runtimeconfig.json",
    ]
    for filename in filenames:
        path = binary_root / filename
        path.write_bytes(filename.encode())
        rows.append({
            "sha256": sha256(path.read_bytes()).hexdigest(),
            "backup_path": path.relative_to(tmp_path).as_posix(),
            "original_path": f"tools/bin/{filename}",
        })
    manifest_path = binary_root / "backup-manifest.json"
    manifest_payload = canonical_json_bytes(rows)
    manifest_path.write_bytes(manifest_payload)
    source = tmp_path / "snapshot" / "OfficialHeadTensorCapture.cs"
    source.parent.mkdir()
    source.write_text("// historical\n", encoding="utf-8")
    source_sha = sha256(source.read_bytes()).hexdigest()
    binding = {
        "original": {
            "path": "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadTensorCapture.cs",
            "sha256": source_sha,
        },
        "snapshot": source.relative_to(tmp_path).as_posix(),
    }
    binding_path = source.parent / "binding.json"
    binding_payload = canonical_json_bytes(binding)
    binding_path.write_bytes(binding_payload)
    generated = {
        "capture_source": {"path": source.relative_to(tmp_path).as_posix(), "sha256": source_sha},
        "assemblies": [{
            "name": name,
            "path": (binary_root / f"{name}.dll").relative_to(tmp_path).as_posix(),
            "sha256": sha256((binary_root / f"{name}.dll").read_bytes()).hexdigest(),
        } for name in subject.head.CAPTURE_ASSEMBLY_NAMES],
        "other": "unchanged",
    }
    stored = json.loads(json.dumps(generated))
    stored["capture_source"]["path"] = "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadTensorCapture.cs"
    for row in stored["assemblies"]:
        row["path"] = f"tools/bin/{row['name']}.dll"
    stored_path = tmp_path / "stored-request.json"
    stored_payload = canonical_json_bytes(stored)
    stored_path.write_bytes(stored_payload)
    fake = subject.head.CapturePreparation(
        "a" * 64,
        SimpleNamespace(train=object(), dev=object()),
        (),
        generated,
    )
    monkeypatch.setattr(
        subject, "EXPECTED_HISTORICAL_REQUEST_SHA256", sha256(stored_payload).hexdigest()
    )
    monkeypatch.setattr(
        subject, "EXPECTED_HISTORICAL_BINARY_MANIFEST_SHA256",
        sha256(manifest_payload).hexdigest(),
    )
    monkeypatch.setattr(
        subject, "EXPECTED_HISTORICAL_SOURCE_BINDING_SHA256",
        sha256(binding_payload).hexdigest(),
    )
    monkeypatch.setattr(subject, "EXPECTED_HISTORICAL_CAPTURE_SOURCE_SHA256", source_sha)
    monkeypatch.setattr(
        subject,
        "EXPECTED_HISTORICAL_DLL_SHA256",
        {
            name: sha256((binary_root / f"{name}.dll").read_bytes()).hexdigest()
            for name in subject.head.CAPTURE_ASSEMBLY_NAMES
        },
    )
    monkeypatch.setattr(subject, "EXPECTED_OFFICIAL_CANDIDATE_SHA256", "c" * 64)
    monkeypatch.setattr(subject.base, "prepare_text_extent_capture_request", lambda *a, **k: fake)

    restored = subject.prepare_historical_base_capture(
        tmp_path / "unused-preflight.json",
        "b" * 64,
        (),
        tmp_path / "unused-candidate.json",
        "c" * 64,
        stored_path,
        sha256(stored_payload).hexdigest(),
        binary_root,
        source,
        manifest_path,
        sha256(manifest_payload).hexdigest(),
        binding_path,
        sha256(binding_payload).hexdigest(),
        repository_root=tmp_path,
    )

    assert restored.request == stored
    assert restored.tiled_inputs is fake.tiled_inputs


def _truths(count: int, sources: int, prefix: str):
    return tuple(SimpleNamespace(
        truth_id=sha256(f"{prefix}-truth-{index}".encode()).hexdigest(),
        source_sha256=sha256(f"{prefix}-source-{index % sources}".encode()).hexdigest(),
        projections=(SimpleNamespace(status="full"),),
    ) for index in range(count))


def _panels(count: int, prefix: str):
    return tuple(SimpleNamespace(
        panel_id=f"{prefix}-panel-{index}",
        panel_sha256=sha256(f"{prefix}-panel-{index}".encode()).hexdigest(),
        tensor_sha256=sha256(f"{prefix}-tensor-{index}".encode()).hexdigest(),
        reconstructed_bgr_sha256=sha256(f"{prefix}-bgr-{index}".encode()).hexdigest(),
    ) for index in range(count))


def _head_split(source_count: int, panel_count: int, truth_count: int, prefix: str):
    return SimpleNamespace(
        name="train",
        source_count=source_count,
        panel_count=panel_count,
        full_source_truth_count=truth_count,
        projected_source_truth_count=truth_count,
        outside_runtime_crop_truth_count=0,
        partial_source_truth_count=0,
        overlapping_source_truth_count=0,
        degenerate_supervised_region_count=0,
        degenerate_source_truth_count=0,
        source_truths=_truths(truth_count, source_count, prefix),
        panels=_panels(panel_count, prefix),
    )


def test_merge_has_26_34_862_and_rejects_tensor_hash_collision() -> None:
    historical = _head_split(20, 28, 709, "base")
    supplemental = _head_split(6, 6, 153, "extra")

    merged = subject._merge_train(historical, supplemental)

    assert (merged.source_count, merged.panel_count, merged.full_source_truth_count) == (26, 34, 862)
    supplemental.panels[0].tensor_sha256 = historical.panels[0].tensor_sha256
    with pytest.raises(subject.LegendCoverageHeadInputError, match="tensor hash collides"):
        subject._merge_train(historical, supplemental)


def test_compose_preserves_the_exact_fixed_development_object() -> None:
    historical = _head_split(20, 28, 709, "base")
    supplemental = _head_split(6, 6, 153, "extra")
    fixed_dev = object()
    base_inputs = SimpleNamespace(
        binding_sha256="1" * 64,
        capture_report_sha256="2" * 64,
        train=historical,
        dev=fixed_dev,
    )
    preparation = SimpleNamespace(binding_sha256="3" * 64)

    result = subject._compose_inputs(
        base_inputs, preparation, supplemental, "4" * 64
    )

    assert result.dev is fixed_dev
    assert (result.train.source_count, result.train.panel_count) == (26, 34)
    assert result.train.full_source_truth_count == 862
