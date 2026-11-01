# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

import cv2
import numpy as np

import tick_rank_train_probe as probe


class TickRankTrainProbeTests(unittest.TestCase):
    def test_line_conversion_and_finite_guard(self):
        self.assertEqual([], probe.line_rows(None))
        rows = probe.line_rows(np.array([[[1, 2, 9, 2]]], dtype=np.float32))
        self.assertEqual({"X": 1.0, "Y": 2.0}, rows[0]["Segment"]["Start"])
        self.assertEqual({"X": 9.0, "Y": 2.0}, rows[0]["Segment"]["End"])
        with self.assertRaises(ValueError):
            probe.line_rows(np.array([[[1, np.nan, 9, 2]]]))

    def test_diagnostic_lines_require_the_bound_image_dimensions(self):
        image = np.full((80, 100), 255, dtype=np.uint8)
        cv2.line(image, (15, 10), (15, 70), 0, 2)
        cv2.line(image, (15, 70), (90, 70), 0, 2)
        ok, encoded = cv2.imencode(".png", image)
        self.assertTrue(ok)
        self.assertTrue(probe.diagnostic_lines(encoded.tobytes(), 100, 80))
        with self.assertRaises(ValueError):
            probe.diagnostic_lines(encoded.tobytes(), 99, 80)

    def test_candidate_identity_binds_panel_rank_and_geometry(self):
        panel = {"source_sha256": "a" * 64, "panel_sha256": "b" * 64}
        first = {"Rank": 0, "Score": 0.5, "Intersection": {"X": 1, "Y": 2}}
        self.assertNotEqual(probe.candidate_identity(panel, first),
                            probe.candidate_identity(panel, {**first, "Rank": 1}))
        self.assertNotEqual(probe.candidate_identity(panel, first),
                            probe.candidate_identity(panel, {**first, "Score": 0.6}))


if __name__ == "__main__":
    unittest.main()
