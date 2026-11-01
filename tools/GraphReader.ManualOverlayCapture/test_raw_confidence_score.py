# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from raw_confidence_score import categories


class ConfidenceCategoryTests(unittest.TestCase):
    def test_operating_threshold_and_missing_proposals(self):
        self.assertEqual("no_supported_local_proposal", categories(None, False))
        self.assertEqual("below_threshold_final_nearby", categories(0.099, True))
        self.assertEqual("at_or_above_threshold_no_final_nearby", categories(0.1, False))
        with self.assertRaises(ValueError):
            categories(float("nan"), False)


if __name__ == "__main__":
    unittest.main()
