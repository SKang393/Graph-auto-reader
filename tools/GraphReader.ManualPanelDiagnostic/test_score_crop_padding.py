# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from score_crop_padding import compare
from test_score_dev_ocr import reading


class CropPaddingScoreTests(unittest.TestCase):
    def test_regressions_and_unscored_numeric_proposals_are_counted(self):
        labels = [{"id": "a", "expected": "1"}, {"id": "b", "expected": "2"}]
        padded = [{"id": "a", "server": reading("1", [("1", 1, .9)])},
                  {"id": "b", "server": reading("3", [("3", 3, .9)])},
                  {"id": "c", "server": reading("2", [("2", 2, .9)])}]
        tight = [{"id": "a", "server": reading("7", [("7", 7, .9)])},
                 {"id": "b", "server": reading("2", [("2", 2, .9)])},
                 {"id": "c", "server": reading("x", [("x", None, .9)])}]
        result = compare(labels, padded, tight, {"c": "other_text"})
        self.assertEqual((result["scored"], result["unscored"]), (2, 1))
        self.assertEqual(result["counts"]["exact_improved"], 1)
        self.assertEqual(result["counts"]["exact_regressed"], 1)
        self.assertEqual(result["unscored_numeric_primaries"]["padded"], {"other_text": 1})
        self.assertEqual(result["unscored_numeric_primaries"]["tight"], {})

    def test_mismatched_proposal_inventory_is_rejected(self):
        with self.assertRaises(ValueError):
            compare([], [{"id": "a", "server": reading("1", [("1", 1, .9)])}], [], {})


if __name__ == "__main__":
    unittest.main()
