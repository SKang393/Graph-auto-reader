from __future__ import annotations

from hashlib import sha256
import json
import os
from pathlib import Path
from tempfile import TemporaryDirectory
from types import SimpleNamespace
from unittest import mock

import pytest
import torch

from ml.ocr.gpu_numeric_tick_recognizer_v1 import foundation


def _write(root: Path, name: str, contents: bytes) -> str:
    path = root / "crops" / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(contents)
    return sha256(contents).hexdigest()


def _manifest(crop_hashes: tuple[str, str]) -> dict:
    rows = []
    cases = []
    for (split, scene, image), crop_hash, crop_name in zip((("train", "one", "a" * 64), ("validation", "two", "b" * 64)), crop_hashes, ("train.png", "validation.png"), strict=True):
        families = {axis: split + "-" + axis for axis in foundation.FAMILY_AXES}
        text = "1" if split == "train" else "2"
        text_id, glyph, annotation, box = "text-" + scene, ("e" if split == "train" else "f") * 64, ("1" if split == "train" else "2") * 64, [1, 2, 5, 7]
        cases.append({"scene_id": scene, "split": split, "source_image_sha256": image, "source_annotation_sha256": annotation, "font_sha256": ("c" if split == "train" else "d") * 64, "seed": 1 if split == "train" else 2, "families": families, "text_ids": [text_id], "text_by_id": {text_id: text}, "glyph_mask_by_text_id": {text_id: glyph}, "crop_xyxy_by_text_id": {text_id: box}})
        rows.append({"split": split, "scene_id": scene, "crop": "crops/" + crop_name, "crop_sha256": crop_hash,
                     "source_image_sha256": image, "source_annotation_sha256": annotation, "font_sha256": ("c" if split == "train" else "d") * 64, "seed": 1 if split == "train" else 2, "families": families, "text_id": text_id, "text": text, "glyph_mask_sha256": glyph, "crop_xyxy": box})
    return {"schema": foundation.CORPUS_SCHEMA, "synthetic_only": True, "private_reads": 0, "sealed_reads": 0,
            "samples": rows, "source_cases": cases}


def test_manifest_rejects_hash_tamper_and_split_overlap():
    temp_root = foundation.ROOT / "artifacts" / "test-temp"
    temp_root.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(dir=temp_root) as temp:
        root = Path(temp)
        crop_hashes = (_write(root, "train.png", b"public-train"), _write(root, "validation.png", b"public-validation"))
        manifest = _manifest(crop_hashes)
        manifest_path = root / "manifest.json"
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        pin = sha256(manifest_path.read_bytes()).hexdigest()
        key = root.relative_to(foundation.ROOT).as_posix()
        with mock.patch.object(foundation, "REGISTERED_CORPORA", {key: pin}):
            assert foundation.validate_corpus_manifest(root, pin)["schema"] == foundation.CORPUS_SCHEMA
        (root / "crops" / "train.png").write_bytes(b"tampered")
        with mock.patch.object(foundation, "REGISTERED_CORPORA", {key: pin}), pytest.raises(foundation.TrainingAdmissionBlocked, match="crop hash"):
            foundation.validate_corpus_manifest(root, pin)
        _write(root, "train.png", b"public-train")
        manifest["samples"][1]["source_image_sha256"] = "a" * 64
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with pytest.raises(foundation.TrainingAdmissionBlocked, match="not independently registered"):
            foundation.validate_corpus_manifest(root, pin)


def test_preflight_never_constructs_optimizer():
    temp_root = foundation.ROOT / "artifacts" / "test-temp"
    temp_root.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(dir=temp_root) as temp:
        root = Path(temp)
        crop_hashes = (_write(root, "train.png", b"public-train"), _write(root, "validation.png", b"public-validation"))
        manifest_path = root / "manifest.json"
        manifest_path.write_text(json.dumps(_manifest(crop_hashes)), encoding="utf-8")
        recipe = foundation.ROOT / "ml/ocr/gpu_numeric_tick_recognizer_v1/recipe.json"
        pin = sha256(manifest_path.read_bytes()).hexdigest()
        key = root.relative_to(foundation.ROOT).as_posix()
        with mock.patch.object(foundation, "REGISTERED_CORPORA", {key: pin}), mock.patch.object(foundation, "require_rx6600xt_gpu"), mock.patch.object(torch.optim, "AdamW") as optimizer:
            result = foundation.preflight(root, pin, recipe)
        assert result["optimizer_steps"] == 0 and result["optimizer_constructed"] is False
        optimizer.assert_not_called()


def test_rehashed_semantic_source_and_font_tampering_are_rejected():
    temp_root = foundation.ROOT / "artifacts" / "test-temp"
    temp_root.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(dir=temp_root) as temp:
        root = Path(temp)
        manifest = _manifest((_write(root, "train.png", b"public-train"), _write(root, "validation.png", b"public-validation")))
        manifest["samples"][0]["font_sha256"] = "e" * 64
        path = root / "manifest.json"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        pin = sha256(path.read_bytes()).hexdigest()
        key = root.relative_to(foundation.ROOT).as_posix()
        with mock.patch.object(foundation, "REGISTERED_CORPORA", {key: pin}), pytest.raises(foundation.TrainingAdmissionBlocked, match="source split provenance"):
            foundation.validate_corpus_manifest(root, pin)


def test_rehashed_family_overlap_and_crop_coordinate_tampering_are_rejected():
    temp_root = foundation.ROOT / "artifacts" / "test-temp"
    temp_root.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(dir=temp_root) as temp:
        root = Path(temp)
        manifest = _manifest((_write(root, "train.png", b"public-train"), _write(root, "validation.png", b"public-validation")))
        manifest["samples"][1]["families"]["renderer"] = manifest["samples"][0]["families"]["renderer"]
        manifest["source_cases"][1]["families"]["renderer"] = manifest["source_cases"][0]["families"]["renderer"]
        path = root / "manifest.json"
        path.write_text(json.dumps(manifest), encoding="utf-8")
        pin, key = sha256(path.read_bytes()).hexdigest(), root.relative_to(foundation.ROOT).as_posix()
        with mock.patch.object(foundation, "REGISTERED_CORPORA", {key: pin}), pytest.raises(foundation.TrainingAdmissionBlocked, match="family provenance"):
            foundation.validate_corpus_manifest(root, pin)
        manifest["samples"][1]["families"]["renderer"] = "validation-renderer"
        manifest["source_cases"][1]["families"]["renderer"] = "validation-renderer"
        manifest["samples"][1]["crop_xyxy"] = [0, 0, 0, 1]
        path.write_text(json.dumps(manifest), encoding="utf-8")
        pin = sha256(path.read_bytes()).hexdigest()
        with mock.patch.object(foundation, "REGISTERED_CORPORA", {key: pin}), pytest.raises(foundation.TrainingAdmissionBlocked, match="glyph or crop geometry"):
            foundation.validate_corpus_manifest(root, pin)


def test_gpu_guard_rejects_cpu_wrong_architecture_and_wrong_tensor_device():
    with mock.patch.object(torch.cuda, "is_available", return_value=False):
        with pytest.raises(foundation.TrainingAdmissionBlocked, match="CPU fallback"):
            foundation.require_rx6600xt_gpu()
    props = SimpleNamespace(name="AMD Radeon RX 6600 XT", gcnArchName="gfx9999")
    with mock.patch.object(torch.cuda, "is_available", return_value=True), mock.patch.object(torch.version, "hip", "test"), mock.patch.object(torch.cuda, "get_device_properties", return_value=props):
        with pytest.raises(foundation.TrainingAdmissionBlocked, match="gfx1032"):
            foundation.require_rx6600xt_gpu()
        props.gcnArchName = "gfx1032"
        with pytest.raises(foundation.TrainingAdmissionBlocked, match="all tensors"):
            foundation.require_rx6600xt_gpu(torch.zeros(1))


def test_unregistered_path_is_rejected_before_manifest_open():
    path = foundation.ROOT / "artifacts" / "not-a-registered-corpus"
    with pytest.raises(foundation.TrainingAdmissionBlocked, match="not independently registered"):
        foundation.validate_corpus_manifest(path, "a" * 64)


def test_actual_project_local_symlink_is_rejected_before_open():
    temp_root = foundation.ROOT / "artifacts" / "test-temp"
    temp_root.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(dir=temp_root) as temp:
        root = Path(temp)
        target, link = root / "target", root / "link"
        target.mkdir()
        try:
            os.symlink(target, link, target_is_directory=True)
        except OSError as error:
            pytest.skip(f"Windows link creation unavailable: {error.winerror if hasattr(error, 'winerror') else error}")
        with pytest.raises(foundation.TrainingAdmissionBlocked, match="reparse point"):
            foundation._require_regular_under(link / "manifest.json", root)


def test_actual_public_final5_v2_metadata_fails_closed_without_opening_crops():
    path = foundation.ROOT / "artifacts/synthetic-tick-corpus-v2-20261031-final5/manifest.json"
    if not path.is_file():
        pytest.skip("optional local public final5 manifest is not present")
    assert sha256(path.read_bytes()).hexdigest() == "1a33a741b36aac5cb3d2f933195a7b7ca168084318a4d4c2df05dd80d6793394"
    manifest = json.loads(path.read_text(encoding="utf-8"))
    assert manifest["schema"] == foundation.LEGACY_CORPUS_SCHEMA
    with pytest.raises(foundation.TrainingAdmissionBlocked, match="lacks v3 source text/glyph/crop provenance"):
        foundation.validate_manifest_metadata(manifest)


def test_committed_synthetic_v2_metadata_always_fails_closed():
    manifest = {"schema": foundation.LEGACY_CORPUS_SCHEMA, "synthetic_only": True,
                "private_reads": 0, "sealed_reads": 0, "source_cases": [], "samples": []}
    with pytest.raises(foundation.TrainingAdmissionBlocked, match="lacks v3 source text/glyph/crop provenance"):
        foundation.validate_manifest_metadata(manifest)
