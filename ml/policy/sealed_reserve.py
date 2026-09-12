# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Checksum-bound bookkeeping for ignored synthetic sealed-set archives.

This registry does not authorize evaluation or approve a model. Registration
reads every archive payload to validate its immutable generation chain. A
future sealed evaluator must bind the registry and record the transition at
its first actual sealed read. Reuse accounting here intentionally follows the
shared policy's distinct-revision limit. The evaluator must authenticate the
gate and training ledger's per-revision candidate budget before it calls
``record_sealed_read``; this registry does not reinterpret that experiment
authorization.
"""

from __future__ import annotations

import argparse
from contextlib import contextmanager
from copy import deepcopy
from io import BytesIO
import json
import os
from pathlib import Path
from typing import Any, Sequence
from uuid import uuid4
import zipfile

from PIL import Image, UnidentifiedImageError

from ml.markers.gate_seal import canonical_json_bytes, sha256_bytes, sha256_file
from ml.policy.evidence_policy import evidence_policy_reference, load_evidence_policy
from ml.synthetic.dataset import _validate_rendered_case
from ml.synthetic.schema import SceneValidationError, validate_scene
from ml.synthetic import sealed_acceptance as marker_acceptance, ocr_sealed_acceptance
from ml.synthetic.sealed_acceptance import (
    SealedAcceptanceError,
    validate_acceptance_cases,
)


REGISTRY_SCHEMA = "graphreader.synthetic-sealed-reserve-registry.v2"
GENERATION_SCHEMA = "graphreader.synthetic-sealed-reserve-generation.v2"
ARCHIVE_SCHEMA = "graphreader.synthetic-sealed-reserve-archive.v2"
SOURCE_SNAPSHOT_SCHEMA = "graphreader.synthetic-generator-source-snapshot.v1"
PLUMBING_PURPOSE = "plumbing_only"
ACCEPTANCE_PURPOSE = "sealed_acceptance"
DISCLOSURE_KINDS = frozenset({"case_identity", "truth", "prediction", "pixel"})
_REGISTRY_FIELDS = {"schema", "evidence_policy", "generation", "sets"}
_SET_FIELDS = {
    "set_id", "scope", "generator", "archive", "chain", "state", "uses",
    "retirement",
}
_SCOPE_FIELDS = {
    "purpose", "acceptance_scope", "coverage_protocol_path",
    "coverage_protocol_sha256",
}
_GENERATOR_FIELDS = {
    "config_path", "config_sha256", "source_paths", "source_sha256",
    "source_bundle_sha256",
}
_ARCHIVE_FIELDS = {"path", "sha256", "byte_count"}
_CHAIN_FIELDS = {
    "config_schema", "archive_schema", "archive_manifest_sha256",
    "source_snapshot_manifest_sha256",
    "payload_bundle_sha256", "case_identity_sha256", "case_count",
}
_USE_FIELDS = {
    "revision", "candidate_id", "gate_identity_sha256", "read_binding_sha256",
    "aggregate_only", "disclosures",
}
_RETIREMENT_FIELDS = {"reason", "evidence_sha256", "disclosures"}
_CONFIG_FIELDS = {
    "schema", "dataset_seed", "preset", "split", "purpose",
    "acceptance_scope", "coverage_protocol_path", "coverage_protocol_sha256",
    "generator_source_paths", "generator_source_sha256",
    "generator_source_bundle_sha256", "environment", "synthetic_only",
    "private_data", "training_permitted", "production_approval",
}
_ENVIRONMENT_FIELDS = {"pillow_version", "python_version", "zlib_version"}
_MANIFEST_FIELDS = {
    "schema", "generation_config_sha256", "generator_source_bundle_sha256",
    "source_snapshot_manifest_sha256", "split", "purpose", "acceptance_scope",
    "coverage_protocol_sha256", "payload_bundle_sha256", "case_identity_sha256",
    "case_count", "cases", "synthetic_only", "private_data",
}
_SOURCE_MANIFEST_FIELDS = {"schema", "source_bundle_sha256", "sources"}
_SOURCE_ROW_FIELDS = {"path", "sha256", "archive_path"}
_CASE_FIELDS = {"ordinal", "files"}
_CASE_PAYLOAD_NAMES = frozenset({
    "scene.json", "image.png", "annotation.json", "marker-mask.png",
})
_FAMILY_AXES = frozenset({"renderer", "font", "degradation", "template", "marker"})


class SealedReserveError(RuntimeError):
    """Raised when reserve identity, capacity, or transition checks fail."""


def _acceptance_protocol(scope: str):
    if scope == marker_acceptance.ACCEPTANCE_SCOPE:
        return marker_acceptance
    if scope == ocr_sealed_acceptance.ACCEPTANCE_SCOPE:
        return ocr_sealed_acceptance
    raise SealedAcceptanceError("acceptance reserve does not use a supported Goal 22 coverage identity")


def _require_acceptance_identity(scope: str, path: str, sha256: str) -> None:
    _acceptance_protocol(scope).require_supported_identity(scope, path, sha256)


def _policy_limits() -> tuple[int, int]:
    reuse = load_evidence_policy()["sealed_set_reuse"]
    maximum = reuse["maximum_distinct_revisions"]
    minimum = reuse["minimum_unused_sets"]
    if (
        type(maximum) is not int
        or maximum < 1
        or type(minimum) is not int
        or minimum < 1
        or reuse["aggregate_only_preserves_set"] is not True
        or reuse["retire_on_case_identity_or_truth_or_prediction_or_pixel_disclosure"] is not True
    ):
        raise SealedReserveError("shared evidence policy has invalid sealed reserve limits")
    return maximum, minimum


def _sha256(value: Any, label: str) -> str:
    if not isinstance(value, str):
        raise SealedReserveError(f"{label} must be a SHA-256 value")
    normalized = value.casefold()
    if len(normalized) != 64 or any(character not in "0123456789abcdef" for character in normalized):
        raise SealedReserveError(f"{label} must be a SHA-256 value")
    return normalized


def _object(value: Any, fields: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != fields:
        raise SealedReserveError(f"{label} has an invalid shape")
    return value


def _canonical_relative(value: Any, label: str) -> str:
    if not isinstance(value, str):
        raise SealedReserveError(f"{label} must be a canonical repository-relative path")
    relative = Path(value)
    canonical = relative.as_posix()
    if (
        not canonical
        or canonical == "."
        or canonical != value
        or relative.is_absolute()
        or relative.drive
        or ".." in relative.parts
    ):
        raise SealedReserveError(f"{label} must be a canonical repository-relative path")
    return canonical


def _repository_file(
    repository_root: Path,
    value: Any,
    label: str,
    *,
    under_artifacts: bool = False,
) -> tuple[Path, str]:
    if not isinstance(value, (str, Path)):
        raise SealedReserveError(f"{label} must be a canonical repository-relative path")
    relative = Path(value)
    canonical = relative.as_posix()
    if (
        not canonical
        or canonical == "."
        or relative.is_absolute()
        or relative.drive
        or ".." in relative.parts
    ):
        raise SealedReserveError(f"{label} must be a canonical repository-relative path")
    root = repository_root.resolve()
    resolved = (root / relative).resolve()
    if root not in resolved.parents or not resolved.is_file():
        raise SealedReserveError(f"{label} must be an existing repository file")
    if under_artifacts:
        artifacts = (root / "artifacts").resolve()
        if artifacts not in resolved.parents:
            raise SealedReserveError(f"{label} must be stored under repository artifacts")
    return resolved, resolved.relative_to(root).as_posix()


def _registry_file(repository_root: Path, registry_path: Path) -> Path:
    root = repository_root.resolve()
    path = registry_path if registry_path.is_absolute() else root / registry_path
    resolved = path.resolve()
    if root not in resolved.parents:
        raise SealedReserveError("reserve registry must remain inside the repository")
    return resolved


def _source_bundle(rows: Sequence[dict[str, str]]) -> str:
    return sha256_bytes(canonical_json_bytes(list(rows)))


def _load_generation_config(
    payload: bytes,
    repository_root: Path,
    *,
    verify_protocol_file: bool,
) -> tuple[dict[str, Any], dict[str, Any], list[dict[str, str]]]:
    try:
        config = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise SealedReserveError("reserve generation config is not valid JSON") from error
    document = _object(config, _CONFIG_FIELDS, "reserve generation config")
    if document["schema"] != GENERATION_SCHEMA:
        raise SealedReserveError("reserve generation config has an unsupported schema")
    if (
        type(document["dataset_seed"]) is not int
        or document["dataset_seed"] < 0
        or not isinstance(document["preset"], str)
        or not document["preset"]
        or document["split"] != "test"
        or document["synthetic_only"] is not True
        or document["private_data"] is not False
        or document["training_permitted"] is not False
        or document["production_approval"] is not False
    ):
        raise SealedReserveError("reserve generation config violates sealed synthetic safety")
    environment = _object(document["environment"], _ENVIRONMENT_FIELDS, "generator environment")
    if any(not isinstance(environment[key], str) or not environment[key] for key in _ENVIRONMENT_FIELDS):
        raise SealedReserveError("generator environment identities must be nonempty strings")

    paths = document["generator_source_paths"]
    hashes = document["generator_source_sha256"]
    if (
        not isinstance(paths, list)
        or not paths
        or not isinstance(hashes, list)
        or len(paths) != len(hashes)
        or len(set(paths)) != len(paths)
    ):
        raise SealedReserveError("generator snapshot identities must be nonempty unique arrays")
    source_rows = [
        {"path": _canonical_relative(path, "generator source"),
         "sha256": _sha256(digest, "generator source hash")}
        for path, digest in zip(paths, hashes, strict=True)
    ]
    if source_rows != sorted(source_rows, key=lambda row: row["path"]):
        raise SealedReserveError("generator snapshot identities must use stable path order")
    source_bundle = _sha256(
        document["generator_source_bundle_sha256"], "generator source bundle hash"
    )
    if _source_bundle(source_rows) != source_bundle:
        raise SealedReserveError("generator source bundle differs from the config snapshot")

    purpose = document["purpose"]
    if purpose == PLUMBING_PURPOSE:
        if document["preset"] != "smoke":
            raise SealedReserveError("plumbing-only config must use the smoke preset")
        if any(document[key] is not None for key in (
            "acceptance_scope", "coverage_protocol_path", "coverage_protocol_sha256"
        )):
            raise SealedReserveError("plumbing-only config cannot claim acceptance scope")
    elif purpose == ACCEPTANCE_PURPOSE:
        scope = document["acceptance_scope"]
        if not isinstance(scope, str) or not scope or scope.strip() != scope:
            raise SealedReserveError("acceptance reserve requires a nonempty exact scope")
        protocol_relative = _canonical_relative(
            document["coverage_protocol_path"], "coverage protocol"
        )
        protocol_sha256 = _sha256(
            document["coverage_protocol_sha256"], "coverage protocol hash"
        )
        try:
            _require_acceptance_identity(scope, protocol_relative, protocol_sha256)
        except SealedAcceptanceError as error:
            raise SealedReserveError(str(error)) from error
        if verify_protocol_file:
            protocol_path, canonical = _repository_file(
                repository_root, protocol_relative, "coverage protocol"
            )
            if canonical != protocol_relative or sha256_file(protocol_path) != protocol_sha256:
                raise SealedReserveError("coverage protocol differs from its reviewed identity")
    else:
        raise SealedReserveError("reserve generation purpose is unsupported")
    scope_record = {
        "purpose": purpose,
        "acceptance_scope": document["acceptance_scope"],
        "coverage_protocol_path": document["coverage_protocol_path"],
        "coverage_protocol_sha256": document["coverage_protocol_sha256"],
    }
    return document, scope_record, source_rows


def _zip_json(archive: zipfile.ZipFile, name: str, fields: set[str], label: str) -> tuple[dict[str, Any], bytes]:
    try:
        payload = archive.read(name)
        document = json.loads(payload)
    except KeyError as error:
        raise SealedReserveError(f"{label} is missing") from error
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise SealedReserveError(f"{label} is not valid JSON") from error
    return _object(document, fields, label), payload


def _decode_complete_png(payload: bytes, label: str) -> Image.Image:
    try:
        with Image.open(BytesIO(payload)) as verification:
            if verification.format != "PNG":
                raise SealedReserveError(f"{label} must be a PNG")
            verification.verify()
        with Image.open(BytesIO(payload)) as decoded:
            if decoded.format != "PNG":
                raise SealedReserveError(f"{label} must be a PNG")
            decoded.load()
            return decoded.copy()
    except (UnidentifiedImageError, OSError, ValueError) as error:
        raise SealedReserveError(f"{label} is not a complete PNG") from error


def _validate_case_payloads(payloads: dict[str, bytes]) -> dict[str, Any]:
    try:
        scene = json.loads(payloads["scene.json"])
        annotation = json.loads(payloads["annotation.json"])
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise SealedReserveError("reserve scene or annotation is not valid JSON") from error
    if not isinstance(scene, dict) or not isinstance(annotation, dict):
        raise SealedReserveError("reserve scene and annotation must be JSON objects")
    try:
        validate_scene(scene)
    except SceneValidationError as error:
        raise SealedReserveError("reserve scene does not satisfy the synthetic schema") from error

    image = _decode_complete_png(payloads["image.png"], "reserve image")
    marker_mask = _decode_complete_png(payloads["marker-mask.png"], "reserve marker mask")
    canvas = annotation.get("canvas")
    if (
        annotation.get("scene_id") != scene.get("scene_id")
        or annotation.get("coordinate_space") != "original_pixels"
        or not isinstance(canvas, dict)
        or type(canvas.get("width")) is not int
        or type(canvas.get("height")) is not int
        or (canvas["width"], canvas["height"]) != image.size
        or marker_mask.size != image.size
        or marker_mask.mode != "L"
    ):
        raise SealedReserveError(
            "reserve annotation, image, and marker mask identities are inconsistent"
        )
    try:
        _validate_rendered_case(scene, annotation, marker_mask)
    except (AssertionError, KeyError, TypeError, ValueError) as error:
        raise SealedReserveError(
            "reserve annotation failed rendered case consistency validation"
        ) from error
    return {
        "scene": scene,
        "annotation": annotation,
        "image_mode": image.mode,
    }


def _validate_generation_chain(
    config_path: Path,
    archive_path: Path,
    repository_root: Path,
) -> tuple[dict[str, Any], dict[str, Any], list[dict[str, str]], dict[str, Any]]:
    config_payload = config_path.read_bytes()
    config, scope, config_sources = _load_generation_config(
        config_payload, repository_root, verify_protocol_file=True
    )
    config_sha256 = sha256_bytes(config_payload)
    try:
        with zipfile.ZipFile(archive_path, "r") as archive:
            names = archive.namelist()
            if len(names) != len(set(names)) or any(
                name != Path(name).as_posix()
                or name.startswith("/")
                or ".." in Path(name).parts
                or archive.getinfo(name).is_dir()
                for name in names
            ):
                raise SealedReserveError("reserve archive contains unsafe or duplicate entries")
            manifest, manifest_payload = _zip_json(
                archive, "manifest.json", _MANIFEST_FIELDS, "reserve archive manifest"
            )
            source_manifest, source_manifest_payload = _zip_json(
                archive,
                "source-snapshot.json",
                _SOURCE_MANIFEST_FIELDS,
                "generator source snapshot",
            )
            if (
                manifest["schema"] != ARCHIVE_SCHEMA
                or manifest["generation_config_sha256"] != config_sha256
                or manifest["generator_source_bundle_sha256"]
                != config["generator_source_bundle_sha256"]
                or manifest["source_snapshot_manifest_sha256"]
                != sha256_bytes(source_manifest_payload)
                or manifest["split"] != "test"
                or manifest["purpose"] != scope["purpose"]
                or manifest["acceptance_scope"] != scope["acceptance_scope"]
                or manifest["coverage_protocol_sha256"]
                != scope["coverage_protocol_sha256"]
                or manifest["synthetic_only"] is not True
                or manifest["private_data"] is not False
            ):
                raise SealedReserveError("reserve archive manifest differs from its generation config")
            if (
                source_manifest["schema"] != SOURCE_SNAPSHOT_SCHEMA
                or source_manifest["source_bundle_sha256"]
                != config["generator_source_bundle_sha256"]
                or not isinstance(source_manifest["sources"], list)
            ):
                raise SealedReserveError("generator source snapshot has invalid identity")
            snapshot_rows = []
            snapshot_payloads: dict[str, bytes] = {}
            expected_names = {"manifest.json", "source-snapshot.json"}
            for index, raw_row in enumerate(source_manifest["sources"]):
                row = _object(raw_row, _SOURCE_ROW_FIELDS, "generator source snapshot row")
                source_path = _canonical_relative(row["path"], "snapshotted generator source")
                source_sha = _sha256(row["sha256"], "snapshotted generator source hash")
                archive_name = f"sources/{index:04d}.bin"
                if row["archive_path"] != archive_name:
                    raise SealedReserveError("generator source snapshot entry order is not canonical")
                try:
                    source_payload = archive.read(archive_name)
                except KeyError as error:
                    raise SealedReserveError("snapshotted generator source is missing") from error
                if sha256_bytes(source_payload) != source_sha:
                    raise SealedReserveError("snapshotted generator source bytes differ")
                snapshot_rows.append({"path": source_path, "sha256": source_sha})
                snapshot_payloads[source_path] = source_payload
                expected_names.add(archive_name)
            if snapshot_rows != config_sources:
                raise SealedReserveError("source snapshot differs from generation config")

            cases = manifest["cases"]
            if (
                type(manifest["case_count"]) is not int
                or manifest["case_count"] <= 0
                or not isinstance(cases, list)
                or len(cases) != manifest["case_count"]
            ):
                raise SealedReserveError("reserve archive case count is invalid")
            payload_rows = []
            case_identity_sha256 = []
            validated_cases = []
            for ordinal, raw_case in enumerate(cases):
                case = _object(raw_case, _CASE_FIELDS, "reserve archive case")
                if case["ordinal"] != ordinal or not isinstance(case["files"], dict) or set(case["files"]) != _CASE_PAYLOAD_NAMES:
                    raise SealedReserveError("reserve archive case manifest is invalid")
                case_payloads: dict[str, bytes] = {}
                for payload_name in sorted(_CASE_PAYLOAD_NAMES):
                    expected_sha = _sha256(
                        case["files"][payload_name], "reserve case payload hash"
                    )
                    archive_name = f"cases/{ordinal:04d}/{payload_name}"
                    try:
                        payload = archive.read(archive_name)
                    except KeyError as error:
                        raise SealedReserveError("reserve case payload is missing") from error
                    if sha256_bytes(payload) != expected_sha:
                        raise SealedReserveError("reserve case payload differs from its manifest")
                    case_payloads[payload_name] = payload
                    payload_rows.append({"path": archive_name, "sha256": expected_sha})
                    expected_names.add(archive_name)
                validated_case = _validate_case_payloads(case_payloads)
                validated_cases.append(validated_case)
                scene = validated_case["scene"]
                scene_id = scene.get("scene_id")
                seed = scene.get("seed")
                families = scene.get("families")
                if (
                    not isinstance(scene_id, str)
                    or not scene_id
                    or type(seed) is not int
                    or not isinstance(families, dict)
                    or set(families) != _FAMILY_AXES
                    or any(
                        not isinstance(family, dict)
                        or set(family) != {"key", "split"}
                        or not isinstance(family["key"], str)
                        or not family["key"]
                        or family["split"] != "test"
                        for family in families.values()
                    )
                ):
                    raise SealedReserveError(
                        "reserve scene is not an independent test-family case"
                    )
                case_identity_sha256.append(sha256_bytes(canonical_json_bytes({
                    "scene_id": scene_id,
                    "seed": seed,
                })))
            if set(names) != expected_names:
                raise SealedReserveError("reserve archive contains unmanifested entries")
            if (
                manifest["payload_bundle_sha256"] != _source_bundle(payload_rows)
                or manifest["case_identity_sha256"] != case_identity_sha256
            ):
                raise SealedReserveError("reserve archive identities differ from case payloads")
            if scope["purpose"] == ACCEPTANCE_PURPOSE:
                protocol_payload = snapshot_payloads.get(
                    str(scope["coverage_protocol_path"]), b""
                )
                try:
                    if scope["acceptance_scope"] == marker_acceptance.ACCEPTANCE_SCOPE:
                        validate_acceptance_cases(config, snapshot_rows, protocol_payload, validated_cases)
                    else:
                        ocr_sealed_acceptance.validate_acceptance_cases(
                            config, snapshot_rows, protocol_payload, validated_cases,
                            snapshot_payloads=snapshot_payloads,
                        )
                except SealedAcceptanceError as error:
                    raise SealedReserveError(str(error)) from error
    except (zipfile.BadZipFile, OSError) as error:
        raise SealedReserveError("reserve archive is not a valid ZIP") from error
    chain = {
        "config_schema": GENERATION_SCHEMA,
        "archive_schema": ARCHIVE_SCHEMA,
        "archive_manifest_sha256": sha256_bytes(manifest_payload),
        "source_snapshot_manifest_sha256": manifest["source_snapshot_manifest_sha256"],
        "payload_bundle_sha256": manifest["payload_bundle_sha256"],
        "case_identity_sha256": case_identity_sha256,
        "case_count": manifest["case_count"],
    }
    return config, scope, config_sources, chain


def _set_identity(
    config_sha256: str, source_bundle_sha256: str, archive_sha256: str
) -> str:
    return sha256_bytes(canonical_json_bytes({
        "archive_sha256": archive_sha256,
        "generator_config_sha256": config_sha256,
        "generator_source_bundle_sha256": source_bundle_sha256,
    }))


def _require_supported_registry_scope(scope: dict[str, Any]) -> None:
    if scope["purpose"] == ACCEPTANCE_PURPOSE:
        try:
            _require_acceptance_identity(
                scope["acceptance_scope"],
                scope["coverage_protocol_path"],
                scope["coverage_protocol_sha256"],
            )
        except SealedAcceptanceError as error:
            raise SealedReserveError(str(error)) from error


def _validate_set(record: Any, repository_root: Path) -> dict[str, Any]:
    item = _object(record, _SET_FIELDS, "reserve set")
    scope = _object(item["scope"], _SCOPE_FIELDS, "reserve scope")
    _require_supported_registry_scope(scope)
    generator = _object(item["generator"], _GENERATOR_FIELDS, "reserve generator")
    archive = _object(item["archive"], _ARCHIVE_FIELDS, "reserve archive")
    chain = _object(item["chain"], _CHAIN_FIELDS, "reserve generation chain")
    config_path, config_relative = _repository_file(
        repository_root, generator["config_path"], "generator config"
    )
    config_sha256 = _sha256(generator["config_sha256"], "generator config hash")
    if config_relative != generator["config_path"] or sha256_file(config_path) != config_sha256:
        raise SealedReserveError("generator config differs from its registered identity")
    _config, config_scope, config_sources = _load_generation_config(
        config_path.read_bytes(), repository_root, verify_protocol_file=False
    )
    if config_scope != scope:
        raise SealedReserveError("reserve scope differs from its generation config")

    source_paths = generator["source_paths"]
    source_hashes = generator["source_sha256"]
    if (
        not isinstance(source_paths, list)
        or not source_paths
        or not isinstance(source_hashes, list)
        or len(source_paths) != len(source_hashes)
        or len(set(source_paths)) != len(source_paths)
    ):
        raise SealedReserveError("generator sources must be nonempty unique path/hash arrays")
    source_rows = [
        {"path": _canonical_relative(path_value, "generator source"),
         "sha256": _sha256(hash_value, "generator source hash")}
        for path_value, hash_value in zip(source_paths, source_hashes, strict=True)
    ]
    if source_rows != sorted(source_rows, key=lambda row: row["path"]):
        raise SealedReserveError("generator sources must use stable path order")
    source_bundle = _sha256(
        generator["source_bundle_sha256"], "generator source bundle hash"
    )
    if _source_bundle(source_rows) != source_bundle:
        raise SealedReserveError("generator source bundle differs from registered sources")
    if source_rows != config_sources:
        raise SealedReserveError("registered source snapshot differs from generation config")

    archive_path, archive_relative = _repository_file(
        repository_root, archive["path"], "reserve archive", under_artifacts=True
    )
    archive_sha256 = _sha256(archive["sha256"], "reserve archive hash")
    if (
        archive_relative != archive["path"]
        or sha256_file(archive_path) != archive_sha256
        or type(archive["byte_count"]) is not int
        or archive["byte_count"] <= 0
        or archive_path.stat().st_size != archive["byte_count"]
    ):
        raise SealedReserveError("reserve archive differs from its registered identity")
    try:
        with zipfile.ZipFile(archive_path, "r") as archive_file:
            archived_manifest, archived_manifest_payload = _zip_json(
                archive_file,
                "manifest.json",
                _MANIFEST_FIELDS,
                "reserve archive manifest",
            )
    except (zipfile.BadZipFile, OSError) as error:
        raise SealedReserveError("reserve archive is not a valid ZIP") from error
    if (
        sha256_bytes(archived_manifest_payload) != chain["archive_manifest_sha256"]
        or archived_manifest["generation_config_sha256"] != config_sha256
        or archived_manifest["generator_source_bundle_sha256"] != source_bundle
        or archived_manifest["source_snapshot_manifest_sha256"]
        != chain["source_snapshot_manifest_sha256"]
        or archived_manifest["payload_bundle_sha256"] != chain["payload_bundle_sha256"]
        or archived_manifest["case_identity_sha256"] != chain["case_identity_sha256"]
        or archived_manifest["case_count"] != chain["case_count"]
        or archived_manifest["purpose"] != scope["purpose"]
        or archived_manifest["acceptance_scope"] != scope["acceptance_scope"]
        or archived_manifest["coverage_protocol_sha256"]
        != scope["coverage_protocol_sha256"]
    ):
        raise SealedReserveError("registered generation chain differs from its archive manifest")

    set_id = _sha256(item["set_id"], "reserve set identity")
    if set_id != _set_identity(config_sha256, source_bundle, archive_sha256):
        raise SealedReserveError("reserve set identity differs from its immutable inputs")
    if (
        chain["config_schema"] != GENERATION_SCHEMA
        or chain["archive_schema"] != ARCHIVE_SCHEMA
        or _sha256(chain["archive_manifest_sha256"], "archive manifest hash")
        != chain["archive_manifest_sha256"]
        or _sha256(
            chain["source_snapshot_manifest_sha256"],
            "source snapshot manifest hash",
        ) != chain["source_snapshot_manifest_sha256"]
        or _sha256(chain["payload_bundle_sha256"], "payload bundle hash")
        != chain["payload_bundle_sha256"]
        or type(chain["case_count"]) is not int
        or chain["case_count"] <= 0
        or not isinstance(chain["case_identity_sha256"], list)
        or len(chain["case_identity_sha256"]) != chain["case_count"]
        or len(set(chain["case_identity_sha256"])) != len(chain["case_identity_sha256"])
        or any(
            _sha256(value, "reserve case identity hash") != value
            for value in chain["case_identity_sha256"]
        )
    ):
        raise SealedReserveError("reserve generation-chain evidence is invalid")

    maximum, _ = _policy_limits()
    uses = item["uses"]
    if not isinstance(uses, list):
        raise SealedReserveError("reserve uses must be an array")
    revisions: set[str] = set()
    candidate_pairs: set[tuple[str, str]] = set()
    for use_value in uses:
        use = _object(use_value, _USE_FIELDS, "reserve use")
        revision = use["revision"]
        candidate_id = use["candidate_id"]
        if (
            not isinstance(revision, str)
            or not revision
            or revision.strip() != revision
            or not isinstance(candidate_id, str)
            or not candidate_id
            or candidate_id.strip() != candidate_id
            or (revision, candidate_id) in candidate_pairs
        ):
            raise SealedReserveError("reserve uses require unique candidate/revision identities")
        revisions.add(revision)
        candidate_pairs.add((revision, candidate_id))
        _sha256(use["gate_identity_sha256"], "sealed gate identity hash")
        _sha256(use["read_binding_sha256"], "sealed read binding hash")
        disclosures = use["disclosures"]
        if (
            not isinstance(disclosures, list)
            or len(disclosures) != len(set(disclosures))
            or not set(disclosures).issubset(DISCLOSURE_KINDS)
            or use["aggregate_only"] is not (not disclosures)
        ):
            raise SealedReserveError("reserve use has an invalid disclosure scope")
    if len(revisions) > maximum:
        raise SealedReserveError("reserve uses exceed the distinct-revision policy limit")

    state = item["state"]
    retirement = item["retirement"]
    if state == "unused":
        if uses or retirement is not None:
            raise SealedReserveError("unused reserve set contains use or retirement state")
    elif state == "reusable":
        if not uses or len(revisions) >= maximum or retirement is not None or any(not use["aggregate_only"] for use in uses):
            raise SealedReserveError("reusable reserve set has inconsistent use state")
    elif state == "revision_limit":
        if (
            len(revisions) != maximum
            or retirement is not None
            or any(not use["aggregate_only"] for use in uses)
        ):
            raise SealedReserveError("revision-limit reserve set has inconsistent use state")
    elif state == "retired":
        retired = _object(retirement, _RETIREMENT_FIELDS, "reserve retirement")
        _sha256(retired["evidence_sha256"], "retirement evidence hash")
        disclosures = retired["disclosures"]
        if (
            not isinstance(disclosures, list)
            or len(disclosures) != len(set(disclosures))
            or disclosures != sorted(disclosures)
            or not set(disclosures).issubset(DISCLOSURE_KINDS)
        ):
            raise SealedReserveError("reserve retirement has invalid disclosures")
        if retired["reason"] == "maximum_distinct_revisions_reached":
            if disclosures or len(revisions) != maximum or any(not use["aggregate_only"] for use in uses):
                raise SealedReserveError("reuse-limit retirement is inconsistent")
        elif retired["reason"] == "case_level_disclosure":
            if not disclosures:
                raise SealedReserveError("case-level retirement must identify a disclosure kind")
            direct_disclosures = [use for use in uses if not use["aggregate_only"]]
            if direct_disclosures and (
                len(direct_disclosures) != 1
                or direct_disclosures[0]["read_binding_sha256"] != retired["evidence_sha256"]
                or direct_disclosures[0]["disclosures"] != disclosures
            ):
                raise SealedReserveError("case-level retirement differs from the sealed read disclosure")
        else:
            raise SealedReserveError("reserve retirement has an unsupported reason")
    else:
        raise SealedReserveError("reserve set has an unsupported state")
    return item


def load_registry(
    registry_path: Path, repository_root: Path
) -> dict[str, Any]:
    path = _registry_file(repository_root, registry_path)
    if not path.is_file():
        raise SealedReserveError("synthetic sealed reserve registry is missing")
    try:
        registry = json.loads(path.read_bytes())
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise SealedReserveError("synthetic sealed reserve registry is not valid JSON") from error
    document = _object(registry, _REGISTRY_FIELDS, "sealed reserve registry")
    if document["schema"] != REGISTRY_SCHEMA:
        raise SealedReserveError("synthetic sealed reserve registry has an unsupported schema")
    if document["evidence_policy"] != evidence_policy_reference():
        raise SealedReserveError("sealed reserve registry is not bound to the current evidence policy")
    if type(document["generation"]) is not int or document["generation"] < 0:
        raise SealedReserveError("sealed reserve registry generation is invalid")
    if not isinstance(document["sets"], list):
        raise SealedReserveError("sealed reserve registry sets must be an array")
    sets = [_validate_set(item, repository_root) for item in document["sets"]]
    set_ids = [item["set_id"] for item in sets]
    archive_hashes = [item["archive"]["sha256"] for item in sets]
    archive_paths = [item["archive"]["path"] for item in sets]
    case_identities = [
        identity for item in sets for identity in item["chain"]["case_identity_sha256"]
    ]
    generation_pairs = [
        (item["generator"]["config_sha256"], item["generator"]["source_bundle_sha256"])
        for item in sets
    ]
    candidate_identities = [
        (
            item["scope"]["acceptance_scope"],
            item["scope"]["coverage_protocol_sha256"],
            use["revision"],
            use["candidate_id"],
        )
        for item in sets
        for use in item["uses"]
    ]
    gate_identities = [
        use["gate_identity_sha256"] for item in sets for use in item["uses"]
    ]
    read_bindings = [use["read_binding_sha256"] for item in sets for use in item["uses"]]
    if (
        len(set(set_ids)) != len(set_ids)
        or len(set(archive_hashes)) != len(archive_hashes)
        or len(set(archive_paths)) != len(archive_paths)
        or len(set(case_identities)) != len(case_identities)
        or len(set(generation_pairs)) != len(generation_pairs)
        or len(set(candidate_identities)) != len(candidate_identities)
        or len(set(gate_identities)) != len(gate_identities)
        or len(set(read_bindings)) != len(read_bindings)
    ):
        raise SealedReserveError("sealed reserve registry contains duplicate identities")
    return deepcopy(document)


def _write_registry(path: Path, registry: dict[str, Any]) -> str:
    payload = canonical_json_bytes(registry)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid4().hex}.tmp")
    try:
        temporary.write_bytes(payload)
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)
    return sha256_bytes(payload)


@contextmanager
def _registry_update_lock(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    lock_path = path.with_name(f".{path.name}.lock")
    token = uuid4().hex.encode("ascii")
    descriptor: int | None = None
    try:
        try:
            descriptor = os.open(
                lock_path,
                os.O_CREAT | os.O_EXCL | os.O_WRONLY,
                0o600,
            )
        except FileExistsError as error:
            raise SealedReserveError(
                "sealed reserve registry is locked by another writer"
            ) from error
        os.write(descriptor, token)
        os.fsync(descriptor)
        yield
    finally:
        if descriptor is not None:
            os.close(descriptor)
            try:
                if lock_path.read_bytes() == token:
                    lock_path.unlink()
            except FileNotFoundError:
                pass


def initialize_registry(registry_path: Path, repository_root: Path) -> str:
    path = _registry_file(repository_root, registry_path)
    with _registry_update_lock(path):
        if path.exists():
            raise SealedReserveError("synthetic sealed reserve registry already exists")
        return _write_registry(path, {
            "schema": REGISTRY_SCHEMA,
            "evidence_policy": evidence_policy_reference(),
            "generation": 0,
            "sets": [],
        })


def _load_for_update(
    registry_path: Path, repository_root: Path, expected_registry_sha256: str
) -> tuple[Path, dict[str, Any]]:
    path = _registry_file(repository_root, registry_path)
    expected = _sha256(expected_registry_sha256, "expected registry hash")
    if not path.is_file() or sha256_file(path) != expected:
        raise SealedReserveError("sealed reserve registry differs from the expected SHA-256")
    return path, load_registry(path, repository_root)


def _register_reserve_set_locked(
    registry_path: Path,
    repository_root: Path,
    *,
    expected_registry_sha256: str,
    expected_generator_config_sha256: str,
    expected_generator_source_bundle_sha256: str,
    expected_archive_sha256: str,
    generator_config_path: Path,
    archive_path: Path,
    required_acceptance_scope: str | None = None,
    required_coverage_protocol_sha256: str | None = None,
) -> tuple[str, str]:
    path, registry = _load_for_update(
        registry_path, repository_root, expected_registry_sha256
    )
    config, config_relative = _repository_file(
        repository_root, generator_config_path, "generator config"
    )
    archive, archive_relative = _repository_file(
        repository_root, archive_path, "reserve archive", under_artifacts=True
    )
    _config_document, scope, source_rows, chain = _validate_generation_chain(
        config, archive, repository_root
    )
    _require_supported_registry_scope(scope)
    config_sha256 = sha256_file(config)
    source_bundle = _source_bundle(source_rows)
    archive_sha256 = sha256_file(archive)
    if config_sha256 != _sha256(
        expected_generator_config_sha256, "expected generator config hash"
    ):
        raise SealedReserveError("generator config differs from the expected SHA-256")
    if source_bundle != _sha256(
        expected_generator_source_bundle_sha256,
        "expected generator source bundle hash",
    ):
        raise SealedReserveError("generator source bundle differs from the expected SHA-256")
    if archive_sha256 != _sha256(expected_archive_sha256, "expected reserve archive hash"):
        raise SealedReserveError("reserve archive differs from the expected SHA-256")
    if scope["purpose"] == ACCEPTANCE_PURPOSE:
        expected_scope = required_acceptance_scope
        expected_protocol = (
            _sha256(
                required_coverage_protocol_sha256,
                "required coverage protocol hash",
            )
            if required_coverage_protocol_sha256 is not None
            else None
        )
        if (
            not isinstance(expected_scope, str)
            or not expected_scope
            or expected_scope.strip() != expected_scope
            or scope["acceptance_scope"] != expected_scope
            or scope["coverage_protocol_sha256"] != expected_protocol
        ):
            raise SealedReserveError(
                "acceptance reserve registration requires its exact reviewed scope and protocol"
            )
    elif required_acceptance_scope is not None or required_coverage_protocol_sha256 is not None:
        raise SealedReserveError("plumbing-only reserve cannot satisfy acceptance registration")
    set_id = _set_identity(config_sha256, source_bundle, archive_sha256)
    record = {
        "set_id": set_id,
        "scope": scope,
        "generator": {
            "config_path": config_relative,
            "config_sha256": config_sha256,
            "source_paths": [row["path"] for row in source_rows],
            "source_sha256": [row["sha256"] for row in source_rows],
            "source_bundle_sha256": source_bundle,
        },
        "archive": {
            "path": archive_relative,
            "sha256": archive_sha256,
            "byte_count": archive.stat().st_size,
        },
        "chain": chain,
        "state": "unused",
        "uses": [],
        "retirement": None,
    }
    _validate_set(record, repository_root)
    if any(
        existing["set_id"] == set_id
        or existing["archive"]["sha256"] == archive_sha256
        or existing["archive"]["path"] == archive_relative
        or (
            existing["generator"]["config_sha256"] == config_sha256
            and existing["generator"]["source_bundle_sha256"] == source_bundle
        )
        or bool(
            set(existing["chain"]["case_identity_sha256"])
            & set(chain["case_identity_sha256"])
        )
        for existing in registry["sets"]
    ):
        raise SealedReserveError("sealed reserve set duplicates a registered identity")
    registry["sets"].append(record)
    registry["generation"] += 1
    new_hash = _write_registry(path, registry)
    load_registry(path, repository_root)
    return set_id, new_hash


def register_reserve_set(
    registry_path: Path,
    repository_root: Path,
    *,
    expected_registry_sha256: str,
    expected_generator_config_sha256: str,
    expected_generator_source_bundle_sha256: str,
    expected_archive_sha256: str,
    generator_config_path: Path,
    archive_path: Path,
    required_acceptance_scope: str | None = None,
    required_coverage_protocol_sha256: str | None = None,
) -> tuple[str, str]:
    path = _registry_file(repository_root, registry_path)
    with _registry_update_lock(path):
        return _register_reserve_set_locked(
            registry_path,
            repository_root,
            expected_registry_sha256=expected_registry_sha256,
            expected_generator_config_sha256=expected_generator_config_sha256,
            expected_generator_source_bundle_sha256=expected_generator_source_bundle_sha256,
            expected_archive_sha256=expected_archive_sha256,
            generator_config_path=generator_config_path,
            archive_path=archive_path,
            required_acceptance_scope=required_acceptance_scope,
            required_coverage_protocol_sha256=required_coverage_protocol_sha256,
        )


def _find_set(registry: dict[str, Any], set_id: str) -> dict[str, Any]:
    normalized = _sha256(set_id, "reserve set identity")
    match = next((item for item in registry["sets"] if item["set_id"] == normalized), None)
    if match is None:
        raise SealedReserveError("sealed reserve set is not registered")
    return match


def _record_sealed_read_locked(
    registry_path: Path,
    repository_root: Path,
    *,
    expected_registry_sha256: str,
    set_id: str,
    revision: str,
    candidate_id: str,
    gate_identity_sha256: str,
    read_binding_sha256: str,
    required_acceptance_scope: str,
    required_coverage_protocol_sha256: str,
    disclosures: Sequence[str] = (),
) -> str:
    path, registry = _load_for_update(
        registry_path, repository_root, expected_registry_sha256
    )
    item = _find_set(registry, set_id)
    if item["state"] == "retired":
        raise SealedReserveError("retired sealed reserve sets cannot be reused")
    if not isinstance(revision, str) or not revision or revision.strip() != revision:
        raise SealedReserveError("sealed read revision must be nonempty")
    if not isinstance(candidate_id, str) or not candidate_id or candidate_id.strip() != candidate_id:
        raise SealedReserveError("sealed read candidate identity must be nonempty")
    if (
        not isinstance(required_acceptance_scope, str)
        or not required_acceptance_scope
        or required_acceptance_scope.strip() != required_acceptance_scope
    ):
        raise SealedReserveError("sealed read requires an exact acceptance scope")
    protocol_sha256 = _sha256(
        required_coverage_protocol_sha256, "required coverage protocol hash"
    )
    try:
        _require_acceptance_identity(
            required_acceptance_scope,
            _acceptance_protocol(required_acceptance_scope).PROTOCOL_PATH.as_posix(),
            protocol_sha256,
        )
    except SealedAcceptanceError as error:
        raise SealedReserveError(str(error)) from error
    if (
        item["scope"]["purpose"] != ACCEPTANCE_PURPOSE
        or item["scope"]["acceptance_scope"] != required_acceptance_scope
        or item["scope"]["coverage_protocol_sha256"] != protocol_sha256
    ):
        raise SealedReserveError("sealed reserve set is not compatible with the required acceptance scope")
    gate_sha256 = _sha256(gate_identity_sha256, "sealed gate identity hash")
    binding_sha256 = _sha256(read_binding_sha256, "sealed read binding hash")
    if any(
        use["gate_identity_sha256"] == gate_sha256
        or use["read_binding_sha256"] == binding_sha256
        or (
            candidate["scope"]["acceptance_scope"] == required_acceptance_scope
            and candidate["scope"]["coverage_protocol_sha256"] == protocol_sha256
            and use["revision"] == revision
            and use["candidate_id"] == candidate_id
        )
        for candidate in registry["sets"]
        for use in candidate["uses"]
    ):
        raise SealedReserveError("sealed candidate, gate, or read binding was already recorded")
    disclosure_list = sorted(set(disclosures))
    if len(disclosure_list) != len(disclosures) or not set(disclosure_list).issubset(DISCLOSURE_KINDS):
        raise SealedReserveError("sealed read disclosures are invalid")

    maximum, minimum = _policy_limits()
    compatible = [
        candidate
        for candidate in registry["sets"]
        if candidate["scope"]["purpose"] == ACCEPTANCE_PURPOSE
        and candidate["scope"]["acceptance_scope"] == required_acceptance_scope
        and candidate["scope"]["coverage_protocol_sha256"] == protocol_sha256
    ]
    unused_before = sum(candidate["state"] == "unused" for candidate in compatible)
    if unused_before < minimum or (
        item["state"] == "unused" and unused_before - 1 < minimum
    ):
        raise SealedReserveError("sealed read would violate the minimum unused reserve")
    used_revisions = {use["revision"] for use in item["uses"]}
    if revision not in used_revisions and len(used_revisions) >= maximum:
        raise SealedReserveError("sealed reserve set reached its revision reuse limit")

    item["uses"].append({
        "revision": revision,
        "candidate_id": candidate_id,
        "gate_identity_sha256": gate_sha256,
        "read_binding_sha256": binding_sha256,
        "aggregate_only": not disclosure_list,
        "disclosures": disclosure_list,
    })
    if disclosure_list:
        item["state"] = "retired"
        item["retirement"] = {
            "reason": "case_level_disclosure",
            "evidence_sha256": binding_sha256,
            "disclosures": disclosure_list,
        }
    elif len({use["revision"] for use in item["uses"]}) == maximum:
        item["state"] = "revision_limit"
    else:
        item["state"] = "reusable"
    registry["generation"] += 1
    new_hash = _write_registry(path, registry)
    load_registry(path, repository_root)
    return new_hash


def record_sealed_read(
    registry_path: Path,
    repository_root: Path,
    *,
    expected_registry_sha256: str,
    set_id: str,
    revision: str,
    candidate_id: str,
    gate_identity_sha256: str,
    read_binding_sha256: str,
    required_acceptance_scope: str,
    required_coverage_protocol_sha256: str,
    disclosures: Sequence[str] = (),
) -> str:
    path = _registry_file(repository_root, registry_path)
    with _registry_update_lock(path):
        return _record_sealed_read_locked(
            registry_path,
            repository_root,
            expected_registry_sha256=expected_registry_sha256,
            set_id=set_id,
            revision=revision,
            candidate_id=candidate_id,
            gate_identity_sha256=gate_identity_sha256,
            read_binding_sha256=read_binding_sha256,
            required_acceptance_scope=required_acceptance_scope,
            required_coverage_protocol_sha256=required_coverage_protocol_sha256,
            disclosures=disclosures,
        )


def _record_case_level_disclosure_locked(
    registry_path: Path,
    repository_root: Path,
    *,
    expected_registry_sha256: str,
    set_id: str,
    evidence_sha256: str,
    disclosures: Sequence[str],
) -> str:
    path, registry = _load_for_update(
        registry_path, repository_root, expected_registry_sha256
    )
    item = _find_set(registry, set_id)
    if item["state"] == "retired":
        raise SealedReserveError("sealed reserve set is already permanently retired")
    disclosure_list = sorted(set(disclosures))
    if (
        not disclosure_list
        or len(disclosure_list) != len(disclosures)
        or not set(disclosure_list).issubset(DISCLOSURE_KINDS)
    ):
        raise SealedReserveError("case-level retirement requires a disclosure kind")
    evidence = _sha256(evidence_sha256, "retirement evidence hash")
    item["state"] = "retired"
    item["retirement"] = {
        "reason": "case_level_disclosure",
        "evidence_sha256": evidence,
        "disclosures": disclosure_list,
    }
    registry["generation"] += 1
    new_hash = _write_registry(path, registry)
    load_registry(path, repository_root)
    return new_hash


def record_case_level_disclosure(
    registry_path: Path,
    repository_root: Path,
    *,
    expected_registry_sha256: str,
    set_id: str,
    evidence_sha256: str,
    disclosures: Sequence[str],
) -> str:
    path = _registry_file(repository_root, registry_path)
    with _registry_update_lock(path):
        return _record_case_level_disclosure_locked(
            registry_path,
            repository_root,
            expected_registry_sha256=expected_registry_sha256,
            set_id=set_id,
            evidence_sha256=evidence_sha256,
            disclosures=disclosures,
        )


def reserve_counts(registry: dict[str, Any]) -> dict[str, int]:
    return {
        state: sum(item["state"] == state for item in registry["sets"])
        for state in ("unused", "reusable", "revision_limit", "retired")
    }


def acceptance_reserve_counts(
    registry: dict[str, Any],
    *,
    required_acceptance_scope: str,
    required_coverage_protocol_sha256: str,
) -> dict[str, int]:
    if (
        not isinstance(required_acceptance_scope, str)
        or not required_acceptance_scope
        or required_acceptance_scope.strip() != required_acceptance_scope
    ):
        raise SealedReserveError("acceptance reserve counts require an exact scope")
    protocol_sha256 = _sha256(
        required_coverage_protocol_sha256, "required coverage protocol hash"
    )
    try:
        _require_acceptance_identity(
            required_acceptance_scope,
            _acceptance_protocol(required_acceptance_scope).PROTOCOL_PATH.as_posix(),
            protocol_sha256,
        )
    except SealedAcceptanceError as error:
        raise SealedReserveError(str(error)) from error
    compatible = [
        item
        for item in registry["sets"]
        if item["scope"]["purpose"] == ACCEPTANCE_PURPOSE
        and item["scope"]["acceptance_scope"] == required_acceptance_scope
        and item["scope"].get("coverage_protocol_path")
        == _acceptance_protocol(required_acceptance_scope).PROTOCOL_PATH.as_posix()
        and item["scope"]["coverage_protocol_sha256"] == protocol_sha256
    ]
    return {
        state: sum(item["state"] == state for item in compatible)
        for state in ("unused", "reusable", "revision_limit", "retired")
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository-root", type=Path, default=Path.cwd())
    subparsers = parser.add_subparsers(dest="command", required=True)
    initialize = subparsers.add_parser("initialize")
    initialize.add_argument("--registry", type=Path, required=True)
    register = subparsers.add_parser("register")
    register.add_argument("--registry", type=Path, required=True)
    register.add_argument("--expected-registry-sha256", required=True)
    register.add_argument("--expected-generator-config-sha256", required=True)
    register.add_argument("--expected-generator-source-bundle-sha256", required=True)
    register.add_argument("--expected-archive-sha256", required=True)
    register.add_argument("--generator-config", type=Path, required=True)
    register.add_argument("--archive", type=Path, required=True)
    register.add_argument("--required-acceptance-scope")
    register.add_argument("--required-coverage-protocol-sha256")
    args = parser.parse_args()
    root = args.repository_root.resolve()
    if args.command == "initialize":
        result = {"registry_sha256": initialize_registry(args.registry, root)}
    else:
        set_id, registry_sha256 = register_reserve_set(
            args.registry,
            root,
            expected_registry_sha256=args.expected_registry_sha256,
            expected_generator_config_sha256=args.expected_generator_config_sha256,
            expected_generator_source_bundle_sha256=args.expected_generator_source_bundle_sha256,
            expected_archive_sha256=args.expected_archive_sha256,
            generator_config_path=args.generator_config,
            archive_path=args.archive,
            required_acceptance_scope=args.required_acceptance_scope,
            required_coverage_protocol_sha256=args.required_coverage_protocol_sha256,
        )
        result = {"set_id": set_id, "registry_sha256": registry_sha256}
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()


__all__ = [
    "DISCLOSURE_KINDS",
    "ACCEPTANCE_PURPOSE",
    "ARCHIVE_SCHEMA",
    "GENERATION_SCHEMA",
    "PLUMBING_PURPOSE",
    "REGISTRY_SCHEMA",
    "SOURCE_SNAPSHOT_SCHEMA",
    "SealedReserveError",
    "acceptance_reserve_counts",
    "initialize_registry",
    "load_registry",
    "record_case_level_disclosure",
    "record_sealed_read",
    "register_reserve_set",
    "reserve_counts",
]
