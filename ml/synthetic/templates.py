# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic declarative templates for synthetic SCD graph scenes."""

from __future__ import annotations

import hashlib
import math
import random
import re
import uuid
from collections.abc import Iterable, Mapping
from typing import Any

from .fonts import FontResolver
from .schema import validate_scene


SUPPORTED_DESIGNS = (
    "ab",
    "aba",
    "abab",
    "multiple_baseline",
    "multiple_probe",
    "alternating_treatments",
    "changing_criterion",
    "maintenance",
    "generalization",
    "staggered_starts",
    "shared_baseline",
)

SCENE_FEATURES = (
    "missing_sessions",
    "sparse_probes",
    "blank_phase_gaps",
    "irregular_spacing",
)

MARKER_SHAPES = (
    "circle",
    "square",
    "triangle_up",
    "triangle_down",
    "diamond",
    "star",
    "asterisk",
    "cross",
    "other",
)
FILL_STATES = ("open", "filled", "degraded")
LINE_STYLES = ("solid", "dashed", "dotted", "missing", "partially_occluded")
PHASE_DIVIDER_STYLES = ("solid", "dashed", "dotted")
TEXT_ROLES = (
    "y_tick",
    "x_tick",
    "axis_title",
    "phase_heading",
    "legend_text",
    "participant",
    "annotation",
    "condition_label",
    "other",
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

RENDERER_FAMILIES = (
    "vector_clean",
    "print_monochrome",
    "scan_rough",
    "hand_drawn",
)
FONT_FAMILIES = (
    "system_sans",
    "system_serif",
    "system_mono",
    "system_handwritten",
)
DEGRADATION_FAMILIES = ("none", "print_light", "scan_noise", "camera_skew")
DEGRADATION_KIND_CATALOG = (
    "none",
    "downsample",
    "isotropic_blur",
    "anisotropic_blur",
    "gaussian_noise",
    "poisson_noise",
    "impulse_noise",
    "jpeg",
    "ringing",
    "overshoot",
    "grayscale",
    "threshold",
    "halftone",
    "paper_texture",
    "faded_ink",
    "erosion",
    "dilation",
    "stroke_dropout",
    "ink_bleed",
    "skew",
    "perspective",
    "scan_shadow",
    "clipping",
    "hand_drawn_jitter",
    "inconsistent_marker_outlines",
    "line_marker_contact",
)
TEMPLATE_FAMILIES = (
    "classic_single",
    "stacked_shared_axes",
    "compact_legend",
    "hand_drawn_grid",
)
MARKER_FAMILIES = ("geometric_basic", "mixed_print", "symbolic", "irregular")

_DEGRADATION_KINDS_BY_FAMILY: dict[str, tuple[str, ...]] = {
    "none": (
        "none",
        "grayscale",
    ),
    "print_light": (
        "faded_ink",
        "threshold",
        "halftone",
        "erosion",
        "dilation",
        "ink_bleed",
        "stroke_dropout",
        "inconsistent_marker_outlines",
        "line_marker_contact",
    ),
    "scan_noise": (
        "downsample",
        "isotropic_blur",
        "anisotropic_blur",
        "gaussian_noise",
        "poisson_noise",
        "impulse_noise",
        "jpeg",
        "ringing",
        "overshoot",
        "paper_texture",
        "scan_shadow",
        "clipping",
    ),
    "camera_skew": (
        "skew",
        "perspective",
        "hand_drawn_jitter",
    ),
}

FAMILY_TO_SPLIT: dict[str, dict[str, str]] = {
    "renderer": {
        "vector_clean": "train",
        "print_monochrome": "train",
        "scan_rough": "validation",
        "hand_drawn": "test",
    },
    "font": {
        "system_sans": "train",
        "system_serif": "train",
        "system_mono": "validation",
        "system_handwritten": "test",
    },
    "degradation": {
        "none": "train",
        "print_light": "train",
        "scan_noise": "validation",
        "camera_skew": "test",
    },
    "template": {
        "classic_single": "train",
        "stacked_shared_axes": "train",
        "compact_legend": "validation",
        "hand_drawn_grid": "test",
    },
    "marker": {
        "geometric_basic": "train",
        "mixed_print": "train",
        "symbolic": "validation",
        "irregular": "test",
    },
}

_RENDERER_PROFILES: dict[str, dict[str, str]] = {
    "vector_clean": {
        "font": "system_sans",
        "degradation": "none",
        "template": "classic_single",
        "marker": "geometric_basic",
    },
    "print_monochrome": {
        "font": "system_serif",
        "degradation": "print_light",
        "template": "stacked_shared_axes",
        "marker": "mixed_print",
    },
    "scan_rough": {
        "font": "system_mono",
        "degradation": "scan_noise",
        "template": "compact_legend",
        "marker": "symbolic",
    },
    "hand_drawn": {
        "font": "system_handwritten",
        "degradation": "camera_skew",
        "template": "hand_drawn_grid",
        "marker": "irregular",
    },
}

_GENERIC_FONT = {
    "system_sans": "sans",
    "system_serif": "serif",
    "system_mono": "mono",
    "system_handwritten": "handwritten",
}

_DESIGN_ALIASES = {
    "ab": "ab",
    "aba": "aba",
    "abab": "abab",
    "multiple_baseline": "multiple_baseline",
    "multiple_baselines": "multiple_baseline",
    "multiple_probe": "multiple_probe",
    "multiple_probes": "multiple_probe",
    "alternating_treatment": "alternating_treatments",
    "alternating_treatments": "alternating_treatments",
    "changing_criterion": "changing_criterion",
    "maintenance": "maintenance",
    "generalization": "generalization",
    "maintenance_generalization": "maintenance",
    "maintenance_and_generalization": "maintenance",
    "staggered_start": "staggered_starts",
    "staggered_starts": "staggered_starts",
    "shared_baseline": "shared_baseline",
    "shared_baseline_with_multiple_intervention_series": "shared_baseline",
}

_SYMBOLS: dict[str, tuple[str, str, str]] = {
    "circle": ("○", "●", "◌"),
    "square": ("□", "■", "▧"),
    "triangle_up": ("△", "▲", "⟁"),
    "triangle_down": ("▽", "▼", "⧨"),
    "diamond": ("◇", "◆", "◈"),
    "star": ("☆", "★", "✯"),
    "asterisk": ("*", "✱", "⁕"),
    "cross": ("×", "✚", "╳"),
    "other": ("+", "⬟", "⊹"),
}

_NAMESPACE = uuid.UUID("8d96c4ce-c56e-56c8-a0d2-647454b58745")


def marker_style_catalog() -> list[dict[str, str]]:
    """Return all 9 shape by 3 fill combinations in stable order."""

    catalog: list[dict[str, str]] = []
    for shape in MARKER_SHAPES:
        for fill_index, fill in enumerate(FILL_STATES):
            catalog.append(
                {
                    "style_id": f"{shape}_{fill}",
                    "shape": shape,
                    "fill": fill,
                    "symbol": _SYMBOLS[shape][fill_index],
                }
            )
    return catalog


def family_split(category: str, family_key: str) -> str:
    """Return the immutable split assigned to one family key."""

    try:
        return FAMILY_TO_SPLIT[category][family_key]
    except KeyError as error:
        raise ValueError(f"Unsupported {category} family: {family_key!r}") from error


def build_scene(
    design: str,
    seed: int,
    renderer_family: str,
    panel_count: int | None = None,
    *,
    session_count: int | None = None,
    features: Iterable[str] | None = None,
    presentation: Mapping[str, Any] | None = None,
    canvas_width: int = 1200,
    panel_height: int = 270,
    marker_radius: float | None = None,
    stroke_width: int | None = None,
) -> dict[str, Any]:
    """Build and schema-validate a deterministic declarative graph scene.

    ``features`` explicitly enables missing sessions, sparse probes, blank phase
    gaps, or irregular spacing. Design-required features are added automatically.
    Presentation values may override the stable renderer-profile defaults.
    """

    design_key = _normalize_design(design)
    seed_value = _validate_seed(seed)
    if renderer_family not in RENDERER_FAMILIES:
        raise ValueError(
            f"Unsupported renderer family {renderer_family!r}; expected one of "
            f"{RENDERER_FAMILIES}"
        )

    resolved_panel_count = (
        _default_panel_count(design_key) if panel_count is None else panel_count
    )
    if isinstance(resolved_panel_count, bool) or not isinstance(resolved_panel_count, int):
        raise TypeError("panel_count must be an integer")
    if not 1 <= resolved_panel_count <= 6:
        raise ValueError("panel_count must be between 1 and 6")
    if isinstance(canvas_width, bool) or not isinstance(canvas_width, int) or not 320 <= canvas_width <= 10000:
        raise ValueError("canvas_width must be an integer from 320 through 10000")
    if isinstance(panel_height, bool) or not isinstance(panel_height, int) or panel_height <= 0:
        raise ValueError("panel_height must be a positive integer")
    if marker_radius is not None and (not math.isfinite(float(marker_radius)) or float(marker_radius) <= 0):
        raise ValueError("marker_radius must be positive")
    if stroke_width is not None and (isinstance(stroke_width, bool) or not isinstance(stroke_width, int) or stroke_width <= 0):
        raise ValueError("stroke_width must be a positive integer")

    session_rng = _rng(seed_value, "session-count")
    resolved_session_count = (
        session_rng.randint(16, 28) if session_count is None else session_count
    )
    if isinstance(resolved_session_count, bool) or not isinstance(
        resolved_session_count, int
    ):
        raise TypeError("session_count must be an integer")
    if not 2 <= resolved_session_count <= 100:
        raise ValueError("session_count must be between 2 and 100")
    required_phase_count = len(_phase_sequence(design_key))
    if resolved_session_count < required_phase_count:
        raise ValueError(
            f"{design_key} requires at least {required_phase_count} sessions to "
            "represent every phase"
        )

    resolved_features = _resolve_features(design_key, renderer_family, features)
    profile = _RENDERER_PROFILES[renderer_family]
    split = family_split("renderer", renderer_family)
    families = {
        "renderer": {"key": renderer_family, "split": split},
        **{
            category: {"key": profile[category], "split": split}
            for category in ("font", "degradation", "template", "marker")
        },
    }
    _assert_family_isolation(families)

    default_presentation = _presentation_defaults(seed_value, renderer_family)
    if presentation:
        presentation_values = dict(presentation)
        font_size_px = presentation_values.pop("font_size_px", None)
        dense_tick_labels = presentation_values.pop("dense_tick_labels", None)
        if font_size_px is not None:
            if isinstance(font_size_px, bool) or not isinstance(font_size_px, int) or not 6 <= font_size_px <= 64:
                raise ValueError("presentation.font_size_px must be an integer from 6 through 64")
            default_presentation["font_size_px"] = font_size_px
        if dense_tick_labels is not None:
            if not isinstance(dense_tick_labels, bool):
                raise ValueError("presentation.dense_tick_labels must be a boolean")
            default_presentation["dense_tick_labels"] = dense_tick_labels
        unknown = set(presentation_values) - set(default_presentation)
        if unknown:
            raise ValueError(f"Unsupported presentation keys: {sorted(unknown)}")
        default_presentation.update(presentation_values)
    default_presentation["features"] = list(resolved_features)
    layout_font = FontResolver().resolve(
        _GENERIC_FONT[profile["font"]],
        int(default_presentation.get("font_size_px", 14)),
    ).load()

    style = _style_variation(
        seed_value,
        resolved_session_count,
        irregular="irregular_spacing" in resolved_features,
    )
    if marker_radius is not None:
        style["marker_radius"] = float(marker_radius)
    if stroke_width is not None:
        style["stroke_width"] = int(stroke_width)
    x_positions = _session_positions(
        seed_value,
        resolved_session_count,
        irregular="irregular_spacing" in resolved_features,
        edge_padding_fraction=style["session_spacing"]["edge_padding_fraction"],
        jitter_fraction=style["session_spacing"]["jitter_fraction"],
    )
    canvas_height = 50 + resolved_panel_count * panel_height + 30
    shared_axes = resolved_panel_count > 1

    annotations: dict[str, list[dict[str, Any]]] = {
        "text_regions": [],
        "artifacts": [],
    }
    panels: list[dict[str, Any]] = []
    for panel_index in range(resolved_panel_count):
        panels.append(
            _build_panel(
                design=design_key,
                seed=seed_value,
                panel_index=panel_index,
                panel_count=resolved_panel_count,
                session_count=resolved_session_count,
                x_positions=x_positions,
                style=style,
                shared_axes=shared_axes,
                families=families,
                presentation=default_presentation,
                features=resolved_features,
                annotations=annotations,
                canvas_width=canvas_width,
                panel_height=panel_height,
                layout_font=layout_font,
            )
        )

    scene_id = _stable_uuid(seed_value, design_key, renderer_family, "scene")
    scene: dict[str, Any] = {
        "schema_version": 1,
        "scene_id": scene_id,
        "seed": seed_value,
        "design": design_key,
        "coordinate_space": "original_pixels",
        "canvas": {
            "width": canvas_width,
            "height": canvas_height,
            "background": "#FFFFFF",
        },
        "families": families,
        "font": {
            "family_key": profile["font"],
            "generic_family": _GENERIC_FONT[profile["font"]],
            "source": "system",
            "packaged": False,
        },
        "style_catalog": marker_style_catalog(),
        "style": style,
        "layout": {
            "panel_count": resolved_panel_count,
            "session_count": resolved_session_count,
            "shared_axes": shared_axes,
            "session_x_positions": x_positions,
            "features": list(resolved_features),
        },
        "presentation": default_presentation,
        "panels": panels,
        "annotations": annotations,
        "degradations": _degradation_stages(seed_value, profile["degradation"]),
        "hard_negatives": _hard_negative_requests(seed_value, panels),
        "provenance": {
            "source": "procedural",
            "license": "Apache-2.0",
            "private_data": False,
            "external_assets": [],
            "bundled_fonts": False,
        },
    }
    _assert_scene_references(scene)
    validate_scene(scene)
    return scene


def _normalize_design(design: str) -> str:
    if not isinstance(design, str):
        raise TypeError("design must be a string")
    normalized = re.sub(r"[^a-z0-9]+", "_", design.strip().lower()).strip("_")
    try:
        return _DESIGN_ALIASES[normalized]
    except KeyError as error:
        raise ValueError(
            f"Unsupported design {design!r}; expected one of {SUPPORTED_DESIGNS}"
        ) from error


def _validate_seed(seed: int) -> int:
    if isinstance(seed, bool) or not isinstance(seed, int):
        raise TypeError("seed must be an integer")
    if not 0 <= seed <= 9_223_372_036_854_775_807:
        raise ValueError("seed must be between 0 and 2^63-1")
    return seed


def _default_panel_count(design: str) -> int:
    if design in {"multiple_baseline", "multiple_probe", "staggered_starts"}:
        return 3
    return 1


def _resolve_features(
    design: str, renderer_family: str, features: Iterable[str] | None
) -> tuple[str, ...]:
    resolved = set(features or ())
    unsupported = resolved - set(SCENE_FEATURES)
    if unsupported:
        raise ValueError(f"Unsupported scene features: {sorted(unsupported)}")
    if design == "multiple_probe":
        resolved.add("sparse_probes")
    if design == "staggered_starts":
        resolved.add("missing_sessions")
    if renderer_family == "hand_drawn":
        resolved.add("irregular_spacing")
    return tuple(feature for feature in SCENE_FEATURES if feature in resolved)


def _presentation_defaults(seed: int, renderer_family: str) -> dict[str, Any]:
    rng = _rng(seed, "presentation")
    if renderer_family == "vector_clean":
        x_visibility, y_visibility = "visible", "visible"
    elif renderer_family == "print_monochrome":
        x_visibility, y_visibility = "partial", "visible"
    elif renderer_family == "scan_rough":
        x_visibility, y_visibility = "partial", "partial"
    else:
        x_visibility, y_visibility = "hidden", "partial"
    return {
        "x_label_visibility": x_visibility,
        "y_label_visibility": y_visibility,
        "hide_zero_label": renderer_family in {"scan_rough", "hand_drawn"},
        "phase_divider_style": PHASE_DIVIDER_STYLES[
            RENDERER_FAMILIES.index(renderer_family) % len(PHASE_DIVIDER_STYLES)
        ],
        "connecting_line_style": LINE_STYLES[
            (RENDERER_FAMILIES.index(renderer_family) + rng.randrange(2))
            % len(LINE_STYLES)
        ],
        "legend_position": ("inside", "outside", "inside", "outside")[
            RENDERER_FAMILIES.index(renderer_family)
        ],
        "grayscale": renderer_family != "vector_clean",
        "monochrome": renderer_family in {"print_monochrome", "hand_drawn"},
        "show_participant_names": True,
        "show_arrows": True,
        "show_brackets": True,
        "show_top_condition_bars": True,
    }


def _style_variation(seed: int, session_count: int, *, irregular: bool) -> dict[str, Any]:
    rng = _rng(seed, "style-variation")
    y_profiles = (
        (0.0, 10.0, 2.0),
        (0.0, 20.0, 5.0),
        (0.0, 50.0, 10.0),
        (0.0, 100.0, 20.0),
        (0.0, 200.0, 25.0),
    )
    y_minimum, y_maximum, tick_interval = y_profiles[rng.randrange(len(y_profiles))]
    edge_padding = (0.0, 0.02, 0.04)[rng.randrange(3)]
    jitter_fraction = (0.15, 0.25, 0.35)[rng.randrange(3)] if irregular else 0.0
    nominal_pitch = (1.0 - 2.0 * edge_padding) / (session_count - 1)
    return {
        "variation_seed": seed,
        "y_axis": {
            "minimum": y_minimum,
            "maximum": y_maximum,
            "tick_interval": tick_interval,
        },
        "stroke_width": (1, 2, 3)[rng.randrange(3)],
        "marker_radius": (3.5, 4.0, 4.5, 5.0, 5.5, 6.0)[rng.randrange(6)],
        "session_spacing": {
            "mode": "irregular" if irregular else "uniform",
            "edge_padding_fraction": edge_padding,
            "jitter_fraction": jitter_fraction,
            "nominal_pitch_fraction": round(nominal_pitch, 8),
        },
    }


def _session_positions(
    seed: int,
    count: int,
    *,
    irregular: bool,
    edge_padding_fraction: float,
    jitter_fraction: float,
) -> list[float]:
    span = 1.0 - 2.0 * edge_padding_fraction
    if count == 2:
        return [edge_padding_fraction, round(1.0 - edge_padding_fraction, 8)]
    if not irregular:
        return [
            round(edge_padding_fraction + span * index / (count - 1), 8)
            for index in range(count)
        ]
    rng = _rng(seed, "irregular-spacing")
    gaps = [
        rng.uniform(1.0 - jitter_fraction, 1.0 + jitter_fraction)
        for _ in range(count - 1)
    ]
    total = sum(gaps)
    positions = [edge_padding_fraction]
    running = 0.0
    for gap in gaps:
        running += gap
        positions.append(round(edge_padding_fraction + span * running / total, 8))
    positions[-1] = round(1.0 - edge_padding_fraction, 8)
    return positions


def _phase_sequence(design: str) -> list[tuple[str, str, str]]:
    sequences: dict[str, list[tuple[str, str, str]]] = {
        "ab": [("a", "baseline", "Baseline"), ("b", "intervention", "Intervention")],
        "aba": [
            ("a", "baseline", "Baseline"),
            ("b", "intervention", "Intervention"),
            ("a", "baseline", "Withdrawal"),
        ],
        "abab": [
            ("a", "baseline", "Baseline"),
            ("b", "intervention", "Intervention"),
            ("a", "baseline", "Withdrawal"),
            ("b", "intervention", "Reintroduction"),
        ],
        "multiple_baseline": [
            ("a", "baseline", "Baseline"),
            ("b", "intervention", "Intervention"),
        ],
        "multiple_probe": [
            ("a", "baseline", "Baseline"),
            ("b", "intervention", "Intervention"),
        ],
        "alternating_treatments": [
            ("a", "baseline", "Baseline"),
            ("b", "intervention", "Alternating Treatments"),
        ],
        "changing_criterion": [
            ("a", "baseline", "Baseline"),
            ("b", "intervention", "Criterion 1"),
            ("phase3", "intervention", "Criterion 2"),
            ("phase4", "intervention", "Criterion 3"),
        ],
        "maintenance": [
            ("a", "baseline", "Baseline"),
            ("b", "intervention", "Intervention"),
            ("m", "maintenance", "Maintenance"),
        ],
        "generalization": [
            ("a", "baseline", "Baseline"),
            ("b", "intervention", "Intervention"),
            ("g", "generalization", "Generalization"),
        ],
        "staggered_starts": [
            ("a", "baseline", "Baseline"),
            ("b", "intervention", "Intervention"),
        ],
        "shared_baseline": [
            ("a", "baseline", "Shared Baseline"),
            ("b", "intervention", "Interventions"),
        ],
    }
    return sequences[design]


def _phase_ranges(
    design: str, session_count: int, panel_index: int
) -> list[tuple[int, int, str, str, str]]:
    sequence = _phase_sequence(design)
    if len(sequence) == 2 and design in {
        "multiple_baseline",
        "multiple_probe",
        "staggered_starts",
    }:
        baseline_end = max(
            1,
            min(
                session_count - 1,
                round(session_count * (0.30 + min(panel_index, 5) * 0.10)),
            ),
        )
        boundaries = [(1, baseline_end), (baseline_end + 1, session_count)]
    else:
        phase_count = len(sequence)
        base_size, remainder = divmod(session_count, phase_count)
        boundaries = []
        start = 1
        for index in range(phase_count):
            size = base_size + (1 if index < remainder else 0)
            end = start + size - 1
            boundaries.append((start, end))
            start = end + 1
    return [
        (start, end, code, normalized_type, label)
        for (start, end), (code, normalized_type, label) in zip(boundaries, sequence)
    ]


def _insert_blank_gap(
    ranges: list[tuple[int, int, str, str, str]], session_count: int
) -> list[tuple[int, int, str, str, str, bool]]:
    candidates = [
        (end - start + 1, index)
        for index, (start, end, _code, _kind, _label) in enumerate(ranges)
        if end - start + 1 >= 3
    ]
    if not candidates:
        raise ValueError(
            f"blank_phase_gaps requires a phase spanning at least 3 of {session_count} sessions"
        )
    _, selected = max(candidates)
    output: list[tuple[int, int, str, str, str, bool]] = []
    blank_index = len(ranges) + 1
    for index, (start, end, code, kind, label) in enumerate(ranges):
        if index != selected:
            output.append((start, end, code, kind, label, False))
            continue
        gap = (start + end) // 2
        output.append((start, gap - 1, code, kind, label, False))
        output.append((gap, gap, f"phase{blank_index}", "unknown", "", True))
        output.append((gap + 1, end, code, kind, f"{label} continued", False))
    return output


def _build_panel(
    *,
    design: str,
    seed: int,
    panel_index: int,
    panel_count: int,
    session_count: int,
    x_positions: list[float],
    style: dict[str, Any],
    shared_axes: bool,
    families: dict[str, dict[str, str]],
    presentation: dict[str, Any],
    features: tuple[str, ...],
    annotations: dict[str, list[dict[str, Any]]],
    canvas_width: int,
    panel_height: int,
    layout_font: Any,
) -> dict[str, Any]:
    panel_id = _stable_uuid(seed, design, str(panel_index), "panel")
    panel_y = 40 + panel_index * panel_height
    panel_box = [20.0, float(panel_y), float(canvas_width - 40), float(panel_height - 8)]
    legend_outside = presentation["legend_position"] == "outside"
    if canvas_width == 1200 and panel_height == 270:
        plot_x = 105.0
        plot_y = float(panel_y + 56)
        plot_width = 855.0 if legend_outside else 990.0
        plot_height = 164.0
    else:
        plot_x = max(48.0, round(canvas_width * 0.0875, 4))
        legend_reserve = 168.0 if legend_outside else 45.0
        plot_width = max(240.0, canvas_width - plot_x - legend_reserve)
        plot_y = float(panel_y + round(panel_height * 0.207, 4))
        plot_height = max(120.0, round(panel_height * 0.607, 4))
    plot_box = [plot_x, plot_y, plot_width, plot_height]
    bottom = plot_y + plot_height
    right = plot_x + plot_width
    y_axis = style["y_axis"]
    y_minimum = float(y_axis["minimum"])
    y_maximum = float(y_axis["maximum"])
    y_tick_interval = float(y_axis["tick_interval"])

    def screen_x(session: int) -> float:
        return round(plot_x + x_positions[session - 1] * plot_width, 4)

    raw_ranges = _phase_ranges(design, session_count, panel_index)
    if "blank_phase_gaps" in features:
        phase_ranges = _insert_blank_gap(raw_ranges, session_count)
    else:
        phase_ranges = [(*phase_range, False) for phase_range in raw_ranges]

    phases: list[dict[str, Any]] = []
    for order, (start, end, code, normalized, label, blank) in enumerate(
        phase_ranges, start=1
    ):
        phase_id = _stable_uuid(seed, panel_id, str(order), "phase")
        x_min = screen_x(start)
        x_max = screen_x(end)
        phase = {
            "phase_id": phase_id,
            "order": order,
            "code": code,
            "normalized_type": normalized,
            "label_text": None if blank else label,
            "session_start": start,
            "session_end": end,
            "screen_x_min": x_min,
            "screen_x_max": x_max,
            "blank": blank,
        }
        phases.append(phase)
        if not blank:
            _add_text(
                annotations,
                seed,
                panel_id,
                _phase_heading_box(plot_x, x_min, x_max, plot_y, label, layout_font),
                label,
                "phase_heading",
            )

    dividers: list[dict[str, Any]] = []
    for divider_index, phase in enumerate(phases[:-1]):
        next_phase = phases[divider_index + 1]
        x = round((phase["screen_x_max"] + next_phase["screen_x_min"]) / 2, 4)
        divider_id = _stable_uuid(seed, panel_id, str(divider_index), "divider")
        line = [x, plot_y, x, bottom]
        divider = {
            "divider_id": divider_id,
            "between_sessions": round(
                (phase["session_end"] + next_phase["session_start"]) / 2, 4
            ),
            "line": line,
            "style": presentation["phase_divider_style"],
            "role": "phase_divider",
        }
        dividers.append(divider)
        _add_artifact(annotations, seed, panel_id, "divider", "phase_divider", "line", line)

    ticks = _build_ticks(
        seed=seed,
        panel_id=panel_id,
        session_count=session_count,
        x_positions=x_positions,
        plot_box=plot_box,
        y_minimum=y_minimum,
        y_maximum=y_maximum,
        y_tick_interval=y_tick_interval,
        presentation=presentation,
        annotations=annotations,
    )
    axes = {
        "x": {
            "line": [plot_x, bottom, right, bottom],
            "shared": shared_axes and panel_index < panel_count - 1,
            "role": "x_axis",
            "session_count": session_count,
        },
        "y": {
            "line": [plot_x, plot_y, plot_x, bottom],
            "shared": False,
            "role": "y_axis",
            "min": y_minimum,
            "max": y_maximum,
            "tick_interval": y_tick_interval,
        },
    }
    _add_artifact(annotations, seed, panel_id, "axis", "x_axis", "line", axes["x"]["line"])
    _add_artifact(annotations, seed, panel_id, "axis", "y_axis", "line", axes["y"]["line"])

    if presentation["show_participant_names"]:
        participant = f"Participant {panel_index + 1:02d}"
        _add_text(
            annotations,
            seed,
            panel_id,
            [25.0, plot_y + plot_height / 2 - 9, 74.0, 18.0],
            participant,
            "participant",
        )
    else:
        participant = None

    if panel_index == panel_count - 1 or not shared_axes:
        _add_text(
            annotations,
            seed,
            panel_id,
            [plot_x + plot_width / 2 - 35, bottom + 25, 70.0, 18.0],
            "Sessions",
            "axis_title",
        )
    _add_text(
        annotations,
        seed,
        panel_id,
        [23.0, plot_y + plot_height / 2 + 16, 72.0, 18.0],
        "Outcome",
        "axis_title",
    )

    series = _series_for_design(
        design,
        seed,
        panel_id,
        panel_index,
        families["marker"]["key"],
        presentation,
        stroke_width=int(style["stroke_width"]),
    )
    missing_sessions = _missing_sessions(
        seed, panel_id, session_count, enabled="missing_sessions" in features
    )
    points = _points_for_panel(
        design=design,
        seed=seed,
        panel_id=panel_id,
        panel_index=panel_index,
        session_count=session_count,
        screen_x=screen_x,
        plot_y=plot_y,
        plot_height=plot_height,
        phases=phases,
        series=series,
        missing_sessions=missing_sessions,
        sparse="sparse_probes" in features,
        ticks=ticks,
        marker_radius=float(style["marker_radius"]),
        y_minimum=y_minimum,
        y_maximum=y_maximum,
    )
    connections = _connections(seed, panel_id, series, points)

    legend = _legend(
        seed,
        panel_id,
        series,
        presentation["legend_position"],
        plot_box,
        annotations,
    )
    arrows = _arrows(
        seed,
        panel_id,
        points,
        presentation["show_arrows"],
        annotations,
    )
    brackets = _brackets(
        seed,
        panel_id,
        plot_box,
        presentation["show_brackets"],
        annotations,
    )
    condition_bars = _condition_bars(
        seed,
        panel_id,
        phases,
        panel_box,
        plot_x,
        plot_y,
        layout_font,
        presentation["show_top_condition_bars"],
        annotations,
    )

    return {
        "panel_id": panel_id,
        "index": panel_index,
        "participant": participant,
        "box": panel_box,
        "plot_box": plot_box,
        "axes": axes,
        "calibration_anchors": [
            {
                "kind": "session1_y0",
                "screen": [screen_x(1), bottom],
                "graph": [1.0, y_minimum],
                "confidence": 1.0,
            },
            {
                "kind": "session1_ymax",
                "screen": [screen_x(1), plot_y],
                "graph": [1.0, y_maximum],
                "confidence": 1.0,
            },
            {
                "kind": "sessionmax_y0",
                "screen": [screen_x(session_count), bottom],
                "graph": [float(session_count), y_minimum],
                "confidence": 1.0,
            },
        ],
        "ticks": ticks,
        "phases": phases,
        "dividers": dividers,
        "series": series,
        "points": points,
        "connections": connections,
        "missing_sessions": missing_sessions,
        "legend": legend,
        "arrows": arrows,
        "brackets": brackets,
        "condition_bars": condition_bars,
    }


def _build_ticks(
    *,
    seed: int,
    panel_id: str,
    session_count: int,
    x_positions: list[float],
    plot_box: list[float],
    y_minimum: float,
    y_maximum: float,
    y_tick_interval: float,
    presentation: dict[str, Any],
    annotations: dict[str, list[dict[str, Any]]],
) -> list[dict[str, Any]]:
    plot_x, plot_y, plot_width, plot_height = plot_box
    bottom = plot_y + plot_height
    ticks: list[dict[str, Any]] = []
    step = 1 if presentation.get("dense_tick_labels", False) else max(
        1,
        math.ceil(session_count / 10),
    )
    x_values = list(range(1, session_count + 1, step))
    if x_values[-1] != session_count:
        x_values.append(session_count)
    for visible_index, value in enumerate(x_values):
        x = round(plot_x + x_positions[value - 1] * plot_width, 4)
        hidden = _label_hidden(
            presentation["x_label_visibility"], visible_index, len(x_values)
        )
        label = None if hidden else str(value)
        tick = {
            "tick_id": _stable_uuid(seed, panel_id, "x", str(value), "tick"),
            "axis": "x",
            "center": [x, bottom],
            "line": [x, bottom - 4.0, x, bottom + 4.0],
            "value": float(value),
            "label": label,
            "hidden": hidden,
            "role": "x_tick",
        }
        ticks.append(tick)
        _add_artifact(annotations, seed, panel_id, "tick", "x_tick", "line", tick["line"])
        if label is not None:
            _add_text(
                annotations,
                seed,
                panel_id,
                [x - 10.0, bottom + 6.0, 20.0, 15.0],
                label,
                "x_tick",
            )

    tick_count = round((y_maximum - y_minimum) / y_tick_interval)
    y_values = tuple(
        round(y_minimum + index * y_tick_interval, 8)
        for index in range(tick_count + 1)
    )
    for visible_index, value in enumerate(y_values):
        y_fraction = (value - y_minimum) / (y_maximum - y_minimum)
        y = round(bottom - y_fraction * plot_height, 4)
        hidden = _label_hidden(
            presentation["y_label_visibility"], visible_index, len(y_values)
        ) or (value == 0 and presentation["hide_zero_label"])
        label = None if hidden else _format_tick_value(value)
        tick = {
            "tick_id": _stable_uuid(seed, panel_id, "y", str(value), "tick"),
            "axis": "y",
            "center": [plot_x, y],
            "line": [plot_x - 4.0, y, plot_x + 4.0, y],
            "value": float(value),
            "label": label,
            "hidden": hidden,
            "role": "y_tick",
        }
        ticks.append(tick)
        _add_artifact(annotations, seed, panel_id, "tick", "y_tick", "line", tick["line"])
        if label is not None:
            _add_text(
                annotations,
                seed,
                panel_id,
                [plot_x - 39.0, y - 8.0, 30.0, 16.0],
                label,
                "y_tick",
            )
    return ticks


def _label_hidden(visibility: str, index: int, count: int) -> bool:
    if visibility == "hidden":
        return True
    if visibility == "partial":
        return index not in {0, count - 1} and index % 2 == 1
    return False


def _format_tick_value(value: float) -> str:
    return str(int(value)) if value.is_integer() else f"{value:g}"


def _series_for_design(
    design: str,
    seed: int,
    panel_id: str,
    panel_index: int,
    marker_family: str,
    presentation: dict[str, Any],
    *,
    stroke_width: int,
) -> list[dict[str, Any]]:
    if design in {"alternating_treatments", "shared_baseline"}:
        specs = [
            ("Shared baseline", "baseline"),
            ("Treatment B", "intervention"),
            ("Treatment C", "intervention"),
        ]
    elif design == "maintenance":
        specs = [("Primary outcome", "intervention"), ("Maintenance probes", "maintenance")]
    elif design == "generalization":
        specs = [
            ("Primary outcome", "intervention"),
            ("Generalization probes", "generalization"),
        ]
    else:
        specs = [("Primary outcome", "intervention")]

    family_offsets = {
        "geometric_basic": 0,
        "mixed_print": 4,
        "symbolic": 15,
        "irregular": 21,
    }
    catalog = marker_style_catalog()
    series: list[dict[str, Any]] = []
    for index, (name, role) in enumerate(specs):
        style = catalog[
            (family_offsets[marker_family] + panel_index * 3 + index) % len(catalog)
        ]
        series_id = _stable_uuid(seed, panel_id, str(index), "series")
        series.append(
            {
                "series_id": series_id,
                "display_name": name,
                "semantic_role": role,
                "shape": style["shape"],
                "fill": style["fill"],
                "symbol": style["symbol"],
                "line_style": presentation["connecting_line_style"],
                "stroke": "#111111",
                "stroke_width": stroke_width,
                "point_ids": [],
                "legend_text": name,
                "shared_baseline_series_id": None,
                "applicable_probe_series_ids": [],
            }
        )
    if design in {"alternating_treatments", "shared_baseline"}:
        baseline_id = series[0]["series_id"]
        for intervention in series[1:]:
            intervention["shared_baseline_series_id"] = baseline_id
    if design in {"maintenance", "generalization"}:
        series[0]["applicable_probe_series_ids"] = [series[1]["series_id"]]
    return series


def _missing_sessions(
    seed: int, panel_id: str, session_count: int, *, enabled: bool
) -> list[int]:
    if not enabled or session_count <= 3:
        return []
    rng = _rng(seed, panel_id, "missing-sessions")
    candidates = list(range(2, session_count))
    count = min(max(1, session_count // 15), len(candidates))
    return sorted(rng.sample(candidates, count))


def _points_for_panel(
    *,
    design: str,
    seed: int,
    panel_id: str,
    panel_index: int,
    session_count: int,
    screen_x: Any,
    plot_y: float,
    plot_height: float,
    phases: list[dict[str, Any]],
    series: list[dict[str, Any]],
    missing_sessions: list[int],
    sparse: bool,
    ticks: list[dict[str, Any]],
    marker_radius: float,
    y_minimum: float,
    y_maximum: float,
) -> list[dict[str, Any]]:
    points: list[dict[str, Any]] = []
    printed_sessions = {
        int(tick["value"])
        for tick in ticks
        if tick["axis"] == "x" and tick["label"] is not None
    }
    observation_counts = {item["series_id"]: 0 for item in series}
    for session in range(1, session_count + 1):
        phase = next(
            item
            for item in phases
            if item["session_start"] <= session <= item["session_end"]
        )
        if session in missing_sessions or phase["blank"]:
            continue
        if sparse and not _keep_sparse_session(session, session_count, phases, panel_index):
            continue
        selected_series = _series_at_session(design, session, phase, series)
        for series_index, series_item in selected_series:
            observation_counts[series_item["series_id"]] += 1
            observation_index = observation_counts[series_item["series_id"]]
            y_value = _y_value(
                seed,
                panel_id,
                session,
                series_index,
                phase["normalized_type"],
                phase["order"],
                y_minimum,
                y_maximum,
            )
            y_fraction = (y_value - y_minimum) / (y_maximum - y_minimum)
            center = [
                screen_x(session),
                round(plot_y + (1.0 - y_fraction) * plot_height, 4),
            ]
            point_id = _stable_uuid(
                seed, panel_id, series_item["series_id"], str(session), "point"
            )
            printed = session if session in printed_sessions else None
            radius = marker_radius
            point = {
                "point_id": point_id,
                "series_id": series_item["series_id"],
                "phase_id": phase["phase_id"],
                "observation_index": observation_index,
                "printed_x_value": printed,
                "estimated_x_value": None if printed is not None else float(session),
                "x_confidence": 1.0 if printed is not None else 0.85,
                "graph": [float(session), y_value],
                "center": center,
                "coordinate_space": "original_pixels",
                "radius": radius,
                "shape": series_item["shape"],
                "fill": series_item["fill"],
                "mask": _marker_mask(center, radius),
            }
            points.append(point)
            series_item["point_ids"].append(point_id)
    return points


def _keep_sparse_session(
    session: int,
    session_count: int,
    phases: list[dict[str, Any]],
    panel_index: int,
) -> bool:
    boundaries = {
        value
        for phase in phases
        for value in (phase["session_start"], phase["session_end"])
    }
    return (
        session in {1, session_count}
        or session in boundaries
        or (session + panel_index) % 3 == 0
    )


def _series_at_session(
    design: str,
    session: int,
    phase: dict[str, Any],
    series: list[dict[str, Any]],
) -> list[tuple[int, dict[str, Any]]]:
    if design in {"alternating_treatments", "shared_baseline"}:
        if phase["normalized_type"] == "baseline":
            return [(0, series[0])]
        if design == "alternating_treatments":
            selected = 1 + (session % 2)
            return [(selected, series[selected])]
        return [(1, series[1]), (2, series[2])]
    if design == "maintenance" and phase["normalized_type"] == "maintenance":
        return [(1, series[1])]
    if design == "generalization" and phase["normalized_type"] == "generalization":
        return [(1, series[1])]
    return [(0, series[0])]


def _y_value(
    seed: int,
    panel_id: str,
    session: int,
    series_index: int,
    phase_type: str,
    phase_order: int,
    y_minimum: float,
    y_maximum: float,
) -> float:
    rng = _rng(seed, panel_id, str(session), str(series_index), "y")
    centers = {
        "baseline": 18.0,
        "intervention": min(82.0, 58.0 + phase_order * 5.0),
        "maintenance": 66.0,
        "generalization": 61.0,
        "unknown": 45.0,
    }
    center = centers[phase_type] + (series_index - 1) * 6.0
    trend = min(8.0, session * 0.35) if phase_type == "intervention" else 0.0
    percentage = max(2.0, min(98.0, center + trend + rng.uniform(-6.0, 6.0)))
    return round(y_minimum + percentage / 100.0 * (y_maximum - y_minimum), 4)


def _marker_mask(center: list[float], radius: float) -> list[list[float]]:
    x, y = center
    return [
        [round(x - radius, 4), round(y, 4)],
        [round(x, 4), round(y - radius, 4)],
        [round(x + radius, 4), round(y, 4)],
        [round(x, 4), round(y + radius, 4)],
    ]


def _connections(
    seed: int,
    panel_id: str,
    series: list[dict[str, Any]],
    points: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    by_id = {point["point_id"]: point for point in points}
    connections: list[dict[str, Any]] = []
    for series_item in series:
        ordered = sorted(
            (by_id[point_id] for point_id in series_item["point_ids"]),
            key=lambda point: point["observation_index"],
        )
        for index, (left, right) in enumerate(zip(ordered, ordered[1:])):
            style = series_item["line_style"]
            connections.append(
                {
                    "edge_id": _stable_uuid(
                        seed, panel_id, series_item["series_id"], str(index), "edge"
                    ),
                    "series_id": series_item["series_id"],
                    "from_point_id": left["point_id"],
                    "to_point_id": right["point_id"],
                    "style": style,
                    "visible": style != "missing",
                }
            )
    return connections


def _legend(
    seed: int,
    panel_id: str,
    series: list[dict[str, Any]],
    position: str,
    plot_box: list[float],
    annotations: dict[str, list[dict[str, Any]]],
) -> dict[str, Any]:
    if position == "none":
        return {"visible": False, "position": "none", "box": None, "entries": []}
    plot_x, plot_y, plot_width, _plot_height = plot_box
    x = plot_x + plot_width - 162.0 if position == "inside" else plot_x + plot_width + 18.0
    y = plot_y + 8.0
    box = [x, y, 150.0, max(34.0, 22.0 * len(series) + 10.0)]
    entries: list[dict[str, Any]] = []
    for index, series_item in enumerate(series):
        entry_y = y + 7.0 + index * 22.0
        glyph_box = [x + 8.0, entry_y, 15.0, 15.0]
        text_box = [x + 29.0, entry_y, 112.0, 15.0]
        text = str(series_item["legend_text"])
        entries.append(
            {
                "series_id": series_item["series_id"],
                "glyph_box": glyph_box,
                "text_box": text_box,
                "text": text,
            }
        )
        _add_text(annotations, seed, panel_id, text_box, text, "legend_text")
    _add_artifact(annotations, seed, panel_id, "legend", "legend", "box", box)
    return {"visible": True, "position": position, "box": box, "entries": entries}


def _arrows(
    seed: int,
    panel_id: str,
    points: list[dict[str, Any]],
    visible: bool,
    annotations: dict[str, list[dict[str, Any]]],
) -> list[dict[str, Any]]:
    if not visible or not points:
        return []
    target = points[-1]["center"]
    start = [round(target[0] - 42.0, 4), round(target[1] - 28.0, 4)]
    arrow = {
        "arrow_id": _stable_uuid(seed, panel_id, "annotation", "arrow"),
        "start": start,
        "tip": target,
        "label": "Probe",
        "role": "annotation_arrow",
    }
    _add_text(
        annotations,
        seed,
        panel_id,
        [start[0] - 5.0, start[1] - 16.0, 48.0, 15.0],
        "Probe",
        "annotation",
    )
    _add_artifact(annotations, seed, panel_id, "arrowhead", "annotation_arrow", "point", target)
    return [arrow]


def _brackets(
    seed: int,
    panel_id: str,
    plot_box: list[float],
    visible: bool,
    annotations: dict[str, list[dict[str, Any]]],
) -> list[dict[str, Any]]:
    if not visible:
        return []
    plot_x, plot_y, plot_width, _plot_height = plot_box
    left = round(plot_x + plot_width * 0.72, 4)
    right = round(plot_x + plot_width * 0.92, 4)
    y = round(plot_y - 34.0, 4)
    points = [[left, y + 6.0], [left, y], [right, y], [right, y + 6.0]]
    bracket = {
        "bracket_id": _stable_uuid(seed, panel_id, "top", "bracket"),
        "points": points,
        "label": "Follow-up",
        "role": "bracket",
    }
    _add_text(
        annotations,
        seed,
        panel_id,
        [(left + right) / 2 - 40.0, y - 17.0, 80.0, 15.0],
        "Follow-up",
        "annotation",
    )
    _add_artifact(
        annotations,
        seed,
        panel_id,
        "bracket",
        "bracket",
        "polyline",
        [coordinate for point in points for coordinate in point],
    )
    return [bracket]


def _condition_bars(
    seed: int,
    panel_id: str,
    phases: list[dict[str, Any]],
    panel_box: list[float],
    plot_x: float,
    plot_y: float,
    layout_font: Any,
    visible: bool,
    annotations: dict[str, list[dict[str, Any]]],
) -> list[dict[str, Any]]:
    if not visible:
        return []
    bars: list[dict[str, Any]] = []
    for index, phase in enumerate(phases):
        if phase["blank"]:
            continue
        y = plot_y - 8.0
        line = [phase["screen_x_min"], y, phase["screen_x_max"], y]
        label = phase["code"].upper()
        bars.append(
            {
                "bar_id": _stable_uuid(seed, panel_id, str(index), "condition-bar"),
                "line": line,
                "label": label,
                "role": "condition_bar",
            }
        )
        _add_text(
            annotations,
            seed,
            panel_id,
            _condition_label_box(
                panel_id,
                phase,
                panel_box,
                plot_x,
                plot_y,
                layout_font,
                annotations["text_regions"],
            ),
            label,
            "condition_label",
        )
        _add_artifact(
            annotations, seed, panel_id, "condition_bar", "condition_bar", "line", line
        )
    return bars


def _phase_heading_box(
    plot_x: float,
    phase_x_min: float,
    phase_x_max: float,
    plot_y: float,
    text: str,
    layout_font: Any,
) -> list[float]:
    width, height = _measured_text_region_size(layout_font, text, 110.0, 18.0)
    return [
        max(plot_x, (phase_x_min + phase_x_max) / 2 - width / 2),
        plot_y - 25.0,
        width,
        height,
    ]


def _condition_label_box(
    panel_id: str,
    phase: Mapping[str, Any],
    panel_box: list[float],
    plot_x: float,
    plot_y: float,
    layout_font: Any,
    text_regions: list[dict[str, Any]],
) -> list[float]:
    """Place a compact condition code in unoccupied panel-header geometry."""

    label = str(phase["code"]).upper()
    label_width, label_height = _measured_text_region_size(
        layout_font, label, 20.0, 13.0
    )
    phase_x_min = float(phase["screen_x_min"])
    phase_x_max = float(phase["screen_x_max"])
    label_y = plot_y - 23.0
    heading_box = _phase_heading_box(
        plot_x,
        phase_x_min,
        phase_x_max,
        plot_y,
        str(phase["label_text"]),
        layout_font,
    )
    occupied_boxes = []
    for item in text_regions:
        if item["panel_id"] != panel_id:
            continue
        box = [float(value) for value in item["box"]]
        box[2], box[3] = _measured_text_region_size(
            layout_font, str(item["text"]), box[2], box[3]
        )
        occupied_boxes.append(box)

    phase_center = (phase_x_min + phase_x_max) / 2
    phase_candidates = (
        [phase_x_min, label_y, label_width, label_height],
        [phase_x_max - label_width, label_y, label_width, label_height],
        [phase_center - label_width / 2, label_y, label_width, label_height],
    )
    adjacent_candidates = (
        [heading_box[0] - label_width, label_y, label_width, label_height],
        [heading_box[0] + heading_box[2], label_y, label_width, label_height],
    )
    upper_y = plot_y - 25.0 - label_height
    upper_candidates = tuple(
        [candidate[0], upper_y, label_width, label_height]
        for candidate in (*phase_candidates, *adjacent_candidates)
    )
    panel_x, panel_y, panel_width, panel_height = panel_box
    candidates = (
        *((candidate, True) for candidate in phase_candidates),
        *((candidate, False) for candidate in adjacent_candidates),
        *((candidate, False) for candidate in upper_candidates),
    )

    def is_inside(candidate: list[float], require_phase_containment: bool) -> bool:
        if require_phase_containment and (
            candidate[0] < phase_x_min or candidate[0] + label_width > phase_x_max
        ):
            return False
        if candidate[1] < panel_y or candidate[1] + label_height > panel_y + panel_height:
            return False
        if candidate[0] < panel_x or candidate[0] + label_width > panel_x + panel_width:
            return False
        return True

    for candidate, require_phase_containment in candidates:
        if not is_inside(candidate, require_phase_containment):
            continue
        if not any(_boxes_overlap(candidate, occupied) for occupied in occupied_boxes):
            return candidate

    raise RuntimeError(
        f"Panel header has no non-overlapping space for condition label {label!r}"
    )


def _measured_text_region_size(
    layout_font: Any,
    text: str,
    minimum_width: float,
    minimum_height: float,
) -> tuple[float, float]:
    left, top, right, bottom = layout_font.getbbox(text, anchor="lt")
    return (
        max(minimum_width, float(right - left)),
        max(minimum_height, float(bottom - top)),
    )


def _boxes_overlap(first: list[float], second: list[float]) -> bool:
    first_x, first_y, first_width, first_height = first
    second_x, second_y, second_width, second_height = second
    return (
        first_x < second_x + second_width
        and second_x < first_x + first_width
        and first_y < second_y + second_height
        and second_y < first_y + first_height
    )


def _hard_negative_requests(
    seed: int, panels: list[dict[str, Any]]
) -> list[dict[str, Any]]:
    requests: list[dict[str, Any]] = []
    shapes: tuple[str | None, ...] = (
        "circle",
        "triangle_up",
        "square",
        "circle",
        "circle",
        "circle",
        "square",
        "circle",
        None,
    )
    geometry_kinds = (
        "box",
        "point",
        "point",
        "line",
        "point",
        "box",
        "polyline",
        "point",
        "point",
    )
    for index, kind in enumerate(HARD_NEGATIVE_KINDS):
        panel = panels[index % len(panels)]
        plot_x, plot_y, plot_width, plot_height = panel["plot_box"]
        x = round(plot_x + plot_width * (0.08 + index * 0.095), 4)
        y = round(plot_y + plot_height * (0.15 + (index % 3) * 0.23), 4)
        geometry_kind = geometry_kinds[index]
        if geometry_kind == "point":
            coordinates = [x, y]
        elif geometry_kind == "line":
            coordinates = [x - 5.0, y, x + 5.0, y]
        elif geometry_kind == "box":
            coordinates = [x - 5.0, y - 5.0, 10.0, 10.0]
        else:
            coordinates = [x - 7.0, y + 5.0, x - 7.0, y - 5.0, x + 7.0, y - 5.0]
        requests.append(
            {
                "request_id": _stable_uuid(seed, panel["panel_id"], kind, "hard-negative"),
                "panel_id": panel["panel_id"],
                "kind": kind,
                "geometry": {"kind": geometry_kind, "coordinates": coordinates},
                "marker_like_shape": shapes[index],
                "role": "hard_negative",
            }
        )
    return requests


def _degradation_stages(seed: int, family_key: str) -> list[dict[str, Any]]:
    if family_key not in _DEGRADATION_KINDS_BY_FAMILY:
        raise ValueError(f"Unsupported degradation family: {family_key!r}")
    if {
        kind
        for family_kinds in _DEGRADATION_KINDS_BY_FAMILY.values()
        for kind in family_kinds
    } != set(DEGRADATION_KIND_CATALOG):
        raise RuntimeError("Degradation family catalog does not cover every supported kind")

    family_kinds = _DEGRADATION_KINDS_BY_FAMILY[family_key]
    selection_rng = _rng(seed, family_key, "degradation-selection-v1")
    stage_count = selection_rng.randint(1, 2)
    selected_kinds = tuple(selection_rng.sample(family_kinds, stage_count))
    return [
        {
            "stage": index,
            "family_key": family_key,
            "kind": kind,
            "parameters": _degradation_parameters(seed, family_key, kind, index),
            "deterministic": True,
        }
        for index, kind in enumerate(selected_kinds, start=1)
    ]


def _degradation_parameters(
    seed: int,
    family_key: str,
    kind: str,
    stage: int,
) -> dict[str, Any]:
    return _degradation_parameter_values(seed, family_key, kind, stage)


def _degradation_parameter_values(
    seed: int,
    family_key: str,
    kind: str,
    stage: int,
) -> dict[str, Any]:
    rng = _rng(seed, family_key, kind, str(stage), "degradation-parameters")

    if kind in {"none", "grayscale"}:
        return {}
    if kind == "downsample":
        return {
            "scale": (0.5, 0.625, 0.75)[rng.randrange(3)],
            "resampler": ("bilinear", "bicubic", "lanczos")[rng.randrange(3)],
        }
    if kind in {"isotropic_blur", "anisotropic_blur"}:
        return {"radius": (0.6, 0.9, 1.2)[rng.randrange(3)]}
    if kind == "gaussian_noise":
        return {"sigma": (2.0, 3.5, 5.0)[rng.randrange(3)]}
    if kind == "poisson_noise":
        return {"scale": (0.08, 0.12, 0.18)[rng.randrange(3)]}
    if kind == "impulse_noise":
        return {"probability": (0.002, 0.005, 0.01)[rng.randrange(3)]}
    if kind == "jpeg":
        return {"quality": (55, 70, 85)[rng.randrange(3)]}
    if kind in {"ringing", "overshoot"}:
        return {"factor": (1.5, 2.0, 2.5)[rng.randrange(3)]}
    if kind == "threshold":
        return {"cutoff": (160, 192, 224)[rng.randrange(3)]}
    if kind == "halftone":
        return {"cell_size": (2, 3, 4)[rng.randrange(3)]}
    if kind == "paper_texture":
        return {"sigma": (1.5, 2.5, 3.5)[rng.randrange(3)]}
    if kind == "faded_ink":
        return {"opacity": (0.65, 0.78, 0.88)[rng.randrange(3)]}
    if kind in {"erosion", "dilation", "ink_bleed"}:
        return {"size": (3, 5)[rng.randrange(2)]}
    if kind == "stroke_dropout":
        return {
            "count": (8, 16, 24)[rng.randrange(3)],
            "length": (4, 6, 8)[rng.randrange(3)],
            "width": (1, 2)[rng.randrange(2)],
        }
    if kind == "scan_shadow":
        return {
            "strength": (0.10, 0.18, 0.25)[rng.randrange(3)],
            "side": ("left", "right", "top", "bottom")[rng.randrange(4)],
        }
    if kind == "clipping":
        return {
            "amount_px": (1, 2, 3)[rng.randrange(3)],
            "side": ("left", "right", "top", "bottom")[rng.randrange(4)],
        }
    if kind == "skew":
        return {
            "strength": (0.03, 0.05, 0.07)[rng.randrange(3)],
            "side": ("left", "right")[rng.randrange(2)],
        }
    if kind == "perspective":
        return {"strength": (0.03, 0.05, 0.08)[rng.randrange(3)]}
    if kind == "hand_drawn_jitter":
        return {
            "strength": (0.03, 0.06, 0.09)[rng.randrange(3)],
            "side": ("left", "right", "top", "bottom")[rng.randrange(4)],
        }
    if kind == "inconsistent_marker_outlines":
        return {"strength": (0.04, 0.08, 0.12)[rng.randrange(3)]}
    if kind == "line_marker_contact":
        return {"strength": (0.35, 0.55, 0.75)[rng.randrange(3)]}
    raise RuntimeError(f"Missing deterministic parameters for degradation kind {kind!r}")


def _add_text(
    annotations: dict[str, list[dict[str, Any]]],
    seed: int,
    panel_id: str,
    box: list[float],
    text: str,
    role: str,
) -> None:
    region_id = _stable_uuid(
        seed,
        panel_id,
        role,
        text,
        ",".join(f"{value:.4f}" for value in box),
        "text",
    )
    annotations["text_regions"].append(
        {
            "region_id": region_id,
            "panel_id": panel_id,
            "box": [round(float(value), 4) for value in box],
            "text": text,
            "role": role,
            "visible": True,
        }
    )
    _add_artifact(annotations, seed, panel_id, "text", role, "box", box)


def _add_artifact(
    annotations: dict[str, list[dict[str, Any]]],
    seed: int,
    panel_id: str,
    kind: str,
    role: str,
    geometry_kind: str,
    coordinates: list[float],
) -> None:
    flattened = [float(value) for value in coordinates]
    token = ",".join(f"{value:.4f}" for value in flattened)
    annotations["artifacts"].append(
        {
            "artifact_id": _stable_uuid(seed, panel_id, kind, role, token, "artifact"),
            "panel_id": panel_id,
            "kind": kind,
            "role": role,
            "geometry": {
                "kind": geometry_kind,
                "coordinates": [round(value, 4) for value in flattened],
            },
        }
    )


def _assert_family_isolation(families: dict[str, dict[str, str]]) -> None:
    splits = {value["split"] for value in families.values()}
    if len(splits) != 1:
        raise RuntimeError("A scene may not mix train, validation, and test families")
    for category, value in families.items():
        expected = family_split(category, value["key"])
        if value["split"] != expected:
            raise RuntimeError(f"Incorrect split for {category} family {value['key']}")


def _assert_scene_references(scene: dict[str, Any]) -> None:
    panel_ids = {panel["panel_id"] for panel in scene["panels"]}
    if len(panel_ids) != len(scene["panels"]):
        raise RuntimeError("Panel IDs must be unique")
    for panel in scene["panels"]:
        phase_ids = {phase["phase_id"] for phase in panel["phases"]}
        series_ids = {series["series_id"] for series in panel["series"]}
        point_ids = {point["point_id"] for point in panel["points"]}
        if len(point_ids) != len(panel["points"]):
            raise RuntimeError("Every marker must have exactly one unique point ID")
        for point in panel["points"]:
            if point["phase_id"] not in phase_ids or point["series_id"] not in series_ids:
                raise RuntimeError("Point references an unknown phase or series")
        for series_item in panel["series"]:
            if not set(series_item["point_ids"]) <= point_ids:
                raise RuntimeError("Series references an unknown point")
        for edge in panel["connections"]:
            if edge["from_point_id"] not in point_ids or edge["to_point_id"] not in point_ids:
                raise RuntimeError("Connection references an unknown point")
    for collection_name in ("text_regions", "artifacts"):
        if any(
            item["panel_id"] not in panel_ids
            for item in scene["annotations"][collection_name]
        ):
            raise RuntimeError(f"{collection_name} references an unknown panel")
    if any(item["panel_id"] not in panel_ids for item in scene["hard_negatives"]):
        raise RuntimeError("Hard-negative request references an unknown panel")
    _assert_hard_negatives_within_canvas(scene)


def _assert_hard_negatives_within_canvas(scene: dict[str, Any]) -> None:
    width = float(scene["canvas"]["width"])
    height = float(scene["canvas"]["height"])
    for item in scene["hard_negatives"]:
        geometry = item["geometry"]
        coordinates = [float(value) for value in geometry["coordinates"]]
        if geometry["kind"] == "box":
            x, y, box_width, box_height = coordinates
            points = ((x, y), (x + box_width, y + box_height))
        else:
            points = tuple(zip(coordinates[::2], coordinates[1::2]))
        if not points or any(
            not (0.0 <= x <= width and 0.0 <= y <= height) for x, y in points
        ):
            raise RuntimeError(
                f"Hard-negative request {item['request_id']} is outside the canvas"
            )


def _stable_uuid(seed: int, *parts: str) -> str:
    name = "|".join((str(seed), *parts))
    return str(uuid.uuid5(_NAMESPACE, name))


def _derived_int(seed: int, *parts: str) -> int:
    payload = "|".join((str(seed), *parts)).encode("utf-8")
    return int.from_bytes(hashlib.sha256(payload).digest()[:8], "big")


def _rng(seed: int, *parts: str) -> random.Random:
    return random.Random(_derived_int(seed, *parts))


__all__ = [
    "DEGRADATION_FAMILIES",
    "DEGRADATION_KIND_CATALOG",
    "FAMILY_TO_SPLIT",
    "FILL_STATES",
    "FONT_FAMILIES",
    "HARD_NEGATIVE_KINDS",
    "LINE_STYLES",
    "MARKER_FAMILIES",
    "MARKER_SHAPES",
    "PHASE_DIVIDER_STYLES",
    "RENDERER_FAMILIES",
    "SCENE_FEATURES",
    "SUPPORTED_DESIGNS",
    "TEMPLATE_FAMILIES",
    "TEXT_ROLES",
    "build_scene",
    "family_split",
    "marker_style_catalog",
]
