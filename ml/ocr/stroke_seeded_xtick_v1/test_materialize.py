# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused geometry and target-integrity tests for the new crop proposal."""

import unittest

from PIL import Image

from ml.synthetic.renderer import _apply_matrix, _warp_mask
from ml.ocr.stroke_seeded_xtick_v1.materialize import (
    INPUT_HEIGHT,
    INPUT_WIDTH,
    CROP_HEIGHT,
    CROP_WIDTH,
    _crop_and_resize,
    _glyph_alpha_mask,
    _map_box,
    build_train_scene,
    classify_crop,
    make_crop_example,
    materialize_recipe,
    render_train_scene,
    source_recipes,
)


class StrokeSeededMaterializerTests(unittest.TestCase):
    def test_multiple_strokes_share_one_train_source_and_glyph_targets_are_exact(self):
        recipe = source_recipes()[0]
        rows = materialize_recipe(recipe)
        self.assertGreater(len(rows), 1)
        self.assertEqual({row["source_id"] for row in rows}, {recipe["source_id"]})
        self.assertEqual({row["split"] for row in rows}, {"train"})
        self.assertEqual(len({row["tick_id"] for row in rows}), len(rows))
        positive = [row for row in rows if row["kind"] == "positive"]
        excluded = [row for row in rows if row["kind"] == "excluded"]
        self.assertGreater(len(positive), 1)
        self.assertTrue(all(len(row["targets"]) == 1 and row["expected_tick_label"] is not None
                            and row["targets"][0]["text"] == row["expected_tick_label"] for row in positive))
        self.assertTrue(all(row["exclusion_reasons"] for row in excluded))
        self.assertTrue(all(row["trainable_single_box"] for row in positive))
        self.assertTrue(all(str(row["rendered_font"]["resolved_path"]).startswith(str(
            __import__("ml.ocr.stroke_seeded_xtick_v1.materialize", fromlist=["ROOT"]).ROOT)) for row in rows))
        self.assertTrue(all(len(row["crop_pixels"]) == INPUT_WIDTH * INPUT_HEIGHT for row in rows))
        self.assertTrue(all(row["crop_transform"]["source_window_xywh"][2:] == [CROP_WIDTH, CROP_HEIGHT] for row in rows))

    def test_hand_drawn_geometry_matches_warped_final_raster(self):
        recipe = source_recipes()[6]
        rows = materialize_recipe(recipe)
        scene = build_train_scene(recipe)
        panel_image, rendered, _ = render_train_scene(scene)
        self.assertTrue(rendered["transforms"])
        matrix = rendered["transforms"][-1]["forward_matrix_3x3"]
        self.assertAlmostEqual(matrix[6], 0.0)
        self.assertAlmostEqual(matrix[7], 0.0)
        self.assertTrue(all(row["renderer_transform_applied_to_nested_geometry"] for row in rows))
        panel = rendered["panels"][0]
        stroke = panel["ticks"][0]
        mapped_center = _apply_matrix(matrix, tuple(stroke["center"]))
        self.assertEqual(rows[0]["stroke_center_original_xy"], list(mapped_center))
        first_text = next(t for t in panel["texts"] if t.get("role") == "x_tick" and t.get("rendered_pixel_box"))
        font_info = rendered["font"]
        from PIL import ImageFont
        mask = _glyph_alpha_mask(first_text, ImageFont.truetype(font_info["resolved_path"], font_info["size_px"]), panel_image.size)
        warped = _warp_mask(mask, matrix)
        self.assertIsNotNone(warped.getbbox())
        row_target_boxes = [target["glyph_mask_xyxy"] for row in rows for target in row["targets"]]
        actual = list(warped.getbbox())
        self.assertIn(actual, row_target_boxes)

    def test_missing_tick_label_is_background_only(self):
        panel = Image.new("L", (64, 40), 255)
        crop, targets = make_crop_example(panel, center_x=32, baseline_y=2, label_boxes=[])
        self.assertEqual(crop.size, (INPUT_WIDTH, INPUT_HEIGHT))
        self.assertEqual(targets, [])

    def test_generated_missing_labels_produce_background_samples(self):
        rows = materialize_recipe(source_recipes()[2])
        self.assertGreater(len(rows), 1)
        background = [row for row in rows if row["kind"] == "background_only"]
        excluded = [row for row in rows if row["kind"] == "excluded"]
        self.assertTrue(background)
        self.assertTrue(all(not row["targets"] and row["expected_tick_label"] is None for row in background))
        self.assertTrue(all("non_xtick_text_intersects_crop" in row["exclusion_reasons"] for row in excluded))

    def test_project_local_regular_medium_and_semibold_fonts_resolve(self):
        names = []
        for index in (0, 2, 4):
            _, rendered, _ = render_train_scene(build_train_scene(source_recipes()[index]))
            names.append(rendered["font"]["resolved_file"])
        self.assertEqual(names, ["NotoSans-Regular.ttf", "NotoSans-Medium.ttf", "NotoSans-SemiBold.ttf"])

    def test_edge_clipping_keeps_reversible_original_pixel_mapping(self):
        panel = Image.new("L", (40, 30), 255)
        _, transform = _crop_and_resize(panel, center_x=0, baseline_y=0)
        point = (0.0, 0.0)
        forward = transform["original_to_crop_pixel"]
        inverse = transform["crop_pixel_to_original"]
        crop_point = (point[0] * forward[0] + forward[2], point[1] * forward[4] + forward[5])
        restored = (crop_point[0] * inverse[0] + inverse[2], crop_point[1] * inverse[4] + inverse[5])
        self.assertEqual(restored, point)
        mapped = _map_box((0.0, 0.0, 5.0, 5.0), transform)
        self.assertIsNotNone(mapped)
        self.assertGreater(mapped[0], 0)
        self.assertEqual(mapped[1], 0)

    def test_target_box_clips_to_content_not_right_padding(self):
        panel = Image.new("L", (200, 100), 255)
        _, transform = _crop_and_resize(panel, center_x=100, baseline_y=10)
        mapped = _map_box((98.0, 11.0, 102.0, 15.0), transform)
        self.assertIsNotNone(mapped)
        self.assertLessEqual(mapped[2], transform["content_xywh"][2])

    def test_one_box_admission_excludes_multiple_or_clipped_targets(self):
        kind, reasons = classify_crop("9", ["9", "10"], expected_found=True,
                                      expected_fully_inside=True, non_tick_text=False, any_tick_clipped=True)
        self.assertEqual(kind, "excluded")
        self.assertIn("multiple_xtick_targets_in_crop", reasons)
        self.assertIn("x_tick_glyph_clipped_by_crop_window", reasons)
        kind, reasons = classify_crop("9", ["9"], expected_found=True,
                                      expected_fully_inside=False, non_tick_text=False, any_tick_clipped=True)
        self.assertEqual(kind, "excluded")
        self.assertIn("visible_tick_label_clipped_by_crop_window", reasons)
        kind, reasons = classify_crop(None, [], expected_found=False,
                                      expected_fully_inside=False, non_tick_text=False, any_tick_clipped=False)
        self.assertEqual((kind, reasons), ("background_only", []))


if __name__ == "__main__":
    unittest.main()
