# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""In-memory tests for the fragmented X-tick preparation contract."""

from __future__ import annotations

import unittest

from ml.ocr.fragmented_xtick_detector_v1.materialize import (
    BACKGROUND,
    MaterializationError,
    _png_bytes,
    render_train_case,
)
from ml.ocr.fragmented_xtick_detector_v1.prepare import (
    FAMILY,
    SOURCE_PATHS,
    build_preflight,
    case_specifications,
    registrations,
    source_bundle_sha256,
)


class FragmentedXTickPreparationTests(unittest.TestCase):
    def test_preflight_is_source_only_and_non_executable(self) -> None:
        receipt = build_preflight()
        self.assertEqual("train-only-materializer-preparation", receipt["state"])
        self.assertTrue(receipt["synthetic_only"])
        self.assertFalse(receipt["pixels_rendered"])
        self.assertFalse(receipt["weights_loaded"])
        self.assertEqual(0, receipt["optimizer_steps"])
        self.assertFalse(receipt["dev_evaluation"])
        self.assertFalse(receipt["sealed_evaluation"])
        self.assertFalse(receipt["production_approval"])
        self.assertFalse(receipt["release_eligible"])

    def test_train_render_is_byte_reproducible_and_annotation_matches_visible_glyphs(self) -> None:
        registration = next(item for item in registrations() if item.split == "train")
        case = case_specifications(registration)[7]
        first = render_train_case(case)
        second = render_train_case(case)
        self.assertEqual(_png_bytes(first.image), _png_bytes(second.image))
        self.assertEqual(first.annotation, second.annotation)
        bounds = tuple(first.annotation["target"]["bounding_box_xyxy"])
        self.assertEqual(bounds, first.glyph_mask.getbbox())
        self.assertEqual(
            first.annotation["target"]["visible_glyph_pixel_count"],
            sum(1 for value in first.glyph_mask.get_flattened_data() if value),
        )
        self.assertTrue(any(value < BACKGROUND for value in first.image.crop(bounds).get_flattened_data()))

    def test_renderer_rejects_dev_case_without_reading_or_rendering_it(self) -> None:
        registration = next(item for item in registrations() if item.split == "dev")
        with self.assertRaises(MaterializationError):
            render_train_case(case_specifications(registration)[0])

    def test_split_cases_are_deterministic_and_globally_distinct(self) -> None:
        first = [case for registration in registrations() for case in case_specifications(registration)]
        second = [case for registration in registrations() for case in case_specifications(registration)]
        self.assertEqual(first, second)
        self.assertEqual(152, len(first))
        self.assertEqual(len(first), len({case["case_id"] for case in first}))
        self.assertEqual(len(first), len({case["case_identity_sha256"] for case in first}))
        self.assertTrue(all(case["case_id"].startswith(FAMILY) for case in first))
        self.assertEqual({"train", "dev", "sealed_public_future"}, {case["split"] for case in first})

    def test_each_case_requires_complete_numeric_label_and_three_separate_confusers(self) -> None:
        for registration in registrations():
            for case in case_specifications(registration):
                positive = case["positive"]
                confusers = case["negative_confusers"]
                self.assertEqual("XTick", positive["role"])
                self.assertTrue(positive["numeric"])
                self.assertTrue(positive["complete_label_required"])
                self.assertGreaterEqual(positive["fragment_count"], 2)
                self.assertEqual(
                    {"x_axis_tick_segment", "x_axis_baseline_fragment", "marker_like_dot"},
                    {item["kind"] for item in confusers},
                )
                self.assertEqual(
                    4,
                    len({positive["candidate_group_key"], *(item["candidate_group_key"] for item in confusers)}),
                )
                self.assertFalse(case["materialized"])

    def test_receipt_binds_every_source_file(self) -> None:
        receipt = build_preflight()
        self.assertEqual(list(SOURCE_PATHS), receipt["source_paths"])
        self.assertEqual(source_bundle_sha256(), receipt["source_bundle_sha256"])
        self.assertEqual(152, receipt["case_count"])
        self.assertEqual(152, receipt["unique_case_identity_count"])


if __name__ == "__main__":
    unittest.main()
