# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from types import SimpleNamespace
from dataclasses import replace

import numpy as np
import pytest

from ml.markers.center.mask_preserving_v24.runtime_inputs import ValidatedRuntimePanelInput
from ml.markers.center.plot_domain_v25.runtime_domain_binding import RuntimePanelDomain
from ml.ocr import production_tiled_inputs as inputs
from ml.ocr.dice_loss_detector_v38.dataset import to_arrays as v38_to_arrays
from ml.ocr.real_range_detector_v35.dataset import TileSample


def _panel(
    panel_id: str,
    crop: tuple[int, int, int, int],
    *,
    source_sha: str = "a" * 64,
    gray8: np.ndarray | None = None,
) -> RuntimePanelDomain:
    left, top, width, height = crop
    plane = np.full((height, width), 255, dtype=np.uint8) if gray8 is None else gray8
    zero = np.zeros((height, width), dtype=np.float32)
    runtime = ValidatedRuntimePanelInput(
        "train",
        393,
        "fixed-family",
        39301,
        source_sha,
        panel_id,
        (panel_id[0] * 64)[:64],
        width,
        height,
        crop,
        tuple(float(value) for value in crop),
        plane,
        zero,
        zero,
        zero,
    )
    forward = (1.0, 0.0, -left, 0.0, 1.0, -top, 0.0, 0.0, 1.0)
    inverse = (1.0, 0.0, left, 0.0, 1.0, top, 0.0, 0.0, 1.0)
    return RuntimePanelDomain(
        runtime,
        None,  # type: ignore[arg-type]
        400,
        300,
        ((0.0, 0.0), (400.0, 0.0), (400.0, 300.0), (0.0, 300.0)),
        forward,
        inverse,
        "b" * 64,
        "c" * 64,
        None,  # type: ignore[arg-type]
        "d" * 64,
    )


def _annotation(box: tuple[float, float, float, float]) -> dict:
    left, top, right, bottom = box
    return {
        "texts": [
            {
                "text_id": "text-1",
                "text": "10",
                "role": "y_tick",
                "visible": True,
                "rendered_pixel_box": [left, top, right - left, bottom - top],
            }
        ]
    }


def test_partial_and_overlapping_crops_preserve_one_source_truth_with_each_visible_target() -> None:
    first = _panel("1-panel", (0, 0, 120, 100))
    second = _panel("2-panel", (80, 0, 120, 100))

    truths = inputs._source_truth_records(
        "train", _annotation((70.0, 10.0, 130.0, 30.0)), "a" * 64, (first, second), 400, 300
    )

    assert len(truths) == 1
    assert truths[0].projection_status == "overlapping_partial_projections"
    assert [item.source_visible_box for item in truths[0].projections] == [
        (70.0, 10.0, 120.0, 30.0),
        (80.0, 10.0, 130.0, 30.0),
    ]
    assert [item.panel_box for item in truths[0].projections] == [
        (70.0, 10.0, 120.0, 30.0),
        (0.0, 10.0, 50.0, 30.0),
    ]
    for panel, projection in zip((first, second), truths[0].projections, strict=True):
        prepared = inputs._prepare_panel(panel, (projection,))
        assert prepared.tiles[0].target.sum() == pytest.approx(1000.0)
        assert prepared.tiles[0].source_truth_ids == (truths[0].truth_id,)


def test_truth_outside_all_crops_remains_in_source_denominator() -> None:
    panel = _panel("3-panel", (0, 0, 100, 100))

    truths = inputs._source_truth_records(
        "train", _annotation((200.0, 10.0, 220.0, 30.0)), "a" * 64, (panel,), 400, 300
    )

    assert len(truths) == 1
    assert truths[0].projections == ()
    assert truths[0].projection_status == "outside_runtime_crops"
    assert inputs._prepare_panel(panel, ()).tiles[0].target.sum() == 0.0


def test_production_gray8_and_v38_tile_tensor_are_exact_and_immutable() -> None:
    gray8 = np.full((270, 300), 255, dtype=np.uint8)
    gray8[4, 3] = 0
    panel = _panel("4-panel", (0, 0, 300, 270), gray8=gray8)

    prepared = inputs._prepare_panel(panel, ())

    assert prepared.gray8.tobytes() == gray8.tobytes()
    assert inputs.tile_starts(300) == (0, 44)
    assert inputs.tile_starts(270) == (0, 14)
    assert [(tile.left, tile.top) for tile in prepared.tiles] == [
        (0, 0),
        (44, 0),
        (0, 14),
        (44, 14),
    ]
    assert prepared.tiles[0].input_values[0, 4, 3] == 1.0
    assert prepared.tiles[0].input_values[0, 0, 0] == 0.0
    assert prepared.tiles[-1].valid_width == 256
    assert prepared.tiles[-1].valid_height == 256
    assert not prepared.gray8.flags.writeable
    assert not prepared.tiles[0].input_values.flags.writeable
    assert not prepared.tiles[0].target.flags.writeable
    with pytest.raises(ValueError):
        prepared.tiles[0].input_values[0, 0, 0] = 1.0


def test_small_panel_uses_white_padding_and_clips_visible_truth_like_v38() -> None:
    gray8 = np.full((20, 30), 127, dtype=np.uint8)
    panel = _panel("5-panel", (10, 20, 30, 20), gray8=gray8)
    truth = inputs._source_truth_records(
        "train", _annotation((5.0, 15.0, 25.0, 30.0)), "a" * 64, (panel,), 400, 300
    )[0]

    prepared = inputs._prepare_panel(panel, truth.projections)
    tile = prepared.tiles[0]

    assert truth.projection_status == "single_partial_projection"
    assert tile.valid_width == 30 and tile.valid_height == 20
    assert tile.input_values[0, 0, 0] == pytest.approx(1.0 - 127.0 / 255.0)
    assert tile.input_values[0, 20, 30] == 0.0
    assert tile.target.sum() == pytest.approx(150.0)

    v38_image = np.full((256, 256), 255, dtype=np.uint8)
    v38_image[:20, :30] = gray8
    v38_target = np.zeros((256, 256), dtype=np.uint8)
    v38_target[:10, :15] = 1
    expected_values, expected_targets = v38_to_arrays(
        (TileSample("fixture", 0, 0, 30, 20, v38_image, v38_target),)
    )
    np.testing.assert_array_equal(tile.input_values, expected_values[0])
    np.testing.assert_array_equal(tile.target, expected_targets[0])


def test_invalid_or_anonymous_visible_text_fails_closed() -> None:
    panel = _panel("6-panel", (0, 0, 100, 100))
    anonymous = _annotation((10.0, 10.0, 20.0, 20.0))
    del anonymous["texts"][0]["text_id"]
    with pytest.raises(inputs.ProductionTiledInputError, match="authoritative identity"):
        inputs._source_truth_records("train", anonymous, "a" * 64, (panel,), 400, 300)

    out_of_bounds = _annotation((390.0, 10.0, 410.0, 20.0))
    with pytest.raises(inputs.ProductionTiledInputError, match="outside"):
        inputs._source_truth_records("train", out_of_bounds, "a" * 64, (panel,), 400, 300)

    unrendered = _annotation((10.0, 10.0, 20.0, 20.0))
    unrendered["texts"][0]["rendered_pixel_box"] = None
    assert inputs._source_truth_records("train", unrendered, "a" * 64, (panel,), 400, 300) == ()


def test_rotating_source_transform_is_rejected_instead_of_fabricating_axis_aligned_truth() -> None:
    panel = _panel("7-panel", (0, 0, 100, 100))
    rotated = RuntimePanelDomain(
        panel.runtime_input,
        panel.domain,
        panel.source_width,
        panel.source_height,
        panel.source_polygon,
        (0.0, -1.0, 100.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0),
        panel.panel_to_source_matrix,
        panel.manifest_sha256,
        panel.report_sha256,
        panel.axis_model,
        panel.evidence_sha256,
    )
    with pytest.raises(inputs.ProductionTiledInputError, match="rotates or skews"):
        inputs._source_truth_records(
            "train", _annotation((10.0, 10.0, 20.0, 20.0)), "a" * 64, (rotated,), 400, 300
        )


def test_binding_authentication_failure_precedes_any_truth_join(monkeypatch, tmp_path) -> None:
    observed: list[str] = []

    def reject(*args, **kwargs):
        observed.append("authenticate")
        raise RuntimeError("bound bytes rejected")

    def forbidden(*args, **kwargs):
        observed.append("truth")
        raise AssertionError("truth was accessed before authentication")

    monkeypatch.setattr(
        inputs.runtime_domain_binding_v3, "load_runtime_domain_binding_v3", reject
    )
    monkeypatch.setattr(inputs, "_build_split", forbidden)

    with pytest.raises(RuntimeError, match="bound bytes rejected"):
        inputs.load_production_tiled_inputs(
            tmp_path / "binding.json", "0" * 64, repository_root=tmp_path
        )
    assert observed == ["authenticate"]


@pytest.mark.parametrize('split_name', ['train', 'validation'])
def test_split_builder_retains_full_denominator_and_reports_crop_projection_counts(
    monkeypatch, split_name,
) -> None:
    first = _panel("8-panel", (0, 0, 120, 100))
    second = _panel("9-panel", (80, 0, 120, 100))
    first = replace(first, runtime_input=replace(first.runtime_input, split=split_name))
    second = replace(second, runtime_input=replace(second.runtime_input, split=split_name))
    annotation = {
        "texts": [
            _annotation((70.0, 10.0, 130.0, 30.0))["texts"][0],
            {
                "text_id": "text-2",
                "text": "20",
                "visible": True,
                "rendered_pixel_box": [300.0, 10.0, 20.0, 20.0],
            },
        ]
    }
    rendered = SimpleNamespace(annotation=annotation, width=400, height=300)
    profile = SimpleNamespace(dataset_seeds=(393,))
    def regenerate(actual_split, *args):
        assert actual_split == split_name
        return {"a" * 64: rendered}
    monkeypatch.setattr(
        inputs.runtime_domain_binding_v3,
        "_regenerate_v3",
        regenerate,
    )

    split = inputs._build_split(split_name, (first, second), profile, 1, 2, 2)

    assert split.full_source_truth_count == 2
    assert split.projected_source_truth_count == 1
    assert split.outside_runtime_crop_truth_count == 1
    assert split.partial_source_truth_count == 1
    assert split.overlapping_source_truth_count == 1
    assert sum(len(item.projections) for item in split.source_truths) == 2
