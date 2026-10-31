"""Fail-closed admission checks for a future GPU-only numeric X-tick run.

This module performs no model construction, optimizer construction, optimizer
step, checkpoint write, or corpus generation.  It validates only an already
authenticated public synthetic_tick_corpus_v3 materialization and deliberately
reports training blocked until its font provenance is reviewed.
"""
from __future__ import annotations

from hashlib import sha256
import json
import os
from pathlib import Path
import stat
from typing import Any

import torch
from ml.synthetic.dataset import FAMILY_AXES

ROOT = Path(__file__).resolve().parents[3]
CORPUS_SCHEMA = "graphreader.synthetic-tick-corpus.v3"
LEGACY_CORPUS_SCHEMA = "graphreader.synthetic-tick-corpus.v2"
RECIPE_SCHEMA = "graphreader.gpu-numeric-tick-recognizer.recipe.v1"
DEVICE = torch.device("cuda:0")
# Deliberately empty until an independently reviewed, public-only corpus is
# registered in source control. Caller-supplied paths and hashes are never an
# authority to open corpus bytes.
REGISTERED_CORPORA: dict[str, str] = {}
RECIPE_SHA256 = "4cabd3176772bce57f095773e2e6f4f6930385338769b9a82bfcb32db0c6e501"


class TrainingAdmissionBlocked(ValueError):
    """The foundation intentionally cannot authorize training."""


def _sha256(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _is_reparse_point(path: Path) -> bool:
    """Windows junctions are reparse points even when Path.is_symlink is false."""
    if path.is_symlink():
        return True
    try:
        attributes = os.stat(path, follow_symlinks=False).st_file_attributes
    except (FileNotFoundError, OSError):
        return False
    return bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400))


def _require_regular_under(path: Path, root: Path) -> Path:
    """Reject reparse points on every ancestor and the target before opening it."""
    root, path = root.absolute(), path.absolute()
    if not path.is_relative_to(root):
        raise TrainingAdmissionBlocked("path escapes an allowed root")
    current = root
    if _is_reparse_point(current):
        raise TrainingAdmissionBlocked("allowed root is a reparse point")
    for part in path.relative_to(root).parts:
        current /= part
        if _is_reparse_point(current):
            raise TrainingAdmissionBlocked("corpus path contains a reparse point")
    return path


def _json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise TrainingAdmissionBlocked("authenticated JSON input is unreadable") from error
    if not isinstance(value, dict):
        raise TrainingAdmissionBlocked("authenticated JSON input must be an object")
    return value


def require_rx6600xt_gpu(*tensors: torch.Tensor) -> None:
    """Reject CPU, CUDA ordinal changes, non-HIP, and non-gfx1032 execution."""
    if not torch.cuda.is_available() or not torch.version.hip:
        raise TrainingAdmissionBlocked("RX 6600 XT HIP cuda:0 is required; CPU fallback forbidden")
    props = torch.cuda.get_device_properties(DEVICE)
    if props.name != "AMD Radeon RX 6600 XT" or props.gcnArchName.split(":")[0] != "gfx1032":
        raise TrainingAdmissionBlocked("RX 6600 XT gfx1032 cuda:0 is required")
    if any(not isinstance(tensor, torch.Tensor) or tensor.device != DEVICE for tensor in tensors):
        raise TrainingAdmissionBlocked("all tensors must reside on RX 6600 XT cuda:0")


def _is_hash(value: object) -> bool:
    return isinstance(value, str) and len(value) == 64 and all(char in "0123456789abcdef" for char in value)


def _validate_recipe(path: Path) -> dict[str, Any]:
    path = _require_regular_under(Path(path), ROOT)
    if not path.is_file() or _sha256(path) != RECIPE_SHA256:
        raise TrainingAdmissionBlocked("recipe hash differs")
    recipe = _json(path)
    required = {
        "schema": RECIPE_SCHEMA,
        "scope": "public_synthetic_train_validation_only",
        "device": "cuda:0",
        "task": "numeric_x_tick_recognition",
        "optimizer_construction_allowed": False,
        "optimizer_steps_allowed": False,
        "checkpoint_writes_allowed": False,
        "production_approved": False,
        "sealed_or_private_inputs_allowed": False,
    }
    if any(recipe.get(key) != value for key, value in required.items()):
        raise TrainingAdmissionBlocked("bounded nonproduction recipe differs")
    gpu = recipe.get("required_gpu")
    if gpu != {"name": "AMD Radeon RX 6600 XT", "architecture": "gfx1032", "hip_required": True}:
        raise TrainingAdmissionBlocked("recipe GPU binding differs")
    return recipe


def validate_manifest_metadata(manifest: dict[str, Any]) -> None:
    """Validate metadata only. It intentionally opens no crop or raster bytes."""
    if manifest.get("schema") == LEGACY_CORPUS_SCHEMA:
        raise TrainingAdmissionBlocked("synthetic_tick_corpus_v2 lacks v3 source text/glyph/crop provenance")
    if (manifest.get("schema") != CORPUS_SCHEMA or manifest.get("synthetic_only") is not True or
            manifest.get("private_reads") != 0 or manifest.get("sealed_reads") != 0):
        raise TrainingAdmissionBlocked("corpus scope is not public synthetic only")


def validate_corpus_manifest(corpus_root: Path, expected_manifest_sha256: str) -> dict[str, Any]:
    """Verify public-only split, sample hash, and source/font provenance without tensors."""
    corpus_root = Path(corpus_root).absolute()
    registered_hash = REGISTERED_CORPORA.get(corpus_root.relative_to(ROOT.absolute()).as_posix()) if corpus_root.is_relative_to(ROOT.absolute()) else None
    if registered_hash is None or registered_hash != expected_manifest_sha256:
        raise TrainingAdmissionBlocked("corpus is not independently registered")
    corpus_root = _require_regular_under(corpus_root, ROOT)
    manifest_path = _require_regular_under(corpus_root / "manifest.json", corpus_root)
    if not manifest_path.is_file() or _sha256(manifest_path) != registered_hash:
        raise TrainingAdmissionBlocked("corpus manifest hash differs")
    manifest = _json(manifest_path)
    validate_manifest_metadata(manifest)
    samples = manifest.get("samples")
    source_cases = manifest.get("source_cases")
    if not isinstance(samples, list) or not samples or not isinstance(source_cases, list) or not source_cases:
        raise TrainingAdmissionBlocked("corpus samples or source provenance are absent")
    splits = {"train": set(), "validation": set()}
    crop_splits = {"train": set(), "validation": set()}
    source_by_scene = {case.get("scene_id"): case for case in source_cases if isinstance(case, dict)}
    if len(source_by_scene) != len(source_cases):
        raise TrainingAdmissionBlocked("source case identity differs")
    for sample in samples:
        if not isinstance(sample, dict) or sample.get("split") not in splits:
            raise TrainingAdmissionBlocked("sample split differs")
        relative = sample.get("crop")
        if not isinstance(relative, str) or Path(relative).is_absolute() or ".." in Path(relative).parts:
            raise TrainingAdmissionBlocked("sample crop path escapes corpus")
        crop = _require_regular_under(corpus_root / relative, corpus_root)
        if not crop.is_file() or _sha256(crop) != sample.get("crop_sha256"):
            raise TrainingAdmissionBlocked("sample crop hash differs")
        if not _is_hash(sample.get("source_image_sha256")) or not _is_hash(sample.get("font_sha256")):
            raise TrainingAdmissionBlocked("sample provenance hash differs")
        scene = source_by_scene.get(sample.get("scene_id"))
        required_source = ("split", "source_image_sha256", "source_annotation_sha256", "font_sha256", "seed", "families", "glyph_mask_by_text_id", "crop_xyxy_by_text_id")
        if (not isinstance(scene, dict) or any(key not in scene for key in required_source) or
                scene.get("split") != sample["split"] or
                scene.get("source_image_sha256") != sample.get("source_image_sha256") or
                scene.get("source_annotation_sha256") != sample.get("source_annotation_sha256") or
                scene.get("font_sha256") != sample.get("font_sha256") or
                scene.get("seed") != sample.get("seed") or
                scene.get("families") != sample.get("families") or
                scene.get("text_ids") is None or sample.get("text_id") not in scene.get("text_ids", [])):
            raise TrainingAdmissionBlocked("sample source split provenance differs")
        splits[sample["split"]].add(sample["source_image_sha256"])
        crop_splits[sample["split"]].add(sample["crop_sha256"])
    font_splits = {split: {sample["font_sha256"] for sample in samples if sample["split"] == split} for split in splits}
    family_splits = {split: {axis: set() for axis in FAMILY_AXES} for split in splits}
    alphabet = set("0123456789.-")
    for sample in samples:
        text = sample.get("text")
        scene = source_by_scene[sample["scene_id"]]
        text_by_id = scene.get("text_by_id")
        glyphs, boxes = scene.get("glyph_mask_by_text_id"), scene.get("crop_xyxy_by_text_id")
        box = sample.get("crop_xyxy")
        if (not isinstance(text, str) or not text or not set(text) <= alphabet or
                not isinstance(text_by_id, dict) or text_by_id.get(sample["text_id"]) != text):
            raise TrainingAdmissionBlocked("sample numeric text provenance differs")
        if (not _is_hash(sample.get("glyph_mask_sha256")) or not isinstance(glyphs, dict) or
                glyphs.get(sample["text_id"]) != sample["glyph_mask_sha256"] or
                not isinstance(boxes, dict) or boxes.get(sample["text_id"]) != box or
                not isinstance(box, list) or len(box) != 4 or any(type(value) is not int or value < 0 for value in box) or
                box[0] >= box[2] or box[1] >= box[3]):
            raise TrainingAdmissionBlocked("sample glyph or crop geometry provenance differs")
        family_map = sample["families"]
        if not isinstance(family_map, dict) or set(family_map) != set(FAMILY_AXES):
            raise TrainingAdmissionBlocked("sample family axes differ")
        for axis in FAMILY_AXES:
            value = family_map[axis]
            if not isinstance(value, str) or not value:
                raise TrainingAdmissionBlocked("sample family value differs")
            family_splits[sample["split"]][axis].add(value)
    if (not splits["train"] or not splits["validation"] or splits["train"] & splits["validation"] or
            crop_splits["train"] & crop_splits["validation"]):
        raise TrainingAdmissionBlocked("train/validation source images or crops are not disjoint")
    if (font_splits["train"] & font_splits["validation"] or
            any(family_splits["train"][axis] & family_splits["validation"][axis] for axis in FAMILY_AXES)):
        raise TrainingAdmissionBlocked("train/validation font or family provenance is not disjoint")
    return manifest


def preflight(corpus_root: Path, manifest_sha256: str, recipe_path: Path) -> dict[str, Any]:
    """Validate immutable inputs then fail closed before any optimizer could exist."""
    require_rx6600xt_gpu()
    recipe = _validate_recipe(Path(recipe_path))
    manifest = validate_corpus_manifest(corpus_root, manifest_sha256)
    return {
        "status": "blocked_pending_training_admission",
        "reason": recipe["training_admission"],
        "manifest_sha256": manifest_sha256,
        "sample_count": len(manifest["samples"]),
        "optimizer_constructed": False,
        "optimizer_steps": 0,
        "training_approved": False,
        "production_approved": False,
    }
