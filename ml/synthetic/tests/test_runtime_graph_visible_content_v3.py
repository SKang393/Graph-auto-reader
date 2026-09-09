# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path

from PIL import ImageFilter
import pytest

from ml.synthetic.dataset import PRESETS, _build_scenes
from ml.synthetic.renderer import render_scene
from ml.synthetic.runtime_graph_axis_preserving_v2 import AxisPreservingSourceError
from ml.synthetic.runtime_graph_visible_content_v3 import (
    render_visible_content_scene,
    render_visible_content_source,
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


def _scene(stages: list[dict[str, object]]) -> dict[str, object]:
    scene = build_scene(
        "ab",
        8101,
        "vector_clean",
        panel_count=1,
        session_count=8,
        canvas_width=480,
        panel_height=270,
    )
    scene["degradations"] = stages
    validate_scene(scene)
    return scene


def _semantic_projection(annotation: dict[str, object]) -> tuple[object, object, object]:
    panel = annotation["panels"][0]
    return (
        [(item["marker_id"], item["center"], item["radius"], item["box"]) for item in panel["markers"]],
        [(item["text_id"], item["text"], item["role"], item["box"]) for item in panel["texts"]],
        (panel["plot_box"], panel["axes"], panel["anchors"]),
    )


def test_v3_preserves_semantic_geometry_and_records_actual_attributed_bounds() -> None:
    scene = _scene([_stage(1, "erosion", size=3)])
    original = deepcopy(scene)
    clean_scene = deepcopy(scene)
    clean_scene["degradations"] = []
    _, clean_annotation, _ = render_scene(clean_scene)

    result = render_visible_content_scene(scene)

    assert scene == original
    assert _semantic_projection(result.annotation) == _semantic_projection(clean_annotation)
    assert result.audit.source_dilation_size == 3
    assert result.audit.definitely_erased_marker_count == 0
    assert result.audit.definitely_erased_text_count == 0
    assert all(item.attributed_final_bounds is not None for item in result.audit.markers)
    assert all(item.attributed_final_bounds is not None for item in result.audit.texts)
    assert all(item.declared_source_box is not None for item in result.audit.texts)
    assert [item["rendered_pixel_box"] for item in result.annotation["panels"][0]["texts"]] == [
        item["rendered_pixel_box"] for item in clean_annotation["panels"][0]["texts"]
    ]
    assert all(item.attributed_final_bounds is not None for item in result.audit.texts)


def test_v3_runtime_raster_is_only_predilation_then_declared_recipe() -> None:
    scene = _scene([_stage(1, "erosion", size=3)])
    clean_scene = deepcopy(scene)
    clean_scene["degradations"] = []
    clean_image, _, _ = render_scene(clean_scene)
    expected = clean_image.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3))

    result = render_visible_content_scene(scene)

    assert result.image.tobytes() == expected.tobytes()
    assert [item["kind"] for item in result.annotation["degradations"]] == ["erosion"]


def test_element_masks_do_not_treat_crossing_unrelated_ink_as_glyph_survival() -> None:
    scene = _scene([_stage(1, "faded_ink", opacity=0.0)])
    panel = scene["panels"][0]
    marker = panel["points"][0]
    old_x, old_y = marker["center"]
    new_x, new_y = panel["plot_box"][0], panel["plot_box"][3]
    marker["center"] = [new_x, new_y]
    marker["mask"] = [
        [x + new_x - old_x, y + new_y - old_y] for x, y in marker["mask"]
    ]

    result = render_visible_content_scene(scene)

    audited = result.audit.markers[0]
    assert audited.final_attributed_support_pixels == 0
    assert audited.support_assessment == "definite_attributed_erasure"
    assert audited.composited_visibility_assessment == "not_established_overlapping_ink_ambiguous"
    assert f"marker:{audited.content_id}:definite_attributed_erasure" in result.audit.failures


def test_attribution_preserves_original_luma_through_threshold() -> None:
    scene = _scene([_stage(1, "threshold", cutoff=200)])
    marker = scene["panels"][0]["points"][0]
    scene["panels"][0]["series"][0]["stroke"] = "#d2d2d2"
    marker_id = marker["point_id"]

    result = render_visible_content_scene(scene)

    audited = next(item for item in result.audit.markers if item.content_id == marker_id)
    assert audited.clean_support_pixels > 0
    assert audited.final_attributed_support_pixels == 0
    assert audited.support_assessment == "definite_attributed_erasure"


def test_open_marker_and_text_counter_topology_are_explicit_diagnostics() -> None:
    scene = _scene([_stage(1, "erosion", size=5)])
    panel = scene["panels"][0]
    panel["series"][0]["fill"] = "open"
    counter = scene["annotations"]["text_regions"][0]
    counter_id = counter["region_id"]
    counter["text"] = "8"
    counter["box"] = [220, 60, 24, 24]

    result = render_visible_content_scene(scene)

    assert any(item.open_interior_retained is not None for item in result.audit.markers)
    counter = next(item for item in result.audit.texts if item.content_id == counter_id)
    assert counter.clean_hole_count >= 1
    assert counter.attributed_final_hole_count != counter.clean_hole_count
    assert counter.readability_assessment == "not_established_by_pixel_support"
    assert f"text:{counter_id}:counter_topology_changed" in result.audit.failures
    assert result.audit.limitations


def test_no_degradation_preserves_exact_raster_and_support_but_not_readability_claim() -> None:
    scene = _scene([_stage(1, "none")])
    expected_image, _, expected_mask = render_scene(scene)

    result = render_visible_content_scene(scene)

    assert result.image.tobytes() == expected_image.tobytes()
    assert result.marker_mask.tobytes() == expected_mask.tobytes()
    assert all(item.support_assessment == "attributed_support_present" for item in (*result.audit.markers, *result.audit.texts))
    assert all(item.readability_assessment == "not_established_by_pixel_support" for item in result.audit.texts)


@pytest.mark.parametrize("scene_seed", (39303, 39501, 39503))
def test_fixed_mask_conditioned_stages_match_source_only_and_preserve_targets(
    scene_seed: int,
) -> None:
    dataset_seed = scene_seed // 100
    scene = next(
        item
        for item in _build_scenes(
            PRESETS["smoke"], dataset_seed, require_complete_style_catalog=True
        )
        if int(item["seed"]) == scene_seed
    )
    clean_scene = deepcopy(scene)
    clean_scene["degradations"] = []
    _, clean_annotation, _ = render_scene(clean_scene)

    source_only = render_visible_content_source(scene)
    audited = render_visible_content_scene(scene)

    assert audited.image.tobytes() == source_only.image.tobytes()
    assert audited.marker_mask.tobytes() == source_only.marker_mask.tobytes()
    assert audited.annotation == source_only.annotation
    assert _semantic_projection(audited.annotation) == _semantic_projection(clean_annotation)
    assert audited.audit.markers
    assert all(item.final_attributed_support_pixels >= 0 for item in audited.audit.markers)


def test_v3_rejects_geometric_stage_outside_fixed_source_scope() -> None:
    with pytest.raises(AxisPreservingSourceError, match="non-geometric fixed source scope"):
        render_visible_content_scene(_scene([_stage(1, "skew", strength=0.03, side="right")]))


@pytest.mark.parametrize("kind", ("gaussian_noise", "poisson_noise", "impulse_noise", "paper_texture"))
def test_stochastic_audit_retains_every_element_as_ungraded_without_replaying_glyphs(kind, monkeypatch) -> None:
    import ml.synthetic.runtime_graph_visible_content_v3 as generator

    def prohibited(*args, **kwargs):
        pytest.fail("Expensive per-element attribution must not run for stochastic stages")

    parameters = {
        "gaussian_noise": {"sigma": 2.0},
        "poisson_noise": {"scale": 0.5},
        "impulse_noise": {"probability": 0.01},
        "paper_texture": {"sigma": 2.0},
    }[kind]
    scene = _scene([_stage(1, kind, **parameters)])
    source = render_visible_content_source(scene)
    monkeypatch.setattr(generator, "_capture_clean_scene", prohibited)
    monkeypatch.setattr(generator, "_attributed_final_mask", prohibited)
    audited = render_visible_content_scene(scene)
    assert audited.image.tobytes() == source.image.tobytes()
    assert audited.marker_mask.tobytes() == source.marker_mask.tobytes()
    assert audited.annotation == source.annotation
    assert audited.audit.ungraded_stage_kinds == (kind,)
    assert len(audited.audit.markers) == len(generator._records(source.annotation, "markers"))
    assert len(audited.audit.texts) == len([item for item in generator._records(source.annotation, "texts") if item.get("visible") is not False])
    for item in (*audited.audit.markers, *audited.audit.texts):
        assert item.support_assessment == "ungradable_full_canvas_stochastic_stage"
        assert item.clean_rendered_bounds is None
        assert item.attributed_final_bounds is None
        assert item.final_attributed_support_pixels is None
        assert item.clean_hole_count is None
        assert item.attributed_final_hole_count is None
        assert item.overlaps_other_elements is None


def test_v3_protocol_binds_implementation_and_axis_v2() -> None:
    root = Path(__file__).resolve().parents[3]
    protocol = json.loads((root / "ml/synthetic/runtime_graph_visible_content_v3_protocol.json").read_bytes())
    for key in ("implementation", "axis_v2", "evidence_policy"):
        item = protocol[key]
        assert sha256((root / item["path"]).read_bytes()).hexdigest() == item["sha256"]
    historical = protocol["fixed_splits"]
    assert sha256((root / historical["historical_runtime_binding_path"]).read_bytes()).hexdigest() == historical["historical_runtime_binding_sha256"]
