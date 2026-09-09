# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Read a frozen training-input binding without granting model approval."""

from dataclasses import asdict, fields
from hashlib import sha256
import json
from pathlib import Path
from typing import Any

from ml.markers.gate_seal import canonical_json_bytes
from . import runtime_inputs, runtime_family_scenes
from .runtime_inputs import (
    RuntimeAlgorithmIdentity, RuntimeAssemblyIdentity, RuntimeEvidenceBinding,
    RuntimeImplementationIdentity, RuntimeInputError, RuntimeModelIdentity,
    RuntimeSourceIdentity,
)
from .runtime_family_scenes import RuntimeFamilySceneJoin


BINDING_SCHEMA = "graphreader.marker-runtime-training-input-binding.v1"
REPOSITORY_ROOT = Path(__file__).resolve().parents[4]


def tensor_set_sha256(scenes) -> str:
    hashes = sorted(
        sha256(scene.tensor.numpy().tobytes(order="C")).hexdigest() for scene in scenes
    )
    return sha256(canonical_json_bytes(hashes)).hexdigest()


def mapping_audit_sha256(joined: RuntimeFamilySceneJoin) -> str:
    return sha256(canonical_json_bytes([asdict(item) for item in joined.audit])).hexdigest()


def _object(value: Any, names: set[str], label: str) -> dict:
    if not isinstance(value, dict) or set(value) != names:
        raise RuntimeInputError(f"{label} has an invalid binding shape")
    return value


def _record(record_type, value: Any, label: str):
    return record_type(**_object(value, {field.name for field in fields(record_type)}, label))


def _records(record_type, value: Any, label: str) -> tuple:
    if not isinstance(value, list):
        raise RuntimeInputError(f"{label} must be an array")
    return tuple(_record(record_type, item, label) for item in value)


def _relative_path(value: Any, root: Path, label: str) -> Path:
    if not isinstance(value, str) or not value or "\\" in value:
        raise RuntimeInputError(f"{label} must be a canonical repository-relative path")
    path = Path(value)
    if path.is_absolute() or path.drive or ".." in path.parts or path.as_posix() != value:
        raise RuntimeInputError(f"{label} must be a canonical repository-relative path")
    result = (root / path).resolve()
    if root not in result.parents:
        raise RuntimeInputError(f"{label} escapes the repository")
    return result


def _split(value: Any, root: Path) -> RuntimeEvidenceBinding:
    record = dict(_object(value, {field.name for field in fields(RuntimeEvidenceBinding)}, "split"))
    for name in ("manifest_path", "report_path"):
        record[name] = _relative_path(record[name], root, name)
    return RuntimeEvidenceBinding(**record)


def _implementation(value: Any, root: Path) -> RuntimeImplementationIdentity:
    record = dict(_object(
        value, {field.name for field in fields(RuntimeImplementationIdentity)}, "implementation"
    ))
    record["candidate_path"] = _relative_path(record["candidate_path"], root, "candidate_path")
    sources = _records(RuntimeSourceIdentity, record["synthetic_sources"], "synthetic source")
    record["synthetic_sources"] = tuple(
        RuntimeSourceIdentity(
            _relative_path(item.relative_path, root, "synthetic source").relative_to(root),
            item.sha256,
        )
        for item in sources
    )
    record["runtime_assemblies"] = _records(
        RuntimeAssemblyIdentity, record["runtime_assemblies"], "runtime assembly"
    )
    record["models"] = _records(RuntimeModelIdentity, record["models"], "runtime model")
    algorithm = dict(_object(
        record["algorithm"], {field.name for field in fields(RuntimeAlgorithmIdentity)}, "algorithm"
    ))
    algorithm["dependencies"] = _records(
        RuntimeAssemblyIdentity, algorithm["dependencies"], "algorithm dependency"
    )
    record["algorithm"] = RuntimeAlgorithmIdentity(**algorithm)
    return RuntimeImplementationIdentity(**record)


def load_runtime_family_binding(
    path: Path,
    expected_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> RuntimeFamilySceneJoin:
    """Validate frozen sources, planes, labels, tensors, and mapping before training.

    This binding permits diagnostic runtime channels as learning inputs. It
    does not certify mask accuracy, consume sealed data, or approve production.
    """
    root = repository_root.resolve()
    _, payload = runtime_inputs._read_bound_artifact(path, expected_sha256, root, "training input binding")
    try:
        document = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise RuntimeInputError("training input binding is not valid JSON") from exception
    _object(document, {
        "schema", "synthetic_only", "private_data", "sealed_runs",
        "training_input_ready", "production_approved", "complete_artifact_mask",
        "train", "dev", "implementation", "loader_source_sha256", "join_source_sha256",
        "train_tensor_set_sha256", "dev_tensor_set_sha256", "mapping_audit_sha256",
    }, "training input")
    if (
        document["schema"] != BINDING_SCHEMA
        or document["synthetic_only"] is not True
        or document["private_data"] is not False
        or type(document["sealed_runs"]) is not int or document["sealed_runs"] != 0
        or document["training_input_ready"] is not True
        or document["production_approved"] is not False
        or document["complete_artifact_mask"] is not False
    ):
        raise RuntimeInputError("training input binding has a foreign or approved evidence scope")
    for module, key in (
        (runtime_inputs, "loader_source_sha256"),
        (runtime_family_scenes, "join_source_sha256"),
    ):
        actual = sha256(Path(module.__file__).read_bytes()).hexdigest()
        if actual != runtime_inputs._sha(document[key], key):
            raise RuntimeInputError(f"frozen {key} differs from the executing implementation")
    inputs = runtime_inputs.load_bound_runtime_inputs(
        _split(document["train"], root), _split(document["dev"], root),
        _implementation(document["implementation"], root), repository_root=root,
    )
    joined = runtime_family_scenes.join_runtime_family_scenes(inputs)
    for actual, key in (
        (tensor_set_sha256(joined.train), "train_tensor_set_sha256"),
        (tensor_set_sha256(joined.dev), "dev_tensor_set_sha256"),
        (mapping_audit_sha256(joined), "mapping_audit_sha256"),
    ):
        if actual != runtime_inputs._sha(document[key], key):
            raise RuntimeInputError(f"joined runtime {key} differs from the frozen binding")
    return joined
