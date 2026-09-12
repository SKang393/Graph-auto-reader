# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic, non-preview synthetic reserve preparation checks."""

from __future__ import annotations

from io import BytesIO
import json
from pathlib import Path
import zipfile

import pytest

from ml.policy import sealed_reserve
from ml.synthetic import prepare_sealed_reserve as prepare
from ml.synthetic import io as synthetic_io
from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.io import canonical_json_bytes, png_bytes
from ml.synthetic.renderer import render_scene


def _synthetic_cases():
    cases = []
    scenes = _build_scenes(
        PRESETS["smoke"], 410, require_complete_style_catalog=True
    )
    for scene in (item for item in scenes if _scene_split(item) == "test"):
        image, annotation, marker_mask = render_scene(scene)
        cases.append({
            "scene.json": canonical_json_bytes(scene),
            "image.png": png_bytes(image),
            "annotation.json": canonical_json_bytes(annotation),
            "marker-mask.png": png_bytes(marker_mask),
        })
        if len(cases) == 2:
            break
    assert len(cases) == 2
    return tuple(cases)


def test_archive_identity_serializer_is_part_of_the_source_snapshot() -> None:
    assert prepare.canonical_json_bytes is synthetic_io.canonical_json_bytes
    assert Path("ml/synthetic/io.py") in prepare.GENERATOR_SOURCE_PATHS
    assert Path("ml/synthetic/io.py") in prepare.ACCEPTANCE_GENERATOR_SOURCE_PATHS


def test_prepare_is_deterministic_and_emits_only_config_and_archive(
    tmp_path: Path, monkeypatch
) -> None:
    source = Path("ml/synthetic/fixed-generator.py")
    (tmp_path / source).parent.mkdir(parents=True)
    (tmp_path / source).write_bytes(b"fixed synthetic generator\n")
    monkeypatch.setattr(prepare, "GENERATOR_SOURCE_PATHS", (source,))
    monkeypatch.setattr(prepare, "_test_cases", lambda _seed: _synthetic_cases())

    first = prepare.prepare_reserve(
        410,
        Path("artifacts/reserve/first.json"),
        Path("artifacts/reserve/first.zip"),
        repository_root=tmp_path,
    )
    second = prepare.prepare_reserve(
        410,
        Path("artifacts/reserve/second.json"),
        Path("artifacts/reserve/second.zip"),
        repository_root=tmp_path,
    )

    assert first["config_sha256"] == second["config_sha256"]
    assert first["archive_sha256"] == second["archive_sha256"]
    assert first["generator_source_bundle_sha256"] == second[
        "generator_source_bundle_sha256"
    ]
    assert first["case_count"] == 2
    assert sorted(
        path.relative_to(tmp_path).as_posix()
        for path in tmp_path.rglob("*") if path.is_file()
    ) == [
        "artifacts/reserve/first.json",
        "artifacts/reserve/first.zip",
        "artifacts/reserve/second.json",
        "artifacts/reserve/second.zip",
        "ml/synthetic/fixed-generator.py",
    ]

    archive_payload = (tmp_path / first["archive_path"]).read_bytes()
    with zipfile.ZipFile(BytesIO(archive_payload)) as archive:
        names = archive.namelist()
        manifest = json.loads(archive.read("manifest.json"))
        source_manifest = json.loads(archive.read("source-snapshot.json"))
    assert "contact-sheet.png" not in names
    assert not any(name.startswith(("images/", "annotations/", "masks/")) for name in names)
    assert all(
        name in {"manifest.json", "source-snapshot.json"}
        or name.startswith(("cases/000", "sources/"))
        for name in names
    )
    assert first["purpose"] == "plumbing_only"
    assert first["acceptance_ready"] is False
    assert source_manifest["source_bundle_sha256"] == first[
        "generator_source_bundle_sha256"
    ]
    assert any(name.startswith("sources/") for name in names)
    assert manifest["case_count"] == 2
    assert manifest["purpose"] == "plumbing_only"
    assert manifest["acceptance_scope"] is None
    assert all(set(case) == {"ordinal", "files"} for case in manifest["cases"])

    registry = Path("docs/reserve.json")
    registry_sha = sealed_reserve.initialize_registry(registry, tmp_path)
    set_id, registry_sha = sealed_reserve.register_reserve_set(
        registry,
        tmp_path,
        expected_registry_sha256=registry_sha,
        expected_generator_config_sha256=first["config_sha256"],
        expected_generator_source_bundle_sha256=first[
            "generator_source_bundle_sha256"
        ],
        expected_archive_sha256=first["archive_sha256"],
        generator_config_path=Path(first["config_path"]),
        archive_path=Path(first["archive_path"]),
    )
    record = sealed_reserve.load_registry(registry, tmp_path)["sets"][0]
    assert record["set_id"] == set_id
    assert record["scope"]["purpose"] == "plumbing_only"
    assert sealed_reserve.acceptance_reserve_counts(
        sealed_reserve.load_registry(registry, tmp_path),
        required_acceptance_scope=prepare.ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=prepare.PROTOCOL_SHA256,
    )["unused"] == 0


def test_prepare_rejects_non_artifact_or_existing_outputs(
    tmp_path: Path, monkeypatch
) -> None:
    monkeypatch.setattr(prepare, "_test_cases", lambda _seed: _synthetic_cases())
    with pytest.raises(ValueError, match="under repository artifacts"):
        prepare.prepare_reserve(
            411,
            Path("outside.json"),
            Path("artifacts/reserve.zip"),
            repository_root=tmp_path,
        )

    existing = tmp_path / "artifacts/existing.json"
    existing.parent.mkdir(parents=True)
    existing.write_bytes(b"preserve me")
    with pytest.raises(FileExistsError, match="already exists"):
        prepare.prepare_reserve(
            411,
            Path("artifacts/existing.json"),
            Path("artifacts/reserve.zip"),
            repository_root=tmp_path,
        )
    assert existing.read_bytes() == b"preserve me"


def test_prepare_does_not_delete_archive_created_by_another_writer(
    tmp_path: Path, monkeypatch
) -> None:
    source = Path("ml/synthetic/fixed-generator.py")
    (tmp_path / source).parent.mkdir(parents=True)
    (tmp_path / source).write_bytes(b"fixed synthetic generator\n")
    monkeypatch.setattr(prepare, "GENERATOR_SOURCE_PATHS", (source,))
    monkeypatch.setattr(prepare, "_test_cases", lambda _seed: _synthetic_cases())
    archive = tmp_path / "artifacts/reserve.zip"
    original_open = Path.open

    def racing_open(path: Path, mode: str = "r", *args, **kwargs):
        if path == archive and mode == "xb":
            archive.write_bytes(b"other writer owns these bytes")
            raise FileExistsError("simulated exclusive-create race")
        return original_open(path, mode, *args, **kwargs)

    monkeypatch.setattr(Path, "open", racing_open)
    with pytest.raises(FileExistsError, match="exclusive-create race"):
        prepare.prepare_reserve(
            412,
            Path("artifacts/reserve.json"),
            Path("artifacts/reserve.zip"),
            repository_root=tmp_path,
        )

    assert archive.read_bytes() == b"other writer owns these bytes"
    assert not (tmp_path / "artifacts/reserve.json").exists()


def test_prepare_rejects_generator_source_change_during_render(
    tmp_path: Path, monkeypatch
) -> None:
    source = Path("ml/synthetic/fixed-generator.py")
    source_path = tmp_path / source
    source_path.parent.mkdir(parents=True)
    source_path.write_bytes(b"fixed synthetic generator\n")
    monkeypatch.setattr(prepare, "GENERATOR_SOURCE_PATHS", (source,))
    monkeypatch.setattr(prepare, "_test_cases", lambda _seed: _synthetic_cases())
    original_archive_bytes = prepare._archive_bytes

    def mutate_after_render(config, snapshot):
        result = original_archive_bytes(config, snapshot)
        source_path.write_bytes(b"changed during render\n")
        return result

    monkeypatch.setattr(prepare, "_archive_bytes", mutate_after_render)
    with pytest.raises(RuntimeError, match="sources changed"):
        prepare.prepare_reserve(
            413,
            Path("artifacts/reserve.json"),
            Path("artifacts/reserve.zip"),
            repository_root=tmp_path,
        )

    assert not (tmp_path / "artifacts/reserve.json").exists()
    assert not (tmp_path / "artifacts/reserve.zip").exists()


def test_acceptance_preparation_uses_explicit_scope_and_keeps_seed_in_identity(
    tmp_path: Path, monkeypatch
) -> None:
    protocol = prepare.PROTOCOL_PATH
    protocol_file = tmp_path / protocol
    protocol_file.parent.mkdir(parents=True)
    protocol_file.write_bytes(b"fixed protocol fixture\n")
    monkeypatch.setattr(prepare, "ACCEPTANCE_GENERATOR_SOURCE_PATHS", (protocol,))
    monkeypatch.setattr(
        prepare, "load_supported_protocol", lambda _root: ({}, b"fixed protocol fixture\n")
    )
    monkeypatch.setattr(prepare, "_render_cases", lambda _specs, _seed: _synthetic_cases())
    monkeypatch.setattr(
        prepare,
        "validate_acceptance_cases",
        lambda _config, _sources, _protocol, _cases: {"case_count": 2},
    )

    first = prepare.prepare_acceptance_reserve(
        700,
        Path("artifacts/acceptance/first.json"),
        Path("artifacts/acceptance/first.zip"),
        repository_root=tmp_path,
    )
    second = prepare.prepare_acceptance_reserve(
        701,
        Path("artifacts/acceptance/second.json"),
        Path("artifacts/acceptance/second.zip"),
        repository_root=tmp_path,
    )

    first_config = json.loads((tmp_path / first["config_path"]).read_bytes())
    assert first["purpose"] == "sealed_acceptance"
    assert first["registration_ready"] is True
    assert first["acceptance_ready"] is False
    assert first["coverage"] == {"case_count": 2}
    assert first_config["acceptance_scope"] == prepare.ACCEPTANCE_SCOPE
    assert first_config["coverage_protocol_sha256"] == prepare.PROTOCOL_SHA256
    assert first["config_sha256"] != second["config_sha256"]
    assert first["archive_sha256"] != second["archive_sha256"]


def test_ocr_prepare_uses_distinct_scope_and_validates_before_writing(tmp_path: Path, monkeypatch) -> None:
    protocol = prepare.ocr_sealed_acceptance
    repository = Path(__file__).resolve().parents[3]
    for relative in protocol.GENERATOR_SOURCE_PATHS:
        target = tmp_path / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes((repository / relative).read_bytes())
    observed = []
    cases = _synthetic_cases()
    def render(specs, seed):
        assert specs == protocol.acceptance_case_specs()
        assert specs[0].presentation["x_label_visibility"] == "visible"
        assert seed == 410
        return cases
    monkeypatch.setattr(prepare, "_render_cases", render)
    monkeypatch.setattr(prepare, "verify_acceptance_font_dependencies", lambda *_args: None)
    def validate(config, rows, payload, semantic_cases, *, snapshot_payloads):
        assert not (tmp_path / "artifacts/reserve/ocr.json").exists()
        assert not (tmp_path / "artifacts/reserve/ocr.zip").exists()
        assert payload == snapshot_payloads[protocol.PROTOCOL_PATH.as_posix()]
        assert {row["path"] for row in rows} == set(snapshot_payloads)
        observed.append(config["acceptance_scope"])
        return {"source_count": len(semantic_cases)}
    monkeypatch.setattr(protocol, "validate_acceptance_cases", validate)
    result = prepare.prepare_ocr_acceptance_reserve(410,
        Path("artifacts/reserve/ocr.json"), Path("artifacts/reserve/ocr.zip"),
        repository_root=tmp_path)
    assert observed == [protocol.ACCEPTANCE_SCOPE]
    assert result["registration_ready"] and result["coverage"] == {"source_count": 2}
    config = json.loads((tmp_path / result["config_path"]).read_bytes())
    assert config["coverage_protocol_path"] == protocol.PROTOCOL_PATH.as_posix()
    assert config["coverage_protocol_sha256"] == protocol.PROTOCOL_SHA256
    assert config["acceptance_scope"] != prepare.ACCEPTANCE_SCOPE
