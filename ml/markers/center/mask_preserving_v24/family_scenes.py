# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Adapt the shared family-disjoint graph renderer to the V24 input contract."""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache
from typing import Any, Mapping, Sequence

import numpy as np
from PIL import Image, ImageDraw
import torch

from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.renderer import render_scene


@dataclass(frozen=True)
class FamilyScene:
    split: str
    family: str
    seed: int
    tensor: torch.Tensor
    centers: tuple[tuple[float, float], ...]
    diameters: tuple[float, ...]
    hard_negatives: tuple[tuple[str, float, float], ...]


def _records(annotation: Mapping[str, Any], key: str) -> list[Mapping[str, Any]]:
    records = list(annotation.get(key, []))
    for panel in annotation.get("panels", []):
        records.extend(panel.get(key, []))
    return records


def _box(record: Mapping[str, Any]) -> tuple[float, float, float, float] | None:
    raw = record.get("rendered_pixel_box", record.get("box"))
    if not isinstance(raw, Sequence) or isinstance(raw, (str, bytes)) or len(raw) != 4:
        return None
    left, top, width, height = (float(value) for value in raw)
    return left, top, left + width, top + height


def _center(record: Mapping[str, Any]) -> tuple[float, float] | None:
    raw = record.get("center")
    if raw is None and isinstance(record.get("geometry"), Mapping):
        raw = record["geometry"].get("center")
    if isinstance(raw, Sequence) and not isinstance(raw, (str, bytes)) and len(raw) == 2:
        return float(raw[0]), float(raw[1])
    bounds = _box(record)
    if bounds is None:
        return None
    return (bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2


def _draw_record(draw: ImageDraw.ImageDraw, record: Mapping[str, Any]) -> None:
    bounds = _box(record)
    if bounds is not None:
        draw.rectangle(bounds, fill=255)
        return
    line = record.get("line")
    if isinstance(line, Sequence) and len(line) == 2:
        draw.line([tuple(line[0]), tuple(line[1])], fill=255, width=5)
        return
    polygon = record.get("polygon")
    if isinstance(polygon, Sequence) and len(polygon) >= 3:
        draw.polygon([tuple(point) for point in polygon], fill=255)


def _mask_channels(
    annotation: Mapping[str, Any],
    size: tuple[int, int],
) -> tuple[np.ndarray, np.ndarray]:
    ocr = Image.new("L", size, 0)
    artifact = Image.new("L", size, 0)
    ocr_draw = ImageDraw.Draw(ocr)
    artifact_draw = ImageDraw.Draw(artifact)
    for record in _records(annotation, "texts"):
        _draw_record(ocr_draw, record)
    for key in ("axes", "ticks", "dividers", "arrows", "brackets", "legends", "top_bars"):
        for record in _records(annotation, key):
            _draw_record(artifact_draw, record)
    for record in annotation.get("hard_negatives", []):
        target = ocr_draw if record.get("kind") in {
            "marker_like_letter", "isolated_punctuation"
        } else artifact_draw
        _draw_record(target, record)
    return (
        np.asarray(ocr, dtype=np.float32) / 255.0,
        np.asarray(artifact, dtype=np.float32) / 255.0,
    )


def _family_identity(scene: Mapping[str, Any]) -> str:
    return "|".join(
        f"{axis}={scene['families'][axis]['key']}"
        for axis in ("renderer", "font", "degradation", "template", "marker")
    )


def _adapt(scene: Mapping[str, Any]) -> FamilyScene:
    image, annotation, _ = render_scene(scene)
    rgb = image.convert("RGB")
    gray = np.asarray(rgb.convert("L"), dtype=np.float32) / 255.0
    ocr, artifact = _mask_channels(annotation, rgb.size)
    markers = _records(annotation, "markers")
    centers = tuple((float(item["center"][0]), float(item["center"][1])) for item in markers)
    diameters = tuple(float(item["radius"]) * 2.0 for item in markers)
    hard_negatives = tuple(
        (str(item["kind"]), *center)
        for item in annotation.get("hard_negatives", [])
        if (center := _center(item)) is not None
    )
    return FamilyScene(
        split=str(_scene_split(scene)),
        family=_family_identity(scene),
        seed=int(scene["seed"]),
        tensor=torch.from_numpy(np.stack((1.0 - gray, ocr, artifact), axis=0).copy()),
        centers=centers,
        diameters=diameters,
        hard_negatives=hard_negatives,
    )


@lru_cache(maxsize=3)
def build_family_split(split: str, seed: int = 393) -> tuple[FamilyScene, ...]:
    """Build one deterministic train/dev family holdout without disk artifacts."""

    normalized = "validation" if split == "dev" else split
    if normalized not in {"train", "validation"}:
        raise ValueError("split must be 'train', 'dev', or 'validation'")
    scenes = _build_scenes(PRESETS["smoke"], seed, require_complete_style_catalog=True)
    return tuple(_adapt(scene) for scene in scenes if _scene_split(scene) == normalized)


__all__ = ["FamilyScene", "build_family_split"]
