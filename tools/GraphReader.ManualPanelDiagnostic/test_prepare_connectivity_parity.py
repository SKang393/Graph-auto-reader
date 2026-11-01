# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from prepare_connectivity_parity import panel_contains


class PrepareConnectivityParityTests(unittest.TestCase):
    def test_panel_containment_requires_a_successful_axis_and_complete_box(self):
        panel = {"axis": {"success": True}, "crop": {"X": 10, "Y": 20, "Width": 100, "Height": 80}}
        self.assertTrue(panel_contains(panel, [10, 20, 110, 100]))
        self.assertFalse(panel_contains(panel, [9, 20, 110, 100]))
        self.assertFalse(panel_contains({**panel, "axis": {"success": False}}, [10, 20, 110, 100]))


if __name__ == "__main__":
    unittest.main()
