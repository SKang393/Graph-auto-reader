"""Deterministic, patch-bounded overlay for the private train-only feasibility pilot."""

from __future__ import annotations

import hashlib
import json
import os
import sys
from pathlib import Path
from typing import Any

from PIL import Image, ImageDraw, __version__ as PIL_VERSION


PROJECT_ROOT = Path(r"E:\Graph-auto-reader")


def _require_project_path(path: Path, *, must_exist: bool) -> Path:
    absolute_root = Path(os.path.abspath(PROJECT_ROOT))
    absolute = Path(os.path.abspath(path))
    try:
        relative = absolute.relative_to(absolute_root)
    except ValueError as exc:
        raise ValueError("path escaped the E project") from exc
    current = Path(absolute_root.anchor)
    for part in absolute_root.parts[1:] + relative.parts:
        current = current / part
        if current.exists() or current.is_symlink():
            attributes = getattr(os.lstat(current), "st_file_attributes", 0)
            if current.is_symlink() or attributes & 0x400:
                raise ValueError("path contains a reparse-point ancestor")
    if must_exist and not (absolute.is_file() or absolute.is_dir()):
        raise ValueError("input is not a regular file")
    return absolute


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def render_one(source_path: Path, expected_sha256: str, row: dict[str, Any], output_path: Path) -> dict[str, Any]:
    source_path = _require_project_path(source_path, must_exist=True)
    output_path = _require_project_path(output_path, must_exist=False)
    if output_path.exists() or output_path.is_symlink():
        raise ValueError("overlay output file must be fresh")
    if sha256_file(source_path) != expected_sha256:
        raise ValueError("source image bytes differ from the frozen plan")
    with Image.open(source_path) as opened:
        source = opened.convert("RGBA")
    width, height = source.size
    if (width, height) != (row["image_width"], row["image_height"]):
        raise ValueError("source image dimensions differ from the frozen plan")
    anchor = row["anchor_pixel"]
    distractor = row["distractor_pixel"]
    if set(anchor) != {"x", "y"} or set(distractor) != {"x", "y"}:
        raise ValueError("frozen anchor or distractor is malformed")
    x, y = anchor["x"], anchor["y"]
    dx, dy = distractor["x"], distractor["y"]
    half_w, half_h = 36, 28
    left, top, right, bottom = x - half_w, y - half_h, x + half_w, y + half_h
    if left < 0 or top < 0 or right > width or bottom > height or dx - 6 < left or dx + 6 >= right or dy != y + 12:
        raise ValueError("frozen overlay patch or distractor is outside the source bounds")
    canvas = source.copy()
    draw = ImageDraw.Draw(canvas)
    draw.rectangle((left, top, right - 1, bottom - 1), fill=(255, 255, 255, 255))
    draw.ellipse((x - 4, y - 4, x + 4, y + 4), fill=(0, 0, 0, 255))
    draw.line((dx - 5, dy, dx + 5, dy), fill=(0, 0, 0, 255), width=1)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(output_path, format="PNG", optimize=False, compress_level=9)
    output_sha = sha256_file(output_path)
    return {
        "source_sha256": expected_sha256,
        "source_width": width,
        "source_height": height,
        "overlay_sha256": output_sha,
        "overlay_path": output_path.name,
        "patch_ltrb_half_open": [left, top, right, bottom],
        "injected_labels": [
            {"id": "injected-positive-0", "label": "injected_positive_only", "kind": "filled_circle", "center": [x, y], "radius_px": 4},
            {"id": "injected-distractor-0", "label": "injected_distractor_only", "kind": "horizontal_line", "center": [dx, dy], "length_px": 11, "width_px": 1},
        ],
        "native_content_labeling": "unknown_everywhere",
        "source_pixel_change_scope": "opaque patch only",
        "pixel_equivalence_outside_patch": _outside_patch_unchanged(source, canvas, left, top, right, bottom),
    }


def _outside_patch_unchanged(source: Image.Image, overlay: Image.Image, left: int, top: int, right: int, bottom: int) -> bool:
    source_px, overlay_px = source.load(), overlay.load()
    for yy in range(source.height):
        for xx in range(source.width):
            if left <= xx < right and top <= yy < bottom:
                continue
            if source_px[xx, yy] != overlay_px[xx, yy]:
                return False
    return True


def render_plan(plan_path: Path, expected_plan_sha256: str, sources_dir: Path, output_dir: Path) -> dict[str, Any]:
    plan_path = _require_project_path(plan_path, must_exist=True)
    if sha256_file(plan_path) != expected_plan_sha256:
        raise ValueError("plan bytes differ from the externally supplied SHA-256")
    sources_dir = _require_project_path(sources_dir, must_exist=True)
    output_dir = _require_project_path(output_dir, must_exist=False)
    if not sources_dir.is_dir():
        raise ValueError("source directory is not a directory")
    if not output_dir.parent.is_dir():
        raise ValueError("overlay output parent must already exist")
    plan = json.loads(plan_path.read_text(encoding="utf-8"))
    if plan.get("schema") != "graphreader.manual-train-marker-overlay-pilot-plan.v1" or plan.get("status") != "frozen_plan_not_yet_materialized":
        raise ValueError("plan schema/status is not eligible for materialization")
    if output_dir.exists() or output_dir.is_symlink():
        raise ValueError("overlay output must be fresh")
    output_dir.mkdir(parents=True)
    rows = []
    for index, source in enumerate(plan["sources"]):
        source_path = _require_project_path(sources_dir / f"source-{index:02d}.png", must_exist=True)
        output_path = _require_project_path(output_dir / f"overlay-{index:02d}.png", must_exist=False)
        rows.append({"assigned_partition": source["assigned_partition"], **render_one(source_path, source["image_sha256"], source, output_path)})
    receipt = {
        "schema": "graphreader.manual-train-marker-overlay-pilot-result.v1",
        "status": "materialized_unapproved_feasibility_pilot",
        "plan_sha256": sha256_file(plan_path),
        "source_count": len(rows),
        "optimizer_steps": 0,
        "development_payload_reads": 0,
        "sealed_payload_reads": 0,
        "training_admitted": False,
        "renderer": {"python": sys.version.split()[0], "pillow": PIL_VERSION},
        "rows": rows,
        "limitation": "Only injected patch glyphs are labeled. Original structures remain unknown. This does not measure full-image false-positive precision or transfer to naturally occurring markers/artifacts.",
    }
    receipt_path = output_dir / "receipt.json"
    receipt_bytes = (json.dumps(receipt, sort_keys=True, indent=2) + "\n").encode("utf-8")
    with receipt_path.open("xb") as stream:
        stream.write(receipt_bytes)
    receipt["receipt_sha256"] = sha256_file(receipt_path)
    with (output_dir / "receipt.sha256").open("xb") as stream:
        stream.write((receipt["receipt_sha256"] + "  receipt.json\n").encode("ascii"))
    return receipt
