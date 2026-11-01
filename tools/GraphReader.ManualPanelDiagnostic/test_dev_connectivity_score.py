# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from dev_connectivity_score import score_categories


class DevConnectivityScoreTests(unittest.TestCase):
    def test_counts_each_category_and_does_not_drop_uncertain(self):
        proposals = [{"id": "a", "dark_vertical": False, "faint_vertical": True},
                     {"id": "b", "dark_vertical": True, "faint_vertical": True},
                     {"id": "c", "dark_vertical": False, "faint_vertical": False}]
        labels = [{"id": "a", "classification": "numeric_text"},
                  {"id": "b", "classification": "graph_structure"},
                  {"id": "c", "classification": "uncertain"}]
        result = score_categories(proposals, labels)
        self.assertEqual({"items": 1, "dark_vertical": 0, "faint_vertical": 1}, result["numeric_text"])
        self.assertEqual({"items": 1, "dark_vertical": 1, "faint_vertical": 1}, result["graph_structure"])
        self.assertEqual(1, result["uncertain"]["items"])

    def test_missing_or_duplicate_labels_are_rejected(self):
        proposal = {"id": "a", "dark_vertical": False, "faint_vertical": False}
        label = {"id": "a", "classification": "numeric_text"}
        with self.assertRaises(ValueError):
            score_categories([proposal], [])
        with self.assertRaises(ValueError):
            score_categories([proposal], [label, label])


if __name__ == "__main__":
    unittest.main()
