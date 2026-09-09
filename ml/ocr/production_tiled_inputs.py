# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authenticated production-panel inputs for a future tiled OCR revision.

This module is model-free.  It validates the existing V3 runtime binding before
regenerating project-owned text truth, then projects that truth into the exact
runtime crops and V38 tile contract.  A projection is a training target view;
the source truth denominator remains one record per rendered source text.
"""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import math
from pathlib import Path
from typing import Any, Mapping, Sequence

import numpy as np

from ml.markers.center.mask_preserving_v24 import family_scenes
from ml.markers.center.plot_domain_v25 import runtime_domain_binding
from ml.markers.center.plot_domain_v25 import runtime_domain_binding_v3


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
TILE_SIZE = 256
TILE_OVERLAP = 64
TILE_STEP = TILE_SIZE - TILE_OVERLAP
EXPECTED_TRAIN_SOURCE_COUNT = 20
EXPECTED_DEV_SOURCE_COUNT = 3
EXPECTED_TRAIN_PANEL_COUNT = 28
EXPECTED_DEV_PANEL_COUNT = 9
EXPECTED_TRAIN_TEXT_TRUTH_COUNT = 709
EXPECTED_DEV_TEXT_TRUTH_COUNT = 183


class ProductionTiledInputError(ValueError):
    """Authenticated runtime panels and regenerated source truth disagree."""


Box = tuple[float, float, float, float]


@dataclass(frozen=True)
class PanelTextProjection:
    truth_id: str
    source_text_id: str
    panel_id: str
    source_visible_box: Box
    panel_box: Box
    status: str


@dataclass(frozen=True)
class SourceTextTruth:
    truth_id: str
    split: str
    dataset_seed: int
    family: str
    scene_seed: int
    source_sha256: str
    source_text_id: str
    role: str
    source_box: Box
    projections: tuple[PanelTextProjection, ...]
    projection_status: str


@dataclass(frozen=True)
class ProductionOcrTile:
    panel_id: str
    left: int
    top: int
    valid_width: int
    valid_height: int
    input_values: np.ndarray
    target: np.ndarray
    source_truth_ids: tuple[str, ...]


@dataclass(frozen=True)
class ProductionTiledPanel:
    split: str
    dataset_seed: int
    family: str
    scene_seed: int
    source_sha256: str
    panel_id: str
    panel_sha256: str
    width: int
    height: int
    crop: tuple[int, int, int, int]
    requested_crop: tuple[float, float, float, float]
    source_to_panel_matrix: tuple[float, ...]
    panel_to_source_matrix: tuple[float, ...]
    gray8: np.ndarray
    projections: tuple[PanelTextProjection, ...]
    tiles: tuple[ProductionOcrTile, ...]


@dataclass(frozen=True)
class ProductionTiledSplit:
    name: str
    source_count: int
    panel_count: int
    full_source_truth_count: int
    projected_source_truth_count: int
    outside_runtime_crop_truth_count: int
    partial_source_truth_count: int
    overlapping_source_truth_count: int
    source_truths: tuple[SourceTextTruth, ...]
    panels: tuple[ProductionTiledPanel, ...]


@dataclass(frozen=True)
class ProductionTiledInputs:
    binding_sha256: str
    train: ProductionTiledSplit
    dev: ProductionTiledSplit


def load_production_tiled_inputs(
    binding_path: Path,
    expected_binding_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> ProductionTiledInputs:
    """Validate V3 evidence, then expose immutable production tiles and truth views."""

    root = repository_root.resolve()
    binding = runtime_domain_binding_v3.load_runtime_domain_binding_v3(
        binding_path, expected_binding_sha256, repository_root=root
    )
    if binding.failures:
        raise ProductionTiledInputError("V3 runtime binding retains failed panels")
    train = _build_split(
        "train",
        binding.train,
        binding.profile.train,
        EXPECTED_TRAIN_SOURCE_COUNT,
        EXPECTED_TRAIN_PANEL_COUNT,
        EXPECTED_TRAIN_TEXT_TRUTH_COUNT,
    )
    dev = _build_split(
        "validation",
        binding.dev,
        binding.profile.dev,
        EXPECTED_DEV_SOURCE_COUNT,
        EXPECTED_DEV_PANEL_COUNT,
        EXPECTED_DEV_TEXT_TRUTH_COUNT,
    )
    if {truth.source_sha256 for truth in train.source_truths} & {
        truth.source_sha256 for truth in dev.source_truths
    }:
        raise ProductionTiledInputError("train and development source truth identities overlap")
    if {panel.panel_id for panel in train.panels} & {panel.panel_id for panel in dev.panels}:
        raise ProductionTiledInputError("train and development panel identities overlap")
    return ProductionTiledInputs(binding.binding_sha256, train, dev)


def _build_split(
    name: str,
    panel_domains: Sequence[runtime_domain_binding.RuntimePanelDomain],
    profile: runtime_domain_binding_v3.V3SplitProfile,
    expected_sources: int,
    expected_panels: int,
    expected_truths: int,
) -> ProductionTiledSplit:
    panels = tuple(panel_domains)
    if len(panels) != expected_panels:
        raise ProductionTiledInputError(f"{name} runtime panel denominator changed")
    if any(panel.runtime_input.split != name for panel in panels):
        raise ProductionTiledInputError(f"{name} runtime panels carry a foreign split")
    by_dataset: dict[int, list[runtime_domain_binding.RuntimePanelDomain]] = {}
    for panel in panels:
        by_dataset.setdefault(panel.runtime_input.dataset_seed, []).append(panel)
    if set(by_dataset) != set(profile.dataset_seeds):
        raise ProductionTiledInputError(f"{name} dataset seed coverage changed")

    regenerated: dict[str, Any] = {}
    for dataset_seed in profile.dataset_seeds:
        runtime_panels = tuple(item.runtime_input for item in by_dataset[dataset_seed])
        current = runtime_domain_binding_v3._regenerate_v3(
            name, dataset_seed, runtime_panels, profile
        )
        if set(regenerated) & set(current):
            raise ProductionTiledInputError(f"{name} regenerated duplicate source identities")
        regenerated.update(current)
    if len(regenerated) != expected_sources:
        raise ProductionTiledInputError(f"{name} source denominator changed")

    panels_by_source: dict[str, list[runtime_domain_binding.RuntimePanelDomain]] = {}
    for panel in panels:
        panels_by_source.setdefault(panel.runtime_input.source_sha256, []).append(panel)
    if set(panels_by_source) != set(regenerated):
        raise ProductionTiledInputError(f"{name} runtime and regenerated source sets differ")

    panel_projections: dict[str, list[PanelTextProjection]] = {
        panel.runtime_input.panel_id: [] for panel in panels
    }
    truths: list[SourceTextTruth] = []
    for source_sha in sorted(regenerated):
        rendered = regenerated[source_sha]
        source_panels = sorted(
            panels_by_source[source_sha], key=lambda item: item.runtime_input.panel_id
        )
        if any(
            panel.source_width != rendered.width or panel.source_height != rendered.height
            for panel in source_panels
        ):
            raise ProductionTiledInputError(
                f"{name} runtime source dimensions differ from regenerated truth"
            )
        source_truths = _source_truth_records(
            name, rendered.annotation, source_sha, source_panels, rendered.width, rendered.height
        )
        for truth in source_truths:
            truths.append(truth)
            for projection in truth.projections:
                panel_projections[projection.panel_id].append(projection)
    if len(truths) != expected_truths:
        raise ProductionTiledInputError(f"{name} full source text denominator changed")
    if len({truth.truth_id for truth in truths}) != len(truths):
        raise ProductionTiledInputError(f"{name} source truth identities repeat")

    prepared_panels = tuple(
        _prepare_panel(panel, tuple(panel_projections[panel.runtime_input.panel_id]))
        for panel in sorted(panels, key=lambda item: item.runtime_input.panel_id)
    )
    projected = sum(bool(truth.projections) for truth in truths)
    partial = sum(any(item.status == "partial" for item in truth.projections) for truth in truths)
    overlapping = sum(len(truth.projections) > 1 for truth in truths)
    return ProductionTiledSplit(
        name,
        len(regenerated),
        len(prepared_panels),
        len(truths),
        projected,
        len(truths) - projected,
        partial,
        overlapping,
        tuple(truths),
        prepared_panels,
    )


def _source_truth_records(
    split: str,
    annotation: Mapping[str, Any],
    source_sha256: str,
    panels: Sequence[runtime_domain_binding.RuntimePanelDomain],
    source_width: int,
    source_height: int,
) -> tuple[SourceTextTruth, ...]:
    if not panels:
        raise ProductionTiledInputError("source truth has no runtime panels")
    first = panels[0].runtime_input
    for panel in panels:
        current = panel.runtime_input
        if (
            current.dataset_seed != first.dataset_seed
            or current.family != first.family
            or current.scene_seed != first.scene_seed
        ):
            raise ProductionTiledInputError("runtime panels disagree on source identity")

    output: list[SourceTextTruth] = []
    source_text_ids: set[str] = set()
    for record in family_scenes._records(annotation, "texts"):
        if record.get("visible", True) is False or not str(record.get("text", "")).strip():
            continue
        if record.get("rendered_pixel_box") is None:
            # The existing V3 OCR evaluator defines source truth as a visible,
            # nonempty record with an observed rendered pixel box.  Semantic
            # text with no rendered pixels cannot become an occupancy target.
            continue
        source_box = family_scenes._box(record)
        if source_box is None:
            raise ProductionTiledInputError("visible source text has an invalid rendered pixel box")
        _validate_box(source_box, source_width, source_height, "source text")
        text_id = str(record.get("text_id", "")).strip()
        if not text_id:
            raise ProductionTiledInputError("visible source text lacks an authoritative identity")
        if text_id in source_text_ids:
            raise ProductionTiledInputError("source text identities repeat within a source")
        source_text_ids.add(text_id)
        truth_id = sha256(f"{source_sha256}\n{text_id}".encode("utf-8")).hexdigest()
        projections = tuple(
            projection
            for panel in panels
            if (projection := _project_truth(truth_id, text_id, source_box, panel)) is not None
        )
        output.append(
            SourceTextTruth(
                truth_id,
                split,
                first.dataset_seed,
                first.family,
                first.scene_seed,
                source_sha256,
                text_id,
                str(record.get("role", "other")),
                source_box,
                projections,
                _projection_status(source_box, projections),
            )
        )
    return tuple(output)


def _project_truth(
    truth_id: str,
    text_id: str,
    source_box: Box,
    panel: runtime_domain_binding.RuntimePanelDomain,
) -> PanelTextProjection | None:
    runtime = panel.runtime_input
    crop_left, crop_top, crop_width, crop_height = runtime.crop
    visible = (
        max(source_box[0], float(crop_left)),
        max(source_box[1], float(crop_top)),
        min(source_box[2], float(crop_left + crop_width)),
        min(source_box[3], float(crop_top + crop_height)),
    )
    if visible[2] <= visible[0] or visible[3] <= visible[1]:
        return None
    corners = tuple(
        runtime_domain_binding._map_point(panel.source_to_panel_matrix, x, y)
        for x, y in (
            (visible[0], visible[1]),
            (visible[2], visible[1]),
            (visible[2], visible[3]),
            (visible[0], visible[3]),
        )
    )
    epsilon = 1e-6
    if (
        abs(corners[0][1] - corners[1][1]) > epsilon
        or abs(corners[1][0] - corners[2][0]) > epsilon
        or abs(corners[2][1] - corners[3][1]) > epsilon
        or abs(corners[3][0] - corners[0][0]) > epsilon
    ):
        raise ProductionTiledInputError("source crop transform rotates or skews text boxes")
    panel_box = (
        min(point[0] for point in corners),
        min(point[1] for point in corners),
        max(point[0] for point in corners),
        max(point[1] for point in corners),
    )
    _validate_box(panel_box, runtime.width, runtime.height, "projected panel text", epsilon)
    return PanelTextProjection(
        truth_id,
        text_id,
        runtime.panel_id,
        visible,
        panel_box,
        "full" if visible == source_box else "partial",
    )


def _projection_status(source_box: Box, projections: Sequence[PanelTextProjection]) -> str:
    if not projections:
        return "outside_runtime_crops"
    has_partial = any(item.status == "partial" for item in projections)
    if len(projections) == 1:
        return "single_partial_projection" if has_partial else "single_full_projection"
    return "overlapping_partial_projections" if has_partial else "overlapping_full_projections"


def _prepare_panel(
    panel: runtime_domain_binding.RuntimePanelDomain,
    projections: tuple[PanelTextProjection, ...],
) -> ProductionTiledPanel:
    runtime = panel.runtime_input
    if runtime.gray8.shape != (runtime.height, runtime.width) or runtime.gray8.dtype != np.uint8:
        raise ProductionTiledInputError("runtime Gray8 plane has a foreign shape or type")
    gray8 = _immutable_array(runtime.gray8, np.uint8, (runtime.height, runtime.width))
    tiles = _build_tiles(runtime.panel_id, gray8, projections)
    return ProductionTiledPanel(
        runtime.split,
        runtime.dataset_seed,
        runtime.family,
        runtime.scene_seed,
        runtime.source_sha256,
        runtime.panel_id,
        runtime.panel_sha256,
        runtime.width,
        runtime.height,
        runtime.crop,
        runtime.requested_crop,
        panel.source_to_panel_matrix,
        panel.panel_to_source_matrix,
        gray8,
        projections,
        tiles,
    )


def _build_tiles(
    panel_id: str,
    gray8: np.ndarray,
    projections: Sequence[PanelTextProjection],
) -> tuple[ProductionOcrTile, ...]:
    height, width = gray8.shape
    output: list[ProductionOcrTile] = []
    for top in tile_starts(height):
        for left in tile_starts(width):
            valid_width = min(TILE_SIZE, width - left)
            valid_height = min(TILE_SIZE, height - top)
            image = np.full((TILE_SIZE, TILE_SIZE), 255, dtype=np.uint8)
            image[:valid_height, :valid_width] = gray8[
                top : top + valid_height, left : left + valid_width
            ]
            target = np.zeros((TILE_SIZE, TILE_SIZE), dtype=np.uint8)
            truth_ids: list[str] = []
            for projection in projections:
                x0 = max(float(left), projection.panel_box[0])
                y0 = max(float(top), projection.panel_box[1])
                x1 = min(float(left + valid_width), projection.panel_box[2])
                y1 = min(float(top + valid_height), projection.panel_box[3])
                if x1 > x0 and y1 > y0:
                    target[int(y0 - top) : int(y1 - top), int(x0 - left) : int(x1 - left)] = 1
                    truth_ids.append(projection.truth_id)
            values = (1.0 - image.astype(np.float32) / np.float32(255.0))[None, :, :]
            frozen_values = _immutable_array(values, np.float32, (1, TILE_SIZE, TILE_SIZE))
            frozen_target = _immutable_array(
                target[None, :, :].astype(np.float32), np.float32, (1, TILE_SIZE, TILE_SIZE)
            )
            output.append(
                ProductionOcrTile(
                    panel_id,
                    left,
                    top,
                    valid_width,
                    valid_height,
                    frozen_values,
                    frozen_target,
                    tuple(sorted(set(truth_ids))),
                )
            )
    return tuple(output)


def tile_starts(length: int) -> tuple[int, ...]:
    if type(length) is not int or length <= 0:
        raise ProductionTiledInputError("tile dimensions must be positive integers")
    if length <= TILE_SIZE:
        return (0,)
    starts = list(range(0, length - TILE_SIZE + 1, TILE_STEP))
    if starts[-1] != length - TILE_SIZE:
        starts.append(length - TILE_SIZE)
    return tuple(starts)


def _immutable_array(array: np.ndarray, dtype: np.dtype[Any], shape: tuple[int, ...]) -> np.ndarray:
    contiguous = np.ascontiguousarray(array, dtype=dtype)
    if contiguous.shape != shape or not np.isfinite(contiguous).all():
        raise ProductionTiledInputError("prepared OCR tensor is invalid")
    return np.frombuffer(contiguous.tobytes(order="C"), dtype=dtype).reshape(shape)


def _validate_box(
    box: Box,
    width: int,
    height: int,
    label: str,
    epsilon: float = 0.0,
) -> None:
    if (
        not all(math.isfinite(value) for value in box)
        or box[2] <= box[0]
        or box[3] <= box[1]
        or box[0] < -epsilon
        or box[1] < -epsilon
        or box[2] > width + epsilon
        or box[3] > height + epsilon
    ):
        raise ProductionTiledInputError(f"{label} is outside its authenticated raster")


__all__ = [
    "PanelTextProjection",
    "ProductionOcrTile",
    "ProductionTiledInputError",
    "ProductionTiledInputs",
    "ProductionTiledPanel",
    "ProductionTiledSplit",
    "SourceTextTruth",
    "load_production_tiled_inputs",
    "tile_starts",
]
