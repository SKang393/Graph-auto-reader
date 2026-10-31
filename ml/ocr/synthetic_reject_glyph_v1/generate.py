# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Generate clean, public-only nonnumeric V5 reject glyph examples.

This is data preparation only. It creates no model, optimizer, checkpoint, or
training admission. Every retained component is produced by the pinned V5
`isolate_glyphs` implementation and has class 13 only when the complete source
phrase has no non-space character in the V5 alphabet.
"""
from __future__ import annotations

from hashlib import sha256
import json
import os
from pathlib import Path
import platform
import re
import stat
import sys
from typing import Any

import numpy as np
from PIL import Image, ImageDraw, ImageFont, __version__ as PILLOW_VERSION

from ml.ocr.component_ensemble_v5.dataset import isolate_glyphs
from ml.ocr.component_geometric_v4.dataset import _filtered_foreground
from ml.synthetic.public_fonts_v1 import fonts as public_fonts

from . import protocol


FONT_SIZES_PX = (16, 20, 22)
INK = 0
BACKGROUND = 255
_ASCII_LETTERS = frozenset("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")
_SIZE_DESCENDING = (22, 20, 18, 16, 14)


def _sha_bytes(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _sha_file(path: Path) -> str:
    return _sha_bytes(path.read_bytes())


def _require_regular_under(path: Path, root: Path) -> Path:
    """Reject a source path that is outside its root or crosses a reparse point."""
    root, path = Path(root).absolute(), Path(path).absolute()
    if not path.is_relative_to(root) or ".." in path.relative_to(root).parts:
        raise ValueError("source path escapes its fixed project root")
    current = root
    root_info = os.stat(current, follow_symlinks=False)
    if stat.S_ISLNK(root_info.st_mode) or getattr(root_info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
        raise ValueError("source root is a reparse point")
    for part in path.relative_to(root).parts:
        current /= part
        info = os.stat(current, follow_symlinks=False)
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            raise ValueError("source path includes a symlink or reparse point")
    if not path.is_file():
        raise ValueError("bound source must be a regular file")
    return path


def _duplicate_key_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON object field")
        result[key] = value
    return result


def _json_object(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=_duplicate_key_object)
    if not isinstance(value, dict):
        raise ValueError("expected JSON object")
    return value


def validate_phrase(text: str) -> None:
    """Require a nonempty, ASCII-letter-only phrase with single spaces.

    This strict rule rejects every mixed numeric string, including decimals,
    signs, percent suffixes, digits, punctuation, and non-ASCII substitutions.
    """
    if not isinstance(text, str) or not text or text != text.strip():
        raise ValueError("reject phrase must be nonempty and trimmed")
    if "  " in text or any(character != " " and character not in _ASCII_LETTERS for character in text):
        raise ValueError("reject phrase must contain only ASCII letters and single spaces")
    if any(character in protocol.V5_ALPHABET for character in text):
        raise ValueError("reject phrase contains a V5 alphabet character")
    if not any(character != " " for character in text):
        raise ValueError("reject phrase has no letters")


def _load_phrase_catalog() -> tuple[dict[str, Any], str]:
    phrase_path = _require_regular_under(protocol.PHRASE_CATALOG, protocol.ROOT)
    if _sha_file(phrase_path) != "1c5081cfb4087bb9cb2813f6b4b1158c8651de158e3c89430c417293d0bfeb45":
        raise ValueError("fixed public phrase catalog hash differs")
    catalog = _json_object(phrase_path)
    if catalog.get("schema") != "graphreader.synthetic-reject-phrase-catalog.v1":
        raise ValueError("phrase catalog schema differs")
    if set(catalog) != {"schema", "scope", "train", "validation", "independence_claim"}:
        raise ValueError("phrase catalog fields differ")
    if catalog.get("scope") != "fixed_package_generated_ascii_words_only":
        raise ValueError("phrase catalog scope differs")
    rows_by_split: dict[str, list[dict[str, str]]] = {}
    seen_ids: set[str] = set()
    seen_text: dict[str, set[str]] = {"train": set(), "validation": set()}
    for split in ("train", "validation"):
        rows = catalog.get(split)
        if not isinstance(rows, list) or not rows:
            raise ValueError("phrase split is empty")
        rows_by_split[split] = []
        for row in rows:
            if not isinstance(row, dict) or set(row) != {"id", "text"}:
                raise ValueError("phrase catalog row fields differ")
            phrase_id, text = row.get("id"), row.get("text")
            if not isinstance(phrase_id, str) or not re.fullmatch(r"[a-z][a-z0-9-]*", phrase_id) or phrase_id in seen_ids:
                raise ValueError("phrase identity is invalid or duplicated")
            validate_phrase(text)
            if text in seen_text[split]:
                raise ValueError("duplicate phrase text within split")
            seen_ids.add(phrase_id)
            seen_text[split].add(text)
            rows_by_split[split].append({"id": phrase_id, "text": text})
    if seen_text["train"] & seen_text["validation"]:
        raise ValueError("train and validation phrase text overlaps")
    return {"schema": catalog["schema"], "train": rows_by_split["train"], "validation": rows_by_split["validation"]}, _sha_file(phrase_path)


def _verify_fixed_sources() -> tuple[dict[str, str], dict[str, Path], dict[str, Any]]:
    source_bindings = dict(protocol.V5_SOURCE_SHA256)
    source_bindings["ml/synthetic/public_fonts_v1/manifest.json"] = protocol.FONT_MANIFEST_SHA256
    source_bindings["ml/synthetic/public_fonts_v1/fonts.py"] = protocol.FONT_VERIFIER_SHA256
    for relative, expected in source_bindings.items():
        path = _require_regular_under(protocol.ROOT / relative, protocol.ROOT)
        if _sha_file(path) != expected:
            raise ValueError(f"bound source hash differs: {relative}")
    font_paths = public_fonts.verify()
    font_manifest = _json_object(protocol.ROOT / "ml/synthetic/public_fonts_v1/manifest.json")
    if _sha_file(protocol.ROOT / "ml/synthetic/public_fonts_v1/manifest.json") != protocol.FONT_MANIFEST_SHA256:
        raise ValueError("public font manifest hash differs")
    return source_bindings, font_paths, font_manifest


def _font_metadata(font_manifest: dict[str, Any]) -> tuple[dict[str, dict[str, str]], dict[str, str]]:
    fonts = font_manifest.get("fonts")
    license_record = font_manifest.get("license")
    if not isinstance(fonts, list) or not isinstance(license_record, dict):
        raise ValueError("font manifest inventory is malformed")
    license_path = protocol.ROOT / "ml/synthetic/public_fonts_v1" / license_record.get("file", "")
    if _sha_file(license_path) != "0dab92d0544f7b233403f14b84a663bdbfa746982eda629e7f4f9ffe1b036feb":
        raise ValueError("font license bytes differ")
    result: dict[str, dict[str, str]] = {}
    for row in fonts:
        if not isinstance(row, dict) or row.get("id") not in {"sans", "serif", "mono"}:
            raise ValueError("font row differs")
        path = protocol.ROOT / "ml/synthetic/public_fonts_v1" / row["file"]
        if _sha_file(path) != row.get("sha256"):
            raise ValueError("font bytes differ from public manifest")
        result[row["id"]] = {
            "id": row["id"], "family": row["family"], "path": row["file"],
            "sha256": row["sha256"], "license_id": row["license_id"],
            "license_sha256": license_record["sha256"],
        }
    if set(result) != {"sans", "serif", "mono"}:
        raise ValueError("font split inventory is incomplete")
    license_binding = {
        "id": license_record["id"], "path": license_record["file"],
        "sha256": license_record["sha256"], "source_url": license_record["source_url"],
        "repository": font_manifest["repository"], "source_commit": font_manifest["source_commit"],
    }
    return result, license_binding


def render_phrase(text: str, font_path: Path, font_size_px: int, *, ink: int = INK, background: int = BACKGROUND) -> tuple[np.ndarray, np.ndarray, int]:
    """Render a clean line and a separate text-only mask on a V5-sized canvas."""
    validate_phrase(text)
    if type(font_size_px) is not int or font_size_px not in _SIZE_DESCENDING:
        raise ValueError("font size is outside the fixed render profile")
    if not 0 <= ink < background <= 255:
        raise ValueError("render ink/background values are invalid")
    font_path = _require_pinned_font(font_path)
    selected_size = next((size for size in _SIZE_DESCENDING if size <= font_size_px and _fits(text, font_path, size)), None)
    if selected_size is None:
        raise ValueError("phrase does not fit the fixed crop without clipping")
    font = ImageFont.truetype(str(font_path), selected_size)
    bbox = font.getbbox(text)
    width, height = bbox[2] - bbox[0], bbox[3] - bbox[1]
    if width <= 0 or height <= 0 or width > protocol.CANVAS_WIDTH or height > protocol.CANVAS_HEIGHT:
        raise ValueError("font produced empty or out-of-canvas phrase bounds")
    origin_x = (protocol.CANVAS_WIDTH - width) // 2 - bbox[0]
    origin_y = (protocol.CANVAS_HEIGHT - height) // 2 - bbox[1]
    raster = Image.new("L", (protocol.CANVAS_WIDTH, protocol.CANVAS_HEIGHT), color=background)
    mask = Image.new("L", (protocol.CANVAS_WIDTH, protocol.CANVAS_HEIGHT), color=0)
    ImageDraw.Draw(raster).text((origin_x, origin_y), text, fill=ink, font=font)
    ImageDraw.Draw(mask).text((origin_x, origin_y), text, fill=255, font=font)
    image_array = np.asarray(raster, dtype=np.uint8).copy()
    mask_array = np.asarray(mask, dtype=np.uint8).copy()
    if image_array.shape != (protocol.CANVAS_HEIGHT, protocol.CANVAS_WIDTH) or not mask_array.any():
        raise ValueError("phrase render did not produce visible foreground")
    return image_array, mask_array, selected_size


def _require_pinned_font(font_path: Path) -> Path:
    """Accept only byte-verified font assets from public_fonts_v1."""
    candidate = Path(font_path).resolve(strict=True)
    approved = public_fonts.verify()
    for font_id, approved_path in approved.items():
        approved_path = approved_path.resolve(strict=True)
        if candidate == approved_path:
            row = next((font for font in _json_object(protocol.ROOT / "ml/synthetic/public_fonts_v1/manifest.json")["fonts"]
                        if font["id"] == font_id), None)
            if row is None or _sha_file(candidate) != row["sha256"]:
                break
            return approved_path
    raise ValueError("font must be an exact verified public_fonts_v1 asset")


def _fits(text: str, font_path: Path, font_size_px: int) -> bool:
    font = ImageFont.truetype(str(font_path), font_size_px)
    bbox = font.getbbox(text)
    return 0 < bbox[2] - bbox[0] <= protocol.CANVAS_WIDTH and 0 < bbox[3] - bbox[1] <= protocol.CANVAS_HEIGHT


def _geometry(encoded: np.ndarray) -> list[float]:
    if encoded.shape != protocol.ENCODED_SHAPE or encoded.dtype != np.float32 or not np.isfinite(encoded).all():
        raise ValueError("V5 encoded component tensor shape or values differ")
    geometry = encoded[0, :, 20:26]
    reference = geometry[0, :]
    if not np.allclose(geometry, np.broadcast_to(reference, geometry.shape), atol=0, rtol=0):
        raise ValueError("V5 geometry columns are not constant")
    return [float(value) for value in reference]


def inspect_crop(raster: np.ndarray, text_mask: np.ndarray | None = None) -> dict[str, Any]:
    """Run exact V5 component extraction and fail closed on empty/contaminated crops."""
    if not isinstance(raster, np.ndarray) or raster.dtype != np.uint8 or raster.shape != (32, 128):
        raise ValueError("source raster must be uint8 [32,128]")
    foreground = _filtered_foreground(raster)
    if text_mask is not None:
        if not isinstance(text_mask, np.ndarray) or text_mask.dtype != np.uint8 or text_mask.shape != raster.shape:
            raise ValueError("text-only mask must be uint8 [32,128]")
        if np.any(foreground & (text_mask == 0)):
            return {"status": "excluded", "reason": "contaminated_crop", "components": (), "excluded_components": ()}
    encoded = isolate_glyphs(raster)
    if not encoded:
        return {"status": "excluded", "reason": "empty_foreground", "components": (), "excluded_components": ()}
    components: list[dict[str, Any]] = []
    for index, glyph in enumerate(encoded):
        geometry = _geometry(glyph)
        components.append({"component_index": index, "geometry": geometry, "encoded": glyph})
    # The production V5 decoder returns before its model runner when the crop
    # has more than eight glyphs or any glyph reaches the structural height
    # cutoff. No component from such a crop is a classifier training example.
    if len(components) > protocol.MAX_RUNTIME_COMPONENT_COUNT:
        excluded = [{"component_index": row["component_index"], "class_index": None,
                     "geometry": row["geometry"], "reason": "v5_component_count_limit_before_classifier"}
                    for row in components]
        return {"status": "excluded", "reason": "v5_component_count_limit_before_classifier",
                "components": (), "excluded_components": tuple(excluded)}
    if any(row["geometry"][0] >= protocol.STRUCTURAL_REJECT_MINIMUM_HEIGHT_RATIO for row in components):
        excluded = [{"component_index": row["component_index"], "class_index": None,
                     "geometry": row["geometry"], "reason": "v5_structural_height_reject_before_classifier"}
                    for row in components]
        return {"status": "excluded", "reason": "v5_structural_height_reject_before_classifier",
                "components": (), "excluded_components": tuple(excluded)}
    retained = tuple({"component_index": row["component_index"], "class_index": protocol.REJECT_CLASS_INDEX,
                      "geometry": row["geometry"], "encoded": row["encoded"]} for row in components)
    return {"status": "eligible", "reason": None, "components": retained, "excluded_components": ()}


def _safe_fresh_output(path: Path) -> Path:
    path = Path(path)
    if not path.is_absolute():
        path = protocol.ROOT / path
    path = path.absolute()
    artifacts = (protocol.ROOT / "artifacts").absolute()
    if (not path.is_relative_to(artifacts) or path == artifacts or
            ".." in path.relative_to(artifacts).parts):
        raise ValueError("output must be a new child of the project artifacts directory")
    root_info = os.stat(protocol.ROOT, follow_symlinks=False)
    if stat.S_ISLNK(root_info.st_mode) or getattr(root_info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
        raise ValueError("project root is a reparse point")
    current = protocol.ROOT
    for part in path.relative_to(protocol.ROOT).parts:
        current /= part
        try:
            info = os.stat(current, follow_symlinks=False)
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            raise ValueError("output path includes a symlink or reparse point")
    if path.exists():
        raise FileExistsError("output directory must be fresh")
    if not path.parent.is_dir():
        raise FileNotFoundError("output parent must already exist")
    return path


def _catalog_samples(fonts: dict[str, dict[str, str]], catalog: dict[str, Any], phrase_hash: str) -> list[dict[str, Any]]:
    planned: list[dict[str, Any]] = []
    phrase_ids: dict[str, set[str]] = {"train": set(), "validation": set()}
    phrase_texts: dict[str, set[str]] = {"train": set(), "validation": set()}
    for split in ("train", "validation"):
        family = protocol.SPLIT_FAMILIES[split]
        for phrase in catalog[split]:
            phrase_ids[split].add(phrase["id"])
            phrase_texts[split].add(phrase["text"])
            for font_id in family["font_ids"]:
                for requested_size in FONT_SIZES_PX:
                    planned.append({"split": split, "split_family": family["id"], "phrase_id": phrase["id"],
                                    "text": phrase["text"], "font_id": font_id, "font": fonts[font_id],
                                    "requested_font_size_px": requested_size, "phrase_source_sha256": phrase_hash})
    if phrase_ids["train"] & phrase_ids["validation"] or phrase_texts["train"] & phrase_texts["validation"]:
        raise ValueError("train/validation phrase identities overlap")
    if set(protocol.SPLIT_FAMILIES["train"]["font_ids"]) & set(protocol.SPLIT_FAMILIES["validation"]["font_ids"]):
        raise ValueError("train/validation font identities overlap")
    return planned


def build_manifest() -> dict[str, Any]:
    """Build the manifest in memory without writing a corpus to disk."""
    return _generate_into_existing_empty_output(None)


def generate_corpus(output_root: Path) -> dict[str, Any]:
    """Write a fresh project-local corpus directory and its manifest."""
    output = _safe_fresh_output(Path(output_root))
    output.mkdir(parents=False, exist_ok=False)
    try:
        # Build records after the root exists; files are created only beneath
        # this freshly allocated project artifacts directory.
        manifest = _generate_into_existing_empty_output(output)
        with (output / "manifest.json").open("x", encoding="utf-8", newline="\n") as handle:
            json.dump(manifest, handle, ensure_ascii=False, indent=2, sort_keys=True)
            handle.write("\n")
        return manifest
    except Exception:
        # Keep partial output for explicit review; never silently clean a run.
        raise


def _generate_into_existing_empty_output(output: Path | None) -> dict[str, Any]:
    # The in-memory preview and artifact writer share this single render path.
    output_root = output
    source_bindings, font_paths, font_manifest = _verify_fixed_sources()
    catalog, phrase_hash = _load_phrase_catalog()
    font_rows, license_binding = _font_metadata(font_manifest)
    planned = _catalog_samples(font_rows, catalog, phrase_hash)
    samples: list[dict[str, Any]] = []
    excluded_samples: list[dict[str, Any]] = []
    for plan in planned:
        sid_material = "\0".join((plan["split"], plan["phrase_id"], plan["text"], plan["phrase_source_sha256"],
                                   plan["font_id"], plan["font"]["sha256"], str(plan["requested_font_size_px"])))
        sid = "reject-" + _sha_bytes(sid_material.encode("utf-8"))[:24]
        raster, mask, actual_size = render_phrase(plan["text"], font_paths[plan["font_id"]], plan["requested_font_size_px"])
        report = inspect_crop(raster, mask)
        raster_rel = f"rasters/{sid}.png"
        raster_path = output_root / raster_rel if output_root is not None else None
        if raster_path is not None:
            raster_path.parent.mkdir(parents=True, exist_ok=True)
            Image.fromarray(raster, mode="L").save(raster_path, format="PNG", optimize=False, compress_level=9)
        base = {"sample_id": sid, "sample_kind": protocol.SAMPLE_KIND, "split": plan["split"],
                "split_family": plan["split_family"], "phrase_id": plan["phrase_id"], "text": plan["text"],
                "text_sha256": _sha_bytes(plan["text"].encode()), "phrase_source": "package_phrase_catalog",
                "phrase_source_sha256": phrase_hash,
                "font": {key: plan["font"][key] for key in ("id", "family", "path", "sha256", "license_id", "license_sha256")},
                "font_manifest_sha256": protocol.FONT_MANIFEST_SHA256,
                "render_spec": {"schema": "graphreader.clean-text-render.v1", "renderer": "Pillow ImageDraw.text",
                                "requested_font_size_px": plan["requested_font_size_px"], "actual_font_size_px": actual_size,
                                "canvas": [128, 32], "mode": "L", "background": BACKGROUND, "ink": INK,
                                "position": "centered_no_clipping", "supersample": 1, "degradation": "none",
                                "context": "isolated_text_only"},
                "raster_path": raster_rel if raster_path is not None else None,
                "raster_png_sha256": _sha_file(raster_path) if raster_path is not None else None,
                "raster_pixels_sha256": _sha_bytes(raster.tobytes()), "text_mask_sha256": _sha_bytes(mask.tobytes()),
                "segmentation_status": report["status"]}
        if report["status"] != "eligible":
            excluded_samples.append({**base, "class_index": None, "exclusion_reason": report["reason"], "components": [],
                                     "excluded_components": list(report["excluded_components"])})
            continue
        components = []
        for component in report["components"]:
            tensor = np.asarray(component["encoded"], dtype="<f4", order="C")
            raw = tensor.tobytes(order="C")
            index = component["component_index"]
            tensor_rel = f"components/{sid}-{index:03d}.f32le"
            tensor_path = output_root / tensor_rel if output_root is not None else None
            if tensor_path is not None:
                tensor_path.parent.mkdir(parents=True, exist_ok=True)
                with tensor_path.open("xb") as handle:
                    handle.write(raw)
            components.append({"component_index": index, "class_index": 13, "encoded_shape": [1, 24, 26],
                               "dtype": "float32-le", "tensor_path": tensor_rel if tensor_path is not None else None,
                               "encoded_tensor_sha256": _sha_bytes(raw), "geometry": component["geometry"]})
        structural = list(report["excluded_components"])
        samples.append({**base, "class_index": 13, "components": components,
                        "structural_short_circuit_exclusions": structural, "exclusion_reason": None})
    source_bindings.update({
        "ml/ocr/synthetic_reject_glyph_v1/phrases.json": phrase_hash,
        "ml/ocr/synthetic_reject_glyph_v1/protocol.py": _sha_file(_require_regular_under(protocol.PACKAGE / "protocol.py", protocol.ROOT)),
        "ml/ocr/synthetic_reject_glyph_v1/generate.py": _sha_file(_require_regular_under(Path(__file__), protocol.ROOT)),
    })
    return {
        "schema": protocol.SCHEMA, "scope": "public_synthetic_train_validation_only", "synthetic_only": True,
        "private_reads": 0, "sealed_reads": 0, "test_reads": 0, "training_approved": False,
        "production_approved": False, "admission_status": "blocked_pending_independent_review_and_numeric_foundation_integration",
        "sample_kind": protocol.SAMPLE_KIND, "class_index": 13, "v5_alphabet": protocol.V5_ALPHABET,
        "phrase_policy": {"accepted_pattern": "ASCII letters with single internal spaces",
                          "nonspace_v5_alphabet_characters_allowed": False,
                          "mixed_string_handling": "fail_closed_before_render; no mixed label enters samples"},
        "v5_preclassifier_eligibility": {"maximum_component_count": protocol.MAX_RUNTIME_COMPONENT_COUNT,
                                         "structural_height_ratio_minimum_reject": protocol.STRUCTURAL_REJECT_MINIMUM_HEIGHT_RATIO,
                                         "scope": "whole_crop_short_circuit_before_model_runner"},
        "font_source": {"manifest_sha256": protocol.FONT_MANIFEST_SHA256, "license": license_binding,
                        "historical_benchmark_independence": "unproven",
                        "cross_host_raster_reproducibility": "not_claimed; Pillow version and source hashes are recorded"},
        "split_families": {split: {"id": protocol.SPLIT_FAMILIES[split]["id"],
                                    "font_ids": list(protocol.SPLIT_FAMILIES[split]["font_ids"]),
                                    "phrase_ids": [row["id"] for row in catalog[split]]}
                           for split in ("train", "validation")},
        "split_disjointness": {"phrase_ids": True, "phrase_text": True, "font_ids": True,
                               "font_sha256": len({row["sha256"] for row in font_rows.values()}) == len(font_rows),
                               "family_ids": protocol.SPLIT_FAMILIES["train"]["id"] != protocol.SPLIT_FAMILIES["validation"]["id"]},
        "source_bindings": source_bindings,
        "renderer_environment": {"python": sys.version.split()[0], "platform": platform.platform(),
                                  "pillow": PILLOW_VERSION, "numpy": np.__version__},
        "counts": {"planned_samples": len(planned), "eligible_samples": len(samples),
                   "excluded_samples": len(excluded_samples), "components": sum(len(row["components"]) for row in samples),
                   "structural_short_circuit_components": sum(
                       len(row["excluded_components"]) for row in excluded_samples
                       if row["exclusion_reason"] == "v5_structural_height_reject_before_classifier"),
                   "component_count_short_circuit_components": sum(
                       len(row["excluded_components"]) for row in excluded_samples
                       if row["exclusion_reason"] == "v5_component_count_limit_before_classifier")},
        "limitations": [
            "Exploratory one-class reject corpus only; it is not a benchmark or model result.",
            "Train/validation phrase IDs and font IDs are disjoint, but both use upstream Noto fonts and historical benchmark independence is unproven.",
            "Samples are clean re-renders from a fixed phrase catalog, not crops or transcriptions of graph/user/private data.",
            "Phrase-level alphabet exclusion labels every retained component; no component-to-character alignment is inferred.",
            "V5 whole-crop structural-height and over-eight-component short circuits are recorded separately and contribute no class-13 target.",
            "No optimizer, model inference, private/sealed/test input, or production admission is used by this package.",
        ],
        "samples": samples, "excluded_samples": excluded_samples,
    }


def main() -> int:
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path, help="fresh child directory under project artifacts")
    arguments = parser.parse_args()
    manifest = generate_corpus(arguments.output)
    print(json.dumps({"status": "generated_nonproduction", "manifest": str(arguments.output / "manifest.json"),
                      "counts": manifest["counts"], "training_approved": False, "production_approved": False}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
