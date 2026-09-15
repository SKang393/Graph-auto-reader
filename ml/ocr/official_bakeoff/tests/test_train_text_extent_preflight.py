# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from copy import deepcopy

import pytest

from ml.ocr.official_bakeoff import train_text_extent_preflight as subject


def test_duplicate_json_keys_are_rejected(tmp_path):
    path = tmp_path / "duplicate.json"
    path.write_text('{"schema":"one","schema":"two"}', encoding="utf-8")

    with pytest.raises(subject.TextExtentPreflightError, match="duplicate key"):
        subject._read_json(path)


def test_overlap_audit_detects_new_intersection():
    separated = [
        {"rendered_pixel_box": [1, 1, 4, 4]},
        {"rendered_pixel_box": [6, 1, 4, 4]},
    ]
    overlapping = deepcopy(separated)
    overlapping[1]["rendered_pixel_box"] = [4, 1, 4, 4]

    assert subject._overlap_pairs(separated) == set()
    assert subject._overlap_pairs(overlapping) == {(0, 1)}


def test_affine_projection_matches_historical_crop_translation():
    inverse = subject._inverse_affine([1, 0, 0, 0, 1, 313, 0, 0, 1])

    assert subject._transform_box([10, 320, 30, 330], inverse) == pytest.approx(
        (10, 7, 30, 17)
    )


def test_current_generator_source_drift_is_rejected(tmp_path, monkeypatch):
    source = tmp_path / "source.py"
    source.write_text("current", encoding="utf-8")
    own = tmp_path / "preflight.py"
    own.write_text("preflight", encoding="utf-8")
    monkeypatch.setattr(subject, "__file__", str(own))

    with pytest.raises(subject.TextExtentPreflightError, match="source changed"):
        subject._current_source_inventory(
            tmp_path,
            [{"relative_path": "source.py", "sha256": "0" * 64}],
        )


def test_train_scene_builder_never_requests_development_specs(monkeypatch):
    captured = []

    def build(specs, dataset_seed, *, require_complete_style_catalog):
        assert require_complete_style_catalog is False
        captured.extend(specs)
        return [
            {
                "seed": dataset_seed * 100 + index,
                "families": {
                    axis: {"split": "train"}
                    for axis in ("renderer", "font", "degradation", "template", "marker")
                },
            }
            for index in range(subject.TRAIN_CASE_COUNT_PER_DATASET_SEED)
        ]

    monkeypatch.setattr(subject, "_build_scenes", build)

    scenes = subject._build_train_scenes(393)

    assert len(scenes) == subject.TRAIN_CASE_COUNT_PER_DATASET_SEED
    assert tuple(captured) == subject.PRESETS["smoke"][:4]
    assert all(spec.renderer_family != "scan_rough" for spec in captured)


def test_treatment_skips_overlapping_position_and_preserves_geometry(monkeypatch):
    regions = [
        {"region_id": str(index), "role": "condition_label", "text": "Old",
         "panel_id": "panel", "box": [x, 10, 5, 10]}
        for index, x in enumerate((10, 100, 200))
    ]
    scene = {"seed": 39300, "annotations": {"text_regions": regions},
             "panels": [{"panel_id": "panel", "top_bars": []}]}
    baseline = [
        {"region_id": row["region_id"], "rendered_pixel_box": row["box"]}
        for row in regions
    ] + [{"region_id": "obstacle", "rendered_pixel_box": [220, 10, 10, 10]}]
    monkeypatch.setattr(subject, "_choose_text", lambda *args: ("Review", 40, 10, 40))
    before = deepcopy(scene)

    treated, selected = subject._apply_text_treatment(
        scene, scale_x=1, include_legend=False,
        baseline_records=baseline, condition_limit=2,
    )

    assert scene == before
    assert {row["old_region_id"] for row in selected} == {"0", "1"}
    assert treated["annotations"]["text_regions"][2] == regions[2]
    assert subject._without_text(treated) == subject._without_text(scene)
