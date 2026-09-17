# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from hashlib import sha256
from types import SimpleNamespace

from PIL import Image
import pytest

from ml.ocr.official_bakeoff import train_legend_coverage_preflight as subject


def _families(renderer: str) -> dict[str, dict[str, str]]:
    return {
        axis: {"key": value, "split": "train"}
        for axis, value in subject.EXPECTED_FAMILIES[renderer].items()
    }


def _scene(index: int, renderer: str, size: int) -> dict:
    series = [
        {"series_id": f"series-{series_index}", "legend_text": text}
        for series_index, text in enumerate((
            "Shared baseline", "Treatment B", "Treatment C",
        ))
    ]
    return {
        "seed": subject.DATASET_SEED * 100 + index,
        "design": "alternating_treatments",
        "families": _families(renderer),
        "presentation": {"font_size_px": size, "legend_position": "inside"},
        "panels": [{
            "panel_id": f"panel-{index}",
            "plot_box": [10, 20, 200, 120],
            "series": series,
            "legend": {
                "visible": True,
                "position": "inside",
                "box": [30, 25, 150, 76],
                "entries": [
                    {"series_id": row["series_id"], "text": row["legend_text"]}
                    for row in series
                ],
            },
        }],
    }


def test_case_matrix_is_six_existing_train_family_sources() -> None:
    specs = subject.case_specs()

    assert len(specs) == 6
    assert {(row.renderer_family, row.presentation["font_size_px"]) for row in specs} == {
        (renderer, size)
        for renderer in subject.RENDERER_FAMILIES
        for size in subject.FONT_SIZES
    }
    assert all(row.design == "alternating_treatments" for row in specs)
    assert all(row.panel_count == 1 and row.session_count == 36 for row in specs)
    assert all(row.presentation["legend_position"] == "inside" for row in specs)


def test_scene_builder_requests_only_fixed_train_specs(monkeypatch) -> None:
    captured = []

    def build(specs, dataset_seed, *, require_complete_style_catalog):
        assert dataset_seed == subject.DATASET_SEED
        assert require_complete_style_catalog is False
        captured.extend(specs)
        return [
            _scene(index, spec.renderer_family, spec.presentation["font_size_px"])
            for index, spec in enumerate(specs)
        ]

    monkeypatch.setattr(subject, "_build_scenes", build)

    scenes = subject._build_train_scenes(subject.DATASET_SEED)

    assert tuple(captured) == subject.case_specs()
    assert len(scenes) == 6
    assert all(subject._scene_split(scene) == "train" for scene in scenes)


def test_held_out_family_is_rejected_even_if_mislabeled_train() -> None:
    scene = _scene(0, "vector_clean", 8)
    scene["families"]["template"] = {"key": "compact_legend", "split": "train"}

    with pytest.raises(subject.LegendCoveragePreflightError, match="held-out template"):
        subject._family_keys(scene)


def test_legend_entries_must_match_panel_series_ids_and_text() -> None:
    disconnected_id = _scene(0, "vector_clean", 8)
    disconnected_id["panels"][0]["legend"]["entries"][1]["series_id"] = "other-series"
    with pytest.raises(subject.LegendCoveragePreflightError, match="ordered bijection"):
        subject._legend_series_text(disconnected_id["panels"][0])

    disconnected_text = _scene(0, "vector_clean", 8)
    disconnected_text["panels"][0]["legend"]["entries"][1]["text"] = "Unrelated label"
    with pytest.raises(subject.LegendCoveragePreflightError, match="differs from its panel series"):
        subject._legend_series_text(disconnected_text["panels"][0])


def test_render_measurement_uses_actual_rendered_boxes_and_system_font(tmp_path, monkeypatch) -> None:
    font_path = tmp_path / "font.ttf"
    font_path.write_bytes(b"test-system-font")
    font_sha = sha256(font_path.read_bytes()).hexdigest()
    font = {
        "requested": "sans",
        "resolved_file": "font.ttf",
        "resolved_path": str(font_path),
        "family": "Test Sans",
        "style": "Regular",
        "size_px": 8,
        "source": "system",
        "sha256": font_sha,
        "bundled": False,
    }
    records = [
        {
            "panel_id": "panel-0",
            "region_id": f"legend-{index}",
            "role": "legend_text",
            "text": text,
            "rendered_pixel_box": [40, 30 + index * 22, width, height],
        }
        for index, (text, width, height) in enumerate((
            ("Shared baseline", 61, 7),
            ("Treatment B", 49, 8),
            ("Treatment C", 50, 8),
        ))
    ]
    rendered = SimpleNamespace(
        image=Image.new("RGB", (240, 180), "white"),
        annotation={
            "texts": [],
            "panels": [{"panel_id": "panel-0", "texts": records}],
            "font": font,
        },
    )
    monkeypatch.setattr(subject, "render_visible_content_source", lambda scene: rendered)

    _image, _annotation, measurement = subject._render_case(
        _scene(0, "vector_clean", 8), 8
    )

    assert measurement["legend_truth_count"] == 3
    assert measurement["inside_plot_truth_count"] == 3
    assert measurement["rendered_widths"] == [61.0, 49.0, 50.0]
    assert measurement["rendered_heights"] == [7.0, 8.0, 8.0]
    assert measurement["normalized_heights"] == [7 / 120, 8 / 120, 8 / 120]
    assert measurement["row_top_deltas_pixels"] == [22.0, 22.0]
    assert measurement["normalized_row_top_deltas"] == [22 / 120, 22 / 120]
    assert measurement["row_gaps_pixels"] == [15.0, 14.0]
    assert measurement["normalized_row_gaps"] == [15 / 120, 14 / 120]
    assert measurement["font"]["sha256"] == font_sha


def test_geometry_rejects_nonfinite_and_overlapping_rows() -> None:
    with pytest.raises(subject.LegendCoveragePreflightError, match="non-finite"):
        subject._box([0, 0, float("nan"), 1], "test box")

    records = [
        {"text": "First", "rendered_pixel_box": [40, 30, 40, 8]},
        {"text": "Second", "rendered_pixel_box": [40, 36, 40, 8]},
    ]
    with pytest.raises(subject.LegendCoveragePreflightError, match="overlap"):
        subject._legend_geometry(
            records,
            ["First", "Second"],
            [10, 20, 200, 120],
            [30, 25, 150, 76],
        )

    spaced_records = [
        {"text": "First", "rendered_pixel_box": [40, 30, 40, 8]},
        {"text": "Second", "rendered_pixel_box": [40, 53, 40, 8]},
    ]
    with pytest.raises(subject.LegendCoveragePreflightError, match="top spacing"):
        subject._legend_geometry(
            spaced_records,
            ["First", "Second"],
            [10, 20, 200, 120],
            [30, 25, 150, 76],
        )


def test_existing_output_is_rejected_before_generation(tmp_path, monkeypatch) -> None:
    root = tmp_path
    output = root / "artifacts" / "existing"
    output.mkdir(parents=True)
    monkeypatch.setattr(
        subject,
        "_build_payloads",
        lambda *args, **kwargs: pytest.fail("generation must not start"),
    )

    with pytest.raises(subject.LegendCoveragePreflightError, match="already exists"):
        subject.run_preflight(root, output)
