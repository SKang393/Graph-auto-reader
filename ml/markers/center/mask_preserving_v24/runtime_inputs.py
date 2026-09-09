# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fail-closed loading of annotation-free production-runtime raster planes.

The loader validates evidence identity and content only.  It does not approve a
model, establish artifact-mask accuracy, join annotations, or select samples.
Callers freeze reviewed hashes in :class:`RuntimeImplementationIdentity` and
:class:`RuntimeEvidenceBinding`; no identity is learned from the evidence being
validated.
"""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from types import ModuleType
from typing import Any, Mapping
from uuid import UUID

import numpy as np
from PIL import Image


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
VALIDATOR_RELATIVE_PATH = Path(
    "tools/GraphReader.SyntheticRuntimeEvidence/score_family_ocr.py"
)
SYNTHETIC_SOURCE_RELATIVE_PATHS = (
    Path("ml/markers/center/mask_preserving_v24/export_family_rasters.py"),
    Path("ml/markers/center/mask_preserving_v24/family_scenes.py"),
    Path("ml/synthetic/contact_sheet.py"),
    Path("ml/synthetic/dataset.py"),
    Path("ml/synthetic/fonts.py"),
    Path("ml/synthetic/io.py"),
    Path("ml/synthetic/renderer.py"),
    Path("ml/synthetic/scene.schema.json"),
    Path("ml/synthetic/schema.py"),
    Path("ml/synthetic/templates.py"),
)
REPORT_SCHEMA = "graphreader.synthetic-runtime-seed-evidence.v2"
MISSING_STAGE = "representative-artifact-validation-and-training-input-binding"


class RuntimeInputError(ValueError):
    """Runtime evidence failed an identity, content, or split boundary."""


@dataclass(frozen=True, order=True)
class RuntimeAssemblyIdentity:
    name: str
    sha256: str


@dataclass(frozen=True, order=True)
class RuntimeSourceIdentity:
    relative_path: Path
    sha256: str


@dataclass(frozen=True, order=True)
class RuntimeModelIdentity:
    stage: str
    stage_version: str
    model_id: str
    version: str
    sha256: str
    provider: str


@dataclass(frozen=True)
class RuntimeAlgorithmIdentity:
    algorithm_id: str
    version: str
    assembly_sha256: str
    configuration_sha256: str
    stage_version: str
    parameters: str
    dependencies: tuple[RuntimeAssemblyIdentity, ...]


@dataclass(frozen=True)
class RuntimeImplementationIdentity:
    """Reviewed identities shared by the train and development reports."""

    validator_source_sha256: str
    synthetic_sources: tuple[RuntimeSourceIdentity, ...]
    candidate_path: Path
    candidate_sha256: str
    native_sha256: str
    native_scope: str
    runtime_assemblies: tuple[RuntimeAssemblyIdentity, ...]
    algorithm: RuntimeAlgorithmIdentity
    models: tuple[RuntimeModelIdentity, ...]


@dataclass(frozen=True)
class RuntimeEvidenceBinding:
    """Exact immutable files authorized as one annotation-free split."""

    split: str
    manifest_path: Path
    manifest_sha256: str
    report_path: Path
    report_sha256: str


@dataclass(frozen=True)
class ValidatedRuntimePanelInput:
    split: str
    dataset_seed: int
    family: str
    scene_seed: int
    source_sha256: str
    panel_id: str
    panel_sha256: str
    width: int
    height: int
    crop: tuple[int, int, int, int]
    requested_crop: tuple[float, float, float, float]
    gray8: np.ndarray
    ocr_mask: np.ndarray
    geometry_mask: np.ndarray
    artifact_mask: np.ndarray


@dataclass(frozen=True)
class ValidatedRuntimeInputs:
    """Validated raw planes.  Arrays are read-only and contain no truth."""

    train: tuple[ValidatedRuntimePanelInput, ...]
    dev: tuple[ValidatedRuntimePanelInput, ...]
    implementation: RuntimeImplementationIdentity


def load_bound_runtime_inputs(
    train: RuntimeEvidenceBinding,
    dev: RuntimeEvidenceBinding,
    implementation: RuntimeImplementationIdentity,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> ValidatedRuntimeInputs:
    """Validate and load disjoint train/dev production-runtime panel planes.

    All expected hashes and identities must come from a separately reviewed
    immutable configuration.  The returned masks remain diagnostic inputs;
    this function does not make them production-approved or accurate.
    """

    root = repository_root.resolve()
    if train.split != "train" or dev.split != "validation":
        raise RuntimeInputError("runtime inputs require explicit train and validation bindings")
    _validate_implementation_shape(implementation)
    validator = _load_bound_validator(implementation.validator_source_sha256)
    _validate_synthetic_source_implementation(implementation.synthetic_sources, root)
    _validate_candidate(implementation, root)

    train_panels, train_families, train_sources, train_seeds = _load_split(
        train, implementation, validator, root
    )
    dev_panels, dev_families, dev_sources, dev_seeds = _load_split(
        dev, implementation, validator, root
    )
    if train_sources & dev_sources:
        raise RuntimeInputError("train and validation source identities overlap")
    if {panel.panel_id for panel in train_panels} & {panel.panel_id for panel in dev_panels}:
        raise RuntimeInputError("train and validation panel identities overlap")
    if train_seeds & dev_seeds:
        raise RuntimeInputError("train and validation scene identities overlap")
    if train_families & dev_families:
        raise RuntimeInputError("train and validation family identities overlap")
    return ValidatedRuntimeInputs(train_panels, dev_panels, implementation)


def _load_split(
    binding: RuntimeEvidenceBinding,
    implementation: RuntimeImplementationIdentity,
    validator: ModuleType,
    repository_root: Path,
) -> tuple[
    tuple[ValidatedRuntimePanelInput, ...], set[str], set[str], set[int]
]:
    manifest_path, manifest_payload = _read_bound_artifact(
        binding.manifest_path,
        binding.manifest_sha256,
        repository_root,
        f"{binding.split} manifest",
    )
    report_path, report_payload = _read_bound_artifact(
        binding.report_path,
        binding.report_sha256,
        repository_root,
        f"{binding.split} report",
    )
    manifest = _json_object(manifest_payload, f"{binding.split} manifest")
    report = _json_object(report_payload, f"{binding.split} report")

    try:
        validator.REPOSITORY_ROOT = repository_root
        split, dataset_seed, images = validator._validate_manifest(manifest, manifest_path)
        if split != binding.split:
            raise RuntimeInputError(
                f"bound split {binding.split!r} differs from manifest split {split!r}"
            )
        if report.get("schema") != REPORT_SCHEMA:
            raise RuntimeInputError("runtime input loader accepts only the panel-aware v2 report")
        case_map = validator._validate_report(
            report,
            _sha256_bytes(manifest_payload),
            images,
            report_path=report_path,
            manifest_path=manifest_path,
        )
        # Regeneration proves that a source labeled synthetic is the exact
        # output of the pinned project-owned generator. The returned renderer
        # annotations are deliberately discarded and never become mask inputs.
        validator._regenerate(split, dataset_seed, images)
    except RuntimeInputError:
        raise
    except Exception as exception:
        if isinstance(exception, getattr(validator, "EvidenceError")):
            raise RuntimeInputError(str(exception)) from exception
        raise

    _validate_complete_diagnostic(report, len(images))
    _validate_report_implementation(report, implementation)
    image_by_hash = {str(image["image_sha256"]): image for image in images}
    panels: list[ValidatedRuntimePanelInput] = []
    for image in images:
        source_sha = str(image["image_sha256"])
        case = case_map[source_sha]
        for panel in case["panels"]:
            panels.append(
                _load_panel(
                    binding.split,
                    dataset_seed,
                    image,
                    panel,
                    report_path,
                    implementation,
                    validator,
                    repository_root,
                )
            )
    if not panels or len(image_by_hash) != len(case_map):
        raise RuntimeInputError("runtime split contains no complete panel inputs")
    return (
        tuple(panels),
        {str(image["family"]) for image in images},
        set(image_by_hash),
        {int(image["seed"]) for image in images},
    )


def _validate_complete_diagnostic(report: Mapping[str, Any], source_count: int) -> None:
    if (
        report.get("production_approved") is not False
        or report.get("training_input_ready") is not False
        or report.get("complete_artifact_mask") is not False
        or report.get("missing_stage") != MISSING_STAGE
    ):
        raise RuntimeInputError(
            "runtime planes must retain their unapproved, incomplete diagnostic scope"
        )
    if (
        report.get("count") != source_count
        or report.get("completed") != source_count
        or report.get("failed") != 0
        or not isinstance(report.get("panel_count"), int)
        or report.get("panel_count", 0) <= 0
        or report.get("completed_panels") != report.get("panel_count")
        or report.get("failed_panels") != 0
    ):
        raise RuntimeInputError("every bound source and panel must be seed-completed")
    for case in report.get("cases", ()):  # Structure was validated by the shared validator.
        if case.get("status") != "panels-completed" or not case.get("panels"):
            raise RuntimeInputError("every bound source must contain completed panels")
        if any(panel.get("status") != "seed-completed" for panel in case["panels"]):
            raise RuntimeInputError("every bound panel must be seed-completed")


def _validate_report_implementation(
    report: Mapping[str, Any], expected: RuntimeImplementationIdentity
) -> None:
    if _sha(report.get("candidate_sha256"), "report candidate") != expected.candidate_sha256:
        raise RuntimeInputError("runtime report candidate identity differs from the frozen identity")
    if (
        _sha(report.get("native_sha256"), "report native runtime") != expected.native_sha256
        or report.get("native_scope") != expected.native_scope
    ):
        raise RuntimeInputError("runtime report native identity differs from the frozen identity")

    assemblies = _assembly_records(report.get("runtime_assemblies"), "runtime assemblies")
    if assemblies != expected.runtime_assemblies:
        raise RuntimeInputError("runtime assembly identities differ from the frozen identity")

    identity = report.get("artifact_candidate_identity")
    configuration = report.get("artifact_candidate_configuration")
    if not isinstance(identity, dict) or not isinstance(configuration, dict):
        raise RuntimeInputError("runtime report is missing artifact algorithm identity")
    algorithm = expected.algorithm
    expected_identity = {
        "algorithm_id": algorithm.algorithm_id,
        "version": algorithm.version,
        "assembly_sha256": algorithm.assembly_sha256,
        "configuration_sha256": algorithm.configuration_sha256,
        "stage_version": algorithm.stage_version,
        "assembly_warning": f"artifact_algorithm_assembly_sha256:{algorithm.assembly_sha256}",
        "configuration_warning": (
            f"artifact_algorithm_configuration_sha256:{algorithm.configuration_sha256}"
        ),
    }
    if identity != expected_identity:
        raise RuntimeInputError("artifact algorithm identity differs from the frozen identity")
    expected_configuration = {
        "algorithm": algorithm.algorithm_id,
        "version": algorithm.version,
        "parameters": algorithm.parameters,
        "dependencies": [
            {"assembly": dependency.name, "sha256": dependency.sha256}
            for dependency in algorithm.dependencies
        ],
    }
    if configuration != expected_configuration:
        raise RuntimeInputError("artifact algorithm configuration differs from the frozen identity")


def _load_panel(
    split: str,
    dataset_seed: int,
    image: Mapping[str, Any],
    panel: Mapping[str, Any],
    report_path: Path,
    implementation: RuntimeImplementationIdentity,
    validator: ModuleType,
    repository_root: Path,
) -> ValidatedRuntimePanelInput:
    width = int(panel["width"])
    height = int(panel["height"])
    panel_id = str(panel["panel_id"])
    panel_sha = _sha(panel.get("image_sha256"), "panel image")
    panel_directory = report_path.parent / str(image["image_sha256"]) / panel_id
    empty_ocr = _validate_panel_envelopes(panel, panel_sha, implementation)

    gray8 = _load_plane(
        panel.get("source_gray"), panel_directory, width, height, np.dtype("u1"),
        "source Gray8", validator, repository_root,
    )
    ocr_mask = _load_plane(
        panel.get("ocr_mask"), panel_directory, width, height, np.dtype("<f4"),
        "OCR mask", validator, repository_root,
    )
    if empty_ocr and np.any(ocr_mask):
        raise RuntimeInputError("zero-region OCR must produce an all-zero OCR mask")
    geometry_mask = _load_plane(
        panel.get("geometry_mask"), panel_directory, width, height, np.dtype("<f4"),
        "geometry mask", validator, repository_root,
    )
    artifact_mask = _load_plane(
        panel.get("composed_artifact_candidate_mask"), panel_directory, width, height,
        np.dtype("<f4"), "composed artifact mask", validator, repository_root,
    )
    if not np.all(artifact_mask >= geometry_mask):
        raise RuntimeInputError("composed artifact mask does not preserve all geometry evidence")

    panel_png = _owned_file(
        validator, panel_directory, panel["panel_png"]["file"],
        "runtime panel PNG", repository_root,
    )
    panel_png_payload = panel_png.read_bytes()
    panel_png_record = panel["panel_png"]
    if (
        len(panel_png_payload) != panel_png_record["byte_count"]
        or _sha256_bytes(panel_png_payload) != _sha(panel_png_record["sha256"], "panel PNG")
        or _sha256_bytes(panel_png_payload) != panel_sha
    ):
        raise RuntimeInputError("runtime panel PNG changed after source-crop validation")
    expected_gray8 = _decode_runtime_gray8(panel_png_payload, width, height)
    if not np.array_equal(gray8, expected_gray8):
        raise RuntimeInputError("source Gray8 plane differs from the exact runtime panel crop")

    for array in (gray8, ocr_mask, geometry_mask, artifact_mask):
        array.setflags(write=False)
    crop = panel["crop"]
    requested = panel["requested_crop"]
    return ValidatedRuntimePanelInput(
        split=split,
        dataset_seed=dataset_seed,
        family=str(image["family"]),
        scene_seed=int(image["seed"]),
        source_sha256=str(image["image_sha256"]),
        panel_id=panel_id,
        panel_sha256=panel_sha,
        width=width,
        height=height,
        crop=(int(crop["x"]), int(crop["y"]), int(crop["width"]), int(crop["height"])),
        requested_crop=(
            float(requested["x"]), float(requested["y"]),
            float(requested["width"]), float(requested["height"]),
        ),
        gray8=gray8,
        ocr_mask=ocr_mask,
        geometry_mask=geometry_mask,
        artifact_mask=artifact_mask,
    )


def _validate_panel_envelopes(
    panel: Mapping[str, Any], panel_sha: str, expected: RuntimeImplementationIdentity
) -> bool:
    empty_ocr = _validate_ocr_configuration_and_result(panel, panel_sha, expected)
    raw = panel.get("composed_mask_source_envelopes")
    if not isinstance(raw, list):
        raise RuntimeInputError("panel is missing composed-mask source envelopes")
    observed_models: list[RuntimeModelIdentity] = []
    artifact_envelopes = 0
    run_context: tuple[str, str] | None = None
    for index, envelope in enumerate(raw):
        if not isinstance(envelope, dict):
            raise RuntimeInputError(f"composed-mask source envelope {index} must be an object")
        context = _envelope_context(envelope, str(panel.get("panel_id")))
        if run_context is not None and context != run_context:
            raise RuntimeInputError("composed-mask source envelopes belong to different runs or projects")
        run_context = context
        if (
            envelope.get("contract_version") != 1
            or envelope.get("coordinate_space") != "original_pixels"
            or _sha(envelope.get("input_sha256"), "source envelope input") != panel_sha
        ):
            raise RuntimeInputError("composed-mask source envelope has a foreign panel identity")
        model = envelope.get("model")
        if model is None:
            if (
                envelope.get("stage") != "markers"
                or envelope.get("stage_version") != expected.algorithm.stage_version
            ):
                raise RuntimeInputError("model-free source envelope is not the frozen artifact algorithm")
            artifact_envelopes += 1
            continue
        if not isinstance(model, dict) or set(model) != {
            "model_id", "version", "sha256", "provider"
        }:
            raise RuntimeInputError("runtime model envelope has an invalid identity shape")
        observed_models.append(
            RuntimeModelIdentity(
                stage=str(envelope.get("stage")),
                stage_version=str(envelope.get("stage_version")),
                model_id=str(model["model_id"]),
                version=str(model["version"]),
                sha256=_sha(model["sha256"], "runtime model"),
                provider=str(model["provider"]),
            )
        )
    executed_models = expected.models[:2] if empty_ocr else expected.models
    if artifact_envelopes != 1 or tuple(observed_models) != executed_models:
        raise RuntimeInputError("panel model or artifact envelope identities differ from the frozen identity")
    # OcrRequest has no parent run ID. OcrPipeline allocates its own subrun UUID;
    # the production adapter wraps that result in the workflow's run envelopes.
    # Validate that UUID while binding the result to the same project and panel.
    ocr_context = _envelope_context(panel["ocr"], str(panel.get("panel_id")))
    if run_context is None or ocr_context[1] != run_context[1]:
        raise RuntimeInputError("OCR result belongs to a different project")

    residual = panel.get("residual_artifact_candidate_envelope")
    if not isinstance(residual, dict) or (
        residual.get("contract_version") != 1
        or residual.get("stage") != "markers"
        or residual.get("stage_version") != expected.algorithm.stage_version
        or residual.get("coordinate_space") != "original_pixels"
        or residual.get("model") is not None
        or str(residual.get("panel_id")) != str(panel.get("panel_id"))
        or _sha(residual.get("input_sha256"), "artifact envelope input") != panel_sha
    ):
        raise RuntimeInputError("residual artifact envelope has a foreign algorithm or panel identity")
    if _envelope_context(residual, str(panel.get("panel_id"))) != run_context:
        raise RuntimeInputError("residual artifact envelope belongs to a different run or project")
    return empty_ocr


def _validate_ocr_configuration_and_result(
    panel: Mapping[str, Any], panel_sha: str, expected: RuntimeImplementationIdentity
) -> bool:
    configured = panel.get("ocr_configured_models")
    expected_models = [
        {
            "task": task,
            "model_id": model.model_id,
            "version": model.version,
            "sha256": model.sha256,
            "execution_provider": model.provider,
        }
        for task, model in zip(("ocr_detection", "ocr_recognition"), expected.models[1:], strict=True)
    ]
    if not isinstance(configured, dict) or configured != {
        "scope": "unapproved_local_synthetic_candidate",
        "models": expected_models,
    }:
        raise RuntimeInputError("both configured OCR models must match the frozen local candidate identities")
    result = panel.get("ocr")
    if not isinstance(result, dict) or (
        result.get("succeeded") is not True
        or result.get("failure") is not None
        or result.get("contract_version") != 1
        or result.get("stage") != "ocr"
        or result.get("stage_version") != expected.models[1].stage_version
        or result.get("coordinate_space") != "original_pixels"
        or _sha(result.get("input_sha256"), "OCR result input") != panel_sha
    ):
        raise RuntimeInputError("OCR result must be successful and bound to the current original panel")
    cache = result.get("cache")
    if not isinstance(cache, dict) or any(
        type(cache.get(key)) is not int or cache[key] < 0 for key in ("crop_count", "batch_count")
    ):
        raise RuntimeInputError("OCR result must have valid crop and batch counts")
    if any(not isinstance(result.get(key), list) for key in (
        "regions", "masks", "region_failures", "warnings"
    )):
        raise RuntimeInputError("OCR result must retain its output, failure, and warning arrays")
    empty = cache["crop_count"] == 0
    if empty and (
        cache["batch_count"] != 0
        or result["regions"] or result["masks"] or result["region_failures"]
        or "no_text_regions_detected" not in result["warnings"]
    ):
        raise RuntimeInputError("zero-region OCR must be an explicit successful empty detector result")
    return empty


def _envelope_context(envelope: Mapping[str, Any], panel_id: str) -> tuple[str, str]:
    values = []
    for field in ("run_id", "project_id", "panel_id"):
        try:
            value = UUID(str(envelope.get(field)))
        except ValueError as exception:
            raise RuntimeInputError(f"source envelope {field} must be a nonempty UUID") from exception
        if value.int == 0:
            raise RuntimeInputError(f"source envelope {field} must be a nonempty UUID")
        values.append(str(value))
    if values[2] != panel_id:
        raise RuntimeInputError("source envelope belongs to a different panel")
    return values[0], values[1]


def _load_plane(
    record: Any,
    directory: Path,
    width: int,
    height: int,
    dtype: np.dtype[Any],
    label: str,
    validator: ModuleType,
    repository_root: Path,
) -> np.ndarray:
    if not isinstance(record, dict) or set(record) != {"file", "sha256", "byte_count"}:
        raise RuntimeInputError(f"{label} envelope must contain only file, sha256, and byte_count")
    path = _owned_file(validator, directory, record.get("file"), label, repository_root)
    if not path.is_file():
        raise RuntimeInputError(f"{label} file is missing")
    payload = path.read_bytes()
    expected_bytes = width * height * dtype.itemsize
    if (
        record.get("byte_count") != expected_bytes
        or len(payload) != expected_bytes
        or _sha256_bytes(payload) != _sha(record.get("sha256"), label)
    ):
        raise RuntimeInputError(f"{label} bytes differ from the exact bound envelope")
    # Retain the immutable bytes object as the array's backing store. An owning
    # NumPy copy could have WRITEABLE re-enabled after validation.
    array = np.frombuffer(payload, dtype=dtype).reshape((height, width))
    if dtype.kind == "f" and (not np.isfinite(array).all() or (array < 0).any() or (array > 1).any()):
        raise RuntimeInputError(f"{label} values must be finite probabilities in [0,1]")
    return array


def _decode_runtime_gray8(payload: bytes, width: int, height: int) -> np.ndarray:
    try:
        with Image.open(BytesIO(payload)) as image:
            if image.format != "PNG" or image.size != (width, height):
                raise RuntimeInputError("runtime panel PNG format or dimensions changed")
            rgba = np.asarray(image.convert("RGBA"), dtype=np.uint16)
    except (OSError, ValueError) as exception:
        raise RuntimeInputError(f"runtime panel PNG cannot be decoded: {exception}") from exception
    alpha = rgba[:, :, 3]
    rgb = (
        (rgba[:, :, :3] * alpha[:, :, None])
        + (255 * (255 - alpha[:, :, None]))
        + 127
    ) // 255
    rgb = rgb.astype(np.uint32, copy=False)
    gray = (
        (299 * rgb[:, :, 0]) + (587 * rgb[:, :, 1]) + (114 * rgb[:, :, 2]) + 500
    ) // 1000
    return np.asarray(gray, dtype=np.uint8)


def _validate_candidate(expected: RuntimeImplementationIdentity, repository_root: Path) -> None:
    _, payload = _read_bound_artifact(
        expected.candidate_path,
        expected.candidate_sha256,
        repository_root,
        "runtime candidate descriptor",
    )
    candidate = _json_object(payload, "runtime candidate descriptor")
    if (
        candidate.get("schema") != "graphreader.local-synthetic-ocr-candidate.v1"
        or candidate.get("production_approved") is not False
        or _sha(candidate.get("native_sha256"), "candidate native runtime") != expected.native_sha256
        or candidate.get("native_scope") != expected.native_scope
    ):
        raise RuntimeInputError("candidate descriptor differs from the frozen unapproved runtime identity")
    candidate_models = []
    for field in ("detector", "recognizer"):
        model = candidate.get(field)
        if not isinstance(model, dict):
            raise RuntimeInputError(f"candidate descriptor is missing {field} identity")
        candidate_models.append(
            (str(model.get("model_id")), str(model.get("model_version")),
             _sha(model.get("model_sha256"), f"candidate {field} model"))
        )
    expected_ocr_models = [
        (model.model_id, model.version, model.sha256)
        for model in expected.models if model.stage == "ocr"
    ]
    if candidate_models != expected_ocr_models:
        raise RuntimeInputError("candidate OCR model identities differ from the frozen model identities")


def _validate_implementation_shape(expected: RuntimeImplementationIdentity) -> None:
    hashes = [
        expected.validator_source_sha256,
        *(entry.sha256 for entry in expected.synthetic_sources),
        expected.candidate_sha256,
        expected.native_sha256,
        expected.algorithm.assembly_sha256,
        expected.algorithm.configuration_sha256,
        *(entry.sha256 for entry in expected.runtime_assemblies),
        *(entry.sha256 for entry in expected.algorithm.dependencies),
        *(entry.sha256 for entry in expected.models),
    ]
    for value in hashes:
        _sha(value, "frozen implementation")
    if not expected.runtime_assemblies or not expected.models:
        raise RuntimeInputError("frozen implementation must name its runtime assemblies and models")
    if len({entry.name for entry in expected.runtime_assemblies}) != len(
        expected.runtime_assemblies
    ):
        raise RuntimeInputError("frozen runtime assembly identities must be unique")
    if len(set(expected.models)) != len(expected.models):
        raise RuntimeInputError("frozen model identities must be unique")
    if tuple(model.stage for model in expected.models) != ("axis", "ocr", "ocr"):
        raise RuntimeInputError("frozen model order must be the axis, OCR detector, and OCR recognizer")


def _validate_synthetic_source_implementation(
    expected: tuple[RuntimeSourceIdentity, ...], repository_root: Path
) -> None:
    expected_paths = tuple(entry.relative_path for entry in expected)
    if expected_paths != SYNTHETIC_SOURCE_RELATIVE_PATHS:
        raise RuntimeInputError(
            "frozen synthetic source identities must name the exact required source list"
        )
    for entry in expected:
        path = entry.relative_path
        if path.is_absolute() or ".." in path.parts or path.as_posix() != str(path).replace("\\", "/"):
            raise RuntimeInputError("synthetic source identity must use a canonical relative path")
        source_path = (repository_root / path).resolve()
        if repository_root not in source_path.parents or not source_path.is_file():
            raise RuntimeInputError(f"synthetic source is missing: {path.as_posix()}")
        if _sha256_bytes(source_path.read_bytes()) != entry.sha256:
            raise RuntimeInputError(
                f"synthetic source differs from the frozen identity: {path.as_posix()}"
            )


def _assembly_records(value: Any, label: str) -> tuple[RuntimeAssemblyIdentity, ...]:
    if not isinstance(value, list):
        raise RuntimeInputError(f"{label} must be an array")
    records: list[RuntimeAssemblyIdentity] = []
    for entry in value:
        if not isinstance(entry, dict) or set(entry) != {"name", "sha256"}:
            raise RuntimeInputError(f"{label} entry has an invalid shape")
        records.append(RuntimeAssemblyIdentity(str(entry["name"]), _sha(entry["sha256"], label)))
    return tuple(records)


def _owned_file(
    validator: ModuleType,
    directory: Path,
    name: Any,
    label: str,
    repository_root: Path,
) -> Path:
    try:
        return validator._require_owned_file(
            directory, name, label, repository_root=repository_root
        )
    except Exception as exception:
        if isinstance(exception, getattr(validator, "EvidenceError")):
            raise RuntimeInputError(str(exception)) from exception
        raise


def _load_bound_validator(expected_sha256: str) -> ModuleType:
    path = REPOSITORY_ROOT / VALIDATOR_RELATIVE_PATH
    payload = path.read_bytes()
    if _sha256_bytes(payload) != _sha(expected_sha256, "validator source"):
        raise RuntimeInputError("shared v2 evidence validator source differs from the frozen identity")
    module = ModuleType("_graphreader_bound_runtime_evidence_validator")
    module.__file__ = str(path)
    exec(compile(payload, str(path), "exec"), module.__dict__)
    return module


def _read_bound_artifact(
    path: Path, expected_sha256: str, repository_root: Path, label: str
) -> tuple[Path, bytes]:
    resolved = path.resolve()
    artifact_root = (repository_root / "artifacts").resolve()
    if resolved == artifact_root or artifact_root not in resolved.parents or not resolved.is_file():
        raise RuntimeInputError(f"{label} must be an existing file under repository artifacts")
    payload = resolved.read_bytes()
    if _sha256_bytes(payload) != _sha(expected_sha256, label):
        raise RuntimeInputError(f"{label} bytes differ from the frozen SHA-256")
    return resolved, payload


def _json_object(payload: bytes, label: str) -> dict[str, Any]:
    try:
        value = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise RuntimeInputError(f"{label} is not valid JSON: {exception}") from exception
    if not isinstance(value, dict):
        raise RuntimeInputError(f"{label} must be a JSON object")
    return value


def _sha(value: Any, label: str) -> str:
    normalized = str(value).casefold()
    if len(normalized) != 64 or any(character not in "0123456789abcdef" for character in normalized):
        raise RuntimeInputError(f"{label} must be a SHA-256 value")
    return normalized


def _sha256_bytes(payload: bytes) -> str:
    return sha256(payload).hexdigest()
