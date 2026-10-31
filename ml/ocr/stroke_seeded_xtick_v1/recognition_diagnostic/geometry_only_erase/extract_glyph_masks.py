# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Recreate exact synthetic glyph masks after geometry-only erasure inference."""

from __future__ import annotations

from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys

from PIL import Image

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.stroke_seeded_xtick_v1 import materialize as crop_materializer

MANIFEST_SHA256 = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
CORPUS = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3"


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def project_bytes(root: Path, relative: str, expected: str, limit: int) -> bytes:
    path = (root / relative).resolve(strict=True)
    if not path.is_relative_to(root.resolve()) or path.is_symlink():
        raise ValueError(f"unsafe input path: {relative}")
    payload = path.read_bytes()
    if not 0 < len(payload) <= limit or digest(payload) != expected:
        raise ValueError(f"bounded input hash/size mismatch: {relative}")
    return payload


def crop_mask(full_panel: Image.Image, transform: dict) -> Image.Image:
    left, top, width, height = (int(value) for value in transform["source_window_xywh"])
    source = Image.new("L", (width, height), 0)
    ix0, iy0 = max(0, left), max(0, top)
    ix1, iy1 = min(full_panel.width, left + width), min(full_panel.height, top + height)
    if ix1 > ix0 and iy1 > iy0:
        source.paste(full_panel.crop((ix0, iy0, ix1, iy1)), (ix0 - left, iy0 - top))
    _, _, content_width, content_height = (int(value) for value in transform["content_xywh"])
    resized = source.resize((content_width, content_height), Image.Resampling.BILINEAR)
    canvas_width, canvas_height = (int(value) for value in transform["canvas_size"])
    output = Image.new("L", (canvas_width, canvas_height), 0)
    x0, y0, _, _ = (int(value) for value in transform["content_xywh"])
    output.paste(resized, (x0, y0))
    return output


def extract(root: Path, manifest_path: Path, output: Path) -> dict:
    manifest_bytes = manifest_path.read_bytes()
    if digest(manifest_bytes) != MANIFEST_SHA256:
        raise ValueError("probe manifest SHA mismatch")
    manifest = json.loads(manifest_bytes)
    records = manifest["records"]
    source_rows = {source["source_id"]: source for source in manifest["sources"]}
    expected_rows = {row["case_id"]: row for row in records}
    if output.exists():
        raise ValueError("glyph-mask output directory must be new")
    output.mkdir(parents=True)
    (output / "masks").mkdir()

    all_masks: dict[str, Image.Image] = {}
    original_glyph = crop_materializer._glyph_alpha_mask
    original_warp = crop_materializer._warp_mask
    mask_owner: dict[int, str] = {}

    def capture_glyph(record, font, size):
        result = original_glyph(record, font, size)
        region = str(record["region_id"])
        mask_owner[id(result)] = region
        all_masks[region] = result.copy()
        return result

    def capture_warp(mask, matrix):
        result = original_warp(mask, matrix)
        region = mask_owner.get(id(mask))
        if region is not None:
            all_masks[region] = result.copy()
        return result

    crop_materializer._glyph_alpha_mask = capture_glyph
    crop_materializer._warp_mask = capture_warp
    index_rows = []
    verified_images = 0
    try:
        for source_id, source in source_rows.items():
            all_masks.clear()
            generated = crop_materializer.materialize_recipe(source["renderer_recipe"])
            for row in generated:
                manifest_row = expected_rows.get(row["case_id"])
                if manifest_row is None:
                    continue
                image = Image.frombytes("L", (256, 96), row["crop_pixels"])
                buffer = BytesIO()
                image.save(buffer, format="PNG", compress_level=9)
                if digest(buffer.getvalue()) != manifest_row["image_sha256"]:
                    raise ValueError(f"rerendered probe image differs: {row['case_id']}")
                verified_images += 1
                if manifest_row["kind"] != "positive":
                    continue
                if not row["targets"]:
                    continue
                annotation_bytes = project_bytes(root, f"{CORPUS}/{manifest_row['annotation_path']}",
                                                 manifest_row["annotation_sha256"], 65_536)
                annotation = json.loads(annotation_bytes)
                if len(row["targets"]) != 1 or len(annotation["targets"]) != 1:
                    raise ValueError(f"ambiguous target mask: {row['case_id']}")
                generated_target, truth_target = row["targets"][0], annotation["targets"][0]
                region = generated_target["text_region_id"]
                full_mask = all_masks.get(region)
                if full_mask is None:
                    raise ValueError(f"renderer mask unavailable: {row['case_id']}")
                full_bytes = full_mask.tobytes()
                if (digest(full_bytes) != truth_target["glyph_mask_sha256"]
                        or sum(value > 0 for value in full_bytes) != truth_target["glyph_pixel_count"]):
                    raise ValueError(f"regenerated full-panel glyph mask does not match pinned annotation: {row['case_id']}")
                crop_transform = row["crop_transform"]
                if crop_transform != annotation["crop_transform"]:
                    raise ValueError(f"regenerated crop transform differs: {row['case_id']}")
                mask = crop_mask(full_mask, crop_transform)
                relative = f"masks/{row['case_id']}.png"
                mask_path = output / relative
                mask.save(mask_path, format="PNG", compress_level=9)
                index_rows.append({"case_id": row["case_id"], "path": relative,
                                   "sha256": digest(mask_path.read_bytes()),
                                   "full_panel_glyph_mask_sha256": digest(full_bytes),
                                   "crop_mask_nonzero_pixels": sum(value > 0 for value in mask.tobytes()),
                                   "annotation_sha256": manifest_row["annotation_sha256"]})
    finally:
        crop_materializer._glyph_alpha_mask = original_glyph
        crop_materializer._warp_mask = original_warp

    if verified_images != len(records) or len(index_rows) != sum(row["kind"] == "positive" for row in records):
        raise ValueError("rerendered image/mask inventory differs from pinned probe")
    index = {"schema": "graphreader.stroke-seeded-geometry-erase-glyph-mask-index.v1",
             "probe_manifest_sha256": MANIFEST_SHA256,
             "record_count": len(index_rows), "verified_probe_image_count": verified_images,
             "records": index_rows}
    (output / "mask-index.json").write_text(json.dumps(index, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    return {"verified_probe_image_count": verified_images, "positive_mask_count": len(index_rows),
            "full_mask_sha_mismatches": 0, "output": str(output)}


if __name__ == "__main__":
    repo = Path(sys.argv[1]).resolve(strict=True)
    manifest_file = repo / CORPUS / "manifest.json"
    output_dir = Path(sys.argv[2]).resolve()
    print(json.dumps(extract(repo, manifest_file, output_dir), sort_keys=True))
