# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic dataset assembly, split manifests, and sanity checks."""

from __future__ import annotations

from dataclasses import dataclass
from importlib.metadata import version as package_version
import math
from pathlib import Path
import statistics
from typing import Any, Iterable, Mapping, Sequence

from PIL import Image

from .contact_sheet import build_contact_sheet
from .io import canonical_json_bytes, sha256, write_csv, write_json, write_png
from .renderer import production_resize_dimensions, render_scene
from .schema import validate_scene
from .templates import (
    FILL_STATES,
    LINE_STYLES,
    MARKER_SHAPES,
    SCENE_FEATURES,
    build_scene,
)


HARD_NEGATIVE_KINDS = (
    "marker_like_letter",
    "arrowhead",
    "phase_line_intersection",
    "tick_mark",
    "dotted_divider_segment",
    "legend_symbol",
    "bracket",
    "isolated_punctuation",
    "line_endpoint",
)
REQUIRED_TEXT_ROLES = (
    "annotation",
    "axis_title",
    "legend_text",
    "participant",
    "phase_heading",
    "x_tick",
    "y_tick",
)
FAMILY_AXES = ("renderer", "font", "degradation", "template", "marker")
CSV_FIELDS = (
    "scene_id",
    "panel_id",
    "participant",
    "series_id",
    "point_id",
    "observation_index",
    "printed_x_value",
    "estimated_x_value",
    "x_confidence",
    "x_value",
    "y_value",
    "phase",
    "original_pixel_x",
    "original_pixel_y",
    "shape",
    "fill",
)


@dataclass(frozen=True, slots=True)
class CaseSpec:
    design: str
    renderer_family: str
    panel_count: int
    session_count: int
    features: tuple[str, ...] = ()
    canvas_width: int = 1200
    panel_height: int = 270
    marker_radius: float | None = None
    stroke_width: int | None = None
    presentation: Mapping[str, Any] | None = None
    degradations: tuple[Mapping[str, Any], ...] | None = None
    output_mode: str = "RGB"


@dataclass(frozen=True, slots=True)
class DatasetResult:
    output_directory: Path
    case_count: int
    marker_count: int
    divider_count: int
    hard_negative_count: int


PRESETS: dict[str, tuple[CaseSpec, ...]] = {
    "smoke": (
        CaseSpec("ab", "vector_clean", 1, 2, ("missing_sessions",)),
        CaseSpec("aba", "print_monochrome", 1, 18, ("blank_phase_gaps",)),
        CaseSpec("abab", "vector_clean", 1, 24),
        CaseSpec(
            "multiple_baseline",
            "print_monochrome",
            3,
            20,
            ("missing_sessions",),
        ),
        CaseSpec(
            "multiple_probe",
            "scan_rough",
            2,
            30,
            ("sparse_probes",),
        ),
        CaseSpec(
            "alternating_treatments",
            "scan_rough",
            1,
            36,
            ("irregular_spacing",),
        ),
        CaseSpec("changing_criterion", "scan_rough", 6, 24),
        CaseSpec("maintenance", "hand_drawn", 1, 28),
        CaseSpec(
            "generalization",
            "hand_drawn",
            1,
            32,
            ("sparse_probes",),
        ),
        CaseSpec(
            "staggered_starts",
            "hand_drawn",
            3,
            40,
            ("irregular_spacing",),
        ),
        CaseSpec(
            "shared_baseline",
            "hand_drawn",
            1,
            100,
            ("blank_phase_gaps",),
        ),
    ),
    "real_range": (
        CaseSpec(
            "ab", "vector_clean", 1, 24,
            canvas_width=361, panel_height=160, marker_radius=3.0,
            stroke_width=1, presentation={"font_size_px": 17},
        ),
        CaseSpec(
            "multiple_probe",
            "vector_clean",
            1,
            24,
            ("sparse_probes",),
            canvas_width=863,
            panel_height=315,
            marker_radius=4.4,
            stroke_width=2,
            presentation={
                "dense_tick_labels": True,
                "font_size_px": 18,
                "grayscale": False,
                "monochrome": False,
            },
        ),
        CaseSpec(
            "multiple_baseline", "print_monochrome", 1, 24,
            canvas_width=1338, panel_height=412, marker_radius=12.0,
            stroke_width=2, output_mode="RGBA",
            presentation={"font_size_px": 18},
        ),
        CaseSpec(
            "ab", "scan_rough", 1, 24,
            canvas_width=6352, panel_height=520, marker_radius=6.2,
            stroke_width=3, presentation={"font_size_px": 18},
        ),
        CaseSpec(
            "abab", "scan_rough", 1, 24,
            canvas_width=600, panel_height=4404, marker_radius=5.0,
            stroke_width=2, presentation={"font_size_px": 18},
        ),
        CaseSpec(
            "ab", "vector_clean", 1, 24,
            canvas_width=1338, panel_height=412, marker_radius=5.0,
            stroke_width=1,
            presentation={"font_size_px": 18, "grayscale": True, "monochrome": True},
        ),
        CaseSpec(
            "abab", "scan_rough", 1, 24,
            canvas_width=1338, panel_height=412, marker_radius=6.2,
            stroke_width=3, presentation={"font_size_px": 18},
        ),
        *(
            CaseSpec(
                "ab", "scan_rough", 1, 24,
                canvas_width=1338, panel_height=412, marker_radius=4.4,
                stroke_width=1 if quality == 55 else 2,
                presentation={"font_size_px": 17},
                degradations=({
                    "stage": 1,
                    "family_key": "real-range-jpeg-roundtrip",
                    "kind": "jpeg",
                    "parameters": {"quality": quality},
                    "deterministic": True,
                },),
            )
            for quality in (55, 70, 85)
        ),
    ),
}


_SYMBOLS = {
    ("circle", "filled"): "●",
    ("circle", "open"): "○",
    ("square", "filled"): "■",
    ("square", "open"): "□",
    ("triangle_up", "filled"): "▲",
    ("triangle_up", "open"): "△",
    ("triangle_down", "filled"): "▼",
    ("triangle_down", "open"): "▽",
    ("diamond", "filled"): "◆",
    ("diamond", "open"): "◇",
    ("star", "filled"): "★",
    ("star", "open"): "☆",
    ("asterisk", "filled"): "✱",
    ("asterisk", "open"): "✱",
    ("cross", "filled"): "✚",
    ("cross", "open"): "✚",
    ("other", "filled"): "⬟",
    ("other", "open"): "⬡",
}


def generate_dataset(
    preset: str,
    seed: int,
    output_directory: Path | None = None,
) -> DatasetResult:
    """Generate one fixed preset and fail if its perfect-label checks drift."""

    if preset not in PRESETS:
        raise ValueError(f"Unknown preset '{preset}'. Expected one of {sorted(PRESETS)}.")
    if isinstance(seed, bool) or not isinstance(seed, int) or seed < 0:
        raise ValueError("seed must be a non-negative integer")

    destination = (
        Path(output_directory).resolve()
        if output_directory is not None
        else Path(__file__).resolve().parent / "datasets" / f"{preset}-{seed}"
    )
    destination.mkdir(parents=True, exist_ok=True)

    specs = PRESETS[preset]
    scenes = _build_scenes(
        specs,
        seed,
        require_complete_style_catalog=preset == "smoke",
    )
    case_entries: list[dict[str, Any]] = []
    rendered_images: list[Image.Image] = []
    rendered_image_paths: list[Path] = []
    output_files: list[Path] = []
    all_annotations: list[Mapping[str, Any]] = []

    for index, (scene, spec) in enumerate(zip(scenes, specs, strict=True)):
        scene_id = str(scene["scene_id"])
        image, annotation, marker_mask = render_scene(scene)
        if spec.output_mode == "RGBA":
            image = image.convert("RGBA")
            alpha = Image.linear_gradient("L").resize(image.size)
            alpha = alpha.point(lambda value: 224 + ((value * 31) // 255))
            image.putalpha(alpha)
        case_metrics = _validate_rendered_case(scene, annotation, marker_mask)
        rows = list(_csv_rows(scene, annotation))

        scene_path = destination / "scenes" / f"{scene_id}.json"
        image_path = destination / "images" / f"{scene_id}.png"
        annotation_path = destination / "annotations" / f"{scene_id}.json"
        mask_path = destination / "masks" / f"{scene_id}.png"
        csv_path = destination / "tables" / f"{scene_id}.csv"

        payloads = {
            scene_path: write_json(scene_path, scene),
            image_path: write_png(image_path, image),
            annotation_path: write_json(annotation_path, annotation),
            mask_path: write_png(mask_path, marker_mask),
            csv_path: write_csv(csv_path, CSV_FIELDS, rows),
        }
        output_files.extend(payloads)
        rendered_images.append(image.copy())
        rendered_image_paths.append(image_path)
        all_annotations.append(annotation)

        split = _scene_split(scene)
        case_entries.append(
            {
                "case_index": index,
                "scene_id": scene_id,
                "seed": scene["seed"],
                "design": scene["design"],
                "split": split,
                "families": _family_keys(scene),
                "features": sorted(scene.get("layout", {}).get("features", [])),
                "files": {
                    path.relative_to(destination).as_posix(): sha256(payload)
                    for path, payload in sorted(
                        payloads.items(), key=lambda item: item[0].as_posix()
                    )
                },
                "metrics": case_metrics,
            }
        )

    split_payloads = _split_manifests(case_entries)
    for split, payload in split_payloads.items():
        path = destination / "splits" / f"{split}.json"
        write_json(path, payload)
        output_files.append(path)

    sanity = _dataset_sanity(
        scenes,
        all_annotations,
        case_entries,
        require_full_acceptance_matrix=preset == "smoke",
    )
    sanity_path = destination / "sanity-report.json"
    write_json(sanity_path, sanity)
    output_files.append(sanity_path)

    if preset == "real_range":
        distribution_path = destination / "distribution-report.json"
        write_json(
            distribution_path,
            _real_range_distribution_report(
                scenes,
                all_annotations,
                rendered_images,
                rendered_image_paths,
            ),
        )
        output_files.append(distribution_path)

    contact_sheet = build_contact_sheet(rendered_images)
    contact_path = destination / "contact-sheet.png"
    write_png(contact_path, contact_sheet)
    output_files.append(contact_path)

    seed_manifest = {
        "manifest_version": 1,
        "generator_version": "0.1.0",
        "preset": preset,
        "dataset_seed": seed,
        "renderer_environment": {
            "pillow_version": package_version("Pillow"),
            "font_policy": "installed_or_user_supplied_only",
            "font_files_bundled": False,
        },
        "cases": case_entries,
        "artifact_sha256": {
            path.relative_to(destination).as_posix(): sha256(path.read_bytes())
            for path in sorted(output_files, key=lambda item: item.as_posix())
        },
        "sanity_passed": sanity["passed"],
    }
    manifest_path = destination / "seed-manifest.json"
    write_json(manifest_path, seed_manifest)

    return DatasetResult(
        output_directory=destination,
        case_count=len(scenes),
        marker_count=int(sanity["counts"]["markers"]),
        divider_count=int(sanity["counts"]["dividers"]),
        hard_negative_count=int(sanity["counts"]["hard_negatives"]),
    )


def _real_range_distribution_report(
    scenes: Sequence[Mapping[str, Any]],
    annotations: Sequence[Mapping[str, Any]],
    images: Sequence[Image.Image],
    image_paths: Sequence[Path],
) -> dict[str, Any]:
    if not (
        len(scenes) == len(annotations) == len(images) == len(image_paths)
    ):
        raise AssertionError(
            "Real-range scene, annotation, image, and encoded streams diverged."
        )
    text_heights_by_case = [
        _measure_text_heights(image, annotation)
        for image, annotation in zip(images, annotations, strict=True)
    ]
    source_text_heights = [
        height for heights in text_heights_by_case for height in heights
    ]
    resize_records = [
        production_resize_dimensions(
            int(scene["canvas"]["width"]),
            int(scene["canvas"]["height"]),
            maximum_side=960,
            stride=128,
        )
        for scene in scenes
    ]
    post_resize_text_heights = [
        height * float(resize["scale"])
        for heights, resize in zip(
            text_heights_by_case,
            resize_records,
            strict=True,
        )
        for height in heights
    ]
    marker_diameters = [
        diameter
        for image, annotation in zip(images, annotations, strict=True)
        for diameter in _measure_circle_diameters(image, annotation)
    ]
    stroke_widths = [
        width
        for image, annotation in zip(images, annotations, strict=True)
        for width in _measure_open_circle_strokes(image, annotation)
    ]
    region_counts = [
        len(_annotation_records(annotation, "texts"))
        for annotation in annotations
    ]
    envelope = {
        "source_width_px": [361, 6352],
        "source_height_px": [207, 4484],
        "source_height_minimum_tolerance_px": 33,
        "source_text_height_px": [10.0, 17.0],
        "post_resize_text_height_px": [1.8, 20.74],
        "marker_diameter_px": [7.0, 24.0],
        "open_stroke_width_px": [1.4, 1.74],
        "text_region_count": 38,
    }
    source_range = [min(source_text_heights), max(source_text_heights)]
    post_resize_range = [min(post_resize_text_heights), max(post_resize_text_heights)]
    diameter_range = [min(marker_diameters), max(marker_diameters)]
    stroke_range = [min(stroke_widths), max(stroke_widths)]
    encoded_png = [_png_header(path) for path in image_paths]
    modes = sorted({record["mode"] for record in encoded_png})
    bit_depths = sorted({int(record["bit_depth"]) for record in encoded_png})
    color_types = sorted({int(record["color_type"]) for record in encoded_png})
    compression_methods = sorted(
        {int(record["compression_method"]) for record in encoded_png}
    )
    filter_methods = sorted({int(record["filter_method"]) for record in encoded_png})
    interlace_methods = sorted(
        {int(record["interlace_method"]) for record in encoded_png}
    )
    alpha_ranges = [record["alpha_range"] for record in encoded_png if record["alpha_range"] is not None]
    jpeg_qualities = sorted(
        {
            int(stage["parameters"]["quality"])
            for scene in scenes
            for stage in scene.get("degradations", [])
            if stage.get("kind") == "jpeg"
        }
    )
    gates = {
        "source_dimensions_cover_real_envelope": (
            min(int(scene["canvas"]["width"]) for scene in scenes) <= envelope["source_width_px"][0]
            and max(int(scene["canvas"]["width"]) for scene in scenes) >= envelope["source_width_px"][1]
            and min(int(scene["canvas"]["height"]) for scene in scenes)
            <= envelope["source_height_px"][0] + envelope["source_height_minimum_tolerance_px"]
            and max(int(scene["canvas"]["height"]) for scene in scenes) >= envelope["source_height_px"][1]
        ),
        "weighted_toward_observed_median": sum(
            int(scene["canvas"]["width"]) == 1338
            and int(scene["canvas"]["height"]) == 492
            for scene in scenes
        ) >= len(scenes) // 2,
        "observed_style_wide_and_tall_cases": (
            any(int(scene["canvas"]["width"]) >= 6000 and int(scene["canvas"]["height"]) < 1000 for scene in scenes)
            and any(int(scene["canvas"]["height"]) >= 4000 and int(scene["canvas"]["width"]) < 1000 for scene in scenes)
        ),
        "exact_863x395_case": any(
            int(scene["canvas"]["width"]) == 863
            and int(scene["canvas"]["height"]) == 395
            for scene in scenes
        ),
        "source_text_matches_realistic_range": (
            source_range[0] >= envelope["source_text_height_px"][0]
            and source_range[0] <= 13.0
            and source_range[1] >= envelope["source_text_height_px"][1]
            and source_range[1] <= 20.0
        ),
        "post_resize_text_contains_envelope": (
            post_resize_range[0] <= envelope["post_resize_text_height_px"][0]
            and post_resize_range[1] >= envelope["post_resize_text_height_px"][1]
        ),
        "marker_diameter_contains_envelope": (
            diameter_range[0] <= envelope["marker_diameter_px"][0]
            and diameter_range[1] >= envelope["marker_diameter_px"][1]
        ),
        "marker_stroke_contains_envelope": (
            stroke_range[0] <= envelope["open_stroke_width_px"][0]
            and stroke_range[1] >= envelope["open_stroke_width_px"][1]
        ),
        "text_region_count_spans_measurement": (
            min(region_counts) <= envelope["text_region_count"] <= max(region_counts)
        ),
        "rgb8_and_rgba8_png": (
            modes == ["RGB", "RGBA"]
            and bit_depths == [8]
            and color_types == [2, 6]
            and compression_methods == [0]
            and filter_methods == [0]
            and interlace_methods == [0]
        ),
        "rgba_alpha_compositing": alpha_ranges == [[224, 255]],
        "jpeg_roundtrip_qualities": jpeg_qualities == [55, 70, 85],
    }
    if not all(gates.values()):
        failed = sorted(name for name, passed in gates.items() if not passed)
        raise AssertionError(
            "Real-range synthetic distribution gate failed: " + ", ".join(failed)
        )
    return {
        "schema_version": 1,
        "report_scope": "synthetic_real_range_aggregate_only",
        "source_dimensions": sorted(
            {f"{int(scene['canvas']['width'])}x{int(scene['canvas']['height'])}" for scene in scenes}
        ),
        "source_pixel_format": {
            "modes": modes,
            "bit_depths": bit_depths,
            "png_color_types": color_types,
        },
        "png_encoding": {
            "compression_methods": compression_methods,
            "filter_methods": filter_methods,
            "interlace_methods": interlace_methods,
            "rgba_alpha_ranges": alpha_ranges,
        },
        "jpeg_roundtrip_qualities": jpeg_qualities,
        "source_text_height_px": source_range,
        "post_resize_text_height_px": post_resize_range,
        "resize_scale": [
            min(float(record["scale"]) for record in resize_records),
            max(float(record["scale"]) for record in resize_records),
        ],
        "db_resize_records": resize_records,
        "marker_diameter_px": diameter_range,
        "open_stroke_width_px": stroke_range,
        "text_region_counts": region_counts,
        "envelope": envelope,
        "gates": gates,
        "measurement_methods": {
            "text_height": "foreground pixel bounds inside each truth-authorized rendered text box multiplied by the production resize scale before stride padding",
            "marker_diameter": "median farthest dark-pixel radius across 72 truth-center rays",
            "open_stroke": "upper-arc dark radial p90 minus p10 inside each truth-authorized open circle",
            "png_encoding": "decoded IHDR bytes from every emitted PNG",
        },
    }


def _measure_text_heights(
    image: Image.Image,
    annotation: Mapping[str, Any],
) -> list[float]:
    gray = image.convert("L")
    heights: list[float] = []
    for text in _annotation_records(annotation, "texts"):
        box = text.get("rendered_pixel_box")
        if not text.get("visible") or not text.get("text") or not box:
            continue
        left, top, width, height = (float(value) for value in box)
        crop = gray.crop((
            max(0, math.floor(left)),
            max(0, math.floor(top)),
            min(gray.width, math.ceil(left + width)),
            min(gray.height, math.ceil(top + height)),
        ))
        foreground = crop.point(lambda value: 255 if value < 200 else 0)
        pixel_box = foreground.getbbox()
        if pixel_box is not None:
            heights.append(float(pixel_box[3] - pixel_box[1]))
    return heights


def _measure_circle_diameters(
    image: Image.Image,
    annotation: Mapping[str, Any],
) -> list[float]:
    gray = image.convert("L")
    diameters: list[float] = []
    for marker in _annotation_records(annotation, "markers"):
        if marker.get("shape") != "circle":
            continue
        center_x, center_y = (float(value) for value in marker["center"])
        _, _, box_width, box_height = (float(value) for value in marker["box"])
        search_radius = max(box_width, box_height) / 2.0 + 3.0
        farthest: list[float] = []
        for angle_index in range(72):
            angle = math.tau * angle_index / 72.0
            dark_radii: list[float] = []
            step_count = max(1, math.ceil((search_radius - 0.5) / 0.25))
            for step_index in range(step_count + 1):
                radius = min(search_radius, 0.5 + step_index * 0.25)
                x = max(0, min(gray.width - 1, round(
                    center_x + radius * math.cos(angle)
                )))
                y = max(0, min(gray.height - 1, round(
                    center_y + radius * math.sin(angle)
                )))
                if gray.getpixel((x, y)) < 160:
                    dark_radii.append(radius)
            if dark_radii:
                farthest.append(max(dark_radii))
        if farthest:
            diameters.append(2.0 * statistics.median(farthest))
    return diameters


def _measure_open_circle_strokes(
    image: Image.Image,
    annotation: Mapping[str, Any],
) -> list[float]:
    gray = image.convert("L")
    widths: list[float] = []
    for marker in _annotation_records(annotation, "markers"):
        if marker.get("shape") != "circle" or marker.get("fill") != "open":
            continue
        center_x, center_y = (float(value) for value in marker["center"])
        _, _, box_width, box_height = (float(value) for value in marker["box"])
        search_radius = max(box_width, box_height) / 2.0 + 3.0
        radial: list[float] = []
        x_start = max(0, math.floor(center_x - search_radius))
        x_end = min(gray.width - 1, math.ceil(center_x + search_radius))
        y_start = max(0, math.floor(center_y - search_radius))
        y_end = min(gray.height - 1, math.floor(center_y - 2.0))
        for y in range(y_start, y_end + 1):
            for x in range(x_start, x_end + 1):
                radius = math.hypot(x - center_x, y - center_y)
                if radius <= search_radius and gray.getpixel((x, y)) < 160:
                    radial.append(radius)
        if radial:
            widths.append(
                _percentile(radial, 0.90) - _percentile(radial, 0.10)
            )
    return widths


def _percentile(values: Sequence[float], fraction: float) -> float:
    ordered = sorted(float(value) for value in values)
    if not ordered:
        raise ValueError("A percentile requires at least one value.")
    position = (len(ordered) - 1) * fraction
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    weight = position - lower
    return ordered[lower] * (1.0 - weight) + ordered[upper] * weight


def _png_header(path: Path) -> dict[str, Any]:
    payload = path.read_bytes()
    if len(payload) < 29 or payload[:8] != b"\x89PNG\r\n\x1a\n":
        raise AssertionError(f"Synthetic image is not a complete PNG: {path.name}")
    with Image.open(path) as image:
        mode = image.mode
        has_alpha = "A" in image.getbands()
        image.verify()
    alpha_range = None
    if has_alpha:
        with Image.open(path) as image:
            alpha_range = list(image.getchannel("A").getextrema())
    return {
        "mode": mode,
        "bit_depth": payload[24],
        "color_type": payload[25],
        "compression_method": payload[26],
        "filter_method": payload[27],
        "interlace_method": payload[28],
        "alpha_range": alpha_range,
    }


def _build_scenes(
    specs: Sequence[CaseSpec],
    dataset_seed: int,
    *,
    require_complete_style_catalog: bool = True,
) -> list[dict[str, Any]]:
    scenes: list[dict[str, Any]] = []
    style_catalog = [
        (shape, fill)
        for shape in MARKER_SHAPES
        for fill in FILL_STATES
    ]
    style_index = 0
    line_index = 0

    for case_index, spec in enumerate(specs):
        case_seed = dataset_seed * 100 + case_index
        scene = build_scene(
            spec.design,
            case_seed,
            spec.renderer_family,
            spec.panel_count,
            session_count=spec.session_count,
            features=spec.features,
            canvas_width=spec.canvas_width,
            panel_height=spec.panel_height,
            marker_radius=spec.marker_radius,
            stroke_width=spec.stroke_width,
            presentation=spec.presentation,
        )
        for panel in scene["panels"]:
            points_by_series: dict[str, list[dict[str, Any]]] = {}
            for point in panel["points"]:
                points_by_series.setdefault(str(point["series_id"]), []).append(point)
            for series in panel["series"]:
                shape, fill = style_catalog[style_index % len(style_catalog)]
                style_index += 1
                line_style = LINE_STYLES[line_index % len(LINE_STYLES)]
                line_index += 1
                series["shape"] = shape
                series["fill"] = fill
                series["symbol"] = _symbol(shape, fill)
                series["line_style"] = line_style
                for point in points_by_series.get(str(series["series_id"]), []):
                    point["shape"] = shape
                    point["fill"] = fill
                    # The renderer emits the authoritative binary mask for the
                    # reassigned style. Avoid retaining a stale template polygon.
                    point["mask"] = None
        validate_scene(scene)
        if spec.output_mode not in {"RGB", "RGBA"}:
            raise ValueError("output_mode must be RGB or RGBA")
        if spec.degradations is not None:
            scene["degradations"] = [dict(item) for item in spec.degradations]
            validate_scene(scene)
        scenes.append(scene)

    if require_complete_style_catalog and style_index < len(style_catalog):
        raise AssertionError(
            f"Smoke matrix exposes {style_index} series but needs "
            f"{len(style_catalog)} for complete marker style coverage."
        )
    return scenes


def _symbol(shape: str, fill: str) -> str:
    if fill == "degraded":
        return _SYMBOLS.get((shape, "open"), "◇")
    return _SYMBOLS.get((shape, fill), "◇")


def _csv_rows(
    scene: Mapping[str, Any],
    annotation: Mapping[str, Any],
) -> Iterable[dict[str, Any]]:
    rendered_centers = {
        str(marker.get("point_id", marker.get("marker_id"))): marker["center"]
        for marker in _annotation_records(annotation, "markers")
    }
    for panel in scene["panels"]:
        phase_codes = {
            str(phase["phase_id"]): str(phase["code"]) for phase in panel["phases"]
        }
        participant = str(panel.get("participant", ""))
        for point in panel["points"]:
            graph = point["graph"]
            center = rendered_centers[str(point["point_id"])]
            yield {
                "scene_id": scene["scene_id"],
                "panel_id": panel["panel_id"],
                "participant": participant,
                "series_id": point["series_id"],
                "point_id": point["point_id"],
                "observation_index": point["observation_index"],
                "printed_x_value": _csv_nullable(point.get("printed_x_value")),
                "estimated_x_value": _csv_nullable(point.get("estimated_x_value")),
                "x_confidence": point["x_confidence"],
                "x_value": graph[0],
                "y_value": graph[1],
                "phase": phase_codes[str(point["phase_id"])],
                "original_pixel_x": center[0],
                "original_pixel_y": center[1],
                "shape": point["shape"],
                "fill": point["fill"],
            }


def _csv_nullable(value: Any) -> Any:
    return "" if value is None else value


def _scene_split(scene: Mapping[str, Any]) -> str:
    splits = {
        str(family["split"]) for family in scene["families"].values()
    }
    if len(splits) != 1:
        raise AssertionError(f"Scene family split drift: {sorted(splits)}")
    return splits.pop()


def _family_keys(scene: Mapping[str, Any]) -> dict[str, str]:
    return {
        axis: str(scene["families"][axis]["key"])
        for axis in FAMILY_AXES
    }


def _split_manifests(
    cases: Sequence[Mapping[str, Any]],
) -> dict[str, dict[str, Any]]:
    payloads: dict[str, dict[str, Any]] = {}
    for split in ("train", "validation", "test"):
        selected = [case for case in cases if case["split"] == split]
        payloads[split] = {
            "manifest_version": 1,
            "split": split,
            "families": {
                axis: sorted({str(case["families"][axis]) for case in selected})
                for axis in FAMILY_AXES
            },
            "cases": [
                {
                    "scene_id": case["scene_id"],
                    "seed": case["seed"],
                    "design": case["design"],
                    "families": case["families"],
                    "files": sorted(case["files"]),
                }
                for case in selected
            ],
        }
    _assert_split_isolation(payloads)
    return payloads


def _assert_split_isolation(
    payloads: Mapping[str, Mapping[str, Any]],
) -> None:
    for axis in FAMILY_AXES:
        family_sets = {
            split: set(payload["families"][axis])
            for split, payload in payloads.items()
        }
        for left, right in (("train", "validation"), ("train", "test"), ("validation", "test")):
            overlap = family_sets[left] & family_sets[right]
            if overlap:
                raise AssertionError(
                    f"{axis} family leakage between {left} and {right}: {sorted(overlap)}"
                )


def family_holdout_audit(seed: int = 393) -> dict[str, Any]:
    """Return aggregate proof that synthetic train and dev families are disjoint.

    This reuses the existing declarative scene machinery and stops before
    rendering.  The returned record contains only counts, booleans, and hashes;
    it never emits scene IDs, labels, truth rows, or pixels.
    """

    if isinstance(seed, bool) or not isinstance(seed, int) or seed < 0:
        raise ValueError("seed must be a non-negative integer")
    scenes = _build_scenes(
        PRESETS["smoke"],
        seed,
        require_complete_style_catalog=True,
    )
    split_scenes = {
        "train": [scene for scene in scenes if _scene_split(scene) == "train"],
        "dev": [scene for scene in scenes if _scene_split(scene) == "validation"],
    }
    if not split_scenes["train"] or not split_scenes["dev"]:
        raise AssertionError("Synthetic smoke matrix must provide train and dev scenes")

    family_sets = {
        split: {
            axis: {str(scene["families"][axis]["key"]) for scene in selected}
            for axis in FAMILY_AXES
        }
        for split, selected in split_scenes.items()
    }

    def set_hash(values: set[str]) -> str:
        return sha256(canonical_json_bytes(sorted(values)))

    def split_hash(selected: Sequence[Mapping[str, Any]]) -> str:
        records = []
        for scene in selected:
            records.append(
                {
                    "seed": int(scene["seed"]),
                    "families": {
                        axis: str(scene["families"][axis]["key"])
                        for axis in FAMILY_AXES
                    },
                    "marker_count": sum(
                        len(panel["points"]) for panel in scene["panels"]
                    ),
                }
            )
        return sha256(canonical_json_bytes(sorted(records, key=lambda item: item["seed"])))

    axes: dict[str, dict[str, Any]] = {}
    for axis in FAMILY_AXES:
        train_families = family_sets["train"][axis]
        dev_families = family_sets["dev"][axis]
        overlap = train_families & dev_families
        axes[axis] = {
            "train_family_count": len(train_families),
            "dev_family_count": len(dev_families),
            "overlap_count": len(overlap),
            "train_dev_disjoint": not overlap,
            "train_family_set_sha256": set_hash(train_families),
            "dev_family_set_sha256": set_hash(dev_families),
        }

    train_count = sum(
        len(panel["points"])
        for scene in split_scenes["train"]
        for panel in scene["panels"]
    )
    dev_count = sum(
        len(panel["points"])
        for scene in split_scenes["dev"]
        for panel in scene["panels"]
    )
    all_axes_disjoint = all(item["train_dev_disjoint"] for item in axes.values())
    return {
        "schema": "graphreader.synthetic-family-holdout-audit.v1",
        "seed": seed,
        "train_split": "train",
        "dev_split": "validation",
        "held_out_axes": list(FAMILY_AXES),
        "splits": {
            "train": {
                "scene_count": len(split_scenes["train"]),
                "marker_count": train_count,
                "aggregate_sha256": split_hash(split_scenes["train"]),
            },
            "dev": {
                "scene_count": len(split_scenes["dev"]),
                "marker_count": dev_count,
                "aggregate_sha256": split_hash(split_scenes["dev"]),
            },
        },
        "axes": axes,
        "train_dev_family_disjoint": all_axes_disjoint,
        "scope": {
            "synthetic_only": True,
            "model_loaded": False,
            "training_performed": False,
            "private_or_article_images": False,
            "sealed_reads": 0,
            "scene_ids_emitted": False,
            "truth_rows_emitted": False,
            "pixels_emitted": False,
        },
    }


def _validate_rendered_case(
    scene: Mapping[str, Any],
    annotation: Mapping[str, Any],
    marker_mask: Image.Image,
) -> dict[str, Any]:
    expected_points = {
        str(point["point_id"]): point
        for panel in scene["panels"]
        for point in panel["points"]
    }
    markers = _annotation_records(annotation, "markers")
    actual_markers = {
        str(marker.get("point_id", marker.get("marker_id"))): marker
        for marker in markers
    }
    if set(actual_markers) != set(expected_points):
        raise AssertionError(
            f"Marker identity drift for {scene['scene_id']}: "
            f"expected {len(expected_points)}, got {len(actual_markers)}"
        )

    for point_id, point in expected_points.items():
        expected_center = _transform_point(annotation, point["center"])
        actual_center = actual_markers[point_id]["center"]
        if not _points_close(actual_center, expected_center):
            raise AssertionError(
                f"Marker center drift for {point_id}: {actual_center} != {expected_center}"
            )
        x = round(float(expected_center[0]))
        y = round(float(expected_center[1]))
        if not 0 <= x < marker_mask.width or not 0 <= y < marker_mask.height:
            raise AssertionError(f"Marker {point_id} center is outside its mask.")
        radius = float(point["radius"])
        left = max(0, int(x - radius - 2))
        top = max(0, int(y - radius - 2))
        right = min(marker_mask.width, int(x + radius + 3))
        bottom = min(marker_mask.height, int(y + radius + 3))
        if marker_mask.crop((left, top, right, bottom)).getbbox() is None:
            raise AssertionError(f"Marker {point_id} has no pixels in its mask region.")

    expected_dividers = {
        str(divider["divider_id"]): divider
        for panel in scene["panels"]
        for divider in panel["dividers"]
    }
    actual_dividers = {
        str(divider["divider_id"]): divider
        for divider in _annotation_records(annotation, "dividers")
    }
    if set(expected_dividers) != set(actual_dividers):
        raise AssertionError("Phase-divider identity drift.")
    for divider_id, divider in expected_dividers.items():
        actual_line = actual_dividers[divider_id]["line"]
        expected_line = [
            _transform_point(annotation, divider["line"][:2]),
            _transform_point(annotation, divider["line"][2:]),
        ]
        if not all(
            _points_close(actual, expected)
            for actual, expected in zip(actual_line, expected_line, strict=True)
        ):
            raise AssertionError(
                f"Phase-divider geometry drift for {divider_id}: "
                f"{actual_line} != {expected_line}"
            )

    return {
        "markers": len(markers),
        "dividers": len(actual_dividers),
        "texts": len(_annotation_records(annotation, "texts")),
        "ticks": len(_annotation_records(annotation, "ticks")),
        "hard_negatives": len(annotation.get("hard_negatives", [])),
    }


def _annotation_records(
    annotation: Mapping[str, Any],
    key: str,
) -> list[Mapping[str, Any]]:
    records = list(annotation.get(key, []))
    for panel in annotation.get("panels", []):
        records.extend(panel.get(key, []))
    return records


def _transform_point(
    annotation: Mapping[str, Any],
    point: Sequence[float],
) -> list[float]:
    x, y = float(point[0]), float(point[1])
    transforms = annotation.get("transforms", [])
    if transforms:
        # Each emitted transform is cumulative from synthetic_clean_pixels to
        # that stage's final raster. The last record is therefore the complete
        # clean-to-final mapping and must be applied exactly once.
        matrix = transforms[-1]["forward_matrix_3x3"]
        denominator = matrix[6] * x + matrix[7] * y + matrix[8]
        if abs(denominator) < 1e-12:
            raise AssertionError("Annotation transform maps a point to infinity.")
        x, y = (
            (matrix[0] * x + matrix[1] * y + matrix[2]) / denominator,
            (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator,
        )
        x, y = round(x, 6), round(y, 6)
    return [x, y]


def _points_close(
    actual: Sequence[float],
    expected: Sequence[float],
    *,
    tolerance: float = 1e-5,
) -> bool:
    return all(
        abs(float(actual_value) - float(expected_value)) <= tolerance
        for actual_value, expected_value in zip(actual, expected, strict=True)
    )


def _dataset_sanity(
    scenes: Sequence[Mapping[str, Any]],
    annotations: Sequence[Mapping[str, Any]],
    cases: Sequence[Mapping[str, Any]],
    *,
    require_full_acceptance_matrix: bool = True,
) -> dict[str, Any]:
    markers = [
        marker
        for annotation in annotations
        for marker in _annotation_records(annotation, "markers")
    ]
    dividers = [
        divider
        for annotation in annotations
        for divider in _annotation_records(annotation, "dividers")
    ]
    hard_negatives = [
        negative
        for annotation in annotations
        for negative in annotation.get("hard_negatives", [])
    ]
    texts = [
        text
        for annotation in annotations
        for text in _annotation_records(annotation, "texts")
    ]
    ticks = [
        tick
        for annotation in annotations
        for tick in _annotation_records(annotation, "ticks")
    ]
    arrows = [
        arrow
        for annotation in annotations
        for arrow in _annotation_records(annotation, "arrows")
    ]
    roles = {str(text["role"]) for text in texts}
    marker_styles = {
        (str(marker["shape"]), str(marker["fill"])) for marker in markers
    }
    line_styles = {
        str(series["line_style"])
        for scene in scenes
        for panel in scene["panels"]
        for series in panel["series"]
    }
    hard_negative_kinds = {str(item["kind"]) for item in hard_negatives}
    feature_set = {
        str(feature)
        for scene in scenes
        for feature in scene.get("layout", {}).get("features", [])
    }
    panel_counts = {len(scene["panels"]) for scene in scenes}
    session_counts = {
        int(scene["layout"]["session_count"])
        for scene in scenes
    }
    degradation_stage_counts = {
        len(scene.get("degradations", [])) for scene in scenes
    }
    x_label_visibility = {
        str(scene["presentation"]["x_label_visibility"]) for scene in scenes
    }
    y_label_visibility = {
        str(scene["presentation"]["y_label_visibility"]) for scene in scenes
    }
    legend_positions = {
        str(scene["presentation"]["legend_position"]) for scene in scenes
    }
    divider_styles = {
        str(divider["style"])
        for scene in scenes
        for panel in scene["panels"]
        for divider in panel["dividers"]
    }
    shared_axes_values = {bool(scene["layout"]["shared_axes"]) for scene in scenes}
    hidden_zero_values = {
        bool(scene["presentation"]["hide_zero_label"]) for scene in scenes
    }
    decoration_coverage = {
        key: any(bool(scene["presentation"][key]) for scene in scenes)
        for key in (
            "show_participant_names",
            "show_arrows",
            "show_brackets",
            "show_top_condition_bars",
        )
    }
    y_axis_profiles = {
        (
            float(scene["style"]["y_axis"]["minimum"]),
            float(scene["style"]["y_axis"]["maximum"]),
            float(scene["style"]["y_axis"]["tick_interval"]),
        )
        for scene in scenes
    }
    stroke_widths = {
        float(series["stroke_width"])
        for scene in scenes
        for panel in scene["panels"]
        for series in panel["series"]
    }
    marker_radii = {
        float(point["radius"])
        for scene in scenes
        for panel in scene["panels"]
        for point in panel["points"]
    }
    spacing_profiles = {
        (
            str(scene["style"]["session_spacing"]["mode"]),
            float(scene["style"]["session_spacing"]["edge_padding_fraction"]),
            float(scene["style"]["session_spacing"]["jitter_fraction"]),
            float(scene["style"]["session_spacing"]["nominal_pitch_fraction"]),
        )
        for scene in scenes
    }

    failures: list[str] = []
    expected_styles = {
        (shape, fill) for shape in MARKER_SHAPES for fill in FILL_STATES
    }
    if require_full_acceptance_matrix:
        _require_subset("marker styles", expected_styles, marker_styles, failures)
        _require_subset("line styles", set(LINE_STYLES), line_styles, failures)
        _require_subset(
            "hard negatives",
            set(HARD_NEGATIVE_KINDS),
            hard_negative_kinds,
            failures,
        )
        _require_subset("text roles", set(REQUIRED_TEXT_ROLES), roles, failures)
        _require_subset("scene features", set(SCENE_FEATURES), feature_set, failures)
        _require_subset("panel counts", {1, 6}, panel_counts, failures)
        _require_subset("session counts", {2, 100}, session_counts, failures)
    if not degradation_stage_counts or not degradation_stage_counts <= {1, 2}:
        failures.append(
            "degradation stage counts must contain only one or two stages, got "
            f"{sorted(degradation_stage_counts)}"
        )
    if require_full_acceptance_matrix:
        _require_subset(
            "x label visibility",
            {"visible", "partial", "hidden"},
            x_label_visibility,
            failures,
        )
        _require_subset(
            "legend positions",
            {"inside", "outside"},
            legend_positions,
            failures,
        )
        _require_subset(
            "divider styles",
            {"dotted"},
            divider_styles,
            failures,
        )
        if True not in shared_axes_values:
            failures.append("missing shared-axis scene")
        if True not in hidden_zero_values:
            failures.append("missing hidden-zero-label scene")
        for feature, covered in decoration_coverage.items():
            if not covered:
                failures.append(f"missing presentation decoration: {feature}")
    for label, values in (
        ("y-axis profiles", y_axis_profiles),
        ("stroke widths", stroke_widths),
        ("marker radii", marker_radii),
        ("session spacing profiles", spacing_profiles),
    ):
        if len(values) < 2:
            failures.append(f"insufficient deterministic {label}: {sorted(values, key=str)}")
    if any(not text.get("role") for text in texts):
        failures.append("one or more text annotations have no role")
    if any(tick.get("role") not in {"x_tick", "y_tick"} for tick in ticks):
        failures.append("one or more tick annotations have no x_tick/y_tick role")
    if not arrows:
        failures.append("missing annotated arrows")
    if any("line" not in arrow and "polygon" not in arrow for arrow in arrows):
        failures.append("one or more arrows have no annotated geometry")

    if failures:
        raise AssertionError("Synthetic dataset sanity failure: " + "; ".join(failures))

    return {
        "sanity_version": 1,
        "passed": True,
        "counts": {
            "scenes": len(scenes),
            "markers": len(markers),
            "dividers": len(dividers),
            "hard_negatives": len(hard_negatives),
            "csv_rows": sum(int(case["metrics"]["markers"]) for case in cases),
        },
        "coverage": {
            "designs": sorted({str(scene["design"]) for scene in scenes}),
            "panel_counts": sorted(panel_counts),
            "session_counts": sorted(session_counts),
            "marker_styles": [
                {"shape": shape, "fill": fill}
                for shape, fill in sorted(marker_styles)
            ],
            "line_styles": sorted(line_styles),
            "text_roles": sorted(roles),
            "hard_negative_kinds": sorted(hard_negative_kinds),
            "scene_features": sorted(feature_set),
            "degradation_stage_counts": sorted(degradation_stage_counts),
            "x_label_visibility": sorted(x_label_visibility),
            "y_label_visibility": sorted(y_label_visibility),
            "legend_positions": sorted(legend_positions),
            "divider_styles": sorted(divider_styles),
            "shared_axes_values": sorted(shared_axes_values),
            "hidden_zero_values": sorted(hidden_zero_values),
            "decorations": decoration_coverage,
            "y_axis_profiles": [
                {
                    "minimum": minimum,
                    "maximum": maximum,
                    "tick_interval": interval,
                }
                for minimum, maximum, interval in sorted(y_axis_profiles)
            ],
            "stroke_widths": sorted(stroke_widths),
            "marker_radii": sorted(marker_radii),
            "session_spacing_profiles": [
                {
                    "mode": mode,
                    "edge_padding_fraction": edge_padding,
                    "jitter_fraction": jitter,
                    "nominal_pitch_fraction": nominal_pitch,
                }
                for mode, edge_padding, jitter, nominal_pitch in sorted(
                    spacing_profiles
                )
            ],
            "splits": sorted({str(case["split"]) for case in cases}),
        },
    }


def _require_subset(
    label: str,
    expected: set[Any],
    actual: set[Any],
    failures: list[str],
) -> None:
    missing = expected - actual
    if missing:
        failures.append(f"missing {label}: {sorted(missing, key=str)}")


__all__ = [
    "CSV_FIELDS",
    "DatasetResult",
    "FAMILY_AXES",
    "HARD_NEGATIVE_KINDS",
    "PRESETS",
    "REQUIRED_TEXT_ROLES",
    "family_holdout_audit",
    "generate_dataset",
]
