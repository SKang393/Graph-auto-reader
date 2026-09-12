# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Canvas intersection regressions for geometrically transformed text pixels."""

from __future__ import annotations

import pytest

from ml.synthetic.renderer import _transform_annotations


IDENTITY = (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)


def _annotation(record: dict[str, object]) -> dict[str, object]:
    return {
        "canvas": {"width": 1338, "height": 492},
        "panels": [{"texts": [record]}],
    }


def test_transformed_rendered_pixels_are_clipped_to_the_canvas() -> None:
    record: dict[str, object] = {
        "box": [1208.413285, 140.090854, 120.032217, 20.194218],
        "rendered_pixel_box": [1208.413285, 140.090854, 120.032217, 20.194218],
        "graph": [7.0, 11.0],
    }
    annotation = _annotation(record)
    translation = (1.0, 0.0, 10.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)

    _transform_annotations(annotation, translation)

    assert record["box"] == [1218.413285, 140.090854, 120.032217, 20.194218]
    assert record["rendered_pixel_box"] == pytest.approx(
        [1218.413285, 140.090854, 119.586715, 20.194218],
        abs=1e-6,
    )
    left, top, width, height = record["rendered_pixel_box"]
    assert left + width <= 1338
    assert top + height <= 492
    assert record["graph"] == [7.0, 11.0]


def test_transformed_rendered_pixels_with_empty_intersection_become_null() -> None:
    record: dict[str, object] = {
        "box": [1.0, 10.0, 5.0, 5.0],
        "rendered_pixel_box": [1.0, 10.0, 5.0, 5.0],
        "graph": [1.0, 2.0],
    }
    annotation = _annotation(record)
    translation = (1.0, 0.0, -10.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)

    _transform_annotations(annotation, translation)

    assert record["rendered_pixel_box"] is None
    assert record["box"] == [-9.0, 10.0, 5.0, 5.0]
    assert record["graph"] == [1.0, 2.0]


def test_fully_in_canvas_rendered_pixels_remain_unchanged() -> None:
    record: dict[str, object] = {
        "box": [20.25, 30.5, 40.75, 10.125],
        "rendered_pixel_box": [20.25, 30.5, 40.75, 10.125],
        "graph": [3.0, 4.0],
    }
    annotation = _annotation(record)

    _transform_annotations(annotation, IDENTITY)

    assert record == {
        "box": [20.25, 30.5, 40.75, 10.125],
        "rendered_pixel_box": [20.25, 30.5, 40.75, 10.125],
        "graph": [3.0, 4.0],
    }


def test_later_transform_does_not_restore_clipped_pixels() -> None:
    record = {"box": [1325.0, 10.0, 5.0, 5.0],
              "rendered_pixel_box": [1325.0, 10.0, 5.0, 5.0]}
    annotation = _annotation(record)
    _transform_annotations(annotation, (1, 0, 10, 0, 1, 0, 0, 0, 1))
    assert record["rendered_pixel_box"] == [1335.0, 10.0, 3.0, 5.0]
    _transform_annotations(annotation, (1, 0, -10, 0, 1, 0, 0, 0, 1))
    assert record["rendered_pixel_box"] == [1325.0, 10.0, 3.0, 5.0]
    assert record["box"] == [1325.0, 10.0, 5.0, 5.0]


@pytest.mark.parametrize("initial", [None, [1.0, 10.0, 5.0, 5.0]])
def test_absent_pixels_remain_absent_after_opposing_transforms(initial) -> None:
    record = {"box": [1.0, 10.0, 5.0, 5.0], "rendered_pixel_box": initial,
              "graph": [7.0, 11.0]}
    annotation = _annotation(record)
    _transform_annotations(annotation, (1, 0, -10, 0, 1, 0, 0, 0, 1))
    assert record["rendered_pixel_box"] is None
    _transform_annotations(annotation, (1, 0, 10, 0, 1, 0, 0, 0, 1))
    assert record == {"box": [1.0, 10.0, 5.0, 5.0],
                      "rendered_pixel_box": None, "graph": [7.0, 11.0]}


def test_rounded_clipped_box_stays_inside_canvas() -> None:
    record = {"rendered_pixel_box": [1.2345675, 10.0, 1338.0, 5.0]}
    _transform_annotations(_annotation(record), IDENTITY)
    left, top, width, height = record["rendered_pixel_box"]
    assert width > 0 and height > 0
    assert left + width <= 1338 and top + height <= 492
    assert left + width == pytest.approx(1338, abs=1e-6)
