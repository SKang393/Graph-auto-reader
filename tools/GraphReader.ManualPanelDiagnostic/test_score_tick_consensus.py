# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from score_tick_consensus import summarize
from test_score_dev_ocr import reading


class TickConsensusScoreTests(unittest.TestCase):
    def test_far_nontext_is_withheld_while_consistent_ticks_qualify(self):
        features = [{"id": key, "image_sha256": "same", "gap_height": gap, "axis_connected": False}
                    for key, gap in (("a", 1.0), ("b", 1.0), ("c", 1.0), ("d", 3.0))]
        padded = [{"id": row["id"], "server": reading("1", [("1", 1, .9), ("7", 7, .01)])}
                  for row in features]
        tight = [{"id": row["id"], "server": reading("1", [("1", 1, .9)])} for row in features]
        labels = {key: {"expected": "1"} for key in ("a", "b", "c")}
        labels["d"] = {"category": "other_text"}
        result = summarize(features, padded, tight, labels)
        self.assertEqual(result["counts"]["labelled_qualified_correct"], 3)
        self.assertEqual(result["counts"]["labelled_current_withheld"], 3)
        self.assertEqual(result["unscored_categories"]["qualified"]["other_text"], 0)

    def test_insufficient_cohort_connected_ink_and_disagreement_withhold(self):
        features = [{"id": key, "image_sha256": "same", "gap_height": 1.0,
                     "axis_connected": key == "a"} for key in ("a", "b", "c")]
        padded = [{"id": key, "server": reading("1", [("1", 1, .9)])} for key in ("a", "b", "c")]
        tight = [{"id": key, "server": reading("2" if key == "b" else "1",
                                                [("2", 2, .9)] if key == "b" else [("1", 1, .9)])}
                 for key in ("a", "b", "c")]
        labels = {key: {"expected": "1"} for key in ("a", "b", "c")}
        self.assertEqual(summarize(features, padded, tight, labels)["counts"]["candidate_qualifies"], 1)
        self.assertEqual(summarize(features[:2], padded[:2], tight[:2],
                                   {key: labels[key] for key in ("a", "b")})["counts"]["candidate_qualifies"], 0)


if __name__ == "__main__":
    unittest.main()
