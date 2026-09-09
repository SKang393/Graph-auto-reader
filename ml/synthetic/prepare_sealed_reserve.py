# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Prepare ignored, deterministic synthetic test-family reserve archives."""

from __future__ import annotations

import argparse
import hashlib
from io import BytesIO
import json
from importlib.metadata import version as package_version
from pathlib import Path
import platform
import zipfile
import zlib

from PIL import Image

from ml.synthetic.dataset import (
    PRESETS,
    _build_scenes,
    _scene_split,
    _validate_rendered_case,
)
from ml.synthetic.io import canonical_json_bytes, png_bytes
from ml.synthetic.renderer import render_scene
from ml.synthetic.sealed_acceptance import (
    ACCEPTANCE_PRESET,
    ACCEPTANCE_PURPOSE,
    ACCEPTANCE_SCOPE,
    GENERATOR_SOURCE_PATHS as ACCEPTANCE_GENERATOR_SOURCE_PATHS,
    PROTOCOL_PATH,
    PROTOCOL_SHA256,
    acceptance_case_specs,
    load_supported_protocol,
    validate_acceptance_cases,
    verify_acceptance_font_dependencies,
)


CONFIG_SCHEMA = "graphreader.synthetic-sealed-reserve-generation.v2"
ARCHIVE_SCHEMA = "graphreader.synthetic-sealed-reserve-archive.v2"
SOURCE_SNAPSHOT_SCHEMA = "graphreader.synthetic-generator-source-snapshot.v1"
PURPOSE = "plumbing_only"
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
GENERATOR_SOURCE_PATHS = (
    Path("ml/synthetic/prepare_sealed_reserve.py"),
    Path("ml/synthetic/dataset.py"),
    Path("ml/synthetic/templates.py"),
    Path("ml/synthetic/renderer.py"),
    Path("ml/synthetic/schema.py"),
    Path("ml/synthetic/scene.schema.json"),
    Path("ml/synthetic/io.py"),
    Path("ml/synthetic/fonts.py"),
)
_ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _artifact_output(repository_root: Path, value: Path, label: str) -> Path:
    root = repository_root.resolve()
    path = value if value.is_absolute() else root / value
    resolved = path.resolve()
    artifacts = (root / "artifacts").resolve()
    if artifacts not in resolved.parents:
        raise ValueError(f"{label} must be under repository artifacts")
    if resolved.exists():
        raise FileExistsError(f"{label} already exists: {resolved}")
    return resolved


def _generator_source_snapshot(
    repository_root: Path,
    source_paths: tuple[Path, ...] | None = None,
) -> tuple[tuple[dict[str, str | bytes], ...], str]:
    root = repository_root.resolve()
    rows = []
    selected_paths = GENERATOR_SOURCE_PATHS if source_paths is None else source_paths
    for relative in sorted(selected_paths, key=lambda path: path.as_posix()):
        source = (root / relative).resolve()
        if root not in source.parents or not source.is_file():
            raise FileNotFoundError(f"generator source is missing: {relative.as_posix()}")
        payload = source.read_bytes()
        rows.append({
            "path": relative.as_posix(),
            "sha256": _sha256_bytes(payload),
            "payload": payload,
        })
    identity_rows = [
        {"path": str(row["path"]), "sha256": str(row["sha256"])} for row in rows
    ]
    return tuple(rows), _sha256_bytes(canonical_json_bytes(identity_rows))


def _generation_config(
    dataset_seed: int,
    generator_source_snapshot: tuple[dict[str, str | bytes], ...],
    generator_source_bundle_sha256: str,
    *,
    preset: str = "smoke",
    purpose: str = PURPOSE,
    acceptance_scope: str | None = None,
    coverage_protocol_path: str | None = None,
    coverage_protocol_sha256: str | None = None,
) -> dict[str, object]:
    if isinstance(dataset_seed, bool) or not isinstance(dataset_seed, int) or dataset_seed < 0:
        raise ValueError("dataset seed must be a non-negative integer")
    return {
        "schema": CONFIG_SCHEMA,
        "dataset_seed": dataset_seed,
        "preset": preset,
        "split": "test",
        # This generator proves deterministic reserve plumbing only. A real
        # sealed acceptance corpus must provide a separately reviewed scope and
        # coverage protocol through the policy registration boundary.
        "purpose": purpose,
        "acceptance_scope": acceptance_scope,
        "coverage_protocol_path": coverage_protocol_path,
        "coverage_protocol_sha256": coverage_protocol_sha256,
        "generator_source_paths": [str(row["path"]) for row in generator_source_snapshot],
        "generator_source_sha256": [str(row["sha256"]) for row in generator_source_snapshot],
        "generator_source_bundle_sha256": generator_source_bundle_sha256,
        "environment": {
            "pillow_version": package_version("Pillow"),
            "python_version": platform.python_version(),
            "zlib_version": zlib.ZLIB_VERSION,
        },
        "synthetic_only": True,
        "private_data": False,
        "training_permitted": False,
        "production_approval": False,
    }


def _render_cases(specs, dataset_seed: int):
    scenes = _build_scenes(specs, dataset_seed, require_complete_style_catalog=True)
    selected = []
    for scene, spec in zip(scenes, specs, strict=True):
        if _scene_split(scene) != "test":
            continue
        image, annotation, marker_mask = render_scene(scene)
        if spec.output_mode == "RGBA":
            image = image.convert("RGBA")
            alpha = Image.linear_gradient("L").resize(image.size)
            alpha = alpha.point(lambda value: 224 + ((value * 31) // 255))
            image.putalpha(alpha)
        _validate_rendered_case(scene, annotation, marker_mask)
        selected.append({
            "scene.json": canonical_json_bytes(scene),
            "image.png": png_bytes(image),
            "annotation.json": canonical_json_bytes(annotation),
            "marker-mask.png": png_bytes(marker_mask),
        })
    if not selected:
        raise RuntimeError("synthetic generator produced no test-family cases")
    return tuple(selected)


def _test_cases(dataset_seed: int):
    return _render_cases(PRESETS["smoke"], dataset_seed)


def _write_zip_entry(archive: zipfile.ZipFile, name: str, payload: bytes) -> None:
    info = zipfile.ZipInfo(name, _ZIP_TIMESTAMP)
    info.create_system = 3
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o600 << 16
    archive.writestr(info, payload, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def _archive_bytes(
    config: dict[str, object],
    generator_source_snapshot: tuple[dict[str, str | bytes], ...],
    cases=None,
) -> tuple[bytes, int]:
    if cases is None:
        cases = _test_cases(int(config["dataset_seed"]))
    rows = []
    payload_rows = []
    case_identity_sha256 = []
    stream = BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        source_rows = []
        for index, source in enumerate(generator_source_snapshot):
            archive_name = f"sources/{index:04d}.bin"
            _write_zip_entry(archive, archive_name, bytes(source["payload"]))
            source_rows.append({
                "path": str(source["path"]),
                "sha256": str(source["sha256"]),
                "archive_path": archive_name,
            })
        source_manifest = {
            "schema": SOURCE_SNAPSHOT_SCHEMA,
            "source_bundle_sha256": config["generator_source_bundle_sha256"],
            "sources": source_rows,
        }
        source_manifest_payload = canonical_json_bytes(source_manifest)
        _write_zip_entry(archive, "source-snapshot.json", source_manifest_payload)
        for index, payloads in enumerate(cases):
            file_hashes = {}
            for name in sorted(payloads):
                payload = payloads[name]
                archive_name = f"cases/{index:04d}/{name}"
                _write_zip_entry(archive, archive_name, payload)
                file_hashes[name] = _sha256_bytes(payload)
                payload_rows.append({"path": archive_name, "sha256": file_hashes[name]})
            rows.append({"ordinal": index, "files": file_hashes})
            scene = json.loads(payloads["scene.json"])
            case_identity_sha256.append(_sha256_bytes(canonical_json_bytes({
                "scene_id": scene["scene_id"],
                "seed": scene["seed"],
            })))
        manifest = {
            "schema": ARCHIVE_SCHEMA,
            "generation_config_sha256": _sha256_bytes(canonical_json_bytes(config)),
            "generator_source_bundle_sha256": config["generator_source_bundle_sha256"],
            "source_snapshot_manifest_sha256": _sha256_bytes(source_manifest_payload),
            "split": "test",
            "purpose": config["purpose"],
            "acceptance_scope": config["acceptance_scope"],
            "coverage_protocol_sha256": config["coverage_protocol_sha256"],
            "payload_bundle_sha256": _sha256_bytes(
                canonical_json_bytes(payload_rows)
            ),
            "case_identity_sha256": case_identity_sha256,
            "case_count": len(cases),
            "cases": rows,
            "synthetic_only": True,
            "private_data": False,
        }
        _write_zip_entry(archive, "manifest.json", canonical_json_bytes(manifest))
    return stream.getvalue(), len(cases)


def _prepare(
    dataset_seed: int,
    config_output: Path,
    archive_output: Path,
    *,
    repository_root: Path,
    source_paths: tuple[Path, ...] | None,
    preset: str,
    purpose: str,
    acceptance_scope: str | None,
    coverage_protocol_path: str | None,
    coverage_protocol_sha256: str | None,
    cases_factory=None,
) -> dict[str, object]:
    config_path = _artifact_output(repository_root, config_output, "generation config")
    archive_path = _artifact_output(repository_root, archive_output, "reserve archive")
    if config_path == archive_path:
        raise ValueError("generation config and reserve archive paths must differ")
    generator_source_snapshot, generator_source_bundle_sha256 = (
        _generator_source_snapshot(repository_root, source_paths)
    )
    config = _generation_config(
        dataset_seed,
        generator_source_snapshot,
        generator_source_bundle_sha256,
        preset=preset,
        purpose=purpose,
        acceptance_scope=acceptance_scope,
        coverage_protocol_path=coverage_protocol_path,
        coverage_protocol_sha256=coverage_protocol_sha256,
    )
    config_payload = canonical_json_bytes(config)
    cases = (
        cases_factory(config, generator_source_snapshot)
        if cases_factory is not None else None
    )
    archive_result = (
        _archive_bytes(config, generator_source_snapshot)
        if cases is None else _archive_bytes(config, generator_source_snapshot, cases)
    )
    archive_payload, case_count = archive_result
    _, confirmed_source_bundle_sha256 = (
        _generator_source_snapshot(repository_root, source_paths)
    )
    if confirmed_source_bundle_sha256 != generator_source_bundle_sha256:
        raise RuntimeError("generator sources changed during reserve preparation")
    config_path.parent.mkdir(parents=True, exist_ok=True)
    archive_path.parent.mkdir(parents=True, exist_ok=True)
    config_created = False
    archive_created = False
    try:
        with config_path.open("xb") as stream:
            config_created = True
            stream.write(config_payload)
        with archive_path.open("xb") as stream:
            archive_created = True
            stream.write(archive_payload)
    except Exception:
        if config_created:
            config_path.unlink(missing_ok=True)
        if archive_created:
            archive_path.unlink(missing_ok=True)
        raise
    return {
        "config_path": config_path.relative_to(repository_root.resolve()).as_posix(),
        "config_sha256": _sha256_bytes(config_payload),
        "archive_path": archive_path.relative_to(repository_root.resolve()).as_posix(),
        "archive_sha256": _sha256_bytes(archive_payload),
        "archive_byte_count": len(archive_payload),
        "case_count": case_count,
        "purpose": purpose,
        "acceptance_ready": False,
        "generator_source_paths": [
            str(source["path"]) for source in generator_source_snapshot
        ],
        "generator_source_sha256": [
            str(source["sha256"]) for source in generator_source_snapshot
        ],
        "generator_source_bundle_sha256": generator_source_bundle_sha256,
    }


def prepare_reserve(
    dataset_seed: int,
    config_output: Path,
    archive_output: Path,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> dict[str, object]:
    return _prepare(
        dataset_seed,
        config_output,
        archive_output,
        repository_root=repository_root,
        source_paths=None,
        preset="smoke",
        purpose=PURPOSE,
        acceptance_scope=None,
        coverage_protocol_path=None,
        coverage_protocol_sha256=None,
    )


def prepare_acceptance_reserve(
    dataset_seed: int,
    config_output: Path,
    archive_output: Path,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> dict[str, object]:
    """Prepare one registration-ready archive for the fixed Goal 22 scope."""

    load_supported_protocol(repository_root)
    coverage: dict[str, object] = {}

    def build_cases(config, source_snapshot):
        nonlocal coverage
        cases = _render_cases(acceptance_case_specs(), dataset_seed)
        source_rows = [
            {"path": str(row["path"]), "sha256": str(row["sha256"])}
            for row in source_snapshot
        ]
        protocol_payload = next(
            bytes(row["payload"])
            for row in source_snapshot
            if row["path"] == PROTOCOL_PATH.as_posix()
        )
        semantic_cases = []
        for case in cases:
            with Image.open(BytesIO(case["image.png"])) as image:
                image_mode = image.mode
            semantic_cases.append({
                "scene": json.loads(case["scene.json"]),
                "annotation": json.loads(case["annotation.json"]),
                "image_mode": image_mode,
            })
        coverage = validate_acceptance_cases(
            config, source_rows, protocol_payload, semantic_cases
        )
        verify_acceptance_font_dependencies(semantic_cases)
        return cases

    result = _prepare(
        dataset_seed,
        config_output,
        archive_output,
        repository_root=repository_root,
        source_paths=ACCEPTANCE_GENERATOR_SOURCE_PATHS,
        preset=ACCEPTANCE_PRESET,
        purpose=ACCEPTANCE_PURPOSE,
        acceptance_scope=ACCEPTANCE_SCOPE,
        coverage_protocol_path=PROTOCOL_PATH.as_posix(),
        coverage_protocol_sha256=PROTOCOL_SHA256,
        cases_factory=build_cases,
    )
    result.update({
        "acceptance_scope": ACCEPTANCE_SCOPE,
        "coverage_protocol_path": PROTOCOL_PATH.as_posix(),
        "coverage_protocol_sha256": PROTOCOL_SHA256,
        "coverage": coverage,
        "registration_ready": True,
    })
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository-root", type=Path, default=REPOSITORY_ROOT)
    parser.add_argument("--dataset-seed", type=int, required=True)
    parser.add_argument("--config-output", type=Path, required=True)
    parser.add_argument("--archive-output", type=Path, required=True)
    args = parser.parse_args()
    result = prepare_reserve(
        args.dataset_seed,
        args.config_output,
        args.archive_output,
        repository_root=args.repository_root.resolve(),
    )
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()


__all__ = [
    "ARCHIVE_SCHEMA",
    "CONFIG_SCHEMA",
    "GENERATOR_SOURCE_PATHS",
    "PURPOSE",
    "SOURCE_SNAPSHOT_SCHEMA",
    "prepare_acceptance_reserve",
    "prepare_reserve",
]
