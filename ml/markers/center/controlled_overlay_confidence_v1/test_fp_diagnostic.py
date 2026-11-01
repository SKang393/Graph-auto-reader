# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from .fp_diagnostic import distance_bin


class FalsePositiveDiagnosticTests(unittest.TestCase):
    def test_distance_bins_include_boundaries(self):
        self.assertEqual("le_5", distance_bin(5))
        self.assertEqual("le_8", distance_bin(5.01))
        self.assertEqual("le_16", distance_bin(16))
        self.assertEqual("le_32", distance_bin(32))
        self.assertEqual("over_32", distance_bin(32.01))
        with self.assertRaises(ValueError):
            distance_bin(float("nan"))


if __name__ == "__main__":
    unittest.main()
