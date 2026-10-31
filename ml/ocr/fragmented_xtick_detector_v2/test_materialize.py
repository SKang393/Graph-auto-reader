# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""In-memory checks for V2 train-only varied-position rendering."""

from __future__ import annotations

import unittest

from ml.ocr.fragmented_xtick_detector_v2.materialize import BACKGROUND, _png_bytes, cases, render_case


class FragmentedXTickV2MaterializationTests(unittest.TestCase):
    def test_cases_have_fixed_positive_negative_balance_and_unique_identities(self) -> None:
        values = cases()
        self.assertEqual(96, len(values))
        self.assertEqual(64, sum(case["kind"] == "positive" for case in values))
        self.assertEqual(32, sum(case["kind"] == "background_negative" for case in values))
        self.assertEqual(96, len({case["case_identity_sha256"] for case in values}))

    def test_positive_renders_are_reproducible_and_localized(self) -> None:
        positives = [case for case in cases() if case["kind"] == "positive"]
        centers: list[tuple[float, float]] = []
        for case in positives:
            first, second = render_case(case), render_case(case)
            self.assertEqual(_png_bytes(first.image), _png_bytes(second.image))
            target = first.annotation["target"]
            self.assertIsInstance(target, dict)
            bounds = target["bounding_box_xyxy"]
            self.assertTrue(any(value < BACKGROUND for value in first.image.crop(bounds).get_flattened_data()))
            centers.append(((bounds[0] + bounds[2]) / 2.0, (bounds[1] + bounds[3]) / 2.0))
        self.assertGreaterEqual(len({int(x // 24) for x, _ in centers}), 8)
        self.assertGreaterEqual(len({int(y // 12) for _, y in centers}), 4)

    def test_background_negative_has_no_target(self) -> None:
        negative = next(case for case in cases() if case["kind"] == "background_negative")
        rendered = render_case(negative)
        self.assertIsNone(rendered.annotation["target"])
        self.assertTrue(any(value < BACKGROUND for value in rendered.image.get_flattened_data()))


if __name__ == "__main__":
    unittest.main()
