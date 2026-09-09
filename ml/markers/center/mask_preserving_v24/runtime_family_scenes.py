# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Join validated runtime planes to project-owned synthetic family labels.

Runtime inputs must pass :func:`load_bound_runtime_inputs` before this module is
called.  Annotation is regenerated only after exact source and split coverage
has been established.  It supplies labels and an omission audit, never tensor
mask channels.
"""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
from io import BytesIO
import json
import math
from typing import Any, Mapping, Sequence

import numpy as np
import torch

from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.renderer import render_scene

from .family_scenes import FamilyScene, _box, _center, _family_identity, _records
from .runtime_inputs import ValidatedRuntimeInputs, ValidatedRuntimePanelInput


DESCRIPTIVE_CATEGORIES = (
    "texts",
    "axes",
    "ticks",
    "dividers",
    "arrows",
    "brackets",
    "legends",
    "top_bars",
)


class RuntimeFamilySceneError(ValueError):
    """Validated runtime inputs cannot be joined to exact synthetic labels."""


@dataclass(frozen=True)
class RuntimeFamilyScene(FamilyScene):
    source_sha256: str
    panel_id: str
    panel_sha256: str
    crop: tuple[int, int, int, int]
    requested_crop: tuple[float, float, float, float]
    dataset_seed: int
    sampling_identity: str


@dataclass(frozen=True)
class OmittedRuntimeRecord:
    category: str
    index: int
    kind: str
    reason: str


@dataclass(frozen=True)
class RuntimePanelMappingAudit:
    panel_id: str
    marker_count: int
    hard_negative_count: int
    descriptive_record_count: int


@dataclass(frozen=True)
class RuntimeSourceMappingAudit:
    split: str
    source_sha256: str
    source_marker_count: int
    mapped_marker_count: int
    source_hard_negative_count: int
    mapped_hard_negative_count: int
    source_descriptive_record_count: int
    mapped_descriptive_record_count: int
    panels: tuple[RuntimePanelMappingAudit, ...]
    omitted: tuple[OmittedRuntimeRecord, ...]


@dataclass(frozen=True)
class RuntimeFamilySceneJoin:
    train: tuple[RuntimeFamilyScene, ...]
    dev: tuple[RuntimeFamilyScene, ...]
    audit: tuple[RuntimeSourceMappingAudit, ...]


@dataclass(frozen=True)
class _RenderedSource:
    scene: Mapping[str, Any]
    annotation: Mapping[str, Any]
    source_sha256: str
    width: int
    height: int


def join_runtime_family_scenes(inputs: ValidatedRuntimeInputs) -> RuntimeFamilySceneJoin:
    """Return panel-bound family scenes built from already validated planes.

    Train and development source coverage must exactly match the selected smoke
    dataset.  Marker targets may never be lost, clipped, or mapped twice.
    Marker-free panels remain valid negative scenes.  Artifacts, text, and hard
    negatives outside actual encoded crops are counted in the returned audit.
    """

    if not isinstance(inputs, ValidatedRuntimeInputs):
        raise RuntimeFamilySceneError("runtime family join requires validated runtime inputs")
    train, train_audit, train_dataset_seed = _join_split(inputs.train, "train")
    dev, dev_audit, dev_dataset_seed = _join_split(inputs.dev, "validation")
    if train_dataset_seed != dev_dataset_seed:
        raise RuntimeFamilySceneError("train and validation inputs use different dataset identities")
    return RuntimeFamilySceneJoin(train, dev, train_audit + dev_audit)


def _join_split(
    panels: tuple[ValidatedRuntimePanelInput, ...],
    expected_split: str,
) -> tuple[tuple[RuntimeFamilyScene, ...], tuple[RuntimeSourceMappingAudit, ...], int]:
    if not panels:
        raise RuntimeFamilySceneError(f"{expected_split} runtime inputs are empty")
    if any(panel.split != expected_split for panel in panels):
        raise RuntimeFamilySceneError(
            f"{expected_split} runtime inputs contain a foreign or sealed split"
        )
    dataset_seeds = {panel.dataset_seed for panel in panels}
    if len(dataset_seeds) != 1:
        raise RuntimeFamilySceneError(f"{expected_split} runtime inputs mix dataset identities")
    dataset_seed = next(iter(dataset_seeds))

    panel_groups: dict[str, list[ValidatedRuntimePanelInput]] = {}
    for panel in panels:
        _validate_panel_shape(panel)
        panel_groups.setdefault(panel.source_sha256, []).append(panel)

    # Rendering returns image and annotation together.  Keep annotation opaque
    # until the exact selected source set, source bytes, seed, family, and image
    # dimensions have all been matched to the validated runtime inputs.
    rendered_sources: dict[str, _RenderedSource] = {}
    selected_scenes = [
        scene for scene in _build_scenes(
            PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True
        )
        if _scene_split(scene) == expected_split
    ]
    if not selected_scenes:
        raise RuntimeFamilySceneError(f"selected {expected_split} smoke dataset is empty")
    for scene in selected_scenes:
        image, annotation, _ = render_scene(scene)
        rgb = image.convert("RGB")
        stream = BytesIO()
        rgb.save(stream, format="PNG")
        source_sha = sha256(stream.getvalue()).hexdigest()
        if source_sha in rendered_sources:
            raise RuntimeFamilySceneError("selected smoke scenes produced duplicate source bytes")
        rendered_sources[source_sha] = _RenderedSource(
            scene=scene,
            annotation=annotation,
            source_sha256=source_sha,
            width=rgb.width,
            height=rgb.height,
        )

    expected_sources = set(rendered_sources)
    actual_sources = set(panel_groups)
    if actual_sources != expected_sources:
        raise RuntimeFamilySceneError(
            "runtime source coverage differs from the selected smoke dataset: "
            f"missing={sorted(expected_sources - actual_sources)}, "
            f"foreign={sorted(actual_sources - expected_sources)}"
        )
    for source_sha, source_panels in panel_groups.items():
        rendered = rendered_sources[source_sha]
        scene_seed = int(rendered.scene["seed"])
        family = _family_identity(rendered.scene)
        if any(
            panel.scene_seed != scene_seed
            or panel.family != family
            or panel.dataset_seed != dataset_seed
            for panel in source_panels
        ):
            raise RuntimeFamilySceneError(
                "runtime source seed, family, or dataset identity differs from regenerated source"
            )
        if any(
            panel.crop[0] + panel.crop[2] > rendered.width
            or panel.crop[1] + panel.crop[3] > rendered.height
            for panel in source_panels
        ):
            raise RuntimeFamilySceneError("runtime panel crop exceeds regenerated source dimensions")

    joined_by_panel: dict[str, RuntimeFamilyScene] = {}
    audits: list[RuntimeSourceMappingAudit] = []
    # Only now may labels be read from the regenerated annotations.
    for source_sha, rendered in rendered_sources.items():
        source_panels = panel_groups[source_sha]
        joined, audit = _join_source(rendered, source_panels, expected_split, dataset_seed)
        joined_by_panel.update((scene.panel_id, scene) for scene in joined)
        audits.append(audit)
    if set(joined_by_panel) != {panel.panel_id for panel in panels}:
        raise RuntimeFamilySceneError("runtime panel scene coverage is incomplete or duplicated")
    return tuple(joined_by_panel[panel.panel_id] for panel in panels), tuple(audits), dataset_seed


def _join_source(
    rendered: _RenderedSource,
    panels: list[ValidatedRuntimePanelInput],
    split: str,
    dataset_seed: int,
) -> tuple[tuple[RuntimeFamilyScene, ...], RuntimeSourceMappingAudit]:
    annotation = rendered.annotation
    markers = _records(annotation, "markers")
    hard_negatives = _records(annotation, "hard_negatives")
    marker_labels: dict[str, list[tuple[float, float, float]]] = {
        panel.panel_id: [] for panel in panels
    }
    hard_negative_labels: dict[str, list[tuple[str, float, float]]] = {
        panel.panel_id: [] for panel in panels
    }
    descriptive_counts = {panel.panel_id: 0 for panel in panels}
    omitted: list[OmittedRuntimeRecord] = []

    for index, marker in enumerate(markers):
        center, radius = _marker_geometry(marker, index)
        extent = (
            center[0] - radius,
            center[1] - radius,
            center[0] + radius,
            center[1] + radius,
        )
        matches = _panels_containing_extent(panels, extent)
        if len(matches) != 1:
            reason = "lost_or_clipped" if not matches else "mapped_to_multiple_panels"
            raise RuntimeFamilySceneError(f"marker {index} {reason}")
        panel = matches[0]
        x, y, _, _ = panel.crop
        marker_labels[panel.panel_id].append((center[0] - x, center[1] - y, radius))

    for index, record in enumerate(hard_negatives):
        center = _safe_center(record)
        kind = str(record.get("kind", "hard_negative"))
        if center is None:
            omitted.append(OmittedRuntimeRecord("hard_negatives", index, kind, "unmappable_geometry"))
            continue
        matches = _panels_containing_point(panels, center)
        if len(matches) > 1:
            raise RuntimeFamilySceneError(f"hard negative {index} maps to multiple panels")
        if not matches:
            omitted.append(OmittedRuntimeRecord("hard_negatives", index, kind, "outside_runtime_crops"))
            continue
        panel = matches[0]
        x, y, _, _ = panel.crop
        hard_negative_labels[panel.panel_id].append((kind, center[0] - x, center[1] - y))

    source_descriptive_count = 0
    mapped_descriptive_count = 0
    for category in DESCRIPTIVE_CATEGORIES:
        for index, record in enumerate(_records(annotation, category)):
            source_descriptive_count += 1
            kind = str(record.get("kind", category))
            extent = _record_extent(record)
            if extent is None:
                omitted.append(OmittedRuntimeRecord(category, index, kind, "unmappable_geometry"))
                continue
            matches = _panels_containing_extent(panels, extent)
            if len(matches) > 1:
                raise RuntimeFamilySceneError(
                    f"descriptive {category} record {index} maps to multiple panels"
                )
            if not matches:
                omitted.append(
                    OmittedRuntimeRecord(category, index, kind, "outside_or_clipped_by_runtime_crops")
                )
                continue
            descriptive_counts[matches[0].panel_id] += 1
            mapped_descriptive_count += 1

    scenes: list[RuntimeFamilyScene] = []
    panel_audits: list[RuntimePanelMappingAudit] = []
    for panel in panels:
        labels = marker_labels[panel.panel_id]
        centers = tuple((x, y) for x, y, _ in labels)
        diameters = tuple(radius * 2.0 for _, _, radius in labels)
        hard = tuple(hard_negative_labels[panel.panel_id])
        tensor = _runtime_tensor(panel)
        scenes.append(
            RuntimeFamilyScene(
                split=split,
                family=panel.family,
                seed=panel.scene_seed,
                tensor=tensor,
                centers=centers,
                diameters=diameters,
                hard_negatives=hard,
                source_sha256=panel.source_sha256,
                panel_id=panel.panel_id,
                panel_sha256=panel.panel_sha256,
                crop=panel.crop,
                requested_crop=panel.requested_crop,
                dataset_seed=dataset_seed,
                sampling_identity=_sampling_identity(panel),
            )
        )
        panel_audits.append(
            RuntimePanelMappingAudit(
                panel.panel_id,
                len(labels),
                len(hard),
                descriptive_counts[panel.panel_id],
            )
        )

    audit = RuntimeSourceMappingAudit(
        split=split,
        source_sha256=rendered.source_sha256,
        source_marker_count=len(markers),
        mapped_marker_count=sum(len(value) for value in marker_labels.values()),
        source_hard_negative_count=len(hard_negatives),
        mapped_hard_negative_count=sum(len(value) for value in hard_negative_labels.values()),
        source_descriptive_record_count=source_descriptive_count,
        mapped_descriptive_record_count=mapped_descriptive_count,
        panels=tuple(panel_audits),
        omitted=tuple(omitted),
    )
    return tuple(scenes), audit


def _runtime_tensor(panel: ValidatedRuntimePanelInput) -> torch.Tensor:
    gray = panel.gray8.astype(np.float32, copy=False) / np.float32(255.0)
    channels = np.stack(
        (
            np.float32(1.0) - gray,
            panel.ocr_mask.astype(np.float32, copy=False),
            panel.artifact_mask.astype(np.float32, copy=False),
        ),
        axis=0,
    )
    return torch.from_numpy(channels.copy())


def _validate_panel_shape(panel: ValidatedRuntimePanelInput) -> None:
    x, y, width, height = panel.crop
    if (
        x < 0
        or y < 0
        or width != panel.width
        or height != panel.height
        or width <= 0
        or height <= 0
        or panel.gray8.shape != (height, width)
        or panel.ocr_mask.shape != (height, width)
        or panel.artifact_mask.shape != (height, width)
    ):
        raise RuntimeFamilySceneError("validated runtime panel has inconsistent crop or plane dimensions")


def _marker_geometry(
    record: Mapping[str, Any], index: int
) -> tuple[tuple[float, float], float]:
    raw_center = record.get("center")
    if (
        not isinstance(raw_center, Sequence)
        or isinstance(raw_center, (str, bytes))
        or len(raw_center) != 2
    ):
        raise RuntimeFamilySceneError(f"marker {index} has no explicit source-pixel center")
    try:
        center = (float(raw_center[0]), float(raw_center[1]))
        radius = float(record["radius"])
    except (KeyError, TypeError, ValueError) as exception:
        raise RuntimeFamilySceneError(f"marker {index} has invalid source-pixel geometry") from exception
    if not all(math.isfinite(value) for value in (*center, radius)) or radius <= 0:
        raise RuntimeFamilySceneError(f"marker {index} has invalid source-pixel geometry")
    return center, radius


def _safe_center(record: Mapping[str, Any]) -> tuple[float, float] | None:
    try:
        center = _center(record)
    except (TypeError, ValueError):
        return None
    if center is None or not all(math.isfinite(value) for value in center):
        return None
    return center


def _record_extent(record: Mapping[str, Any]) -> tuple[float, float, float, float] | None:
    try:
        bounds = _box(record)
    except (TypeError, ValueError):
        bounds = None
    if bounds is not None and all(math.isfinite(value) for value in bounds):
        return bounds
    for key in ("line", "polygon"):
        raw = record.get(key)
        if not isinstance(raw, Sequence) or isinstance(raw, (str, bytes)) or not raw:
            continue
        try:
            points = [(float(point[0]), float(point[1])) for point in raw]
        except (IndexError, TypeError, ValueError):
            continue
        values = [coordinate for point in points for coordinate in point]
        if all(math.isfinite(value) for value in values):
            xs, ys = zip(*points)
            return min(xs), min(ys), max(xs), max(ys)
    center = _safe_center(record)
    if center is not None:
        return center[0], center[1], center[0], center[1]
    return None


def _panels_containing_extent(
    panels: list[ValidatedRuntimePanelInput],
    extent: tuple[float, float, float, float],
) -> list[ValidatedRuntimePanelInput]:
    left, top, right, bottom = extent
    if not all(math.isfinite(value) for value in extent) or right < left or bottom < top:
        return []
    if right == left and bottom == top:
        return _panels_containing_point(panels, (left, top))
    return [
        panel for panel in panels
        if left >= panel.crop[0]
        and top >= panel.crop[1]
        and right <= panel.crop[0] + panel.crop[2]
        and bottom <= panel.crop[1] + panel.crop[3]
    ]


def _panels_containing_point(
    panels: list[ValidatedRuntimePanelInput],
    point: tuple[float, float],
) -> list[ValidatedRuntimePanelInput]:
    x, y = point
    return [
        panel for panel in panels
        if panel.crop[0] <= x < panel.crop[0] + panel.crop[2]
        and panel.crop[1] <= y < panel.crop[1] + panel.crop[3]
    ]


def _sampling_identity(panel: ValidatedRuntimePanelInput) -> str:
    payload = json.dumps(
        {
            "schema": "graphreader.runtime-family-panel-sampling.v1",
            "split": panel.split,
            "dataset_seed": panel.dataset_seed,
            "family": panel.family,
            "scene_seed": panel.scene_seed,
            "source_sha256": panel.source_sha256,
            "panel_id": panel.panel_id,
            "panel_sha256": panel.panel_sha256,
            "crop": panel.crop,
        },
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return sha256(payload).hexdigest()


__all__ = [
    "OmittedRuntimeRecord",
    "RuntimeFamilyScene",
    "RuntimeFamilySceneError",
    "RuntimeFamilySceneJoin",
    "RuntimePanelMappingAudit",
    "RuntimeSourceMappingAudit",
    "join_runtime_family_scenes",
]
