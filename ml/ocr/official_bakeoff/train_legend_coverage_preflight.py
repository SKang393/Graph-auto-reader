# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Publish a bounded train-only OCR legend coverage preflight.

The supplemental sources use existing train family profiles with three-row,
inside-plot legends at smaller font sizes. This module does not load a model,
run inference, open a revision, authorize training, parse dev truth, or read
dev pixels. It authenticates prior train raster identities and the fixed dev
binding to preserve split identity.
"""

from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
import math
import os
from pathlib import Path
import shutil
import statistics
from typing import Any, Mapping, Sequence

from ml.synthetic.dataset import CaseSpec, FAMILY_AXES, _build_scenes, _scene_split
from ml.synthetic.io import canonical_json_bytes, png_bytes
from ml.synthetic.ocr_sealed_acceptance import (
    ACCEPTANCE_SCOPE,
    PROTOCOL_PATH,
    PROTOCOL_SHA256,
    load_supported_protocol,
)
from ml.synthetic.runtime_graph_visible_content_v3 import render_visible_content_source


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
SCHEMA = "graphreader.train-legend-coverage-preflight.v1"
DIAGNOSIS_PATH = Path("docs/GOAL-22-OCR-LEGEND-EXTENT-DIAGNOSIS.json")
DIAGNOSIS_SHA256 = "c9a9903bad60e9b5a50b4c7eb73c48bf458fe9576f4873bd22a402ca04f4f204"
TEXT_EXTENT_PREFLIGHT_PATH = Path("artifacts/goal22-runs/ocr-text-extent-preflight/preflight-report.json")
TEXT_EXTENT_PREFLIGHT_SHA256 = "23afd6d3163c84998586b54e72ef28d46cf80c561ff2a63449a005b48188cc53"
TRAIN_DEV_SUPPORT_PATH = Path("artifacts/goal22-runs/ocr-v44-legend-train-dev-support/result-v2.json")
TRAIN_DEV_SUPPORT_SHA256 = "29833f2a843f468fe836e6a5e9f0baff6f6a2bccfb7113955aebb854da232432"
TARGET_RENDERED_WIDTH_FLOOR_PIXELS = 87.0
TARGET_RENDERED_HEIGHT_FLOOR_PIXELS = 9.0
EXPECTED_HISTORICAL_DEV = {
    "binding": {
        "path": "artifacts/goal22-runs/marker-v25-plot-domain/visible-content-v3-binding/binding-v3.json",
        "sha256": "bcf821a2aaadc5ee8e18bd97dbe0488d120d2ac974a249c00d76b6cfccf0d98a",
    },
    "manifest": {
        "path": "artifacts/goal22-runs/marker-v25-plot-domain/axis-preserving-v3-sources-run2/dev393/input-manifest.json",
        "sha256": "36ca412992581713cffaaa006b5babb84bc04ad6fbfacd92a6c7df84a5196ac6",
    },
    "oracle_request": {
        "path": "artifacts/goal22-runs/ocr-v39-pretrained-db-head/inverse-target-oracle-inputs-v1/request.json",
        "sha256": "ee14d67f651e20ec9479fb9e59ce1ea1ef77542b057f3d62312c476685520b0b",
    },
    "synthetic_truth": {
        "path": "artifacts/goal22-runs/ocr-v39-pretrained-db-head/inverse-target-oracle-inputs-v1/synthetic-truth.json",
        "sha256": "829beb67695011f936b08272e69bb718b8b35d33bdb94c32a45f521f523fc3f5",
    },
}
DATASET_SEED = 917
FONT_SIZES = (8, 10, 12)
RENDERER_FAMILIES = ("vector_clean", "print_monochrome")
EXPECTED_FAMILIES = {
    "vector_clean": {
        "renderer": "vector_clean",
        "font": "system_sans",
        "degradation": "none",
        "template": "classic_single",
        "marker": "geometric_basic",
    },
    "print_monochrome": {
        "renderer": "print_monochrome",
        "font": "system_serif",
        "degradation": "print_light",
        "template": "stacked_shared_axes",
        "marker": "mixed_print",
    },
}
HELD_OUT_DEV_FAMILIES = {
    "renderer": "scan_rough",
    "font": "system_mono",
    "degradation": "scan_noise",
    "template": "compact_legend",
    "marker": "symbolic",
}
SOURCE_PATHS = (
    Path("ml/synthetic/dataset.py"),
    Path("ml/synthetic/fonts.py"),
    Path("ml/synthetic/io.py"),
    Path("ml/synthetic/ocr_sealed_acceptance.py"),
    Path("ml/synthetic/renderer.py"),
    Path("ml/synthetic/runtime_graph_visible_content_v3.py"),
    Path("ml/synthetic/schema.py"),
    Path("ml/synthetic/sealed_acceptance.py"),
    Path("ml/synthetic/templates.py"),
    Path("ml/policy/goal22-ocr-sealed-coverage-v1.json"),
    Path("ml/policy/goal22-sealed-coverage-v1.json"),
    Path("docs/GOAL-22-OCR-LEGEND-EXTENT-DIAGNOSIS.json"),
    Path("ml/ocr/official_bakeoff/train_legend_coverage_preflight.py"),
)


class LegendCoveragePreflightError(ValueError):
    """The supplemental train-only coverage evidence is invalid."""


def case_specs() -> tuple[CaseSpec, ...]:
    """Return the fixed six-case train-family coverage matrix."""

    return tuple(
        CaseSpec(
            "alternating_treatments",
            renderer,
            1,
            36,
            presentation={"font_size_px": size, "legend_position": "inside"},
        )
        for renderer in RENDERER_FAMILIES
        for size in FONT_SIZES
    )


def _sha(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def _repo_path(root: Path, path: Path) -> str:
    resolved = path.resolve()
    if resolved != root and root not in resolved.parents:
        raise LegendCoveragePreflightError(f"path escaped repository: {path}")
    return resolved.relative_to(root).as_posix()


def _read_json(path: Path) -> dict[str, Any]:
    def reject(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise LegendCoveragePreflightError(f"duplicate key in {path}: {key}")
            result[key] = value
        return result

    value = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=reject)
    if not isinstance(value, dict):
        raise LegendCoveragePreflightError(f"JSON root must be an object: {path}")
    return value


def _verify_file(path: Path, expected: str, label: str) -> None:
    if not path.is_file() or _sha(path.read_bytes()) != expected:
        raise LegendCoveragePreflightError(f"{label} identity changed: {path}")


def _validate_diagnosis(root: Path) -> dict[str, Any]:
    path = root / DIAGNOSIS_PATH
    _verify_file(path, DIAGNOSIS_SHA256, "corrected legend diagnosis")
    document = _read_json(path)
    support = document.get("train_dev_support")
    if not isinstance(support, dict):
        raise LegendCoveragePreflightError("corrected legend diagnosis lacks train/dev support")
    if support.get("result") != {
        "path": TRAIN_DEV_SUPPORT_PATH.as_posix(),
        "sha256": TRAIN_DEV_SUPPORT_SHA256,
    }:
        raise LegendCoveragePreflightError("corrected legend support result binding changed")
    _verify_file(
        root / TRAIN_DEV_SUPPORT_PATH,
        TRAIN_DEV_SUPPORT_SHA256,
        "legend train/dev support aggregate",
    )
    holdout = support.get("categorical_holdout")
    if not isinstance(holdout, dict):
        raise LegendCoveragePreflightError("corrected legend diagnosis lacks holdout evidence")
    axes = holdout.get("axes")
    if (
        holdout.get("acceptance_scope") != ACCEPTANCE_SCOPE
        or holdout.get("all_five_axes_intentionally_train_dev_disjoint") is not True
        or holdout.get("empty_family_overlap_is_a_required_holdout_property") is not True
        or not isinstance(axes, dict)
    ):
        raise LegendCoveragePreflightError("corrected five-axis holdout contract changed")
    for axis in FAMILY_AXES:
        row = axes.get(axis)
        expected_train = sorted({families[axis] for families in EXPECTED_FAMILIES.values()})
        if (
            not isinstance(row, dict)
            or sorted(row.get("train", [])) != expected_train
            or row.get("dev") != [HELD_OUT_DEV_FAMILIES[axis]]
            or row.get("overlap") != []
            or row.get("intentionally_disjoint") is not True
        ):
            raise LegendCoveragePreflightError(f"corrected {axis} holdout changed")
    return document


def _source_bindings(root: Path) -> list[dict[str, str]]:
    rows = []
    for relative in SOURCE_PATHS:
        path = root / relative
        if not path.is_file():
            raise LegendCoveragePreflightError(f"bound source is missing: {relative}")
        rows.append({"path": relative.as_posix(), "sha256": _sha(path.read_bytes())})
    if len({row["path"] for row in rows}) != len(rows):
        raise LegendCoveragePreflightError("bound source inventory contains duplicates")
    return rows


def _validate_historical_bindings(
    root: Path,
) -> tuple[dict[str, Any], frozenset[str], frozenset[str]]:
    """Authenticate prior train rasters and fixed dev identity without dev data reads."""

    path = root / TEXT_EXTENT_PREFLIGHT_PATH
    _verify_file(path, TEXT_EXTENT_PREFLIGHT_SHA256, "text extent preflight")
    document = _read_json(path)
    historical = document.get("historical_dev")
    if not isinstance(historical, Mapping):
        raise LegendCoveragePreflightError("text extent preflight lacks historical dev binding")
    for name, expected in EXPECTED_HISTORICAL_DEV.items():
        if historical.get(name) != expected:
            raise LegendCoveragePreflightError(f"historical dev {name} descriptor changed")
        _verify_file(root / expected["path"], expected["sha256"], f"historical dev {name}")
    source_ids = historical.get("source_ids")
    if (
        historical.get("truth_count") != 183
        or historical.get("source_count") != 3
        or historical.get("truth_regenerations") != 0
        or historical.get("pixel_regenerations") != 0
        or not isinstance(source_ids, list)
        or len(source_ids) != 3
        or len(set(source_ids)) != 3
        or any(not isinstance(value, str) or len(value) != 64 for value in source_ids)
    ):
        raise LegendCoveragePreflightError("historical dev denominator or provenance changed")
    train_sources = document.get("sources")
    if not isinstance(train_sources, list) or len(train_sources) != 20:
        raise LegendCoveragePreflightError("historical train source inventory changed")
    train_source_ids: list[str] = []
    for index, row in enumerate(train_sources):
        if not isinstance(row, Mapping) or row.get("split") != "train":
            raise LegendCoveragePreflightError(f"historical train source {index} is invalid")
        source_id = row.get("source_id")
        if (
            not isinstance(source_id, str)
            or len(source_id) != 64
            or row.get("source_sha256") != source_id
            or row.get("image_sha256") != source_id
        ):
            raise LegendCoveragePreflightError(
                f"historical train source {index} identity changed"
            )
        train_source_ids.append(source_id)
    if len(set(train_source_ids)) != len(train_source_ids):
        raise LegendCoveragePreflightError("historical train source identities repeat")
    descriptor = {
        "preflight": {
            "path": TEXT_EXTENT_PREFLIGHT_PATH.as_posix(),
            "sha256": TEXT_EXTENT_PREFLIGHT_SHA256,
        },
        **EXPECTED_HISTORICAL_DEV,
        "source_count": 3,
        "truth_count": 183,
        "historical_train_source_count": len(train_source_ids),
        "descriptor_payloads_hashed_only": True,
        "truth_rows_parsed": 0,
        "pixels_read": 0,
    }
    return descriptor, frozenset(train_source_ids), frozenset(source_ids)


def _family_keys(scene: Mapping[str, Any]) -> dict[str, str]:
    families = scene.get("families")
    if not isinstance(families, Mapping) or set(families) != set(FAMILY_AXES):
        raise LegendCoveragePreflightError("scene five-axis family inventory changed")
    result = {}
    for axis in FAMILY_AXES:
        row = families.get(axis)
        if not isinstance(row, Mapping) or set(row) != {"key", "split"}:
            raise LegendCoveragePreflightError(f"scene {axis} family record is invalid")
        if row.get("split") != "train":
            raise LegendCoveragePreflightError(f"scene {axis} family is not train-only")
        key = row.get("key")
        if not isinstance(key, str) or key == HELD_OUT_DEV_FAMILIES[axis]:
            raise LegendCoveragePreflightError(f"scene leaked held-out {axis} family")
        result[axis] = key
    return result


def _legend_series_text(panel: Mapping[str, Any]) -> tuple[str, ...]:
    series = panel.get("series")
    legend = panel.get("legend")
    entries = legend.get("entries") if isinstance(legend, Mapping) else None
    if not isinstance(series, list) or len(series) != 3 or not isinstance(entries, list) or len(entries) != 3:
        raise LegendCoveragePreflightError("supplemental panel requires three series and legend entries")
    series_ids = [row.get("series_id") if isinstance(row, Mapping) else None for row in series]
    entry_ids = [row.get("series_id") if isinstance(row, Mapping) else None for row in entries]
    if (
        any(not isinstance(value, str) or not value for value in series_ids + entry_ids)
        or len(set(series_ids)) != 3
        or len(set(entry_ids)) != 3
        or entry_ids != series_ids
    ):
        raise LegendCoveragePreflightError("legend entries are not an ordered bijection with panel series")
    text: list[str] = []
    for series_row, entry in zip(series, entries, strict=True):
        series_text = series_row.get("legend_text")
        entry_text = entry.get("text")
        if not isinstance(series_text, str) or not series_text or entry_text != series_text:
            raise LegendCoveragePreflightError("legend entry text differs from its panel series")
        text.append(series_text)
    if len(set(text)) != len(text):
        raise LegendCoveragePreflightError("panel series legend text repeats")
    return tuple(text)


def _build_train_scenes(dataset_seed: int) -> tuple[dict[str, Any], ...]:
    specs = case_specs()
    scenes = _build_scenes(specs, dataset_seed, require_complete_style_catalog=False)
    if len(scenes) != len(specs):
        raise LegendCoveragePreflightError("supplemental scene count changed")
    expected_seeds = {dataset_seed * 100 + index for index in range(len(specs))}
    if {int(scene.get("seed", -1)) for scene in scenes} != expected_seeds:
        raise LegendCoveragePreflightError("supplemental scene seed schedule changed")
    for scene, spec in zip(scenes, specs, strict=True):
        if _scene_split(scene) != "train" or scene.get("design") != "alternating_treatments":
            raise LegendCoveragePreflightError("supplemental scene escaped train/design scope")
        if _family_keys(scene) != EXPECTED_FAMILIES[spec.renderer_family]:
            raise LegendCoveragePreflightError("supplemental scene family identity changed")
        presentation = scene.get("presentation")
        if (
            not isinstance(presentation, Mapping)
            or presentation.get("font_size_px") != spec.presentation["font_size_px"]
            or presentation.get("legend_position") != "inside"
        ):
            raise LegendCoveragePreflightError("supplemental presentation coverage changed")
        panels = scene.get("panels")
        if not isinstance(panels, list) or len(panels) != 1:
            raise LegendCoveragePreflightError("supplemental scene must contain one panel")
        legend = panels[0].get("legend")
        if (
            not isinstance(legend, Mapping)
            or legend.get("visible") is not True
            or legend.get("position") != "inside"
            or not isinstance(legend.get("entries"), list)
            or len(legend["entries"]) != 3
        ):
            raise LegendCoveragePreflightError("supplemental scene must contain one three-row inside legend")
        _legend_series_text(panels[0])
    return tuple(scenes)


def _box(value: Any, label: str) -> tuple[float, float, float, float]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes)) or len(value) != 4:
        raise LegendCoveragePreflightError(f"{label} is not a four-value box")
    left, top, width, height = (float(item) for item in value)
    if not all(math.isfinite(item) for item in (left, top, width, height)):
        raise LegendCoveragePreflightError(f"{label} contains a non-finite value")
    if width <= 0 or height <= 0:
        raise LegendCoveragePreflightError(f"{label} has non-positive extent")
    return left, top, width, height


def _inside(inner: Sequence[float], outer: Sequence[float]) -> bool:
    left, top, width, height = inner
    outer_left, outer_top, outer_width, outer_height = outer
    return (
        left >= outer_left
        and top >= outer_top
        and left + width <= outer_left + outer_width
        and top + height <= outer_top + outer_height
    )


def _legend_geometry(
    records: Sequence[Mapping[str, Any]],
    expected_text: Sequence[str],
    plot_box: Sequence[float],
    legend_box: Sequence[float],
) -> tuple[list[tuple[float, float, float, float]], list[float], list[float]]:
    if len(set(expected_text)) != len(expected_text):
        raise LegendCoveragePreflightError("declared legend entry text repeats")
    by_text: dict[str, Mapping[str, Any]] = {}
    for row in records:
        text = row.get("text")
        if not isinstance(text, str) or text in by_text:
            raise LegendCoveragePreflightError("rendered legend text is missing or duplicated")
        by_text[text] = row
    if set(by_text) != set(expected_text):
        raise LegendCoveragePreflightError("rendered legend text differs from declared legend entries")
    boxes = [
        _box(by_text[text].get("rendered_pixel_box"), "rendered legend box")
        for text in expected_text
    ]
    if not _inside(legend_box, plot_box):
        raise LegendCoveragePreflightError("declared legend frame escaped the plot")
    if not all(_inside(box, legend_box) and _inside(box, plot_box) for box in boxes):
        raise LegendCoveragePreflightError("rendered legend text escaped its frame or plot")
    gaps: list[float] = []
    top_deltas: list[float] = []
    for previous, current in zip(boxes, boxes[1:]):
        top_delta = current[1] - previous[1]
        gap = current[1] - (previous[1] + previous[3])
        if top_delta <= 0 or gap < 0:
            raise LegendCoveragePreflightError("rendered legend rows overlap or are out of order")
        if top_delta != 22.0:
            raise LegendCoveragePreflightError("rendered legend row top spacing changed")
        top_deltas.append(top_delta)
        gaps.append(gap)
    return boxes, top_deltas, gaps


def _render_case(scene: Mapping[str, Any], expected_font_size: int) -> tuple[bytes, dict[str, Any], dict[str, Any]]:
    rendered = render_visible_content_source(scene)
    annotation = rendered.annotation
    panel = scene["panels"][0]
    panel_id = str(panel["panel_id"])
    plot_box = _box(panel["plot_box"], "plot box")
    legend_box = _box(panel["legend"].get("box"), "legend frame")
    all_texts: list[Any] = list(annotation.get("texts", []))
    for annotated_panel in annotation.get("panels", []):
        if isinstance(annotated_panel, Mapping):
            all_texts.extend(annotated_panel.get("texts", []))
    records = [row for row in all_texts if isinstance(row, Mapping) and row.get("role") == "legend_text"]
    if len(records) != 3 or any(str(row.get("panel_id")) != panel_id for row in records):
        raise LegendCoveragePreflightError("rendered legend truth count or panel ownership changed")
    expected_text = _legend_series_text(panel)
    identities = [str(row.get("region_id", "")) for row in records]
    if any(not identity for identity in identities) or len(set(identities)) != 3:
        raise LegendCoveragePreflightError("rendered legend identities are missing or duplicated")
    boxes, row_top_deltas, row_gaps = _legend_geometry(
        records, expected_text, plot_box, legend_box
    )
    font = annotation.get("font")
    if not isinstance(font, Mapping) or set(font) != {
        "requested", "resolved_file", "resolved_path", "family", "style",
        "size_px", "source", "sha256", "bundled",
    }:
        raise LegendCoveragePreflightError("renderer font provenance is incomplete")
    if font.get("size_px") != expected_font_size or font.get("source") != "system" or font.get("bundled") is not False:
        raise LegendCoveragePreflightError("renderer did not use the requested non-bundled system font")
    font_path = Path(str(font["resolved_path"])).resolve()
    _verify_file(font_path, str(font["sha256"]), "resolved system font")
    measurement = {
        "legend_truth_count": 3,
        "inside_plot_truth_count": 3,
        "row_count": 3,
        "rendered_widths": [box[2] for box in boxes],
        "rendered_heights": [box[3] for box in boxes],
        "normalized_heights": [box[3] / plot_box[3] for box in boxes],
        "row_top_deltas_pixels": row_top_deltas,
        "normalized_row_top_deltas": [delta / plot_box[3] for delta in row_top_deltas],
        "row_gaps_pixels": row_gaps,
        "normalized_row_gaps": [gap / plot_box[3] for gap in row_gaps],
        "font": dict(font),
    }
    return png_bytes(rendered.image.convert("RGB")), annotation, measurement


def _distribution(values: Sequence[float]) -> dict[str, float]:
    if not values:
        raise LegendCoveragePreflightError("coverage distribution is empty")
    return {
        "minimum": min(values),
        "median": statistics.median(values),
        "maximum": max(values),
    }


def _build_payloads(root: Path, output: Path, dataset_seed: int) -> tuple[dict[str, bytes], dict[str, Any]]:
    if isinstance(dataset_seed, bool) or not isinstance(dataset_seed, int) or dataset_seed < 0:
        raise LegendCoveragePreflightError("dataset seed must be a non-negative integer")
    _validate_diagnosis(root)
    historical_dev, historical_train_source_ids, historical_dev_source_ids = (
        _validate_historical_bindings(root)
    )
    _protocol, protocol_payload = load_supported_protocol(root)
    if _sha(protocol_payload) != PROTOCOL_SHA256:
        raise LegendCoveragePreflightError("OCR holdout protocol identity changed")
    sources = _source_bindings(root)
    scenes = _build_train_scenes(dataset_seed)
    specs = case_specs()
    generated: dict[str, bytes] = {}
    inventory = []
    all_widths: list[float] = []
    all_heights: list[float] = []
    all_normalized_heights: list[float] = []
    all_row_top_deltas: list[float] = []
    all_normalized_row_top_deltas: list[float] = []
    all_row_gaps: list[float] = []
    all_normalized_row_gaps: list[float] = []
    font_rows: dict[tuple[str, str], dict[str, Any]] = {}
    family_counts: Counter[str] = Counter()

    for index, (scene, spec) in enumerate(zip(scenes, specs, strict=True)):
        image, annotation, measurement = _render_case(
            scene, int(spec.presentation["font_size_px"])
        )
        scene_payload = canonical_json_bytes(scene)
        annotation_payload = canonical_json_bytes(annotation)
        image_sha = _sha(image)
        stem = f"legend-train-{index:02d}-{image_sha[:12]}"
        image_name = f"sources/{stem}.png"
        scene_name = f"scenes/{stem}.json"
        annotation_name = f"annotations/{stem}.json"
        generated[image_name] = image
        generated[scene_name] = scene_payload
        generated[annotation_name] = annotation_payload
        family = _family_keys(scene)
        family_key = "|".join(f"{axis}={family[axis]}" for axis in FAMILY_AXES)
        family_counts[family_key] += 1
        all_widths.extend(measurement["rendered_widths"])
        all_heights.extend(measurement["rendered_heights"])
        all_normalized_heights.extend(measurement["normalized_heights"])
        all_row_top_deltas.extend(measurement["row_top_deltas_pixels"])
        all_normalized_row_top_deltas.extend(measurement["normalized_row_top_deltas"])
        all_row_gaps.extend(measurement["row_gaps_pixels"])
        all_normalized_row_gaps.extend(measurement["normalized_row_gaps"])
        font = measurement["font"]
        font_rows[(str(font["resolved_path"]), str(font["sha256"]))] = font
        inventory.append({
            "index": index,
            "seed": int(scene["seed"]),
            "design": scene["design"],
            "font_size_px": int(spec.presentation["font_size_px"]),
            "families": family,
            "legend_position": "inside",
            "legend_row_count": measurement["row_count"],
            "legend_truth_count": measurement["legend_truth_count"],
            "image": {"path": _repo_path(root, output / image_name), "sha256": image_sha},
            "scene": {"path": _repo_path(root, output / scene_name), "sha256": _sha(scene_payload)},
            "annotation": {
                "path": _repo_path(root, output / annotation_name),
                "sha256": _sha(annotation_payload),
            },
        })

    if len({row["image"]["sha256"] for row in inventory}) != len(inventory):
        raise LegendCoveragePreflightError("supplemental source raster identity repeats")
    generated_source_ids = {row["image"]["sha256"] for row in inventory}
    if generated_source_ids & historical_train_source_ids:
        raise LegendCoveragePreflightError("supplemental source raster overlaps historical train identity")
    if generated_source_ids & historical_dev_source_ids:
        raise LegendCoveragePreflightError("supplemental source raster overlaps fixed dev identity")
    if sum(row["legend_truth_count"] for row in inventory) != 18:
        raise LegendCoveragePreflightError("supplemental legend truth denominator changed")
    achieved_width_floor = min(all_widths)
    achieved_height_floor = min(all_heights)
    width_floor_covered = achieved_width_floor <= TARGET_RENDERED_WIDTH_FLOOR_PIXELS
    height_floor_covered = achieved_height_floor <= TARGET_RENDERED_HEIGHT_FLOOR_PIXELS
    coverage_ready = width_floor_covered and height_floor_covered
    report = {
        "schema": SCHEMA,
        "status": "supplemental_train_legend_coverage_preflight_execution_complete",
        "execution_complete": True,
        "coverage_ready": coverage_ready,
        "scope": "project-owned-synthetic-train-only-model-free-coverage-preflight",
        "synthetic_only": True,
        "train_only": True,
        "dev_cases_generated": 0,
        "dev_truth_rows_parsed": 0,
        "dev_pixels_read": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "model_loads": 0,
        "model_inference_runs": 0,
        "optimizer_steps": 0,
        "ocr_revision_opened": False,
        "training_authorized": False,
        "production_approved": False,
        "configuration": {
            "dataset_seed": dataset_seed,
            "case_count": len(inventory),
            "design": "alternating_treatments",
            "session_count": 36,
            "panel_count_per_case": 1,
            "renderer_families": list(RENDERER_FAMILIES),
            "font_sizes_px": list(FONT_SIZES),
            "legend_position": "inside",
            "expected_rows_per_legend": 3,
            "declared_legend_row_spacing_pixels": 22,
            "font_size_override_scope": "global_scene_text_not_legend_only",
        },
        "coverage": {
            "source_count": len(inventory),
            "panel_count": len(inventory),
            "multi_row_legend_panel_count": len(inventory),
            "legend_truth_count": 18,
            "inside_plot_legend_truth_count": 18,
            "legend_rows_per_panel": {"minimum": 3, "maximum": 3},
            "rendered_width_pixels": _distribution(all_widths),
            "rendered_height_pixels": _distribution(all_heights),
            "rendered_height_over_plot_height": _distribution(all_normalized_heights),
            "rendered_row_top_delta_pixels": _distribution(all_row_top_deltas),
            "rendered_row_top_delta_over_plot_height": _distribution(
                all_normalized_row_top_deltas
            ),
            "rendered_row_gap_pixels": _distribution(all_row_gaps),
            "rendered_row_gap_over_plot_height": _distribution(all_normalized_row_gaps),
            "rendered_rows_ordered_nonoverlapping": True,
            "rendered_rows_enclosed_by_legend_frame_and_plot": True,
            "new_raster_hashes_disjoint_from_historical_train": True,
            "new_raster_hashes_disjoint_from_fixed_dev": True,
            "diagnosed_small_extent_targets": {
                "source": {
                    "path": TRAIN_DEV_SUPPORT_PATH.as_posix(),
                    "sha256": TRAIN_DEV_SUPPORT_SHA256,
                },
                "target_minimum_width_pixels": TARGET_RENDERED_WIDTH_FLOOR_PIXELS,
                "achieved_minimum_width_pixels": achieved_width_floor,
                "width_floor_covered": width_floor_covered,
                "target_minimum_height_pixels": TARGET_RENDERED_HEIGHT_FLOOR_PIXELS,
                "achieved_minimum_height_pixels": achieved_height_floor,
                "height_floor_covered": height_floor_covered,
                "coverage_ready": coverage_ready,
                "readiness_is_descriptive_not_a_model_gate": True,
            },
            "case_counts_by_five_axis_family": dict(sorted(family_counts.items())),
            "case_counts_by_font_size_px": {
                str(size): sum(row["font_size_px"] == size for row in inventory)
                for size in FONT_SIZES
            },
        },
        "five_axis_holdout": {
            "acceptance_scope": ACCEPTANCE_SCOPE,
            "protocol": {"path": PROTOCOL_PATH.as_posix(), "sha256": PROTOCOL_SHA256},
            "corrected_diagnosis": {
                "path": DIAGNOSIS_PATH.as_posix(), "sha256": DIAGNOSIS_SHA256,
            },
            "all_generated_family_records_are_train": True,
            "held_out_dev_family_keys_absent": True,
            "dev_cases_or_styles_copied": False,
            "family_identities_changed_by_legend_position_or_font_size": False,
            "historical_dev_binding": historical_dev,
        },
        "sources": inventory,
        "generator_sources": sources,
        "system_fonts": [
            {
                **font,
                "license_and_distribution_status": (
                    "Host-installed system dependency; verified for reproducibility, "
                    "not copied, bundled, committed, or redistributed."
                ),
            }
            for font in sorted(
                font_rows.values(),
                key=lambda row: (str(row["resolved_file"]).casefold(), str(row["sha256"])),
            )
        ],
        "interpretation": [
            "This preflight measures whether existing train families can express smaller, three-row inside-plot legends.",
            "The font-size override applies to every text role in each supplemental scene, not only legend text.",
            "Legend rows retain the generator's fixed 22-pixel spacing; compact row spacing is not claimed.",
            "Coverage uses actual rendered_pixel_box widths and heights and may report an insufficient range.",
            "Execution completion is separate from coverage readiness; insufficient rendered minima remain an explicit gap.",
            "It does not measure detector, recognizer, role, or end-to-end accuracy.",
            "It does not select a model, threshold, architecture, candidate, or training revision.",
            "Any future use requires a separately authorized revision with fixed train/dev evidence and acceptance bars.",
        ],
    }
    generated["preflight-report.json"] = canonical_json_bytes(report)
    return generated, report


def run_preflight(
    repository_root: Path = REPOSITORY_ROOT,
    output_directory: Path | None = None,
    *,
    dataset_seed: int = DATASET_SEED,
) -> dict[str, Any]:
    """Build and atomically publish the supplemental train-only evidence."""

    root = Path(repository_root).resolve()
    output = (
        Path(output_directory).resolve()
        if output_directory is not None
        else root / "artifacts/goal22-runs/ocr-train-legend-coverage-preflight-v1"
    )
    artifacts = (root / "artifacts").resolve()
    if output == artifacts or artifacts not in output.parents:
        raise LegendCoveragePreflightError("output must be a new directory below repository artifacts")
    temporary = output.with_name(output.name + ".publishing")
    if output.exists() or temporary.exists():
        raise LegendCoveragePreflightError("preflight output already exists")
    payloads, report = _build_payloads(root, output, dataset_seed)
    temporary.mkdir(parents=True)
    try:
        for relative, payload in sorted(payloads.items()):
            path = temporary / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)
        os.replace(temporary, output)
    except BaseException:
        if temporary.exists():
            shutil.rmtree(temporary)
        raise
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository-root", type=Path, default=REPOSITORY_ROOT)
    parser.add_argument("--output-directory", type=Path)
    parser.add_argument("--dataset-seed", type=int, default=DATASET_SEED)
    arguments = parser.parse_args()
    report = run_preflight(
        arguments.repository_root,
        arguments.output_directory,
        dataset_seed=arguments.dataset_seed,
    )
    print(canonical_json_bytes({
        "status": report["status"],
        "source_count": report["coverage"]["source_count"],
        "legend_truth_count": report["coverage"]["legend_truth_count"],
    }).decode("utf-8"), end="")


if __name__ == "__main__":
    main()
