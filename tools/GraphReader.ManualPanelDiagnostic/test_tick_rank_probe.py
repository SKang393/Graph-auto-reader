# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from tick_rank_probe import choose, regular_run


def candidate(rank, x, right, score):
    return {
        "Rank": rank, "Score": score, "Intersection": {"X": x, "Y": 300},
        "TopLeft": {"X": x, "Y": 50}, "BottomRight": {"X": right, "Y": 300},
        "YAxisLine": {"Start": {"X": x, "Y": 50}, "End": {"X": x, "Y": 300}},
    }


def line(x, y):
    return {"Segment": {"Start": {"X": x - 10, "Y": y}, "End": {"X": x, "Y": y}}}


class TickRankProbeTests(unittest.TestCase):
    def test_regular_evidence_requires_a_run(self):
        self.assertEqual(5, regular_run([30, 70, 110, 150, 190]))
        self.assertLess(regular_run([10, 22, 36, 55, 79]), 4)

    def test_regular_ticks_can_rescue_a_supported_axis(self):
        baseline = candidate(0, 200, 700, 0.95)
        actual = candidate(1, 100, 700, 0.94)
        panel = {"Width": 800, "Height": 400, "axis": {"ranked_candidates": [baseline, actual],
                 "line_candidates": [line(100, y) for y in (80, 130, 180, 230, 280)]}}
        selected, run, _ = choose(panel)
        self.assertEqual(1, selected["Rank"])
        self.assertEqual(5, run)

    def test_short_plot_or_insufficient_ticks_does_not_replace_baseline(self):
        baseline = candidate(0, 200, 700, 0.95)
        short_plot = candidate(1, 100, 150, 0.94)
        panel = {"Width": 800, "Height": 400, "axis": {"ranked_candidates": [baseline, short_plot],
                 "line_candidates": [line(100, y) for y in (80, 130, 180, 230, 280)]}}
        self.assertEqual(0, choose(panel)[0]["Rank"])
        panel["axis"]["ranked_candidates"][1] = candidate(1, 100, 700, 0.94)
        panel["axis"]["line_candidates"] = [line(100, y) for y in (80, 130, 180)]
        self.assertEqual(0, choose(panel)[0]["Rank"])


if __name__ == "__main__":
    unittest.main()
