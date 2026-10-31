# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Strict public-synthetic corpus admission and V5 component encoding."""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import re
import stat
from typing import Any

import numpy as np
from PIL import Image
from PIL import __version__ as PILLOW_VERSION
from ml.synthetic.io import canonical_json_bytes

from ml.ocr.component_ensemble_v5.dataset import CANVAS_HEIGHT, CANVAS_WIDTH, isolate_glyphs
from ml.ocr.component_ensemble_v5.protocol import (
    ALPHABET, GLYPH_WIDTH, REJECT_CLASS_INDEX, STRUCTURAL_REJECT_MINIMUM_HEIGHT_RATIO,
)
from ml.ocr.synthetic_reject_glyph_v1.generate import inspect_crop as inspect_reject_crop
from ml.ocr.synthetic_reject_glyph_v1.generate import render_phrase as render_reject_phrase
from ml.synthetic.public_fonts_v1 import fonts as public_fonts

from .protocol import (
    ALPHABET as FULL_ALPHABET,
    CLASS_COUNT,
    ENCODED_SHAPE,
    POSITIVE_ALPHABET,
    POSITIVE_SCHEMA,
    POSITIVE_V5_ADAPTER_SCHEMA,
    REJECT_SCHEMA,
    ROOT,
    V5_SOURCE_HASHES,
)

_SHA = re.compile(r"^[0-9a-f]{64}$")
_V3_NO_PRIVATE_FLAGS = ("private_data_accessed", "sealed_data_accessed", "reserved_data_accessed")
_NUMERIC_ONLY = re.compile(r"^-?\d+(?:\.\d+)?$", re.ASCII)
_REJECT_ONLY_NONNUMERIC = re.compile(r"^[A-Za-z]+(?: [A-Za-z]+)*$", re.ASCII)


def _has_prohibited_scope_segment(path: Path) -> bool:
    return any(part.lower() in {"private", "sealed", "test", "tests"} for part in path.parts)


class CorpusError(ValueError):
    """A corpus or sample does not satisfy the immutable V5 contract."""


@dataclass(frozen=True)
class SplitArrays:
    features: np.ndarray
    labels: np.ndarray
    sample_ids: tuple[str, ...]
    source_groups: tuple[str, ...]
    font_ids: tuple[str, ...]


@dataclass(frozen=True)
class CorpusPair:
    train: SplitArrays
    validation: SplitArrays
    input_bundle_sha256: str
    source_bindings: dict[str, str]


def sha256_bytes(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def sha256_file(path: Path) -> str:
    digest = sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _is_reparse(path: Path) -> bool:
    try:
        info = path.lstat()
    except FileNotFoundError:
        return False
    attrs = getattr(info, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return path.is_symlink() or bool(attrs & reparse_flag)


def project_file(relative_path: str) -> Path:
    """Resolve only an existing, non-reparse project file under E root."""
    if not isinstance(relative_path, str) or not relative_path or "\\" in relative_path:
        raise CorpusError("registered paths must be normalized project-relative POSIX paths")
    relative = Path(relative_path)
    if relative.is_absolute() or any(part in ("", ".", "..") for part in relative.parts):
        raise CorpusError("registered path is not a safe relative path")
    if _has_prohibited_scope_segment(relative):
        raise CorpusError("registered path names a prohibited private/sealed/test scope")
    candidate = ROOT.joinpath(*relative.parts)
    resolved_root = ROOT.resolve(strict=True)
    lexical = candidate
    while lexical != ROOT:
        if _is_reparse(lexical):
            raise CorpusError("registered path traverses a reparse point")
        lexical = lexical.parent
    resolved = candidate.resolve(strict=True)
    if resolved_root not in resolved.parents:
        raise CorpusError("registered path escaped the E project")
    cursor = resolved
    while cursor != resolved_root:
        if _is_reparse(cursor):
            raise CorpusError("registered path traverses a reparse point")
        cursor = cursor.parent
    if not resolved.is_file():
        raise CorpusError("registered input is not a file")
    return resolved


def _artifact_root(relative_path: str) -> Path:
    if not isinstance(relative_path, str) or "\\" in relative_path:
        raise CorpusError("corpus root must be a normalized project-relative path")
    relative = Path(relative_path)
    if relative.is_absolute() or any(part in ("", ".", "..") for part in relative.parts):
        raise CorpusError("corpus root path is not relative")
    if _has_prohibited_scope_segment(relative):
        raise CorpusError("registered corpus root names a prohibited data scope")
    lexical_root = ROOT.joinpath(*relative.parts)
    root = lexical_root.resolve(strict=True)
    root_base = ROOT.resolve(strict=True)
    if root_base not in root.parents or not root.is_dir() or "artifacts" not in root.parts:
        raise CorpusError("registered corpus root must be a project artifacts directory")
    lexical = lexical_root
    while lexical != ROOT:
        if _is_reparse(lexical):
            raise CorpusError("corpus root traverses a reparse point")
        lexical = lexical.parent
    cursor = root
    while cursor != root_base:
        if _is_reparse(cursor):
            raise CorpusError("corpus root traverses a reparse point")
        cursor = cursor.parent
    return root


def _artifact_child(base: Path, relative_path: str) -> Path:
    if not isinstance(relative_path, str) or not relative_path or "\\" in relative_path:
        raise CorpusError("artifact member path must be normalized relative POSIX text")
    relative = Path(relative_path)
    if relative.is_absolute() or any(part in ("", ".", "..") for part in relative.parts):
        raise CorpusError("artifact member path is not relative")
    if _has_prohibited_scope_segment(relative):
        raise CorpusError("artifact member names a prohibited private/sealed/test scope")
    candidate = base.joinpath(*relative.parts)
    resolved_base = base.resolve(strict=True)
    lexical = candidate
    while lexical != base:
        if _is_reparse(lexical):
            raise CorpusError("artifact member traverses a reparse point")
        lexical = lexical.parent
    resolved = candidate.resolve(strict=True)
    if resolved_base not in resolved.parents:
        raise CorpusError("artifact member escaped its registered corpus root")
    cursor = resolved
    while cursor != resolved_base:
        if _is_reparse(cursor):
            raise CorpusError("artifact member traverses a reparse point")
        cursor = cursor.parent
    if not resolved.is_file():
        raise CorpusError("artifact member is not a file")
    return resolved


def verify_v5_sources() -> dict[str, str]:
    actual: dict[str, str] = {}
    for relative, expected in V5_SOURCE_HASHES.items():
        path = project_file(relative)
        digest = sha256_file(path)
        if digest != expected:
            raise CorpusError(f"frozen V5 source hash differs: {relative}")
        actual[relative] = digest
    return actual


def _read_json(relative_path: str, expected_sha256: str) -> dict[str, Any]:
    if not _SHA.fullmatch(expected_sha256):
        raise CorpusError("manifest SHA-256 is malformed")
    path = project_file(relative_path)
    payload = path.read_bytes()
    if sha256_bytes(payload) != expected_sha256:
        raise CorpusError("registered manifest hash differs")
    try:
        value = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise CorpusError("registered manifest is not valid UTF-8 JSON") from error
    if not isinstance(value, dict):
        raise CorpusError("manifest root must be an object")
    return value


def _validate_corpus_entry(entry: object, name: str) -> dict[str, Any]:
    if not isinstance(entry, dict) or set(entry) != {"artifact_root", "manifest_path", "manifest_sha256"}:
        raise CorpusError(f"registered {name} corpus identity is incomplete")
    if not all(isinstance(entry.get(key), str) for key in entry):
        raise CorpusError(f"registered {name} corpus paths/hashes are malformed")
    root = Path(entry["artifact_root"])
    manifest = Path(entry["manifest_path"])
    if (root.is_absolute() or manifest.is_absolute() or ".." in root.parts or ".." in manifest.parts or
            root.parts[:1] != ("artifacts",) or manifest != root / "manifest.json" or
            _has_prohibited_scope_segment(root) or _has_prohibited_scope_segment(manifest)):
        raise CorpusError(f"registered {name} corpus path is outside public artifact scope")
    _require_sha(entry["manifest_sha256"], f"{name} corpus manifest hash")
    return entry


def _require_sha(value: object, name: str) -> str:
    if not isinstance(value, str) or not _SHA.fullmatch(value):
        raise CorpusError(f"{name} must be a lowercase SHA-256")
    return value


def numeric_class_labels(text: str) -> tuple[int, ...]:
    if (not isinstance(text, str) or len(text) > 8 or not _NUMERIC_ONLY.fullmatch(text) or
            any(character not in POSITIVE_ALPHABET for character in text)):
        raise CorpusError("numeric supervision must be nonempty V5 digits/dot/minus without percent")
    return tuple(ALPHABET.index(character) for character in text)


def is_nonnumeric_reject_text(text: str) -> bool:
    return isinstance(text, str) and _REJECT_ONLY_NONNUMERIC.fullmatch(text) is not None


def encode_positive_components(raster: np.ndarray, text: str, sample_id: str) -> tuple[tuple[np.ndarray, int], ...]:
    labels = numeric_class_labels(text)
    glyphs = isolate_glyphs(raster)
    _check_runtime_eligible(glyphs, sample_id)
    if len(glyphs) != len(labels):
        raise CorpusError(f"V5 components do not align one-to-one with positive text: {sample_id}")
    return tuple(zip(glyphs, labels, strict=True))


def encode_reject_components(raster: np.ndarray, text: str, sample_id: str) -> tuple[tuple[np.ndarray, int], ...]:
    if not is_nonnumeric_reject_text(text):
        raise CorpusError("reject text contains V5 numeric alphabet or unsupported mixed characters")
    glyphs = isolate_glyphs(raster)
    _check_runtime_eligible(glyphs, sample_id)
    return tuple((glyph, REJECT_CLASS_INDEX) for glyph in glyphs)


def _load_gray_v5_raster(base: Path, relative_path: str, expected_sha: str,
                         expected_pixel_sha: str | None = None) -> np.ndarray:
    path = _artifact_child(base, relative_path)
    # Hash and decode the same immutable byte buffer. Hash-then-reopen would
    # allow a path swap between verification and image decoding.
    try:
        payload = path.read_bytes()
    except OSError as error:
        raise CorpusError("V5 component raster cannot be read") from error
    if sha256_bytes(payload) != expected_sha:
        raise CorpusError("V5 component raster hash differs")
    try:
        with Image.open(BytesIO(payload)) as source:
            source.load()
            if source.mode != "L" or source.size != (CANVAS_WIDTH, CANVAS_HEIGHT):
                raise CorpusError("V5 component raster must be exact 128x32 grayscale")
            raster = np.asarray(source, dtype=np.uint8).copy()
    except OSError as error:
        raise CorpusError("V5 component raster cannot be decoded") from error
    if raster.shape != (CANVAS_HEIGHT, CANVAS_WIDTH):
        raise CorpusError("V5 raster dimensions differ from production canvas")
    if expected_pixel_sha is not None and sha256_bytes(raster.tobytes(order="C")) != expected_pixel_sha:
        raise CorpusError("decoded V5 raster pixel hash differs")
    return raster


def _height_ratios(glyphs: tuple[np.ndarray, ...]) -> np.ndarray:
    if not glyphs:
        return np.zeros((0,), dtype=np.float32)
    values = np.stack(glyphs).astype(np.float32)
    return values[:, 0, 0, GLYPH_WIDTH]


def _check_runtime_eligible(glyphs: tuple[np.ndarray, ...], sample_id: str) -> None:
    if not glyphs:
        raise CorpusError(f"sample has no runtime component: {sample_id}")
    if len(glyphs) > 8:
        raise CorpusError(f"sample exceeds V5 whole-crop component limit: {sample_id}")
    if np.any(_height_ratios(glyphs) >= STRUCTURAL_REJECT_MINIMUM_HEIGHT_RATIO):
        raise CorpusError(f"sample is short-circuited by V5 structural height guard: {sample_id}")
    if any(tuple(glyph.shape) != ENCODED_SHAPE or not np.isfinite(glyph).all() for glyph in glyphs):
        raise CorpusError(f"V5 component encoder returned an invalid tensor: {sample_id}")


def _verify_artifact_inventory(base: Path, inventory: object) -> dict[str, str]:
    if not isinstance(inventory, dict) or not inventory:
        raise CorpusError("corpus artifact inventory is missing")
    verified: dict[str, str] = {}
    for relative_path, digest in inventory.items():
        digest = _require_sha(digest, "artifact inventory digest")
        path = _artifact_child(base, relative_path)
        if sha256_file(path) != digest:
            raise CorpusError(f"corpus artifact hash differs: {relative_path}")
        verified[relative_path] = digest
    return verified


def _positive_training_rows(manifest: dict[str, Any], registration: dict[str, Any],
                            base: Path, adapter_manifest: dict[str, Any], adapter_base: Path,
                            positive_manifest_sha256: str) -> list[tuple[str, str, np.ndarray, int, str, str]]:
    if manifest.get("schema") != POSITIVE_SCHEMA or manifest.get("preset") != "real_range":
        raise CorpusError("positive input is not the fixed public V3 corpus schema/scope")
    if (manifest.get("synthetic_only") is not True or manifest.get("private_reads") != 0 or
            manifest.get("sealed_reads") != 0 or manifest.get("reserved_test_family_opened") is not False or
            manifest.get("test_scene_count") != 0 or manifest.get("optimizer_steps") != 0 or
            manifest.get("training_approved") is not False or manifest.get("production_approved") is not False):
        raise CorpusError("positive corpus does not explicitly certify restricted-data exclusion")
    if manifest.get("historical_benchmark_independence") != "unproven":
        raise CorpusError("positive corpus test-split status is not closed")
    expected_source_bundle = registration.get("positive_source_bundle")
    source_bundle = manifest.get("source_bundle")
    if not isinstance(expected_source_bundle, dict) or source_bundle != expected_source_bundle:
        raise CorpusError("positive corpus generator source bundle differs from independent registration")
    source_files = source_bundle.get("files") if isinstance(source_bundle, dict) else None
    if not isinstance(source_files, dict) or set(source_bundle) != {"schema", "files", "pillow_version", "jsonschema_version", "font_manifest_sha256"}:
        raise CorpusError("positive corpus source bundle schema differs")
    for relative_path, expected_hash in source_files.items():
        if not isinstance(relative_path, str) or not isinstance(expected_hash, str) or sha256_file(project_file(relative_path)) != expected_hash:
            raise CorpusError(f"positive corpus generator source binding differs: {relative_path}")
    canonical_source = canonical_json_bytes(source_bundle)
    if sha256_bytes(canonical_source) != manifest.get("source_sha256"):
        raise CorpusError("positive corpus source bundle digest differs")
    gate_path = manifest.get("full_gate_preflight")
    gate_sha = _require_sha(manifest.get("full_gate_preflight_sha256"), "positive full-gate preflight hash")
    if not isinstance(gate_path, str) or sha256_file(project_file(gate_path)) != gate_sha:
        raise CorpusError("positive full-gate preflight path/hash differs")
    if (adapter_manifest.get("schema") != POSITIVE_V5_ADAPTER_SCHEMA or
            adapter_manifest.get("scope") != "public_synthetic_train_validation_only" or
            adapter_manifest.get("synthetic_only") is not True or
            adapter_manifest.get("positive_manifest_sha256") != positive_manifest_sha256 or
            adapter_manifest.get("adapter_source_sha256") != registration.get("positive_v5_adapter_sha256") or
            adapter_manifest.get("production_source_bindings") != registration.get("positive_v5_production_source_bindings") or
            adapter_manifest.get("crop_transform") != registration.get("positive_v5_crop_transform") or
            adapter_manifest.get("private_reads") != 0 or adapter_manifest.get("sealed_reads") != 0 or
            adapter_manifest.get("test_reads") != 0 or adapter_manifest.get("training_approved") is not False or
            adapter_manifest.get("production_approved") is not False):
        raise CorpusError("separate V5 positive-crop adapter manifest is not bound to the exact V3 corpus")
    adapter_source_path = registration.get("positive_v5_adapter_path")
    adapter_source_sha = registration.get("positive_v5_adapter_sha256")
    if (not isinstance(adapter_source_path, str) or not isinstance(adapter_source_sha, str) or
            sha256_file(project_file(adapter_source_path)) != adapter_source_sha):
        raise CorpusError("positive crop adapter implementation source hash differs")
    adapter_inventory = _verify_artifact_inventory(adapter_base, adapter_manifest.get("artifact_sha256"))
    adapter_rows = adapter_manifest.get("rows")
    if not isinstance(adapter_rows, list):
        raise CorpusError("positive V5 adapter row inventory is missing")
    adapter_by_id: dict[str, dict[str, Any]] = {}
    for row in adapter_rows:
        if not isinstance(row, dict) or not isinstance(row.get("sample_id"), str) or row["sample_id"] in adapter_by_id:
            raise CorpusError("positive adapter sample IDs are missing or duplicated")
        adapter_by_id[row["sample_id"]] = row
    inventory = _verify_artifact_inventory(base, manifest.get("artifact_sha256"))
    source_cases = manifest.get("source_cases")
    samples = manifest.get("samples")
    if not isinstance(source_cases, list) or not isinstance(samples, list) or not samples:
        raise CorpusError("positive source/sample inventory is missing")
    try:
        from ml.ocr.synthetic_tick_corpus_v3.prepare import verify as verify_v3
        verification = verify_v3(base, positive_manifest_sha256, manifest.get("source_sha256"))
    except Exception as error:
        raise CorpusError("pinned V3 verifier rejected the registered positive corpus") from error
    if not isinstance(verification, dict) or verification.get("training_approved") is not False:
        raise CorpusError("V3 positive verifier returned an unexpected admission result")
    cases = {}
    for case in source_cases:
        if not isinstance(case, dict) or not isinstance(case.get("scene_id"), str):
            raise CorpusError("positive source case identity is invalid")
        for path_key, digest_key in (("source_image", "source_image_sha256"),
                                     ("scene", "scene_sha256"),
                                     ("annotation", "annotation_sha256")):
            source_path = case.get(path_key)
            source_digest = case.get(digest_key)
            if not isinstance(source_path, str) or inventory.get(source_path) != source_digest:
                raise CorpusError("positive source image/scene/annotation is not artifact-bound")
        scene_id = case["scene_id"]
        if scene_id in cases:
            raise CorpusError("positive scene identity is duplicated")
        cases[scene_id] = case

    rows = []
    seen_ids: set[str] = set()
    for sample in samples:
        if not isinstance(sample, dict):
            raise CorpusError("positive sample is not an object")
        sample_id = sample.get("sample_id")
        split = sample.get("split")
        text = sample.get("text")
        if not isinstance(sample_id, str) or not sample_id or sample_id in seen_ids:
            raise CorpusError("positive sample ID is missing or duplicated")
        seen_ids.add(sample_id)
        if split not in ("train", "validation"):
            raise CorpusError("positive sample split is not train/validation")
        try:
            labels_for_text = numeric_class_labels(text)
        except CorpusError as error:
            raise CorpusError("positive supervision is outside the numeric non-percent V5 alphabet") from error
        scene_id = sample.get("scene_id")
        case = cases.get(scene_id)
        if case is None or case.get("split") != split:
            raise CorpusError("positive sample scene does not bind to its split source case")
        if (sample.get("source_image_sha256") != case.get("source_image_sha256") or
                sample.get("source_scene_sha256") != case.get("scene_sha256") or
                sample.get("source_annotation_sha256") != case.get("annotation_sha256") or
                sample.get("seed") != case.get("seed")):
            raise CorpusError("positive sample source hashes/seed differ from its case")
        if sample.get("font_sha256") != case.get("font_sha256"):
            raise CorpusError("positive sample font identity differs from source case")
        families = sample.get("families")
        if not isinstance(families, dict) or families != case.get("families"):
            raise CorpusError("positive sample family identity differs from its source case")
        text_id = sample.get("text_id")
        text_records = case.get("text_records")
        if not isinstance(text_id, str) or not isinstance(text_records, list):
            raise CorpusError("positive sample lacks source text-record identity")
        matches = [record for record in text_records if isinstance(record, dict) and record.get("text_id") == text_id]
        if len(matches) != 1 or any(sample.get(key) != matches[0].get(key) for key in
                                     ("text", "region_id", "panel_id", "crop_xyxy", "crop", "crop_sha256", "glyph_mask_sha256")):
            raise CorpusError("positive text/crop does not match its authenticated source record")
        source_group = sample.get("source_image_sha256")
        font_id = sample.get("font_sha256")
        _require_sha(source_group, "positive source image identity")
        _require_sha(font_id, "positive font identity")

        crop_sha = _require_sha(sample.get("crop_sha256"), "V3 source crop hash")
        crop_path = sample.get("crop")
        if not isinstance(crop_path, str) or inventory.get(crop_path) != crop_sha:
            raise CorpusError("positive source crop is not bound by the V3 inventory")
        component = adapter_by_id.get(sample_id)
        exact_keys = {"sample_id", "source_rgb_crop_sha256", "path", "raster_sha256"}
        if not isinstance(component, dict) or set(component) != exact_keys:
            raise CorpusError("positive sample lacks exact separately authenticated V5 crop adapter row")
        if component.get("source_rgb_crop_sha256") != crop_sha:
            raise CorpusError("positive V5 crop does not bind the V3 source crop")
        raster_path = component.get("path")
        raster_sha = _require_sha(component.get("raster_sha256"), "V5 component raster hash")
        if not isinstance(raster_path, str) or adapter_inventory.get(raster_path) != raster_sha:
            raise CorpusError("V5 component raster is not in the registered adapter artifact inventory")
        raster = _load_gray_v5_raster(adapter_base, raster_path, raster_sha)
        encoded_components = encode_positive_components(raster, text, sample_id)
        if tuple(label for _, label in encoded_components) != labels_for_text:
            raise CorpusError("positive class mapping changed during component encoding")
        for glyph, label in encoded_components:
            rows.append((split, sample_id, glyph, label, source_group, font_id))
    if set(adapter_by_id) != seen_ids:
        raise CorpusError("V5 adapter rows do not exactly cover the admitted positive samples")
    return rows


def _reject_training_rows(manifest: dict[str, Any], registration: dict[str, Any],
                          base: Path) -> list[tuple[str, str, np.ndarray, int, str, str]]:
    if manifest.get("schema") != REJECT_SCHEMA or manifest.get("sample_kind") != "non_numeric_text_reject":
        raise CorpusError("reject input is not the fixed V1 nonnumeric-text schema")
    if (manifest.get("scope") != "public_synthetic_train_validation_only" or
            manifest.get("synthetic_only") is not True or manifest.get("private_reads") != 0 or
            manifest.get("sealed_reads") != 0 or manifest.get("test_reads") != 0 or
            manifest.get("training_approved") is not False or manifest.get("production_approved") is not False):
        raise CorpusError("reject corpus cannot independently grant model or production approval")
    if manifest.get("v5_alphabet") != FULL_ALPHABET or manifest.get("class_index") != REJECT_CLASS_INDEX:
        raise CorpusError("reject class/alphabet binding differs from V5")
    samples = manifest.get("samples")
    if not isinstance(samples, list) or not samples:
        raise CorpusError("reject sample inventory is missing")
    expected_component_source = registration.get("v5_component_source_sha256")
    expected_source_bindings = registration.get("reject_source_bindings")
    if (expected_component_source != V5_SOURCE_HASHES["ml/ocr/component_ensemble_v5/dataset.py"] or
            not isinstance(expected_source_bindings, dict) or manifest.get("source_bindings") != expected_source_bindings):
        raise CorpusError("registration does not pin the V5 component encoder source")
    for relative_path, expected_hash in expected_source_bindings.items():
        if sha256_file(project_file(relative_path)) != expected_hash:
            raise CorpusError(f"registered reject source binding differs: {relative_path}")
    if manifest.get("split_disjointness") != {"family_ids": True, "font_ids": True,
            "font_sha256": True, "phrase_ids": True, "phrase_text": True}:
        raise CorpusError("reject family/font/phrase split disjointness receipt differs")
    declared_families = manifest.get("split_families")
    if not isinstance(declared_families, dict) or set(declared_families) != {"train", "validation"}:
        raise CorpusError("reject corpus split-family declaration is incomplete")
    counts = manifest.get("counts")
    excluded = manifest.get("excluded_samples")
    if (not isinstance(counts, dict) or not isinstance(excluded, list) or
            counts.get("eligible_samples") != len(samples) or
            counts.get("components") != sum(len(row.get("components", [])) for row in samples if isinstance(row, dict)) or
            counts.get("excluded_samples") != len(excluded) or
            counts.get("planned_samples") != counts.get("eligible_samples", -1) + counts.get("excluded_samples", -1)):
        raise CorpusError("reject eligible/excluded/component denominators differ")
    if (counts.get("structural_short_circuit_components") != 0 or
            counts.get("component_count_short_circuit_components") != sum(
                len(row.get("excluded_components", [])) for row in excluded if isinstance(row, dict))):
        raise CorpusError("reject whole-crop preclassifier exclusions are misaccounted")
    for excluded_row in excluded:
        if (not isinstance(excluded_row, dict) or excluded_row.get("class_index") is not None or
                excluded_row.get("components") != [] or
                excluded_row.get("exclusion_reason") != "v5_component_count_limit_before_classifier"):
            raise CorpusError("reject exclusion inventory contains an unsupported/partially labeled sample")
        if any(component.get("reason") != "v5_component_count_limit_before_classifier"
               for component in excluded_row.get("excluded_components", [])):
            raise CorpusError("reject exclusion inventory contains unsupported component causes")
    catalog_path = project_file("ml/ocr/synthetic_reject_glyph_v1/phrases.json")
    catalog_sha = sha256_file(catalog_path)
    if catalog_sha != expected_source_bindings.get("ml/ocr/synthetic_reject_glyph_v1/phrases.json"):
        raise CorpusError("fixed reject phrase catalog source hash differs")
    try:
        catalog = json.loads(catalog_path.read_bytes())
    except (OSError, json.JSONDecodeError) as error:
        raise CorpusError("fixed reject phrase catalog is invalid") from error
    catalog_rows = {(split, row.get("id")): row.get("text") for split in ("train", "validation")
                    for row in catalog.get(split, []) if isinstance(row, dict)}
    renderer_environment = manifest.get("renderer_environment")
    if not isinstance(renderer_environment, dict) or renderer_environment.get("pillow") != PILLOW_VERSION:
        raise CorpusError("reject raster recreation requires the corpus-bound Pillow version")
    try:
        approved_fonts = public_fonts.verify()
    except Exception as error:
        raise CorpusError("pinned public font assets failed verification") from error
    font_source = manifest.get("font_source")
    if (not isinstance(font_source, dict) or
            font_source.get("manifest_sha256") != expected_source_bindings.get("ml/synthetic/public_fonts_v1/manifest.json")):
        raise CorpusError("reject font manifest source binding differs")
    train_family = declared_families["train"]
    validation_family = declared_families["validation"]
    for key in ("phrase_ids", "font_ids"):
        if set(train_family.get(key, [])) & set(validation_family.get(key, [])):
            raise CorpusError(f"reject declared train/validation {key} overlap")
    if train_family.get("id") == validation_family.get("id"):
        raise CorpusError("reject train/validation family IDs overlap")
    rows = []
    seen_ids: set[str] = set()
    for sample in samples:
        if not isinstance(sample, dict):
            raise CorpusError("reject sample is not an object")
        sample_id = sample.get("sample_id")
        split = sample.get("split")
        if not isinstance(sample_id, str) or not sample_id or sample_id in seen_ids:
            raise CorpusError("reject sample ID is missing or duplicated")
        seen_ids.add(sample_id)
        if split not in ("train", "validation"):
            raise CorpusError("reject sample split is not train/validation")
        text = sample.get("text")
        if not is_nonnumeric_reject_text(text):
            raise CorpusError("reject source text contains numeric or mixed-class characters")
        if catalog_rows.get((split, sample.get("phrase_id"))) != text:
            raise CorpusError("reject text does not match its fixed split-specific phrase catalog row")
        if sample.get("class_index") != REJECT_CLASS_INDEX:
            raise CorpusError("reject sample does not bind class 13")
        font = sample.get("font")
        if not isinstance(font, dict):
            raise CorpusError("reject font identity is not bound by the public font registry")
        font_id = _require_sha(font.get("sha256"), "reject font identity")
        if font.get("id") not in declared_families.get(split, {}).get("font_ids", []):
            raise CorpusError("reject font identity is outside its registered split family")
        font_path = font.get("path")
        if not isinstance(font_path, str) or sha256_file(_artifact_child(base, font_path)) != font_id:
            raise CorpusError("reject font bytes do not match the declared font hash")
        public_font = approved_fonts.get(font.get("id"))
        if public_font is None or sha256_file(public_font) != font_id:
            raise CorpusError("reject font is not the pinned public font asset")
        license_info = font_source.get("license")
        if (not isinstance(license_info, dict) or font.get("license_sha256") != license_info.get("sha256") or
                font.get("license_id") != license_info.get("id")):
            raise CorpusError("reject font license bytes are not consistently recorded")
        license_path = license_info.get("path")
        if not isinstance(license_path, str) or sha256_file(_artifact_child(base, license_path)) != license_info.get("sha256"):
            raise CorpusError("reject bundled font license hash differs")
        split_family = sample.get("split_family")
        if not isinstance(split_family, str) or not split_family or declared_families.get(split, {}).get("id") != split_family:
            raise CorpusError("reject sample family is not bound to the declared split")
        source_group = split_family
        raster_path = sample.get("raster_path")
        raster_sha = _require_sha(sample.get("raster_png_sha256"), "reject crop PNG SHA")
        if not isinstance(raster_path, str):
            raise CorpusError("reject crop path is missing")
        text_sha = _require_sha(sample.get("text_sha256"), "reject source text hash")
        if sha256_bytes(text.encode("utf-8")) != text_sha:
            raise CorpusError("reject text hash differs")
        if (sample.get("sample_kind") != "non_numeric_text_reject" or sample.get("segmentation_status") != "eligible" or
                sample.get("exclusion_reason") is not None or sample.get("structural_short_circuit_exclusions") != []):
            raise CorpusError("reject sample is excluded or does not use the eligible crop contract")
        if (sample.get("phrase_source") != "package_phrase_catalog" or
                sample.get("phrase_source_sha256") != manifest.get("source_bindings", {}).get("ml/ocr/synthetic_reject_glyph_v1/phrases.json")):
            raise CorpusError("reject phrase source is not pinned to its package catalog")
        if sample.get("font_manifest_sha256") != manifest.get("font_source", {}).get("manifest_sha256"):
            raise CorpusError("reject font manifest identity differs")
        raster = _load_gray_v5_raster(base, raster_path, raster_sha, sample.get("raster_pixels_sha256"))
        render_spec = sample.get("render_spec")
        if (not isinstance(render_spec, dict) or render_spec.get("schema") != "graphreader.clean-text-render.v1" or
                render_spec.get("mode") != "L" or render_spec.get("canvas") != [128, 32] or
                render_spec.get("background") != 255 or render_spec.get("ink") != 0 or
                render_spec.get("degradation") != "none" or render_spec.get("context") != "isolated_text_only" or
                render_spec.get("position") != "centered_no_clipping" or render_spec.get("supersample") != 1):
            raise CorpusError("reject crop render specification differs from clean synthetic contract")
        try:
            recreated, text_mask, actual_font_size = render_reject_phrase(
                text, public_font, render_spec["requested_font_size_px"],
                ink=render_spec["ink"], background=render_spec["background"])
        except Exception as error:
            raise CorpusError("reject phrase/font cannot be recreated by the pinned clean renderer") from error
        if (actual_font_size != render_spec.get("actual_font_size_px") or
                not np.array_equal(recreated, raster) or
                sha256_bytes(text_mask.tobytes(order="C")) != sample.get("text_mask_sha256")):
            raise CorpusError("reject crop or text mask differs from its fixed source rendering")
        exact_inspection = inspect_reject_crop(recreated, text_mask)
        if (exact_inspection.get("status") != "eligible" or
                len(exact_inspection.get("components", ())) != len(sample.get("components", []))):
            raise CorpusError("reject crop is not eligible under the exact generator/runtime extractor")
        reject_components = encode_reject_components(raster, text, sample_id)
        glyphs = tuple(glyph for glyph, label in reject_components)
        if any(label != REJECT_CLASS_INDEX for _, label in reject_components):
            raise CorpusError("reject component mapping does not assign class 13")
        component_rows = sample.get("components")
        if not isinstance(component_rows, list) or len(component_rows) != len(glyphs):
            raise CorpusError("reject component inventory differs from V5 crop segmentation")
        for component_index, (component, glyph) in enumerate(zip(component_rows, glyphs, strict=True)):
            if not isinstance(component, dict) or component.get("component_index") != component_index:
                raise CorpusError("reject component ordering/identity differs from V5 segmentation")
            if component.get("class_index") != REJECT_CLASS_INDEX:
                raise CorpusError("reject component does not carry class 13")
            encoded = np.asarray(glyph, dtype="<f4")
            expected_bytes = encoded.tobytes(order="C")
            tensor_path = component.get("tensor_path")
            tensor_sha = _require_sha(component.get("encoded_tensor_sha256"), "reject tensor hash")
            if not isinstance(tensor_path, str):
                raise CorpusError("reject tensor path is missing")
            tensor_file = _artifact_child(base, tensor_path)
            tensor_bytes = tensor_file.read_bytes()
            if (sha256_bytes(tensor_bytes) != tensor_sha or tensor_bytes != expected_bytes or
                    component.get("encoded_shape") != list(ENCODED_SHAPE) or component.get("dtype") != "float32-le"):
                raise CorpusError("saved reject tensor does not match V5 production component encoding")
            geometry = np.asarray(glyph, dtype=np.float32)[0, 0, 20:26].tolist()
            if component.get("geometry") != geometry:
                raise CorpusError("saved reject component geometry differs from exact V5 encoding")
            rows.append((split, sample_id, glyph, REJECT_CLASS_INDEX, source_group, font_id))
    return rows


def _validate_split_disjointness(rows: list[tuple[str, str, np.ndarray, int, str, str]]) -> None:
    train = [row for row in rows if row[0] == "train"]
    validation = [row for row in rows if row[0] == "validation"]
    if not train or not validation:
        raise CorpusError("both train and validation must contain eligible components")
    for index, name in ((4, "source identities"), (5, "font identities")):
        train_values = {row[index] for row in train}
        validation_values = {row[index] for row in validation}
        if train_values & validation_values:
            raise CorpusError(f"train/validation {name} overlap")


def load_registered_pair(positive_entry: dict[str, Any], reject_entry: dict[str, Any], registration: dict[str, Any]) -> CorpusPair:
    """Verify registered manifest/crop bytes and return GPU-transfer-ready arrays.

    Positive V3 rows must carry the separately reviewed V5 crop bridge. Existing
    V3 RGB bounding-box crops do not meet this interface and fail closed.
    """
    verify_v5_sources()
    positive_entry = _validate_corpus_entry(positive_entry, "positive")
    reject_entry = _validate_corpus_entry(reject_entry, "reject")
    positive = _read_json(positive_entry["manifest_path"], positive_entry["manifest_sha256"])
    adapter_entry = registration.get("positive_v5_component_corpus")
    adapter_entry = _validate_corpus_entry(adapter_entry, "positive V5 component adapter")
    adapter_manifest = _read_json(adapter_entry["manifest_path"], adapter_entry["manifest_sha256"])
    reject = _read_json(reject_entry["manifest_path"], reject_entry["manifest_sha256"])
    positive_root = _artifact_root(positive_entry["artifact_root"])
    adapter_root = _artifact_root(adapter_entry["artifact_root"])
    reject_root = _artifact_root(reject_entry["artifact_root"])
    positive_rows = _positive_training_rows(positive, registration, positive_root, adapter_manifest,
                                            adapter_root, positive_entry["manifest_sha256"])
    reject_rows = _reject_training_rows(reject, registration, reject_root)
    all_rows = positive_rows + reject_rows
    _validate_split_disjointness(all_rows)
    train_rows = [row for row in all_rows if row[0] == "train"]
    validation_rows = [row for row in all_rows if row[0] == "validation"]

    def arrays(rows: list[tuple[str, str, np.ndarray, int, str, str]]) -> SplitArrays:
        features = np.stack([row[2] for row in rows]).astype(np.float32)
        labels = np.asarray([row[3] for row in rows], dtype=np.int64)
        if features.shape[1:] != ENCODED_SHAPE or labels.shape != (len(rows),):
            raise CorpusError("combined corpus arrays differ from V5 feature/label shapes")
        if labels.size == 0 or np.any(labels < 0) or np.any(labels >= CLASS_COUNT):
            raise CorpusError("combined corpus contains no labels or out-of-range labels")
        return SplitArrays(features, labels, tuple(row[1] for row in rows),
                           tuple(row[4] for row in rows), tuple(row[5] for row in rows))

    train = arrays(train_rows)
    validation = arrays(validation_rows)
    for labels in (train.labels, validation.labels):
        if not np.any(labels == REJECT_CLASS_INDEX):
            raise CorpusError("class-13 reject supervision is missing from a split")
        if not np.any(labels != REJECT_CLASS_INDEX):
            raise CorpusError("numeric positive supervision is missing from a split")
    binding = {
        "positive_manifest_sha256": positive_entry["manifest_sha256"],
        "positive_v5_component_manifest_sha256": adapter_entry["manifest_sha256"],
        "reject_manifest_sha256": reject_entry["manifest_sha256"],
        "positive_v5_adapter_sha256": registration["positive_v5_adapter_sha256"],
        **verify_v5_sources(),
    }
    for source_map in (positive.get("source_bundle", {}).get("files", {}),
                       reject.get("source_bindings", {}),
                       registration.get("positive_v5_production_source_bindings", {})):
        for source_path, digest in source_map.items():
            previous = binding.get(source_path)
            if previous is not None and previous != digest:
                raise CorpusError(f"corpus source bindings disagree: {source_path}")
            binding[source_path] = digest
    bundle_body = json.dumps({
        "source_bindings": binding,
        "positive_rows": len(positive_rows), "reject_rows": len(reject_rows),
        "train_components": len(train.labels), "validation_components": len(validation.labels),
    }, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return CorpusPair(train, validation, sha256_bytes(bundle_body), binding)
