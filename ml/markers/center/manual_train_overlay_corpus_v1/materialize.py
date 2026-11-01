# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Streaming train-only patch materializer; all source pixels outside patches stay unknown."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import shutil
import sys
from pathlib import Path
from typing import Any

from PIL import Image, ImageChops, ImageDraw, __version__ as PIL_VERSION

ROOT = Path(r"E:\Graph-auto-reader")
PLAN_SCHEMA = "graphreader.manual-train-marker-overlay-corpus-plan.v1"
EXTRACTION_SCHEMA = "graphreader.manual-train-marker-overlay-corpus-source-extraction.v1"
MANIFEST_SHA = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57"
RENDERER_RELATIVE_PATH = "ml/markers/center/manual_train_overlay_corpus_v1/materialize.py"
MAX_SOURCE_PIXELS = 40_000_000
MAX_OUTPUT_BYTES = 1_000_000_000
MAX_SOURCE_EXTRACTION_BYTES = 1_000_000_000


class MaterializationError(ValueError):
    pass


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _project_path(path: Path, *, exists: bool = True, directory: bool = False) -> Path:
    root = Path(os.path.abspath(ROOT))
    absolute = Path(os.path.abspath(path))
    try:
        relative = absolute.relative_to(root)
    except ValueError as exc:
        raise MaterializationError("path escaped E project") from exc
    current = Path(root.anchor)
    for part in root.parts[1:] + relative.parts:
        current /= part
        if current.exists() or current.is_symlink():
            attributes = getattr(os.lstat(current), "st_file_attributes", 0)
            if current.is_symlink() or attributes & 0x400:
                raise MaterializationError("path contains a reparse-point ancestor")
    if exists:
        if directory and not absolute.is_dir():
            raise MaterializationError("path is not a regular directory")
        if not directory and not absolute.is_file():
            raise MaterializationError("path is not a regular file")
    return absolute


def _read_hashed_json(path: Path, expected: str) -> tuple[dict[str, Any], bytes]:
    raw = _project_path(path).read_bytes()
    if _sha(raw) != expected:
        raise MaterializationError("input bytes differ from their externally supplied SHA-256")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise MaterializationError("input JSON root must be an object")
    return value, raw


def _assert_protocol(plan: dict[str, Any], expected_materializer_sha: str) -> tuple[str, dict[str, Any]]:
    protocol_path = ROOT / "ml/markers/center/manual_train_overlay_corpus_v1/protocol.json"
    raw = _project_path(protocol_path).read_bytes()
    digest = _sha(raw)
    if plan.get("protocol_sha256") != digest:
        raise MaterializationError("plan does not bind the current protocol")
    protocol = json.loads(raw)
    if protocol.get("selection", {}).get("max_total_source_png_bytes") != MAX_SOURCE_EXTRACTION_BYTES:
        raise MaterializationError("source PNG extraction byte budget differs from this materializer")
    sources = protocol.get("source_files", {})
    if sources.get(RENDERER_RELATIVE_PATH) != expected_materializer_sha or _sha(_project_path(ROOT / RENDERER_RELATIVE_PATH).read_bytes()) != expected_materializer_sha:
        raise MaterializationError("materializer source differs from its protocol and external pins")
    for relative, expected in sources.items():
        if not isinstance(relative, str) or not isinstance(expected, str):
            raise MaterializationError("protocol source map is malformed")
        if _sha(_project_path(ROOT / relative).read_bytes()) != expected:
            raise MaterializationError("frozen source closure changed")
    return digest, protocol


def _verify_capture_attempt(path: Path, expected_sha: str, expected_events: int, source_count: int) -> None:
    raw = _project_path(path).read_bytes()
    if _sha(raw) != expected_sha:
        raise MaterializationError("source extraction attempt journal hash differs")
    try:
        rows = [json.loads(line) for line in raw.decode("utf-8").splitlines()]
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise MaterializationError("source extraction attempt journal is malformed") from exc
    if len(rows) != expected_events or rows[-1] != {"event_name": "sources_complete", "source_count": source_count}:
        raise MaterializationError("source extraction attempt is incomplete")
    expected_names = [name for _ in range(source_count) for name in ("source_started", "source_completed")] + ["sources_complete"]
    if [row.get("event_name") for row in rows] != expected_names or [row.get("source_index") for row in rows[:-1:2]] != list(range(source_count)):
        raise MaterializationError("source extraction event order or index differs")


def _recipe_draw(draw: ImageDraw.ImageDraw, recipe: dict[str, Any], x: int, y: int) -> tuple[int, int, int, int]:
    kind = recipe["shape"]
    if kind in ("filled_circle", "open_circle"):
        radius = recipe["radius_px"]
        box = (x - radius, y - radius, x + radius, y + radius)
        draw.ellipse(box, fill=(0, 0, 0, 255) if kind == "filled_circle" else None, outline=(0, 0, 0, 255), width=max(1, recipe["stroke_px"]))
        return box
    if kind == "filled_square":
        half = recipe["half_extent_px"]
        box = (x - half, y - half, x + half, y + half)
        draw.rectangle(box, fill=(0, 0, 0, 255))
        return box
    if kind == "filled_diamond":
        radius = recipe["radius_px"]
        points = [(x, y - radius), (x + radius, y), (x, y + radius), (x - radius, y)]
        draw.polygon(points, fill=(0, 0, 0, 255))
        return (x - radius, y - radius, x + radius, y + radius)
    if kind in ("horizontal_line", "vertical_line", "diagonal_line"):
        half = recipe["length_px"] // 2
        if kind == "horizontal_line":
            points = [(x - half, y), (x + half, y)]
        elif kind == "vertical_line":
            points = [(x, y - half), (x, y + half)]
        else:
            points = [(x - half, y + half), (x + half, y - half)]
        draw.line(points, fill=(0, 0, 0, 255), width=recipe["stroke_px"])
        return (min(p[0] for p in points), min(p[1] for p in points), max(p[0] for p in points) + 1, max(p[1] for p in points) + 1)
    if kind in ("plus", "tee"):
        half = recipe["span_px"] // 2
        if kind == "plus":
            draw.line((x - half, y, x + half, y), fill=(0, 0, 0, 255), width=recipe["stroke_px"])
            draw.line((x, y - half, x, y + half), fill=(0, 0, 0, 255), width=recipe["stroke_px"])
            return (x - half, y - half, x + half + 1, y + half + 1)
        draw.line((x - half, y - half, x + half, y - half), fill=(0, 0, 0, 255), width=recipe["stroke_px"])
        draw.line((x, y - half, x, y + half), fill=(0, 0, 0, 255), width=recipe["stroke_px"])
        return (x - half, y - half, x + half + 1, y + half + 1)
    raise MaterializationError("unknown frozen recipe shape")


def _outside_patch_unchanged(base: Image.Image, overlay: Image.Image, patch: tuple[int, int, int, int]) -> bool:
    left, top, right, bottom = patch
    delta = ImageChops.difference(base, overlay).convert("RGB")
    regions = ((0, 0, base.width, top), (0, bottom, base.width, base.height),
               (0, top, left, bottom), (right, top, base.width, bottom))
    return all(delta.crop(region).getbbox() is None for region in regions if region[2] > region[0] and region[3] > region[1])


def materialize_source_views(
    base: Image.Image,
    source: dict[str, Any],
    recipes: list[dict[str, Any]],
    output_directory: Path,
    patch_width: int = 112,
    patch_height: int = 80,
    patch_fill: tuple[int, int, int, int] = (255, 255, 255, 255),
    remaining_output_bytes: int = MAX_OUTPUT_BYTES,
) -> list[dict[str, Any]]:
    """Render the fixed views for one source image, then release them before the next source."""
    x, y = source["anchor_pixel"]["x"], source["anchor_pixel"]["y"]
    left, top, right, bottom = x - patch_width // 2, y - patch_height // 2, x + patch_width // 2, y + patch_height // 2
    if left < 0 or top < 0 or right > base.width or bottom > base.height:
        raise MaterializationError("frozen patch extends beyond source image")
    patch = (left, top, right, bottom)
    rows: list[dict[str, Any]] = []
    for recipe in recipes:
        canvas = base.copy()
        try:
            draw = ImageDraw.Draw(canvas)
            draw.rectangle((left, top, right - 1, bottom - 1), fill=patch_fill)
            bbox = _recipe_draw(draw, recipe, x, y)
            if not _outside_patch_unchanged(base, canvas, patch):
                raise MaterializationError("render changed decoded source pixels outside the patch")
            filename = f"source-{source['source_index']:03d}_{recipe['id']}.png"
            path = output_directory / filename
            with io.BytesIO() as encoded:
                canvas.save(encoded, format="PNG", optimize=False, compress_level=9)
                payload = encoded.getvalue()
            size = len(payload)
            if size > remaining_output_bytes:
                raise MaterializationError("overlay bytes exceeded the frozen output bound")
            _write_new_file(path, payload)
            rows.append({
                "source_index": source["source_index"],
                "assigned_partition": source["assigned_partition"],
                "component_sha256": source["component_sha256"],
                "source_image_sha256": source["image_sha256"],
                "recipe_id": recipe["id"],
                "label": recipe["label"],
                "shape": recipe["shape"],
                "patch_ltrb_half_open": list(patch),
                "injected_shape_bbox": list(bbox),
                "output_path": f"{source['assigned_partition']}/{filename}",
                "output_sha256": _sha(payload),
                "output_bytes": size,
                "native_content_labeling": "unknown_everywhere",
                "pixel_equivalence_outside_patch": True,
            })
            remaining_output_bytes -= size
        finally:
            canvas.close()
    return rows


def _write_new_file(path: Path, payload: bytes) -> None:
    descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())


def materialize(
    plan_path: Path,
    expected_plan_sha256: str,
    extraction_receipt_path: Path,
    expected_extraction_receipt_sha256: str,
    expected_materializer_sha256: str,
    output_dir: Path,
) -> dict[str, Any]:
    plan, plan_bytes = _read_hashed_json(plan_path, expected_plan_sha256)
    protocol_sha, protocol = _assert_protocol(plan, expected_materializer_sha256)
    extraction, extraction_bytes = _read_hashed_json(extraction_receipt_path, expected_extraction_receipt_sha256)
    if plan.get("schema") != PLAN_SCHEMA or plan.get("status") != "frozen_train_only_expansion_plan_unmaterialized" or plan.get("protocol_sha256") != protocol_sha:
        raise MaterializationError("plan schema/status/protocol identity differs")
    if extraction.get("schema") != EXTRACTION_SCHEMA or extraction.get("status") != "train_only_source_group_pngs_extracted_not_materialized":
        raise MaterializationError("source extraction receipt schema/status differs")
    if extraction.get("plan_sha256") != expected_plan_sha256 or extraction.get("protocol_sha256") != protocol_sha or extraction.get("source_manifest_sha256") != MANIFEST_SHA:
        raise MaterializationError("source receipt is not bound to this plan and train manifest")
    if extraction.get("development_payload_reads") != 0 or extraction.get("sealed_payload_reads") != 0 or extraction.get("optimizer_steps") != 0 or extraction.get("training_admitted") is not False:
        raise MaterializationError("source extraction exceeded train-only/no-training scope")
    if extraction.get("selected_sources") != len(plan.get("source_rows", [])) or extraction.get("extracted_sources") != extraction.get("selected_sources") or \
        extraction.get("failed_sources") != 0 or not isinstance(extraction.get("extracted_image_bytes"), int) or \
        not 0 <= extraction["extracted_image_bytes"] <= MAX_SOURCE_EXTRACTION_BYTES:
        raise MaterializationError("source extraction is incomplete or exceeds its byte budget")
    source_rows, extracted_rows = plan.get("source_rows"), extraction.get("sources")
    if not isinstance(source_rows, list) or not source_rows or not isinstance(extracted_rows, list) or len(source_rows) != len(extracted_rows):
        raise MaterializationError("source row counts differ")
    recipes = plan.get("recipes")
    if not isinstance(recipes, list) or len(recipes) != 10:
        raise MaterializationError("fixed recipe set differs")
    expected_runtime = protocol.get("runtime_assemblies", {})
    runtime = extraction.get("runtime", {})
    if runtime != {"ExportAssemblySha256": expected_runtime.get("GraphReader.Export"), "ExtractorAssemblySha256": expected_runtime.get("GraphReader.MarkerTrainOverlayCorpus")}:
        raise MaterializationError("source extractor runtime identities differ")
    _verify_capture_attempt(extraction_receipt_path.parent / "source-attempt.jsonl", extraction.get("capture_attempt_sha256", ""), extraction.get("capture_attempt_event_count", -1), len(source_rows))

    captured_total_bytes = 0
    sources: list[tuple[dict[str, Any], Path]] = []
    for index, (source, captured) in enumerate(zip(source_rows, extracted_rows, strict=True)):
        if captured.get("source_index") != index or captured.get("case_id") != source["case_id"] or captured.get("study_id") != source["study_id"] or \
            captured.get("component_sha256") != source["component_sha256"] or captured.get("image_sha256") != source["image_sha256"] or \
            captured.get("project_sha256") != source["project_sha256"] or captured.get("assigned_partition") != source["assigned_partition"]:
            raise MaterializationError("plan and extracted source identities differ")
        filename = captured.get("path")
        if not isinstance(filename, str) or Path(filename).name != filename or not filename:
            raise MaterializationError("extracted source path must be a simple file name")
        image_path = _project_path(extraction_receipt_path.parent / filename)
        if image_path.stat().st_size != captured.get("byte_length") or _sha(image_path.read_bytes()) != captured.get("extracted_png_sha256") or captured.get("extracted_png_sha256") != source["image_sha256"]:
            raise MaterializationError("embedded source PNG bytes differ from authenticated source identity")
        captured_total_bytes += captured["byte_length"]
        if source["image_width"] * source["image_height"] > MAX_SOURCE_PIXELS:
            raise MaterializationError("source frame exceeds frozen pixel bound")
        sources.append((source, image_path))
    if captured_total_bytes != extraction["extracted_image_bytes"]:
        raise MaterializationError("source extraction byte total differs from its rows")

    output_dir = _project_path(output_dir, exists=False)
    if output_dir.exists() or output_dir.is_symlink() or not output_dir.parent.is_dir():
        raise MaterializationError("output path must be fresh inside an existing project directory")
    if shutil.disk_usage(output_dir.parent).free < MAX_OUTPUT_BYTES:
        raise MaterializationError("less than the frozen output budget is free")
    staging = _project_path(output_dir.with_name(output_dir.name + ".staging"), exists=False)
    if staging.exists() or staging.is_symlink():
        raise MaterializationError("staging output already exists")
    staging.mkdir()
    _project_path(staging, directory=True)
    try:
        total_bytes = 0
        result_rows: list[dict[str, Any]] = []
        for source, image_path in sources:
            split_dir = staging / source["assigned_partition"]
            split_dir.mkdir(exist_ok=True)
            _project_path(split_dir, directory=True)
            with Image.open(image_path) as opened:
                base = opened.convert("RGBA")
            try:
                if base.size != (source["image_width"], source["image_height"]):
                    raise MaterializationError("decoded source dimensions differ from frozen plan")
                if base.width * base.height > MAX_SOURCE_PIXELS:
                    raise MaterializationError("decoded source exceeds pixel memory bound")
                source_rows = materialize_source_views(base, source, recipes, split_dir,
                    protocol["rendering"]["patch_width_px"], protocol["rendering"]["patch_height_px"], tuple(protocol["rendering"]["patch_fill_rgba"]),
                    MAX_OUTPUT_BYTES - total_bytes)
            finally:
                base.close()
            total_bytes += sum(row["output_bytes"] for row in source_rows)
            if total_bytes > MAX_OUTPUT_BYTES:
                raise MaterializationError("overlay bytes exceeded frozen output bound")
            result_rows.extend(source_rows)
        receipt = {
            "schema": "graphreader.manual-train-marker-overlay-corpus-result.v1",
            "status": "completed_train_only_controlled_overlay_materialization_unapproved",
            "protocol_sha256": protocol_sha,
            "plan_sha256": _sha(plan_bytes),
            "source_extraction_receipt_sha256": _sha(extraction_bytes),
            "materializer_source_sha256": expected_materializer_sha256,
            "python": sys.version.split()[0],
            "pillow": PIL_VERSION,
            "source_group_count": len(sources),
            "overlay_count": len(result_rows),
            "output_bytes": total_bytes,
            "max_source_frames_open_concurrently": 1,
            "development_payload_reads": 0,
            "sealed_payload_reads": 0,
            "optimizer_steps": 0,
            "training_admitted": False,
            "label_counts": {"injected_positive_only": sum(row["label"] == "injected_positive_only" for row in result_rows),
                             "injected_distractor_only": sum(row["label"] == "injected_distractor_only" for row in result_rows)},
            "native_content_labeling": "unknown_everywhere",
            "rows": result_rows,
            "limitations": plan["limitations"],
        }
        receipt_path = staging / "receipt.json"
        _write_new_file(receipt_path, (json.dumps(receipt, sort_keys=True, indent=2) + "\n").encode("utf-8"))
        receipt_sha = _sha(receipt_path.read_bytes())
        _write_new_file(staging / "receipt.sha256", (receipt_sha + "  receipt.json\n").encode("ascii"))
        staging.rename(output_dir)
    except Exception:
        if staging.exists() and not staging.is_symlink():
            try:
                _project_path(staging, directory=True)
                shutil.rmtree(staging)
            except (OSError, MaterializationError):
                pass
        raise
    return {"status": receipt["status"], "source_group_count": len(sources), "overlay_count": len(result_rows),
            "receipt_sha256": receipt_sha, "output_bytes": total_bytes, "optimizer_steps": 0,
            "development_payload_reads": 0, "sealed_payload_reads": 0, "training_admitted": False}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--expected-plan-sha256", required=True)
    parser.add_argument("--expected-extraction-receipt-sha256", required=True)
    parser.add_argument("--expected-materializer-sha256", required=True)
    parser.add_argument("--output-dir", required=True)
    args = parser.parse_args()
    result = materialize(
        ROOT / "artifacts/goal22-runs/marker-train-overlay-corpus-plan-v7/plan.json",
        args.expected_plan_sha256,
        ROOT / "artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/source-receipt.json",
        args.expected_extraction_receipt_sha256,
        args.expected_materializer_sha256,
        ROOT / args.output_dir,
    )
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
