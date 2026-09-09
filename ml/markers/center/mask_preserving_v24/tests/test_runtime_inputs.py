# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from dataclasses import dataclass, replace
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from typing import Callable
from uuid import UUID

import numpy as np
from PIL import Image
import pytest

from ml.markers.center.mask_preserving_v24 import runtime_inputs as runtime_inputs_module
from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    REPOSITORY_ROOT,
    SYNTHETIC_SOURCE_RELATIVE_PATHS,
    VALIDATOR_RELATIVE_PATH,
    RuntimeAlgorithmIdentity,
    RuntimeAssemblyIdentity,
    RuntimeEvidenceBinding,
    RuntimeImplementationIdentity,
    RuntimeInputError,
    RuntimeModelIdentity,
    RuntimeSourceIdentity,
    load_bound_runtime_inputs,
)


def _hash(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _fake_hash(character: str) -> str:
    return character * 64


ASSEMBLIES = (
    RuntimeAssemblyIdentity("GraphReader.SyntheticRuntimeEvidence", _fake_hash("1")),
    RuntimeAssemblyIdentity("GraphReader.App", _fake_hash("2")),
    RuntimeAssemblyIdentity("GraphReader.Axis", _fake_hash("3")),
    RuntimeAssemblyIdentity("GraphReader.Ocr", _fake_hash("4")),
)
ALGORITHM = RuntimeAlgorithmIdentity(
    algorithm_id="raster-residual-artifacts",
    version="1",
    assembly_sha256=_fake_hash("2"),
    configuration_sha256=_fake_hash("5"),
    stage_version="raster-residual-artifacts:1",
    parameters="fixed-test-parameters",
    dependencies=(RuntimeAssemblyIdentity("GraphReader.Ocr", _fake_hash("4")),),
)
MODELS = (
    RuntimeModelIdentity(
        "axis", "axis-opencv-v1", "OpenCvSharpExtern", "axis-opencv-v1",
        _fake_hash("6"), "cpu",
    ),
    RuntimeModelIdentity("ocr", "5.0.0", "detector", "5.0.0", _fake_hash("7"), "cpu"),
    RuntimeModelIdentity("ocr", "5.0.0", "recognizer", "5.0.0", _fake_hash("8"), "cpu"),
)


@dataclass(frozen=True)
class Fixture:
    root: Path
    train: RuntimeEvidenceBinding
    dev: RuntimeEvidenceBinding
    implementation: RuntimeImplementationIdentity


def _source_pixels(scene_seed: int) -> np.ndarray:
    common_panel = np.asarray(
        [[[0, 0, 0], [64, 64, 64], [255, 255, 255]],
         [[20, 20, 20], [128, 128, 128], [220, 220, 220]]],
        dtype=np.uint8,
    )
    edge = 10 if scene_seed == 1001 else 240
    return np.concatenate(
        (common_panel, np.full((2, 1, 3), edge, dtype=np.uint8)), axis=1
    )


@pytest.fixture(autouse=True)
def _use_tiny_deterministic_regeneration(monkeypatch: pytest.MonkeyPatch) -> None:
    load_validator = runtime_inputs_module._load_bound_validator

    def load_with_fixture_regenerator(expected_sha256: str):
        validator = load_validator(expected_sha256)

        def regenerate(split: str, dataset_seed: int, images: list[dict[str, object]]):
            if dataset_seed != 393:
                raise validator.EvidenceError("regenerated dataset seed differs")
            regenerated = {}
            for image in images:
                payload = _png_bytes(_source_pixels(int(image["seed"])))
                image_sha = _hash(payload)
                expected_name = f"{split}-{int(image['seed'])}-{image_sha[:12]}.png"
                if (
                    image_sha != image["image_sha256"]
                    or expected_name != image["image"]
                ):
                    raise validator.EvidenceError(
                        f"regenerated PNG identity differs for seed {image['seed']}"
                    )
                regenerated[image_sha] = ({}, {})
            return regenerated

        validator._regenerate = regenerate
        return validator

    monkeypatch.setattr(runtime_inputs_module, "_load_bound_validator", load_with_fixture_regenerator)


def _write_json(path: Path, value: object) -> str:
    payload = json.dumps(value, indent=2, sort_keys=True).encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return _hash(payload)


def _png_bytes(pixels: np.ndarray) -> bytes:
    stream = BytesIO()
    Image.fromarray(pixels, mode="RGB").save(stream, format="PNG")
    return stream.getvalue()


def _envelope(path: Path) -> dict[str, object]:
    payload = path.read_bytes()
    return {"file": path.name, "sha256": _hash(payload), "byte_count": len(payload)}


def _model_envelope(model: RuntimeModelIdentity, panel_sha: str) -> dict[str, object]:
    return {
        "contract_version": 1,
        "stage": model.stage,
        "stage_version": model.stage_version,
        "input_sha256": panel_sha,
        "coordinate_space": "original_pixels",
        "model": {
            "model_id": model.model_id,
            "version": model.version,
            "sha256": model.sha256,
            "provider": model.provider,
        },
    }


def _make_split(
    root: Path,
    split: str,
    scene_seed: int,
    family: str,
    pixels: np.ndarray,
) -> RuntimeEvidenceBinding:
    source_payload = _png_bytes(pixels)
    source_sha = _hash(source_payload)
    input_directory = root / "artifacts" / "inputs" / split
    source_name = f"{split}-{scene_seed}-{source_sha[:12]}.png"
    source_path = input_directory / source_name
    source_path.parent.mkdir(parents=True, exist_ok=True)
    source_path.write_bytes(source_payload)
    source_height, source_width = pixels.shape[:2]
    panel_pixels = pixels[:, :3]
    panel_payload = _png_bytes(panel_pixels)
    panel_sha = _hash(panel_payload)
    height, width = panel_pixels.shape[:2]
    manifest = {
        "schema": "graphreader.synthetic-runtime-raster-inputs.v1",
        "source": "project-owned-synthetic-five-axis-family-v1",
        "preset": "smoke",
        "seed": 393,
        "split": split,
        "contains_truth": False,
        "contains_precomputed_masks": False,
        "images": [{
            "image": source_name,
            "image_sha256": source_sha,
            "width": source_width,
            "height": source_height,
            "split": split,
            "family": family,
            "seed": scene_seed,
        }],
    }
    manifest_path = input_directory / "input-manifest.json"
    manifest_sha = _write_json(manifest_path, manifest)

    panel_id = str(UUID(int=scene_seed))
    panel_directory = root / "artifacts" / "runtime" / split / source_sha / panel_id
    panel_directory.mkdir(parents=True)
    panel_png = panel_directory / "panel.png"
    panel_png.write_bytes(panel_payload)
    gray8 = np.asarray(Image.open(BytesIO(panel_payload)).convert("L"), dtype=np.uint8)
    geometry = np.asarray([[0.0, 0.25, 0.5], [0.0, 0.0, 0.75]], dtype="<f4")
    ocr = np.asarray([[0.1, 0.0, 0.2], [0.0, 0.3, 0.0]], dtype="<f4")
    composed = np.maximum(geometry, ocr).astype("<f4")
    plane_records: dict[str, dict[str, object]] = {}
    for key, name, array in (
        ("source_gray", "source-gray8.bin", gray8),
        ("ocr_mask", "ocr-seed.f32", ocr),
        ("geometry_mask", "geometry-seed.f32", geometry),
        ("composed_artifact_candidate_mask", "composed-artifact-candidate.f32", composed),
    ):
        path = panel_directory / name
        path.write_bytes(array.tobytes())
        plane_records[key] = _envelope(path)

    context = {
        "run_id": "00000000-0000-0000-0000-000000000001",
        "project_id": "00000000-0000-0000-0000-000000000002",
        "panel_id": panel_id,
    }
    artifact_source = {
        **context,
        "contract_version": 1,
        "stage": "markers",
        "stage_version": ALGORITHM.stage_version,
        "input_sha256": panel_sha,
        "coordinate_space": "original_pixels",
        "model": None,
    }
    residual = {
        **artifact_source,
        "panel_id": panel_id,
    }
    panel = {
        "panel_id": panel_id,
        "image_sha256": panel_sha,
        "width": width,
        "height": height,
        "source_image_sha256": source_sha,
        "source_width": source_width,
        "source_height": source_height,
        "crop": {"x": 0, "y": 0, "width": width, "height": height},
        "requested_crop": {"x": 0.0, "y": 0.0, "width": float(width), "height": float(height)},
        "source_to_panel_matrix": [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0],
        "panel_to_source_matrix": [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0],
        "panel_png": _envelope(panel_png),
        "import_warnings": [],
        "status": "seed-completed",
        **plane_records,
        "ocr_configured_models": {
            "scope": "unapproved_local_synthetic_candidate",
            "models": [
                {"task": task, "model_id": model.model_id, "version": model.version,
                 "sha256": model.sha256, "execution_provider": model.provider}
                for task, model in zip(("ocr_detection", "ocr_recognition"), MODELS[1:], strict=True)
            ],
        },
        "ocr": {
            **context, "succeeded": True, "failure": None,
            # The actual OCR pipeline allocates its own subrun UUID.
            "run_id": "00000000-0000-0000-0000-000000000003",
            "contract_version": 1, "stage": "ocr", "stage_version": MODELS[1].stage_version,
            "input_sha256": panel_sha, "coordinate_space": "original_pixels",
            "regions": [{"text": "fixture"}], "masks": [], "region_failures": [], "warnings": [],
            "cache": {"crop_count": 1, "batch_count": 1},
        },
        "composed_mask_source_envelopes": [
            *({**context, **_model_envelope(model, panel_sha)} for model in MODELS),
            artifact_source,
        ],
        "residual_artifact_candidate_envelope": residual,
    }
    report = {
        "schema": "graphreader.synthetic-runtime-seed-evidence.v2",
        "scope": "local-synthetic-seed-diagnostic",
        "production_approved": False,
        "training_input_ready": False,
        "complete_artifact_mask": False,
        "missing_stage": "representative-artifact-validation-and-training-input-binding",
        "input_manifest_sha256": manifest_sha,
        "candidate_sha256": "",  # Filled after the shared candidate is written.
        "native_sha256": _fake_hash("6"),
        "native_scope": "test-runtime-unapproved",
        "runtime_assemblies": [
            {"name": entry.name, "sha256": entry.sha256} for entry in ASSEMBLIES
        ],
        "artifact_candidate_identity": {
            "algorithm_id": ALGORITHM.algorithm_id,
            "version": ALGORITHM.version,
            "assembly_sha256": ALGORITHM.assembly_sha256,
            "configuration_sha256": ALGORITHM.configuration_sha256,
            "stage_version": ALGORITHM.stage_version,
            "assembly_warning": f"artifact_algorithm_assembly_sha256:{ALGORITHM.assembly_sha256}",
            "configuration_warning": (
                f"artifact_algorithm_configuration_sha256:{ALGORITHM.configuration_sha256}"
            ),
        },
        "artifact_candidate_configuration": {
            "algorithm": ALGORITHM.algorithm_id,
            "version": ALGORITHM.version,
            "parameters": ALGORITHM.parameters,
            "dependencies": [
                {"assembly": entry.name, "sha256": entry.sha256}
                for entry in ALGORITHM.dependencies
            ],
        },
        "count": 1,
        "completed": 1,
        "failed": 0,
        "panel_count": 1,
        "completed_panels": 1,
        "failed_panels": 0,
        "cases": [{
            "image_sha256": source_sha,
            "width": source_width,
            "height": source_height,
            "status": "panels-completed",
            "panels": [panel],
        }],
    }
    report_path = root / "artifacts" / "runtime" / split / "report.json"
    _write_json(report_path, report)
    return RuntimeEvidenceBinding(split, manifest_path, manifest_sha, report_path, "")


def _make_fixture(
    tmp_path: Path, *, overlap: bool = False, relabeled_source: bool = False
) -> Fixture:
    train_pixels = _source_pixels(1001).copy()
    if relabeled_source:
        train_pixels[0, 0] = [1, 2, 3]
    dev_seed = 1001 if overlap else 2001
    dev_pixels = _source_pixels(dev_seed)
    train = _make_split(tmp_path, "train", 1001, "family-train", train_pixels)
    dev = _make_split(tmp_path, "validation", dev_seed, "family-dev", dev_pixels)
    candidate = {
        "schema": "graphreader.local-synthetic-ocr-candidate.v1",
        "production_approved": False,
        "native_sha256": _fake_hash("6"),
        "native_scope": "test-runtime-unapproved",
        "detector": {
            "model_id": MODELS[1].model_id,
            "model_version": MODELS[1].version,
            "model_sha256": MODELS[1].sha256,
        },
        "recognizer": {
            "model_id": MODELS[2].model_id,
            "model_version": MODELS[2].version,
            "model_sha256": MODELS[2].sha256,
        },
    }
    candidate_path = tmp_path / "artifacts" / "metadata" / "candidate.json"
    candidate_sha = _write_json(candidate_path, candidate)
    for binding in (train, dev):
        report = json.loads(binding.report_path.read_text(encoding="utf-8"))
        report["candidate_sha256"] = candidate_sha
        report_sha = _write_json(binding.report_path, report)
        if binding.split == "train":
            train = replace(binding, report_sha256=report_sha)
        else:
            dev = replace(binding, report_sha256=report_sha)
    validator_sha = _hash((REPOSITORY_ROOT / VALIDATOR_RELATIVE_PATH).read_bytes())
    synthetic_sources = []
    for relative_path in SYNTHETIC_SOURCE_RELATIVE_PATHS:
        payload = (REPOSITORY_ROOT / relative_path).read_bytes()
        copied_path = tmp_path / relative_path
        copied_path.parent.mkdir(parents=True, exist_ok=True)
        copied_path.write_bytes(payload)
        synthetic_sources.append(RuntimeSourceIdentity(relative_path, _hash(payload)))
    implementation = RuntimeImplementationIdentity(
        validator_source_sha256=validator_sha,
        synthetic_sources=tuple(synthetic_sources),
        candidate_path=candidate_path,
        candidate_sha256=candidate_sha,
        native_sha256=_fake_hash("6"),
        native_scope="test-runtime-unapproved",
        runtime_assemblies=ASSEMBLIES,
        algorithm=ALGORITHM,
        models=MODELS,
    )
    return Fixture(tmp_path, train, dev, implementation)


def _rewrite_report(
    binding: RuntimeEvidenceBinding, mutate: Callable[[dict[str, object]], None]
) -> RuntimeEvidenceBinding:
    report = json.loads(binding.report_path.read_text(encoding="utf-8"))
    mutate(report)
    return replace(binding, report_sha256=_write_json(binding.report_path, report))


def _first_panel(report: dict[str, object]) -> dict[str, object]:
    return report["cases"][0]["panels"][0]  # type: ignore[index]


def test_loads_only_complete_bound_disjoint_inputs_as_immutable_arrays(tmp_path: Path):
    fixture = _make_fixture(tmp_path)
    loaded = load_bound_runtime_inputs(
        fixture.train, fixture.dev, fixture.implementation, repository_root=fixture.root
    )

    assert len(loaded.train) == len(loaded.dev) == 1
    panel = loaded.train[0]
    assert panel.split == "train"
    assert panel.gray8.tolist() == [[0, 64, 255], [20, 128, 220]]
    assert panel.artifact_mask.shape == panel.geometry_mask.shape == (2, 3)
    assert np.all(panel.artifact_mask >= panel.geometry_mask)
    assert all(
        not array.flags.writeable
        for array in (panel.gray8, panel.ocr_mask, panel.geometry_mask, panel.artifact_mask)
    )
    for array in (panel.gray8, panel.ocr_mask, panel.geometry_mask, panel.artifact_mask):
        with pytest.raises(ValueError, match="WRITEABLE"):
            array.setflags(write=True)


def test_allows_identical_crop_pixels_from_distinct_synthetic_scenes(tmp_path: Path):
    fixture = _make_fixture(tmp_path)
    loaded = load_bound_runtime_inputs(
        fixture.train, fixture.dev, fixture.implementation, repository_root=fixture.root
    )

    assert loaded.train[0].source_sha256 != loaded.dev[0].source_sha256
    assert loaded.train[0].panel_id != loaded.dev[0].panel_id
    assert loaded.train[0].panel_sha256 == loaded.dev[0].panel_sha256


def _make_empty_ocr_report(fixture: Fixture, corruption: str | None = None):
    def mutate(report):
        panel = _first_panel(report)
        panel["composed_mask_source_envelopes"] = [
            envelope for envelope in panel["composed_mask_source_envelopes"]
            if not envelope.get("model") or envelope["model"]["model_id"] != "recognizer"
        ]
        result = panel["ocr"]
        result.update(regions=[], masks=[], region_failures=[], warnings=["no_text_regions_detected"])
        result["cache"] = {"crop_count": 0, "batch_count": 0}
        directory = fixture.train.report_path.parent / panel["source_image_sha256"] / panel["panel_id"]
        path = directory / panel["ocr_mask"]["file"]
        path.write_bytes(np.zeros(6, dtype="<f4").tobytes())
        panel["ocr_mask"] = _envelope(path)
        if corruption == "recognizer_envelope":
            envelope = {**panel["composed_mask_source_envelopes"][1]}
            envelope["model"] = dict(_model_envelope(MODELS[2], panel["image_sha256"])["model"])
            panel["composed_mask_source_envelopes"].insert(2, envelope)
        elif corruption == "missing_recognizer_configuration":
            panel["ocr_configured_models"]["models"].pop()
        elif corruption == "failure":
            result["succeeded"] = False
            result["failure"] = {"code": "recognizer_failed"}
        elif corruption == "mask":
            path.write_bytes(np.ones(6, dtype="<f4").tobytes())
            panel["ocr_mask"] = _envelope(path)
        elif corruption == "missing_warning":
            result["warnings"] = []
        elif corruption == "region_failure":
            result["region_failures"] = [{"code": "crop_failed"}]
        elif corruption == "nonempty_region":
            result["regions"] = [{"text": "invented"}]
        elif corruption == "approved_scope":
            panel["ocr_configured_models"]["scope"] = "approved_production"
    return _rewrite_report(fixture.train, mutate)


def test_successful_empty_detector_uses_zero_mask_without_recognizer_execution(tmp_path: Path):
    fixture = _make_fixture(tmp_path)
    train = _make_empty_ocr_report(fixture)
    loaded = load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)
    assert not np.any(loaded.train[0].ocr_mask)
    assert len(loaded.train) == 1


@pytest.mark.parametrize("corruption", [
    "recognizer_envelope", "missing_recognizer_configuration", "failure", "mask",
    "missing_warning", "region_failure", "nonempty_region", "approved_scope",
])
def test_empty_ocr_never_hides_failure_or_invents_recognizer_evidence(tmp_path: Path, corruption: str):
    fixture = _make_fixture(tmp_path)
    train = _make_empty_ocr_report(fixture, corruption)
    with pytest.raises(RuntimeInputError):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


def test_rejects_tampered_plane_bytes_before_returning_any_arrays(tmp_path: Path):
    fixture = _make_fixture(tmp_path)
    report = json.loads(fixture.train.report_path.read_text(encoding="utf-8"))
    panel = _first_panel(report)
    path = fixture.train.report_path.parent / report["cases"][0]["image_sha256"] / panel["panel_id"] / panel["ocr_mask"]["file"]  # type: ignore[index]
    payload = bytearray(path.read_bytes())
    payload[0] ^= 0x01
    path.write_bytes(payload)

    with pytest.raises(RuntimeInputError, match="OCR mask bytes differ"):
        load_bound_runtime_inputs(
            fixture.train, fixture.dev, fixture.implementation, repository_root=fixture.root
        )


def test_rejects_hash_consistent_gray8_that_differs_from_runtime_crop(tmp_path: Path):
    fixture = _make_fixture(tmp_path)

    def mutate(report: dict[str, object]) -> None:
        panel = _first_panel(report)
        directory = fixture.train.report_path.parent / panel["source_image_sha256"] / panel["panel_id"]  # type: ignore[index]
        path = directory / panel["source_gray"]["file"]  # type: ignore[index]
        payload = bytearray(path.read_bytes())
        payload[0] = 255
        path.write_bytes(payload)
        panel["source_gray"] = _envelope(path)

    train = _rewrite_report(fixture.train, mutate)
    with pytest.raises(RuntimeInputError, match="Gray8 plane differs"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


@pytest.mark.parametrize("bad_value", [float("nan"), 1.25])
def test_rejects_non_probability_plane_content(tmp_path: Path, bad_value: float):
    fixture = _make_fixture(tmp_path)

    def mutate(report: dict[str, object]) -> None:
        panel = _first_panel(report)
        directory = fixture.train.report_path.parent / panel["source_image_sha256"] / panel["panel_id"]  # type: ignore[index]
        path = directory / panel["ocr_mask"]["file"]  # type: ignore[index]
        values = np.frombuffer(path.read_bytes(), dtype="<f4").copy()
        values[0] = bad_value
        path.write_bytes(values.tobytes())
        panel["ocr_mask"] = _envelope(path)

    train = _rewrite_report(fixture.train, mutate)
    with pytest.raises(RuntimeInputError, match=r"finite probabilities in \[0,1\]"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


def test_rejects_composed_mask_that_drops_geometry(tmp_path: Path):
    fixture = _make_fixture(tmp_path)

    def mutate(report: dict[str, object]) -> None:
        panel = _first_panel(report)
        directory = fixture.train.report_path.parent / panel["source_image_sha256"] / panel["panel_id"]  # type: ignore[index]
        path = directory / panel["composed_artifact_candidate_mask"]["file"]  # type: ignore[index]
        values = np.zeros(6, dtype="<f4")
        path.write_bytes(values.tobytes())
        panel["composed_artifact_candidate_mask"] = _envelope(path)

    train = _rewrite_report(fixture.train, mutate)
    with pytest.raises(RuntimeInputError, match="does not preserve all geometry"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


def test_rejects_semantically_complete_report_with_foreign_runtime_identity(tmp_path: Path):
    fixture = _make_fixture(tmp_path)

    def mutate(report: dict[str, object]) -> None:
        report["runtime_assemblies"][0]["sha256"] = _fake_hash("9")  # type: ignore[index]

    train = _rewrite_report(fixture.train, mutate)
    with pytest.raises(RuntimeInputError, match="runtime assembly identities differ"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


def test_rejects_hash_bound_report_with_foreign_model_or_algorithm_identity(tmp_path: Path):
    fixture = _make_fixture(tmp_path)

    def mutate_model(report: dict[str, object]) -> None:
        panel = _first_panel(report)
        panel["composed_mask_source_envelopes"][1]["model"]["sha256"] = _fake_hash("9")  # type: ignore[index]

    train = _rewrite_report(fixture.train, mutate_model)
    with pytest.raises(RuntimeInputError, match="model or artifact envelope identities differ"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)

    fixture = _make_fixture(tmp_path / "algorithm")

    def mutate_algorithm(report: dict[str, object]) -> None:
        report["artifact_candidate_configuration"]["parameters"] = "unreviewed"  # type: ignore[index]

    train = _rewrite_report(fixture.train, mutate_algorithm)
    with pytest.raises(RuntimeInputError, match="algorithm configuration differs"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


@pytest.mark.parametrize("field", ["run_id", "project_id", "panel_id"])
@pytest.mark.parametrize("residual", [False, True])
def test_rejects_foreign_source_envelope_context(tmp_path: Path, field: str, residual: bool):
    fixture = _make_fixture(tmp_path)

    def mutate(report):
        panel = _first_panel(report)
        envelope = (
            panel["residual_artifact_candidate_envelope"]
            if residual else panel["composed_mask_source_envelopes"][1]
        )
        envelope[field] = "00000000-0000-0000-0000-000000000099"

    train = _rewrite_report(fixture.train, mutate)
    with pytest.raises(RuntimeInputError, match="different|foreign"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


@pytest.mark.parametrize("field", ["project_id", "panel_id"])
def test_rejects_foreign_ocr_context(tmp_path: Path, field: str):
    fixture = _make_fixture(tmp_path)

    def mutate(report):
        _first_panel(report)["ocr"][field] = "00000000-0000-0000-0000-000000000099"

    train = _rewrite_report(fixture.train, mutate)
    with pytest.raises(RuntimeInputError, match="different|foreign"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


def test_rejects_missing_source_envelope_identity(tmp_path: Path):
    fixture = _make_fixture(tmp_path)

    def mutate(report):
        del _first_panel(report)["composed_mask_source_envelopes"][0]["run_id"]

    train = _rewrite_report(fixture.train, mutate)
    with pytest.raises(RuntimeInputError, match="nonempty UUID"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


def test_rejects_failed_panel_even_when_v2_counts_are_self_consistent(tmp_path: Path):
    fixture = _make_fixture(tmp_path)

    def mutate(report: dict[str, object]) -> None:
        panel = _first_panel(report)
        panel["status"] = "failed"
        report["cases"][0]["status"] = "failed"  # type: ignore[index]
        report["completed"] = 0
        report["failed"] = 1
        report["completed_panels"] = 0
        report["failed_panels"] = 1

    train = _rewrite_report(fixture.train, mutate)
    with pytest.raises(RuntimeInputError, match="every bound source and panel"):
        load_bound_runtime_inputs(train, fixture.dev, fixture.implementation, repository_root=fixture.root)


def test_rejects_cross_split_source_reuse(tmp_path: Path):
    fixture = _make_fixture(tmp_path, overlap=True)
    with pytest.raises(RuntimeInputError, match="source identities overlap"):
        load_bound_runtime_inputs(
            fixture.train, fixture.dev, fixture.implementation, repository_root=fixture.root
        )


def test_rejects_hash_bound_source_relabelled_as_synthetic(tmp_path: Path):
    fixture = _make_fixture(tmp_path, relabeled_source=True)
    with pytest.raises(RuntimeInputError, match="regenerated PNG identity differs"):
        load_bound_runtime_inputs(
            fixture.train, fixture.dev, fixture.implementation, repository_root=fixture.root
        )


def test_rejects_changed_synthetic_generator_source(tmp_path: Path):
    fixture = _make_fixture(tmp_path)
    source_path = fixture.root / SYNTHETIC_SOURCE_RELATIVE_PATHS[-1]
    source_path.write_bytes(source_path.read_bytes() + b"\n# changed after review\n")

    with pytest.raises(RuntimeInputError, match="synthetic source differs"):
        load_bound_runtime_inputs(
            fixture.train, fixture.dev, fixture.implementation, repository_root=fixture.root
        )


def test_rejects_unbound_report_path_and_report_bytes(tmp_path: Path):
    fixture = _make_fixture(tmp_path)
    outside = tmp_path / "outside-report.json"
    outside.write_bytes(fixture.train.report_path.read_bytes())
    unbound = replace(fixture.train, report_path=outside)
    with pytest.raises(RuntimeInputError, match="under repository artifacts"):
        load_bound_runtime_inputs(unbound, fixture.dev, fixture.implementation, repository_root=fixture.root)

    fixture.train.report_path.write_text("{}", encoding="utf-8")
    with pytest.raises(RuntimeInputError, match="bytes differ from the frozen SHA-256"):
        load_bound_runtime_inputs(
            fixture.train, fixture.dev, fixture.implementation, repository_root=fixture.root
        )


def test_rejects_candidate_or_validator_not_matching_frozen_bytes(tmp_path: Path):
    fixture = _make_fixture(tmp_path)
    bad_validator = replace(fixture.implementation, validator_source_sha256=_fake_hash("9"))
    with pytest.raises(RuntimeInputError, match="validator source differs"):
        load_bound_runtime_inputs(
            fixture.train, fixture.dev, bad_validator, repository_root=fixture.root
        )

    fixture.implementation.candidate_path.write_text("{}", encoding="utf-8")
    with pytest.raises(RuntimeInputError, match="candidate descriptor bytes differ"):
        load_bound_runtime_inputs(
            fixture.train, fixture.dev, fixture.implementation, repository_root=fixture.root
        )
