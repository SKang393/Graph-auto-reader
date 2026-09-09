# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Regression coverage for Session 06 repair pass 1."""

from __future__ import annotations

import copy
import json
import math
from pathlib import Path
from typing import Any, Iterable, Mapping

import pytest
from PIL import Image, ImageChops, ImageDraw

from ml.synthetic.renderer import render_scene
from ml.synthetic.schema import SceneValidationError, validate_scene
from ml.synthetic.templates import (
    DEGRADATION_FAMILIES,
    DEGRADATION_KIND_CATALOG,
    RENDERER_FAMILIES,
    _degradation_stages,
    build_scene,
)


def _read_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8"))


def _point_inside(point: Iterable[float], width: int, height: int) -> bool:
    x, y = point
    return 0.0 <= float(x) < width and 0.0 <= float(y) < height


def _box_inside(box: Iterable[float], width: int, height: int) -> bool:
    x, y, box_width, box_height = (float(value) for value in box)
    return (
        x >= 0.0
        and y >= 0.0
        and box_width >= 0.0
        and box_height >= 0.0
        and x + box_width <= width
        and y + box_height <= height
    )


def _assert_geometry_inside(
    geometry: Mapping[str, Any],
    width: int,
    height: int,
) -> None:
    if "center" in geometry:
        assert _point_inside(geometry["center"], width, height)
    if "box" in geometry:
        assert _box_inside(geometry["box"], width, height)
    for key in ("line", "polygon", "polyline"):
        points = geometry.get(key, [])
        for point in points:
            assert _point_inside(point, width, height)
    for segment in geometry.get("segments", []):
        for point in segment:
            assert _point_inside(point, width, height)


def _assert_request_geometry_inside(
    geometry: Mapping[str, Any],
    width: int,
    height: int,
) -> None:
    kind = geometry["kind"]
    coordinates = [float(value) for value in geometry["coordinates"]]
    if kind == "point":
        points = [coordinates]
    elif kind == "line":
        points = [coordinates[:2], coordinates[2:]]
    elif kind == "box":
        assert _box_inside(coordinates, width, height)
        return
    elif kind == "polyline":
        assert len(coordinates) % 2 == 0
        points = [
            coordinates[offset : offset + 2]
            for offset in range(0, len(coordinates), 2)
        ]
    else:
        raise AssertionError(f"Unsupported geometry kind in fixture: {kind}")
    assert all(_point_inside(point, width, height) for point in points)


def _rendered_geometry_mask(
    geometry: Mapping[str, Any],
    width: int,
    height: int,
) -> Image.Image:
    mask = Image.new("L", (width, height), 0)
    draw = ImageDraw.Draw(mask)
    box = geometry.get("box")
    if isinstance(box, list):
        x, y, box_width, box_height = (float(value) for value in box)
        draw.rectangle((x, y, x + box_width, y + box_height), fill=255)
    polygon = geometry.get("polygon")
    if isinstance(polygon, list):
        draw.polygon([tuple(point) for point in polygon], fill=255)
    polyline = geometry.get("polyline")
    if isinstance(polyline, list):
        draw.line([tuple(point) for point in polyline], fill=255, width=3)
    line = geometry.get("line")
    if isinstance(line, list):
        draw.line([tuple(point) for point in line], fill=255, width=3)
    for segment in geometry.get("segments", []):
        draw.line([tuple(point) for point in segment], fill=255, width=3)
    return mask


def _boxes_overlap(first: Iterable[float], second: Iterable[float]) -> bool:
    first_x, first_y, first_width, first_height = (float(value) for value in first)
    second_x, second_y, second_width, second_height = (
        float(value) for value in second
    )
    return (
        first_x < second_x + second_width
        and second_x < first_x + first_width
        and first_y < second_y + second_height
        and second_y < first_y + first_height
    )


def test_every_hard_negative_geometry_is_inside_canvas(smoke_root: Path) -> None:
    for scene_path in sorted((smoke_root / "scenes").glob("*.json")):
        scene = _read_json(scene_path)
        annotation = _read_json(
            smoke_root / "annotations" / f"{scene['scene_id']}.json"
        )
        width = int(scene["canvas"]["width"])
        height = int(scene["canvas"]["height"])
        for request in scene["hard_negatives"]:
            _assert_request_geometry_inside(request["geometry"], width, height)
        for rendered in annotation["hard_negatives"]:
            _assert_geometry_inside(rendered["geometry"], width, height)


def test_every_hard_negative_primitive_is_marker_mask_disjoint(
    smoke_root: Path,
) -> None:
    for annotation_path in sorted((smoke_root / "annotations").glob("*.json")):
        annotation = _read_json(annotation_path)
        marker_mask = Image.open(
            smoke_root / "masks" / f"{annotation['scene_id']}.png"
        ).convert("L")
        marker_boxes = [
            marker["box"]
            for panel in annotation["panels"]
            for marker in panel["markers"]
        ]
        for negative in annotation["hard_negatives"]:
            assert negative["excluded_from_marker_mask"] is True
            primitive_mask = _rendered_geometry_mask(
                negative["geometry"], marker_mask.width, marker_mask.height
            )
            assert primitive_mask.getbbox() is not None
            assert ImageChops.multiply(primitive_mask, marker_mask).getbbox() is None
            primitive_box = primitive_mask.getbbox()
            assert primitive_box is not None
            extent = [
                primitive_box[0],
                primitive_box[1],
                primitive_box[2] - primitive_box[0],
                primitive_box[3] - primitive_box[1],
            ]
            assert all(
                not _boxes_overlap(extent, marker_box) for marker_box in marker_boxes
            )


def test_colliding_hard_negative_is_relocated_deterministically() -> None:
    scene = build_scene("ab", 721, "vector_clean", session_count=8)
    scene["degradations"] = [
        {
            "stage": 1,
            "family_key": "repair_regression",
            "kind": "none",
            "parameters": {},
            "deterministic": True,
        }
    ]
    marker_center = scene["panels"][0]["points"][0]["center"]
    request = scene["hard_negatives"][1]
    request["geometry"] = {"kind": "point", "coordinates": marker_center}
    validate_scene(scene)

    first_image, first_annotation, first_marker_mask = render_scene(scene)
    second_image, second_annotation, second_marker_mask = render_scene(scene)
    first_negative = first_annotation["hard_negatives"][1]
    second_negative = second_annotation["hard_negatives"][1]

    assert first_negative == second_negative
    assert first_negative["requested_center"] == pytest.approx(marker_center)
    assert first_negative["relocated"] is True
    assert first_negative["placement_attempts"] > 1
    assert first_negative["geometry"]["center"] != pytest.approx(marker_center)
    assert first_image.tobytes() == second_image.tobytes()
    assert first_marker_mask.tobytes() == second_marker_mask.tobytes()
    primitive_mask = _rendered_geometry_mask(
        first_negative["geometry"],
        first_marker_mask.width,
        first_marker_mask.height,
    )
    assert ImageChops.multiply(primitive_mask, first_marker_mask).getbbox() is None


def test_visible_text_boxes_are_measured_and_contain_rendered_glyphs(
    smoke_root: Path,
) -> None:
    visible_count = 0
    for annotation_path in sorted((smoke_root / "annotations").glob("*.json")):
        annotation = _read_json(annotation_path)
        width = int(annotation["canvas"]["width"])
        height = int(annotation["canvas"]["height"])
        for panel in annotation["panels"]:
            for text in panel["texts"]:
                if not text["visible"] or not text["text"]:
                    continue
                visible_count += 1
                measured = text["rendered_pixel_box"]
                assert measured is not None
                assert _box_inside(text["box"], width, height)
                assert _box_inside(measured, width, height)
                outer_x, outer_y, outer_width, outer_height = text["box"]
                inner_x, inner_y, inner_width, inner_height = measured
                assert inner_x >= outer_x
                assert inner_y >= outer_y
                assert inner_x + inner_width <= outer_x + outer_width
                assert inner_y + inner_height <= outer_y + outer_height
    assert visible_count > 300


def test_phase_and_condition_labels_do_not_overlap_in_complete_smoke_catalog(
    smoke_root: Path,
) -> None:
    checked_pairs = 0
    retained_hard_negatives = 0
    for annotation_path in sorted((smoke_root / "annotations").glob("*.json")):
        annotation = _read_json(annotation_path)
        canvas_width = int(annotation["canvas"]["width"])
        canvas_height = int(annotation["canvas"]["height"])
        retained_hard_negatives += len(annotation["hard_negatives"])
        for panel in annotation["panels"]:
            phases = [phase for phase in panel["phases"] if phase["label_text"]]
            phase_headings = [
                text for text in panel["texts"] if text["role"] == "phase_heading"
            ]
            condition_labels = [
                text for text in panel["texts"] if text["role"] == "condition_label"
            ]

            assert [text["text"] for text in phase_headings] == [
                phase["label_text"] for phase in phases
            ]
            assert [text["text"] for text in condition_labels] == [
                phase["code"].upper() for phase in phases
            ]
            assert len(condition_labels) == len(panel["top_bars"])

            occupied_header_text = [
                text
                for text in panel["texts"]
                if text["role"] in {"phase_heading", "participant", "annotation"}
            ]
            for condition in condition_labels:
                condition_box = condition["rendered_pixel_box"]
                assert condition_box is not None
                assert _box_inside(condition_box, canvas_width, canvas_height)
                panel_x, panel_y, panel_width, panel_height = panel["box"]
                condition_x, condition_y, condition_width, condition_height = condition_box
                assert (
                    condition_x >= panel_x
                    and condition_y >= panel_y
                    and condition_x + condition_width <= panel_x + panel_width
                    and condition_y + condition_height <= panel_y + panel_height
                )
                for heading in phase_headings:
                    heading_box = heading["rendered_pixel_box"]
                    assert heading_box is not None
                    assert not _boxes_overlap(heading_box, condition_box)
                    checked_pairs += 1
                for occupied in occupied_header_text:
                    occupied_box = occupied["rendered_pixel_box"]
                    assert occupied_box is not None
                    assert not _boxes_overlap(occupied_box, condition_box)

    assert checked_pairs > 0
    assert retained_hard_negatives > 0


@pytest.mark.parametrize(
    ("design", "seed", "session_count", "canvas_width", "panel_height", "presentation"),
    (
        ("ab", 901, 24, 361, 160, {"font_size_px": 17}),
        (
            "changing_criterion",
            902,
            24,
            2400,
            500,
            {"font_size_px": 64, "show_arrows": False, "show_brackets": False},
        ),
    ),
    ids=("narrow-panel", "maximum-font-size"),
)
def test_condition_labels_preserve_measured_glyphs_in_representative_layouts(
    design: str,
    seed: int,
    session_count: int,
    canvas_width: int,
    panel_height: int,
    presentation: dict[str, Any],
) -> None:
    scene = build_scene(
        design,
        seed,
        "vector_clean",
        session_count=session_count,
        canvas_width=canvas_width,
        panel_height=panel_height,
        presentation=presentation,
    )
    _, annotation, _ = render_scene(scene)

    assert scene["hard_negatives"]
    for panel in annotation["panels"]:
        headings = [
            text for text in panel["texts"] if text["role"] == "phase_heading"
        ]
        conditions = [
            text for text in panel["texts"] if text["role"] == "condition_label"
        ]
        assert headings
        assert conditions
        panel_x, panel_y, panel_width, panel_box_height = panel["box"]
        for condition in conditions:
            rendered = condition["rendered_pixel_box"]
            assert rendered is not None
            rendered_x, rendered_y, rendered_width, rendered_height = rendered
            assert rendered_width > 0
            assert rendered_height > 0
            assert rendered_x >= panel_x
            assert rendered_y >= panel_y
            assert rendered_x + rendered_width <= panel_x + panel_width
            assert rendered_y + rendered_height <= panel_y + panel_box_height
            assert all(
                not _boxes_overlap(rendered, heading["rendered_pixel_box"])
                for heading in headings
            )


def test_seed_driven_graph_style_variation_is_deterministic() -> None:
    first = build_scene("ab", 393, "vector_clean", session_count=25)
    assert first == build_scene("ab", 393, "vector_clean", session_count=25)

    scenes = [
        build_scene("ab", 800 + index, "vector_clean", session_count=25)
        for index in range(16)
    ]
    y_profiles = {
        (
            scene["style"]["y_axis"]["minimum"],
            scene["style"]["y_axis"]["maximum"],
            scene["style"]["y_axis"]["tick_interval"],
        )
        for scene in scenes
    }
    stroke_widths = {scene["style"]["stroke_width"] for scene in scenes}
    marker_radii = {scene["style"]["marker_radius"] for scene in scenes}
    spacing_profiles = {
        (
            scene["style"]["session_spacing"]["mode"],
            scene["style"]["session_spacing"]["edge_padding_fraction"],
            scene["style"]["session_spacing"]["jitter_fraction"],
        )
        for scene in scenes
    }
    assert len(y_profiles) >= 2
    assert len(stroke_widths) >= 2
    assert len(marker_radii) >= 2
    assert len(spacing_profiles) >= 2

    for scene in scenes:
        style = scene["style"]
        positions = scene["layout"]["session_x_positions"]
        assert positions == sorted(positions)
        assert all(0.0 <= value <= 1.0 for value in positions)
        for panel in scene["panels"]:
            y_axis = panel["axes"]["y"]
            assert y_axis["min"] == style["y_axis"]["minimum"]
            assert y_axis["max"] == style["y_axis"]["maximum"]
            assert y_axis["tick_interval"] == style["y_axis"]["tick_interval"]
            assert all(
                series["stroke_width"] == style["stroke_width"]
                for series in panel["series"]
            )
            assert all(
                math.isclose(point["radius"], style["marker_radius"])
                for point in panel["points"]
            )
            assert all(
                style["y_axis"]["minimum"]
                <= point["graph"][1]
                <= style["y_axis"]["maximum"]
                for point in panel["points"]
            )


def test_seed_driven_degradation_recipes_vary_and_cover_the_catalog() -> None:
    selected_kinds: set[str] = set()
    for family in DEGRADATION_FAMILIES:
        recipes: set[str] = set()
        for seed in range(256):
            stages = _degradation_stages(seed, family)
            assert stages == _degradation_stages(seed, family)
            assert len(stages) in {1, 2}
            assert [stage["stage"] for stage in stages] == list(
                range(1, len(stages) + 1)
            )
            selected_kinds.update(stage["kind"] for stage in stages)
            recipes.add(json.dumps(stages, sort_keys=True))
        assert len(recipes) > 1
    assert selected_kinds == set(DEGRADATION_KIND_CATALOG)

    for renderer_family in RENDERER_FAMILIES:
        first = build_scene("ab", 100, renderer_family, session_count=8)
        second = build_scene("ab", 101, renderer_family, session_count=8)
        assert first["families"] == second["families"]


def _scene_with_degradation(
    kind: str,
    parameters: Mapping[str, Any],
) -> dict[str, Any]:
    scene = build_scene("ab", 919, "vector_clean", session_count=8)
    scene["degradations"] = [
        {
            "stage": 1,
            "family_key": "repair_regression",
            "kind": kind,
            "parameters": dict(parameters),
            "deterministic": True,
        }
    ]
    validate_scene(scene)
    return scene


@pytest.mark.parametrize(
    ("kind", "parameters", "geometric"),
    (
        ("skew", {"strength": 0.08, "side": "right"}, True),
        ("perspective", {"strength": 0.08}, True),
        (
            "hand_drawn_jitter",
            {"strength": 0.08, "side": "bottom"},
            True,
        ),
        (
            "inconsistent_marker_outlines",
            {"strength": 0.2},
            False,
        ),
        (
            "line_marker_contact",
            {"strength": 0.7},
            False,
        ),
        ("anisotropic_blur", {"radius": 1.2}, False),
    ),
)
def test_requested_degradations_are_genuine_and_annotations_remain_aligned(
    kind: str,
    parameters: Mapping[str, Any],
    geometric: bool,
) -> None:
    clean_image, _, _ = render_scene(
        _scene_with_degradation("none", {})
    )
    image, annotation, marker_mask = render_scene(
        _scene_with_degradation(kind, parameters)
    )
    assert image.tobytes() != clean_image.tobytes()
    record = annotation["degradations"][0]
    assert record["kind"] == kind
    assert record["implementation"] == "genuine"
    assert "surrogate" not in str(record).casefold()

    if geometric:
        assert record["source_space"] == "synthetic_clean_pixels"
        assert record["target_space"] == "original_pixels"
        assert len(record["forward_matrix_3x3"]) == 9
        assert len(record["inverse_matrix_3x3"]) == 9
        assert annotation["transforms"][-1]["source_space"] == "synthetic_clean_pixels"
        assert annotation["transforms"][-1]["target_space"] == "original_pixels"
    else:
        assert "forward_matrix_3x3" not in record

    markers = [
        marker
        for panel in annotation["panels"]
        for marker in panel["markers"]
    ]
    assert markers
    for marker in markers:
        x, y = marker["center"]
        box_x, box_y, box_width, box_height = marker["box"]
        assert 0 <= x < marker_mask.width
        assert 0 <= y < marker_mask.height
        assert box_x <= x <= box_x + box_width
        assert box_y <= y <= box_y + box_height
        assert marker["radius"] == pytest.approx(
            max(box_width, box_height) / 2.0,
            abs=1e-6,
        )
        assert marker["radius"] < 25
        crop = marker_mask.crop(
            (
                max(0, math.floor(box_x) - 1),
                max(0, math.floor(box_y) - 1),
                min(marker_mask.width, math.ceil(box_x + box_width) + 1),
                min(marker_mask.height, math.ceil(box_y + box_height) + 1),
            )
        )
        assert crop.getbbox() is not None


@pytest.mark.parametrize(
    ("kind", "bad_parameters"),
    (
        ("faded_ink", {"factor": 0.5}),
        ("downsample", {"scale": 0.5, "resampling": "lanczos"}),
        ("gaussian_noise", {"sigma": 2.0, "unexpected": True}),
        ("skew", {"strength": 0.08, "side": "left", "seed": 1}),
        ("perspective", {"strength": 0.08, "side": "left"}),
        (
            "inconsistent_marker_outlines",
            {"strength": 0.2, "side": "left"},
        ),
        ("line_marker_contact", {"strength": 0.7, "side": "left"}),
    ),
)
def test_unknown_or_legacy_degradation_parameters_are_rejected(
    kind: str,
    bad_parameters: Mapping[str, Any],
) -> None:
    scene = build_scene("ab", 88, "vector_clean", session_count=8)
    scene["degradations"] = [
        {
            "stage": 1,
            "family_key": "repair_regression",
            "kind": kind,
            "parameters": dict(bad_parameters),
            "deterministic": True,
        }
    ]
    with pytest.raises(SceneValidationError):
        validate_scene(scene)
    with pytest.raises(ValueError, match="unknown keys"):
        render_scene(scene)


def test_top_level_degradation_stage_keys_are_rejected() -> None:
    scene = _scene_with_degradation("faded_ink", {"opacity": 0.7})
    scene["degradations"][0]["unexpected"] = True
    with pytest.raises(SceneValidationError):
        validate_scene(scene)
    with pytest.raises(ValueError):
        render_scene(scene)


def test_harmonized_degradation_parameters_are_preserved() -> None:
    faded = _scene_with_degradation(
        "faded_ink",
        {"opacity": 0.6},
    )
    downsampled = _scene_with_degradation(
        "downsample",
        {"scale": 0.5, "resampler": "lanczos"},
    )
    _, faded_annotation, _ = render_scene(faded)
    _, downsampled_annotation, _ = render_scene(downsampled)
    assert faded_annotation["degradations"][0]["parameters"]["opacity"] == 0.6
    assert (
        downsampled_annotation["degradations"][0]["parameters"]["resampler"]
        == "lanczos"
    )


def test_clipping_side_and_amount_matrix_changes_content_deterministically() -> None:
    clean_image, clean_annotation, clean_mask = render_scene(
        _scene_with_degradation("none", {})
    )
    outputs: dict[tuple[str, int], bytes] = {}
    changed_counts: dict[str, list[int]] = {}

    for side in ("left", "right", "top", "bottom"):
        changed_counts[side] = []
        for amount in (1, 2, 3):
            scene = _scene_with_degradation(
                "clipping", {"amount_px": amount, "side": side}
            )
            image, annotation, marker_mask = render_scene(scene)
            repeated_image, repeated_annotation, repeated_mask = render_scene(scene)

            assert image.tobytes() == repeated_image.tobytes()
            assert annotation == repeated_annotation
            assert marker_mask.tobytes() == repeated_mask.tobytes()
            assert image.tobytes() != clean_image.tobytes()
            assert annotation["degradations"][0]["parameters"] == {
                "amount_px": amount,
                "side": side,
            }
            record = annotation["degradations"][0]
            assert record["implementation"] == "genuine"
            assert record["geometry_preserved"] is True
            assert record["annotation_coordinates_unchanged"] is True
            assert record["visibility_loss_recorded"] is True
            assert record["removed_content_pixel_count"] > 0
            assert "forward_matrix_3x3" not in record
            clip_left, clip_top, clip_right, clip_bottom = record[
                "realized_clipping_pixel_box"
            ]
            if side in {"left", "right"}:
                assert clip_right - clip_left == amount
            else:
                assert clip_bottom - clip_top == amount
            assert annotation["panels"] == clean_annotation["panels"]

            added_marker_pixels = ImageChops.subtract(marker_mask, clean_mask)
            assert added_marker_pixels.getbbox() is None
            removed_marker_pixels = ImageChops.subtract(clean_mask, marker_mask)
            if removed_marker_pixels.getbbox() is not None:
                removed = removed_marker_pixels.load()
                pixels = image.load()
                for y in range(image.height):
                    for x in range(image.width):
                        if removed[x, y]:
                            assert pixels[x, y] == (255, 255, 255)

            difference = ImageChops.difference(clean_image, image).convert("L")
            changed = sum(difference.histogram()[1:])
            assert changed > 0
            changed_counts[side].append(changed)
            outputs[(side, amount)] = image.tobytes()

    assert len(set(outputs.values())) == 12
    assert all(
        counts[0] < counts[1] < counts[2]
        for counts in changed_counts.values()
    )
