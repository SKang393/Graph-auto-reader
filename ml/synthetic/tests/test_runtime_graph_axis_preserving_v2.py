# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw
import pytest

from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.io import canonical_json_bytes
from ml.synthetic.renderer import render_scene
from ml.synthetic.runtime_graph_axis_preserving_v2 import (
    _physical_source_scene,
    AxisPreservingSourceError,
    area_integrated_halftone,
    render_axis_preserving_scene,
    require_complete_visible_content,
    required_source_stroke_width,
)
from ml.synthetic.schema import validate_scene
from ml.synthetic.templates import build_scene


def _stage(index: int, kind: str, **parameters: object) -> dict[str, object]:
    return {
        "stage": index,
        "family_key": "print_light",
        "kind": kind,
        "parameters": parameters,
        "deterministic": True,
    }


def _scene_with(stages: list[dict[str, object]], *, seed: int = 7101) -> dict[str, object]:
    scene = build_scene(
        "ab",
        seed,
        "vector_clean",
        panel_count=1,
        session_count=8,
        canvas_width=480,
        panel_height=270,
    )
    scene["degradations"] = stages
    validate_scene(scene)
    return scene


def test_required_source_width_back_propagates_declared_morphology() -> None:
    assert required_source_stroke_width([_stage(1, "erosion", size=3)]) == 3
    assert required_source_stroke_width([_stage(1, "erosion", size=5)]) == 5
    assert required_source_stroke_width(
        [_stage(1, "dilation", size=5), _stage(2, "erosion", size=5)]
    ) == 1


def test_physical_source_width_never_shrinks_wider_declared_strokes() -> None:
    scene = _scene_with([_stage(1, "none")])
    axes = scene["panels"][0]["axes"]
    axes["x"]["width"] = 5
    axes["y"]["width"] = 3
    scene["panels"][0]["ticks"][0]["width"] = 4
    original = deepcopy(scene)

    physical = _physical_source_scene(scene, 1)

    assert scene == original
    assert physical["panels"][0]["axes"]["x"]["width"] == 5
    assert physical["panels"][0]["axes"]["y"]["width"] == 3
    assert physical["panels"][0]["ticks"][0]["width"] == 4
    assert required_source_stroke_width(
        [_stage(1, "threshold", cutoff=224), _stage(2, "erosion", size=3)]
    ) == 3
    assert required_source_stroke_width(
        [_stage(1, "ink_bleed", size=3), _stage(2, "halftone", cell_size=4)]
    ) == 1


@pytest.mark.parametrize("cell_size", (2, 3, 4))
def test_area_halftone_integrates_thin_lines_at_every_grid_phase(cell_size: int) -> None:
    for phase in range(cell_size):
        vertical = Image.new("RGB", (24, 24), "white")
        ImageDraw.Draw(vertical).line((phase, 0, phase, 23), fill="black", width=1)
        vertical_result = area_integrated_halftone(vertical, cell_size)
        for top in range(0, 24, cell_size):
            cell = vertical_result.crop((0, top, cell_size, min(24, top + cell_size)))
            assert ImageChops.invert(cell.convert("L")).getbbox() is not None

        horizontal = Image.new("RGB", (24, 24), "white")
        ImageDraw.Draw(horizontal).line((0, phase, 23, phase), fill="black", width=1)
        horizontal_result = area_integrated_halftone(horizontal, cell_size)
        for left in range(0, 24, cell_size):
            cell = horizontal_result.crop((left, 0, min(24, left + cell_size), cell_size))
            assert ImageChops.invert(cell.convert("L")).getbbox() is not None


def test_v2_preserves_scene_recipe_geometry_and_labels_without_mutating_input() -> None:
    scene = _scene_with(
        [_stage(1, "threshold", cutoff=224), _stage(2, "erosion", size=3)]
    )
    original = deepcopy(scene)
    clean_scene = deepcopy(scene)
    clean_scene["degradations"] = []
    _, clean_annotation, _ = render_scene(clean_scene)

    result = render_axis_preserving_scene(scene)

    assert scene == original
    assert [item["kind"] for item in result.annotation["degradations"]] == [
        "threshold",
        "erosion",
    ]
    assert [item["parameters"] for item in result.annotation["degradations"]] == [
        {"cutoff": 224},
        {"size": 3},
    ]
    assert result.annotation["plots"] == clean_annotation["plots"]
    assert [item["center"] for item in result.annotation["markers"]] == [
        item["center"] for item in clean_annotation["markers"]
    ]
    assert [
        (item["text"], item["role"], item["position"])
        for item in result.annotation["texts"]
    ] == [
        (item["text"], item["role"], item["position"])
        for item in clean_annotation["texts"]
    ]
    assert result.audit.source_stroke_width == 3
    assert not [item for item in result.audit.axes if item.classification == "destructively_erased"]


@pytest.mark.parametrize(
    "stages",
    (
        [_stage(1, "dilation", size=5), _stage(2, "erosion", size=5)],
        [_stage(1, "halftone", cell_size=2), _stage(2, "line_marker_contact", strength=0.55)],
        [_stage(1, "ink_bleed", size=3), _stage(2, "halftone", cell_size=4)],
    ),
)
def test_v2_keeps_declared_stage_order_and_parameters(
    stages: list[dict[str, object]],
) -> None:
    scene = _scene_with(stages)

    result = render_axis_preserving_scene(scene)

    assert [item["kind"] for item in result.annotation["degradations"]] == [
        item["kind"] for item in stages
    ]
    assert [item["parameters"] for item in result.annotation["degradations"]] == [
        item["parameters"] for item in stages
    ]


def test_v2_reports_erased_axes_markers_and_text_without_retrying_recipe() -> None:
    scene = _scene_with([_stage(1, "faded_ink", opacity=0.0)])

    result = render_axis_preserving_scene(scene)

    assert [item["kind"] for item in result.annotation["degradations"]] == ["faded_ink"]
    assert all(item.classification == "destructively_erased" for item in result.audit.axes)
    assert result.audit.definitely_erased_marker_count > 0
    assert result.audit.definitely_erased_text_count > 0
    assert all(
        item.support_assessment == "definite_erasure_zero_bbox_support"
        for item in (*result.audit.markers, *result.audit.texts)
    )
    assert result.audit.failures
    with pytest.raises(AxisPreservingSourceError, match="visible-content validation failed"):
        require_complete_visible_content(result)


def test_positive_bbox_ink_is_inconclusive_and_never_grants_complete_visibility() -> None:
    result = render_axis_preserving_scene(_scene_with([_stage(1, "none")]))

    assert result.audit.failures == ()
    assert all(
        item.support_assessment == "inconclusive_bbox_support_present"
        for item in (*result.audit.markers, *result.audit.texts)
    )
    with pytest.raises(
        AxisPreservingSourceError, match="cannot establish complete visible content"
    ):
        require_complete_visible_content(result)


@pytest.mark.parametrize(
    "stages",
    (
        [_stage(1, "gaussian_noise", sigma=3.5)],
        [_stage(1, "none"), _stage(2, "gaussian_noise", sigma=3.5)],
    ),
)
def test_unchanged_stochastic_stages_preserve_rng_and_exact_output(
    stages: list[dict[str, object]],
) -> None:
    scene = _scene_with(stages, seed=7231)

    expected_image, expected_annotation, expected_mask = render_scene(scene)
    result = render_axis_preserving_scene(scene)

    assert result.image.tobytes() == expected_image.tobytes()
    assert result.marker_mask.tobytes() == expected_mask.tobytes()
    assert result.annotation == expected_annotation


def test_known_clean_first_session_marker_occlusion_is_fragmented_not_erased() -> None:
    scene = next(
        item
        for item in _build_scenes(
            PRESETS["smoke"], 393, require_complete_style_catalog=True
        )
        if _scene_split(item) == "train" and int(item["seed"]) == 39302
    )

    result = render_axis_preserving_scene(scene)
    y_axis = next(
        item
        for item in result.audit.axes
        if item.panel_id == result.annotation["panels"][0]["panel_id"]
        and item.axis == "y"
    )

    assert y_axis.classification == "fragmented_or_occluded"
    assert y_axis.degraded_supported_cross_sections > 0
    assert y_axis.longest_degraded_gap > 2


def test_geometric_degradation_is_rejected_outside_fixed_v2_source_scope() -> None:
    scene = _scene_with([_stage(1, "skew", strength=0.03, side="right")])

    with pytest.raises(AxisPreservingSourceError, match="non-geometric fixed source scope"):
        render_axis_preserving_scene(scene)


def test_protocol_binds_implementation_policy_and_fixed_scene_identities() -> None:
    root = Path(__file__).resolve().parents[3]
    protocol = json.loads(
        (root / "ml/synthetic/runtime_graph_axis_preserving_v2_protocol.json").read_bytes()
    )
    implementation = root / protocol["implementation"]["path"]
    policy = root / protocol["evidence_policy"]["path"]
    assert sha256(implementation.read_bytes()).hexdigest() == protocol["implementation"]["sha256"]
    assert sha256(policy.read_bytes()).hexdigest() == protocol["evidence_policy"]["sha256"]
    for key, relative_path in (
        ("legacy_templates_sha256", "ml/synthetic/templates.py"),
        ("legacy_renderer_sha256", "ml/synthetic/renderer.py"),
        ("legacy_dataset_sha256", "ml/synthetic/dataset.py"),
        ("scene_schema_sha256", "ml/synthetic/scene.schema.json"),
    ):
        assert sha256((root / relative_path).read_bytes()).hexdigest() == protocol[
            "implementation"
        ][key]

    for split, expected_split in (("train", "train"), ("dev", "validation")):
        fixed = protocol["fixed_splits"][split]
        rows = []
        for dataset_seed in fixed["dataset_seeds"]:
            for scene in _build_scenes(
                PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True
            ):
                if _scene_split(scene) == expected_split:
                    rows.append(
                        {
                            "dataset_seed": dataset_seed,
                            "scene_seed": scene["seed"],
                            "scene_id": scene["scene_id"],
                            "resolved_scene_sha256": sha256(
                                canonical_json_bytes(scene)
                            ).hexdigest(),
                        }
                    )
        assert [item["scene_seed"] for item in rows] == fixed["scene_seeds"]
        assert len(rows) == fixed["scene_count"]
        assert sha256(canonical_json_bytes(rows)).hexdigest() == fixed[
            "resolved_scene_identity_set_sha256"
        ]
