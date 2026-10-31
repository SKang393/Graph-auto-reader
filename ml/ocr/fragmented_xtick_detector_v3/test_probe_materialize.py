# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Label and distribution checks for the fresh procedural probe generator."""

from __future__ import annotations

import unittest

from ml.ocr.fragmented_xtick_detector_v3.probe_materialize import (
    FONT_RELATIVES,
    FONT_SIZES,
    LABELS,
    TRAIN_LABELS,
    cases,
    render_case,
)


class FreshProbeMaterializationTests(unittest.TestCase):
    def test_probe_labels_fonts_placements_and_seeds_are_distinct_from_train_recipe(self) -> None:
        probe_cases = cases()
        self.assertEqual(96, len(probe_cases))
        self.assertEqual(64, sum(case["kind"] == "positive" for case in probe_cases))
        self.assertTrue(set(LABELS).isdisjoint(TRAIN_LABELS))
        self.assertNotIn("src/GraphReader.App/Assets/Fonts/NotoSans-Regular.ttf", FONT_RELATIVES)
        self.assertNotIn(18, FONT_SIZES)
        self.assertTrue(all(case["seed"] > 24014196 for case in probe_cases))
        self.assertGreater(len({tuple(case["origin"]) for case in probe_cases[:64]}), 50)

    def test_rendered_bounds_are_exactly_the_glyph_mask_bounds(self) -> None:
        for case in cases()[:64]:
            rendered = render_case(case)
            target = rendered.annotation["target"]
            self.assertIsNotNone(target)
            self.assertEqual(list(rendered.glyph_mask.getbbox()), target["bounding_box_xyxy"])
            self.assertEqual(sum(bool(pixel) for pixel in rendered.glyph_mask.get_flattened_data()), target["visible_glyph_pixel_count"])
            left, top, right, bottom = target["bounding_box_xyxy"]
            self.assertTrue(0 <= left < right <= 256 and 0 <= top < bottom <= 96)
            self.assertTrue(all(rendered.image.getpixel((x, y)) < 245 for y in range(top, bottom) for x in range(left, right) if rendered.glyph_mask.getpixel((x, y))))

    def test_background_only_cases_have_no_target_glyphs_and_render_repeatably(self) -> None:
        probe_cases = cases()
        for case in probe_cases[64:]:
            rendered = render_case(case)
            self.assertIsNone(rendered.annotation["target"])
            self.assertIsNone(rendered.glyph_mask.getbbox())
        first = render_case(probe_cases[0])
        again = render_case(probe_cases[0])
        self.assertEqual(first.image.tobytes(), again.image.tobytes())
        self.assertEqual(first.annotation, again.annotation)


if __name__ == "__main__":
    unittest.main()
