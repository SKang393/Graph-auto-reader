# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused fixtures for the frozen scan and one-parameter diagnostic variant."""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import diagnose_misses as diagnostic


def frame_with(axis: tuple[int, int, int, int], *strokes: tuple[int, int, int, int]) -> np.ndarray:
    image = np.full((60, 100), 255, dtype=np.uint8)

    def draw(x0: int, y0: int, x1: int, y1: int) -> None:
        dx, dy = abs(x1 - x0), -abs(y1 - y0)
        sx, sy = (1 if x0 < x1 else -1), (1 if y0 < y1 else -1)
        error = dx + dy
        while True:
            image[y0, x0] = 0
            if (x0, y0) == (x1, y1):
                return
            twice = 2 * error
            if twice >= dy:
                error += dy
                x0 += sx
            if twice <= dx:
                error += dx
                y0 += sy

    draw(*axis)
    for stroke in strokes:
        draw(*stroke)
    return image


class ShortTickScanTests(unittest.TestCase):
    axis = (10, 40, 90, 40)

    def count(self, frame: np.ndarray, core_exclusion: int = 2) -> int:
        proposals, _ = diagnostic.scan(frame, list(self.axis), core_exclusion=core_exclusion)
        return len(proposals)

    def test_non_crossing_hard_negatives_remain_empty_for_both_core_settings(self) -> None:
        fixtures = [
            frame_with(self.axis),
            frame_with(self.axis, (15, 30, 85, 30)),
            frame_with(self.axis, (10, 29, 90, 29)),
        ]
        for frame in fixtures:
            for core in (2, 1):
                with self.subTest(core=core):
                    self.assertEqual(self.count(frame, core), 0)

    def test_confusers_and_short_tick_match_frozen_scanner_contract(self) -> None:
        border = frame_with(self.axis, *((10 + x, 34, 10 + x, 40) for x in range(6)))
        crossing_grid = frame_with(self.axis, (50, 28, 50, 52))
        nearby_text = frame_with(self.axis, (68, 43, 68, 51), (66, 43, 68, 43))
        tick = frame_with(self.axis, (40, 36, 40, 44))

        self.assertGreater(self.count(border), 0)
        self.assertGreater(self.count(crossing_grid), 0)
        self.assertGreater(self.count(nearby_text), 0)
        self.assertEqual(self.count(tick), 1)

    def test_core_change_is_isolated_to_one_scan_parameter(self) -> None:
        frame = frame_with(self.axis, (40, 36, 40, 44))
        baseline, _ = diagnostic.scan(frame, list(self.axis), core_exclusion=2)
        variant, _ = diagnostic.scan(frame, list(self.axis), core_exclusion=1)
        self.assertEqual(len(baseline), 1)
        self.assertEqual(len(variant), 1)
        self.assertEqual(diagnostic.SCAN_RADIUS, 12)
        self.assertEqual(diagnostic.EDGE_GUARD, 4)
        self.assertEqual(diagnostic.MAX_CLUSTER_WIDTH, 8)
        self.assertEqual(diagnostic.MAX_INK_GRAY, 200)

    def test_wide_cluster_rule_relaxation_recovers_but_also_admits_a_wide_confuser(self) -> None:
        # A dark block crossing the selected axis is deliberately a hard
        # negative. It forms a >8px contiguous hit cluster, which the frozen
        # rule rejects and the diagnostic-only relaxation emits.
        frame = frame_with(self.axis)
        frame[35:46, 40:55] = 0
        baseline, baseline_trace = diagnostic.scan(frame, list(self.axis))
        relaxed, relaxed_trace = diagnostic.scan(
            frame, list(self.axis), max_cluster_width=diagnostic.WIDE_CLUSTER_LIMIT)

        self.assertEqual(baseline, [])
        self.assertTrue(any(c["rejection"] == "cluster_width_over_limit"
            for c in baseline_trace["clusters"]))
        self.assertEqual(len(relaxed), 1)
        self.assertEqual(relaxed_trace["clusters"][0]["rejection"], None)


if __name__ == "__main__":
    unittest.main()
