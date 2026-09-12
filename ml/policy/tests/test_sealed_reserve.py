# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Synthetic sealed reserve identity and transition safeguards."""

from __future__ import annotations

from copy import deepcopy
from functools import lru_cache
from io import BytesIO
import json
from pathlib import Path
from uuid import NAMESPACE_URL, uuid5
import zipfile

import pytest

from ml.markers.gate_seal import canonical_json_bytes, sha256_bytes, sha256_file
from ml.policy import sealed_reserve as reserve_module
from ml.policy.sealed_reserve import (
    ACCEPTANCE_PURPOSE,
    ARCHIVE_SCHEMA,
    GENERATION_SCHEMA,
    PLUMBING_PURPOSE,
    SOURCE_SNAPSHOT_SCHEMA,
    SealedReserveError,
    acceptance_reserve_counts,
    initialize_registry,
    load_registry,
    record_case_level_disclosure,
    record_sealed_read,
    register_reserve_set,
    reserve_counts,
)
from ml.synthetic import ocr_sealed_acceptance as ocr_protocol
from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.io import png_bytes
from ml.synthetic.renderer import render_scene
from ml.synthetic.sealed_acceptance import (
    ACCEPTANCE_PRESET as GOAL22_ACCEPTANCE_PRESET,
    ACCEPTANCE_SCOPE as GOAL22_ACCEPTANCE_SCOPE,
    PROTOCOL_PATH as GOAL22_PROTOCOL_PATH,
    PROTOCOL_SHA256 as GOAL22_PROTOCOL_SHA256,
)


REGISTRY = Path("docs/synthetic-sealed-reserve.json")
SCOPE = "goal22.marker-center.acceptance"
PROTOCOL = Path("artifacts/reserve/coverage-protocol.json")
SOURCE = Path("ml/synthetic/fixed-generator.py")


def _scene_identity(index: int) -> str:
    return str(uuid5(NAMESPACE_URL, f"graphreader-reserve-fixture-{index}"))


@lru_cache(maxsize=1)
def _base_rendered_case() -> tuple[dict, dict, bytes, bytes]:
    scene = next(
        item
        for item in _build_scenes(
            PRESETS["smoke"], 1000, require_complete_style_catalog=True
        )
        if _scene_split(item) == "test"
    )
    image, annotation, marker_mask = render_scene(scene)
    return scene, annotation, png_bytes(image), png_bytes(marker_mask)


def _zip_entry(archive: zipfile.ZipFile, name: str, payload: bytes) -> None:
    info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    archive.writestr(info, payload)


def _replace_zip_entry(path: Path, name: str, payload: bytes) -> None:
    with zipfile.ZipFile(path, "r") as archive:
        entries = {entry: archive.read(entry) for entry in archive.namelist()}
    entries[name] = payload
    stream = BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        for entry, entry_payload in entries.items():
            _zip_entry(archive, entry, entry_payload)
    path.write_bytes(stream.getvalue())


def _rewrite_scene(path: Path, scene: dict[str, object]) -> None:
    with zipfile.ZipFile(path, "r") as archive:
        entries = {entry: archive.read(entry) for entry in archive.namelist()}
    scene_payload = canonical_json_bytes(scene)
    entries["cases/0000/scene.json"] = scene_payload
    manifest = json.loads(entries["manifest.json"])
    manifest["cases"][0]["files"]["scene.json"] = sha256_bytes(scene_payload)
    annotation = json.loads(entries["cases/0000/annotation.json"])
    annotation["scene_id"] = scene["scene_id"]
    annotation["seed"] = scene["seed"]
    annotation_payload = canonical_json_bytes(annotation)
    entries["cases/0000/annotation.json"] = annotation_payload
    manifest["cases"][0]["files"]["annotation.json"] = sha256_bytes(
        annotation_payload
    )
    manifest["case_identity_sha256"] = [sha256_bytes(canonical_json_bytes({
        "scene_id": scene["scene_id"], "seed": scene["seed"],
    }))]
    manifest["payload_bundle_sha256"] = sha256_bytes(canonical_json_bytes([
        {"path": f"cases/0000/{payload_name}", "sha256": payload_sha}
        for payload_name, payload_sha in sorted(manifest["cases"][0]["files"].items())
    ]))
    entries["manifest.json"] = canonical_json_bytes(manifest)
    stream = BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        for entry, entry_payload in entries.items():
            _zip_entry(archive, entry, entry_payload)
    path.write_bytes(stream.getvalue())


def _rewrite_case_payload(path: Path, name: str, payload: bytes) -> None:
    with zipfile.ZipFile(path, "r") as archive:
        entries = {entry: archive.read(entry) for entry in archive.namelist()}
    archive_name = f"cases/0000/{name}"
    entries[archive_name] = payload
    manifest = json.loads(entries["manifest.json"])
    manifest["cases"][0]["files"][name] = sha256_bytes(payload)
    manifest["payload_bundle_sha256"] = sha256_bytes(canonical_json_bytes([
        {"path": f"cases/0000/{payload_name}", "sha256": payload_sha}
        for payload_name, payload_sha in sorted(manifest["cases"][0]["files"].items())
    ]))
    entries["manifest.json"] = canonical_json_bytes(manifest)
    stream = BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        for entry, entry_payload in entries.items():
            _zip_entry(archive, entry, entry_payload)
    path.write_bytes(stream.getvalue())


def _generation_files(
    root: Path,
    index: int,
    *,
    purpose: str = ACCEPTANCE_PURPOSE,
    scope: str = SCOPE,
    supported_acceptance: bool = False,
    ocr_acceptance: bool = False,
) -> tuple[Path, Path, str, str]:
    source_payload = f"immutable generator source {index}\n".encode()
    source_rows = [{"path": SOURCE.as_posix(), "sha256": sha256_bytes(source_payload)}]
    source_bundle = sha256_bytes(canonical_json_bytes(source_rows))
    supported_acceptance = supported_acceptance or ocr_acceptance
    supported_path = ocr_protocol.PROTOCOL_PATH if ocr_acceptance else GOAL22_PROTOCOL_PATH
    supported_scope = ocr_protocol.ACCEPTANCE_SCOPE if ocr_acceptance else GOAL22_ACCEPTANCE_SCOPE
    supported_preset = ocr_protocol.ACCEPTANCE_PRESET if ocr_acceptance else GOAL22_ACCEPTANCE_PRESET
    protocol_relative = supported_path if supported_acceptance else PROTOCOL
    protocol_path = root / protocol_relative
    protocol_path.parent.mkdir(parents=True, exist_ok=True)
    if supported_acceptance:
        repository_root = Path(__file__).resolve().parents[3]
        protocol_path.write_bytes((repository_root / supported_path).read_bytes())
        scope = supported_scope
    elif not protocol_path.exists():
        protocol_path.write_bytes(b'{"scope":"goal22.marker-center.acceptance"}\n')
    protocol_sha = sha256_file(protocol_path)
    acceptance_scope = scope if purpose == ACCEPTANCE_PURPOSE else None
    coverage_path = protocol_relative.as_posix() if purpose == ACCEPTANCE_PURPOSE else None
    coverage_sha = protocol_sha if purpose == ACCEPTANCE_PURPOSE else None
    config = {
        "schema": GENERATION_SCHEMA,
        "dataset_seed": index,
        "preset": (
            supported_preset if supported_acceptance
            else "acceptance-fixture" if purpose == ACCEPTANCE_PURPOSE
            else "smoke"
        ),
        "split": "test",
        "purpose": purpose,
        "acceptance_scope": acceptance_scope,
        "coverage_protocol_path": coverage_path,
        "coverage_protocol_sha256": coverage_sha,
        "generator_source_paths": [row["path"] for row in source_rows],
        "generator_source_sha256": [row["sha256"] for row in source_rows],
        "generator_source_bundle_sha256": source_bundle,
        "environment": {
            "pillow_version": "test",
            "python_version": "test",
            "zlib_version": "test",
        },
        "synthetic_only": True,
        "private_data": False,
        "training_permitted": False,
        "production_approval": False,
    }
    config_payload = canonical_json_bytes(config)
    source_manifest = {
        "schema": SOURCE_SNAPSHOT_SCHEMA,
        "source_bundle_sha256": source_bundle,
        "sources": [{**source_rows[0], "archive_path": "sources/0000.bin"}],
    }
    source_manifest_payload = canonical_json_bytes(source_manifest)
    base_scene, base_annotation, image_payload, marker_mask_payload = (
        _base_rendered_case()
    )
    scene = deepcopy(base_scene)
    annotation = deepcopy(base_annotation)
    scene["scene_id"] = _scene_identity(index)
    scene["seed"] = index
    annotation["scene_id"] = scene["scene_id"]
    annotation["seed"] = scene["seed"]
    payloads = {
        "annotation.json": canonical_json_bytes(annotation),
        "image.png": image_payload,
        "marker-mask.png": marker_mask_payload,
        "scene.json": canonical_json_bytes(scene),
    }
    payload_rows = [
        {"path": f"cases/0000/{name}", "sha256": sha256_bytes(payload)}
        for name, payload in sorted(payloads.items())
    ]
    case_identity_sha = sha256_bytes(canonical_json_bytes({
        "scene_id": scene["scene_id"], "seed": scene["seed"],
    }))
    manifest = {
        "schema": ARCHIVE_SCHEMA,
        "generation_config_sha256": sha256_bytes(config_payload),
        "generator_source_bundle_sha256": source_bundle,
        "source_snapshot_manifest_sha256": sha256_bytes(source_manifest_payload),
        "split": "test",
        "purpose": purpose,
        "acceptance_scope": acceptance_scope,
        "coverage_protocol_sha256": coverage_sha,
        "payload_bundle_sha256": sha256_bytes(canonical_json_bytes(payload_rows)),
        "case_identity_sha256": [case_identity_sha],
        "case_count": 1,
        "cases": [{
            "ordinal": 0,
            "files": {name: sha256_bytes(payload) for name, payload in payloads.items()},
        }],
        "synthetic_only": True,
        "private_data": False,
    }
    stream = BytesIO()
    with zipfile.ZipFile(stream, "w") as archive:
        _zip_entry(archive, "sources/0000.bin", source_payload)
        _zip_entry(archive, "source-snapshot.json", source_manifest_payload)
        for name, payload in sorted(payloads.items()):
            _zip_entry(archive, f"cases/0000/{name}", payload)
        _zip_entry(archive, "manifest.json", canonical_json_bytes(manifest))
    config_relative = Path(f"artifacts/reserve/set-{index}.json")
    archive_relative = Path(f"artifacts/reserve/set-{index}.zip")
    config_path = root / config_relative
    archive_path = root / archive_relative
    config_path.parent.mkdir(parents=True, exist_ok=True)
    config_path.write_bytes(config_payload)
    archive_path.write_bytes(stream.getvalue())
    return config_relative, archive_relative, source_bundle, protocol_sha


def _workspace(root: Path) -> str:
    return initialize_registry(REGISTRY, root)


def _register(
    root: Path,
    registry_sha256: str,
    index: int,
    *,
    purpose: str = ACCEPTANCE_PURPOSE,
    scope: str = SCOPE,
    supported_acceptance: bool = False,
    ocr_acceptance: bool = False,
) -> tuple[str, str]:
    config, archive, source_bundle, protocol_sha = _generation_files(
        root,
        index,
        purpose=purpose,
        scope=scope,
        supported_acceptance=supported_acceptance,
        ocr_acceptance=ocr_acceptance,
    )
    effective_scope = (ocr_protocol.ACCEPTANCE_SCOPE if ocr_acceptance
                       else GOAL22_ACCEPTANCE_SCOPE if supported_acceptance else scope)
    return register_reserve_set(
        REGISTRY,
        root,
        expected_registry_sha256=registry_sha256,
        expected_generator_config_sha256=sha256_file(root / config),
        expected_generator_source_bundle_sha256=source_bundle,
        expected_archive_sha256=sha256_file(root / archive),
        generator_config_path=config,
        archive_path=archive,
        required_acceptance_scope=(
            effective_scope if purpose == ACCEPTANCE_PURPOSE else None
        ),
        required_coverage_protocol_sha256=(
            protocol_sha if purpose == ACCEPTANCE_PURPOSE else None
        ),
    )


def _read(
    root: Path,
    registry_sha256: str,
    set_id: str,
    revision: str,
    candidate_id: str,
    ordinal: int,
    protocol_sha: str,
    *,
    scope: str = SCOPE,
    disclosures: tuple[str, ...] = (),
) -> str:
    return record_sealed_read(
        REGISTRY,
        root,
        expected_registry_sha256=registry_sha256,
        set_id=set_id,
        revision=revision,
        candidate_id=candidate_id,
        gate_identity_sha256=f"{ordinal + 100:064x}",
        read_binding_sha256=f"{ordinal + 1:064x}",
        required_acceptance_scope=scope,
        required_coverage_protocol_sha256=protocol_sha,
        disclosures=disclosures,
    )


def test_registration_validates_chain_and_retains_immutable_source_snapshot(
    tmp_path: Path,
) -> None:
    registry_sha256 = _workspace(tmp_path)
    set_id, registry_sha256 = _register(
        tmp_path, registry_sha256, 1, purpose=PLUMBING_PURPOSE
    )
    record = load_registry(REGISTRY, tmp_path)["sets"][0]

    live_source = tmp_path / SOURCE
    live_source.parent.mkdir(parents=True)
    live_source.write_bytes(b"later generator revision\n")
    reloaded = load_registry(REGISTRY, tmp_path)["sets"][0]

    assert record["set_id"] == set_id == reloaded["set_id"]
    assert record["chain"]["case_count"] == 1
    assert sha256_file(tmp_path / REGISTRY) == registry_sha256


def test_registry_transition_rejects_concurrent_writer_and_preserves_owner_lock(
    tmp_path: Path,
) -> None:
    registry_sha256 = _workspace(tmp_path)
    config, archive, source_bundle, _ = _generation_files(
        tmp_path, 12, purpose=PLUMBING_PURPOSE
    )
    registry_path = tmp_path / REGISTRY
    lock_path = registry_path.with_name(f".{registry_path.name}.lock")

    with reserve_module._registry_update_lock(registry_path):
        owner_token = lock_path.read_bytes()
        with pytest.raises(SealedReserveError, match="locked by another writer"):
            register_reserve_set(
                REGISTRY, tmp_path, expected_registry_sha256=registry_sha256,
                expected_generator_config_sha256=sha256_file(tmp_path / config),
                expected_generator_source_bundle_sha256=source_bundle,
                expected_archive_sha256=sha256_file(tmp_path / archive),
                generator_config_path=config, archive_path=archive,
            )
        assert lock_path.read_bytes() == owner_token

    assert not lock_path.exists()
    assert sha256_file(registry_path) == registry_sha256


@pytest.mark.parametrize("mutation", ["not_zip", "config_link", "source", "payload"])
def test_registration_rejects_incomplete_or_mismatched_generation_chain(
    tmp_path: Path, mutation: str
) -> None:
    registry_sha256 = _workspace(tmp_path)
    config, archive, source_bundle, _ = _generation_files(
        tmp_path, 1, purpose=PLUMBING_PURPOSE
    )
    if mutation == "not_zip":
        (tmp_path / archive).write_bytes(b"not a zip")
    else:
        name = {
            "config_link": "manifest.json",
            "source": "sources/0000.bin",
            "payload": "cases/0000/image.png",
        }[mutation]
        payload = b'{}\n' if mutation == "config_link" else b"changed payload"
        _replace_zip_entry(tmp_path / archive, name, payload)
    with pytest.raises(SealedReserveError, match="ZIP|source|payload|manifest"):
        register_reserve_set(
            REGISTRY,
            tmp_path,
            expected_registry_sha256=registry_sha256,
            expected_generator_config_sha256=sha256_file(tmp_path / config),
            expected_generator_source_bundle_sha256=source_bundle,
            expected_archive_sha256=sha256_file(tmp_path / archive),
            generator_config_path=config,
            archive_path=archive,
        )


@pytest.mark.parametrize("payload_name", ["image.png", "marker-mask.png"])
def test_registration_rejects_plaintext_png_with_self_consistent_hashes(
    tmp_path: Path, payload_name: str
) -> None:
    registry_sha256 = _workspace(tmp_path)
    config, archive, source_bundle, _ = _generation_files(
        tmp_path, 10, purpose=PLUMBING_PURPOSE
    )
    _rewrite_case_payload(tmp_path / archive, payload_name, b"plain text, not png")

    with pytest.raises(SealedReserveError, match="PNG"):
        register_reserve_set(
            REGISTRY, tmp_path, expected_registry_sha256=registry_sha256,
            expected_generator_config_sha256=sha256_file(tmp_path / config),
            expected_generator_source_bundle_sha256=source_bundle,
            expected_archive_sha256=sha256_file(tmp_path / archive),
            generator_config_path=config, archive_path=archive,
        )


def test_registration_rejects_semantically_mismatched_annotation(
    tmp_path: Path,
) -> None:
    registry_sha256 = _workspace(tmp_path)
    config, archive, source_bundle, _ = _generation_files(
        tmp_path, 11, purpose=PLUMBING_PURPOSE
    )
    _rewrite_case_payload(
        tmp_path / archive,
        "annotation.json",
        canonical_json_bytes({"scene_id": "wrong", "markers": []}),
    )

    with pytest.raises(SealedReserveError, match="annotation|rendered case"):
        register_reserve_set(
            REGISTRY, tmp_path, expected_registry_sha256=registry_sha256,
            expected_generator_config_sha256=sha256_file(tmp_path / config),
            expected_generator_source_bundle_sha256=source_bundle,
            expected_archive_sha256=sha256_file(tmp_path / archive),
            generator_config_path=config, archive_path=archive,
        )


def test_registration_rejects_non_test_family_and_cross_reserve_case_overlap(
    tmp_path: Path,
) -> None:
    registry_sha256 = _workspace(tmp_path)
    first_id, registry_sha256 = _register(
        tmp_path, registry_sha256, 1, purpose=PLUMBING_PURPOSE
    )
    assert first_id

    config, archive, source_bundle, _ = _generation_files(
        tmp_path, 2, purpose=PLUMBING_PURPOSE
    )
    non_test_scene = deepcopy(_base_rendered_case()[0])
    non_test_scene["scene_id"] = _scene_identity(2)
    non_test_scene["seed"] = 2
    for family in non_test_scene["families"].values():
        family["split"] = "train"
    _rewrite_scene(tmp_path / archive, non_test_scene)
    with pytest.raises(SealedReserveError, match="independent test-family"):
        register_reserve_set(
            REGISTRY, tmp_path, expected_registry_sha256=registry_sha256,
            expected_generator_config_sha256=sha256_file(tmp_path / config),
            expected_generator_source_bundle_sha256=source_bundle,
            expected_archive_sha256=sha256_file(tmp_path / archive),
            generator_config_path=config, archive_path=archive,
        )

    config, archive, source_bundle, _ = _generation_files(
        tmp_path, 3, purpose=PLUMBING_PURPOSE
    )
    overlapping_scene = deepcopy(_base_rendered_case()[0])
    overlapping_scene["scene_id"] = _scene_identity(1)
    overlapping_scene["seed"] = 1
    _rewrite_scene(tmp_path / archive, overlapping_scene)
    registry_before = (tmp_path / REGISTRY).read_bytes()
    with pytest.raises(SealedReserveError, match="duplicates"):
        register_reserve_set(
            REGISTRY, tmp_path, expected_registry_sha256=registry_sha256,
            expected_generator_config_sha256=sha256_file(tmp_path / config),
            expected_generator_source_bundle_sha256=source_bundle,
            expected_archive_sha256=sha256_file(tmp_path / archive),
            generator_config_path=config, archive_path=archive,
        )
    assert (tmp_path / REGISTRY).read_bytes() == registry_before


def test_plumbing_and_unrelated_sets_do_not_satisfy_scope_reserve(tmp_path: Path) -> None:
    registry_sha256 = _workspace(tmp_path)
    plumbing_id, registry_sha256 = _register(
        tmp_path, registry_sha256, 80, purpose=PLUMBING_PURPOSE
    )
    registry = load_registry(REGISTRY, tmp_path)
    assert acceptance_reserve_counts(
        registry,
        required_acceptance_scope=GOAL22_ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=GOAL22_PROTOCOL_SHA256,
    )["unused"] == 0
    with pytest.raises(SealedReserveError, match="not compatible"):
        _read(
            tmp_path, registry_sha256, plumbing_id, "revision-1", "P1", 2,
            GOAL22_PROTOCOL_SHA256,
            scope=GOAL22_ACCEPTANCE_SCOPE,
        )


def test_acceptance_registration_rejects_unreviewed_coverage_identity(
    tmp_path: Path,
) -> None:
    registry_sha256 = _workspace(tmp_path)
    config, archive, source_bundle, protocol_sha = _generation_files(tmp_path, 1)
    registry_before = (tmp_path / REGISTRY).read_bytes()
    with pytest.raises(SealedReserveError, match="supported Goal 22 coverage identity"):
        register_reserve_set(
            REGISTRY,
            tmp_path,
            expected_registry_sha256=registry_sha256,
            expected_generator_config_sha256=sha256_file(tmp_path / config),
            expected_generator_source_bundle_sha256=source_bundle,
            expected_archive_sha256=sha256_file(tmp_path / archive),
            generator_config_path=config,
            archive_path=archive,
            required_acceptance_scope=SCOPE,
            required_coverage_protocol_sha256=protocol_sha,
        )
    assert (tmp_path / REGISTRY).read_bytes() == registry_before


def test_supported_acceptance_registration_runs_semantic_validator(
    tmp_path: Path, monkeypatch
) -> None:
    registry_sha256 = _workspace(tmp_path)
    observed = []

    def validate(config, source_rows, protocol_payload, cases):
        observed.append((config, source_rows, protocol_payload, cases))
        return {"case_count": len(cases)}

    monkeypatch.setattr(reserve_module, "validate_acceptance_cases", validate)
    set_id, _ = _register(
        tmp_path,
        registry_sha256,
        71,
        supported_acceptance=True,
    )
    record = load_registry(REGISTRY, tmp_path)["sets"][0]

    assert record["set_id"] == set_id
    assert record["scope"] == {
        "purpose": ACCEPTANCE_PURPOSE,
        "acceptance_scope": GOAL22_ACCEPTANCE_SCOPE,
        "coverage_protocol_path": GOAL22_PROTOCOL_PATH.as_posix(),
        "coverage_protocol_sha256": GOAL22_PROTOCOL_SHA256,
    }
    assert len(observed) == 1
    assert observed[0][0]["preset"] == GOAL22_ACCEPTANCE_PRESET
    assert len(observed[0][3]) == 1


def test_supported_acceptance_read_preserves_two_unused_sets(
    tmp_path: Path, monkeypatch
) -> None:
    monkeypatch.setattr(
        reserve_module, "validate_acceptance_cases", lambda *_arguments: {}
    )
    registry_sha256 = _workspace(tmp_path)
    set_ids = []
    for index in (81, 82, 83):
        set_id, registry_sha256 = _register(
            tmp_path,
            registry_sha256,
            index,
            supported_acceptance=True,
        )
        set_ids.append(set_id)

    registry_sha256 = _read(
        tmp_path,
        registry_sha256,
        set_ids[0],
        "revision-1",
        "P1",
        201,
        GOAL22_PROTOCOL_SHA256,
        scope=GOAL22_ACCEPTANCE_SCOPE,
    )
    counts = acceptance_reserve_counts(
        load_registry(REGISTRY, tmp_path),
        required_acceptance_scope=GOAL22_ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=GOAL22_PROTOCOL_SHA256,
    )
    assert counts["unused"] == 2
    assert counts["reusable"] == 1
    with pytest.raises(SealedReserveError, match="minimum unused reserve"):
        _read(
            tmp_path,
            registry_sha256,
            set_ids[1],
            "revision-2",
            "P1",
            202,
            GOAL22_PROTOCOL_SHA256,
            scope=GOAL22_ACCEPTANCE_SCOPE,
        )


def test_acceptance_counts_reject_unvalidated_acceptance_records() -> None:
    registry = {
        "sets": [{
            "scope": {
                "purpose": ACCEPTANCE_PURPOSE,
                "acceptance_scope": SCOPE,
                "coverage_protocol_sha256": "0" * 64,
            },
            "state": "unused",
        }],
    }
    with pytest.raises(SealedReserveError, match="supported Goal 22 coverage identity"):
        acceptance_reserve_counts(
            registry,
            required_acceptance_scope=SCOPE,
            required_coverage_protocol_sha256="0" * 64,
        )


def test_acceptance_counts_only_exact_supported_scope() -> None:
    registry = {
        "sets": [{
            "scope": {
                "purpose": ACCEPTANCE_PURPOSE,
                "acceptance_scope": GOAL22_ACCEPTANCE_SCOPE,
                "coverage_protocol_path": GOAL22_PROTOCOL_PATH.as_posix(),
                "coverage_protocol_sha256": GOAL22_PROTOCOL_SHA256,
            },
            "state": "unused",
        }],
    }

    assert acceptance_reserve_counts(
        registry,
        required_acceptance_scope=GOAL22_ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=GOAL22_PROTOCOL_SHA256,
    ) == {"unused": 1, "reusable": 0, "revision_limit": 0, "retired": 0}


def test_disclosure_retires_immediately_and_permanently(tmp_path: Path) -> None:
    registry_sha256 = _workspace(tmp_path)
    set_id, registry_sha256 = _register(
        tmp_path, registry_sha256, 1, purpose=PLUMBING_PURPOSE
    )
    registry_sha256 = record_case_level_disclosure(
        REGISTRY, tmp_path, expected_registry_sha256=registry_sha256,
        set_id=set_id, evidence_sha256="e" * 64,
        disclosures=("prediction", "case_identity"),
    )
    record = load_registry(REGISTRY, tmp_path)["sets"][0]
    assert record["state"] == "retired"
    assert record["retirement"]["disclosures"] == ["case_identity", "prediction"]
    with pytest.raises(SealedReserveError, match="already permanently retired"):
        record_case_level_disclosure(
            REGISTRY, tmp_path, expected_registry_sha256=registry_sha256,
            set_id=set_id, evidence_sha256="f" * 64, disclosures=("pixel",),
        )


def test_registry_rejects_forged_reusable_state(tmp_path: Path) -> None:
    registry_sha256 = _workspace(tmp_path)
    _, _ = _register(
        tmp_path, registry_sha256, 1, purpose=PLUMBING_PURPOSE
    )
    path = tmp_path / REGISTRY
    registry = json.loads(path.read_text(encoding="utf-8"))
    registry["sets"][0]["state"] = "reusable"
    path.write_text(json.dumps(registry), encoding="utf-8")
    with pytest.raises(SealedReserveError, match="inconsistent"):
        load_registry(REGISTRY, tmp_path)


def test_reserve_counts_remains_descriptive_across_all_scopes(tmp_path: Path) -> None:
    registry_sha256 = _workspace(tmp_path)
    _, registry_sha256 = _register(
        tmp_path, registry_sha256, 1, purpose=PLUMBING_PURPOSE
    )
    _, _ = _register(
        tmp_path, registry_sha256, 2, purpose=PLUMBING_PURPOSE
    )
    assert reserve_counts(load_registry(REGISTRY, tmp_path)) == {
        "unused": 2, "reusable": 0, "revision_limit": 0, "retired": 0,
    }


def test_ocr_registration_dispatch_and_read_budget_are_scope_specific(tmp_path: Path, monkeypatch) -> None:
    observed = []
    def validate(config, rows, payload, cases, *, snapshot_payloads):
        observed.append((config["acceptance_scope"], len(cases), snapshot_payloads))
        return {}
    monkeypatch.setattr(ocr_protocol, "validate_acceptance_cases", validate)
    monkeypatch.setattr(reserve_module, "validate_acceptance_cases", lambda *_args: {})
    digest = _workspace(tmp_path)
    marker_id, digest = _register(tmp_path, digest, 170, supported_acceptance=True)
    ids = []
    for index in (171, 172, 173):
        identity, digest = _register(tmp_path, digest, index, ocr_acceptance=True)
        ids.append(identity)
    assert len(observed) == 3
    assert all(row[0] == ocr_protocol.ACCEPTANCE_SCOPE and row[1] == 1 and row[2] for row in observed)
    before = (tmp_path / REGISTRY).read_bytes()
    with pytest.raises(SealedReserveError, match="not compatible"):
        _read(tmp_path, digest, marker_id, "ocr-revision", "P1", 800,
              ocr_protocol.PROTOCOL_SHA256, scope=ocr_protocol.ACCEPTANCE_SCOPE)
    assert (tmp_path / REGISTRY).read_bytes() == before
    digest = _read(tmp_path, digest, ids[0], "ocr-revision", "P1", 801,
                   ocr_protocol.PROTOCOL_SHA256, scope=ocr_protocol.ACCEPTANCE_SCOPE)
    counts = acceptance_reserve_counts(load_registry(REGISTRY, tmp_path),
        required_acceptance_scope=ocr_protocol.ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=ocr_protocol.PROTOCOL_SHA256)
    assert counts == {"unused": 2, "reusable": 1, "revision_limit": 0, "retired": 0}
    with pytest.raises(SealedReserveError, match="minimum unused"):
        _read(tmp_path, digest, ids[1], "ocr-revision-2", "P1", 802,
              ocr_protocol.PROTOCOL_SHA256, scope=ocr_protocol.ACCEPTANCE_SCOPE)


def test_ocr_scope_cannot_use_marker_protocol_identity() -> None:
    with pytest.raises(SealedReserveError, match="identity"):
        acceptance_reserve_counts({"sets": []},
            required_acceptance_scope=ocr_protocol.ACCEPTANCE_SCOPE,
            required_coverage_protocol_sha256=GOAL22_PROTOCOL_SHA256)


@pytest.mark.parametrize("protocol", [ocr_protocol, reserve_module.marker_acceptance])
@pytest.mark.parametrize("path", [None, "ml/policy/wrong-coverage.json"])
def test_acceptance_counts_exclude_wrong_protocol_path(protocol, path) -> None:
    registry = {"sets": [{"scope": {
        "purpose": ACCEPTANCE_PURPOSE,
        "acceptance_scope": protocol.ACCEPTANCE_SCOPE,
        "coverage_protocol_path": path,
        "coverage_protocol_sha256": protocol.PROTOCOL_SHA256,
    }, "state": "unused"}]}
    assert acceptance_reserve_counts(registry,
        required_acceptance_scope=protocol.ACCEPTANCE_SCOPE,
        required_coverage_protocol_sha256=protocol.PROTOCOL_SHA256
    ) == {"unused": 0, "reusable": 0, "revision_limit": 0, "retired": 0}
