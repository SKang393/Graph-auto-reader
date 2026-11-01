# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from .audit_overlap import select_unique


def key(char):
    return char * 64


class ControlledOverlapTests(unittest.TestCase):
    def test_excludes_cross_partition_and_repeated_ink_without_predictions(self):
        build = [(key("a"), key("1"), 1), (key("b"), key("1"), 1), (key("c"), key("2"), 0)]
        holdout = [(key("a"), key("1"), 1), (key("d"), key("1"), 1),
                   (key("e"), key("3"), 0), (key("f"), key("3"), 0), (key("g"), key("4"), 1)]
        result = select_unique(build, holdout)
        self.assertEqual([0, 2], result["build_indices"])
        self.assertEqual([2, 4], result["holdout_indices"])
        self.assertEqual((1, 2, 1, 1), (result["holdout_exact_overlap"], result["holdout_ink_overlap"],
                                        result["build_duplicate_ink_excluded"],
                                        result["holdout_duplicate_novel_ink_excluded"]))

    def test_conflicting_labels_for_identical_ink_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "conflicting controlled labels"):
            select_unique([(key("a"), key("1"), 1)], [(key("b"), key("1"), 0)])


if __name__ == "__main__":
    unittest.main()
