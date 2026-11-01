# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest
from unittest import mock

import tick_rank_guarded_train_probe as guarded


def candidate(rank, origin_x, line_left):
    return {
        "Rank": rank, "Score": 0.9 - rank * 0.01,
        "Intersection": {"X": origin_x, "Y": 80.0},
        "BottomRight": {"X": 180.0, "Y": 80.0},
        "TopLeft": {"X": origin_x, "Y": 10.0},
        "XAxisLine": {"Start": {"X": line_left, "Y": 80.0},
                      "End": {"X": 180.0, "Y": 80.0}},
    }


class GuardedTickRankTests(unittest.TestCase):
    def test_axis_extension_is_measured_from_the_candidate_origin(self):
        self.assertEqual(0, guarded.left_extension(candidate(0, 20.0, 20.0)))
        self.assertEqual(80, guarded.left_extension(candidate(1, 100.0, 20.0)))
        invalid = candidate(0, 20.0, 20.0)
        invalid["BottomRight"] = invalid["Intersection"]
        with self.assertRaises(ValueError):
            guarded.left_extension(invalid)

    def test_phase_line_with_stronger_tick_like_evidence_does_not_replace_axis(self):
        first = candidate(0, 20.0, 20.0)
        interior = candidate(1, 100.0, 20.0)
        panel = {"Width": 200, "Height": 100,
                 "axis": {"ranked_candidates": [first, interior], "line_candidates": []}}
        with mock.patch.object(guarded.base, "tick_evidence",
                               side_effect=lambda c, *_: (8, 8) if c["Rank"] else (4, 4)):
            chosen, _, _ = guarded.choose(panel)
        self.assertIs(chosen, first)

    def test_guard_allows_a_supported_alternative_without_left_extension(self):
        first = candidate(0, 20.0, 20.0)
        alternative = candidate(1, 35.0, 35.0)
        panel = {"Width": 200, "Height": 100,
                 "axis": {"ranked_candidates": [first, alternative], "line_candidates": []}}
        with mock.patch.object(guarded.base, "tick_evidence",
                               side_effect=lambda c, *_: (8, 8) if c["Rank"] else (4, 4)):
            chosen, _, _ = guarded.choose(panel)
        self.assertIs(chosen, alternative)


if __name__ == "__main__":
    unittest.main()
