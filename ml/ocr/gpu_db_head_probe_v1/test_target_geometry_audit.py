# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

from ml.ocr.gpu_db_head_probe_v1.target_geometry_audit import analyze
from ml.ocr.official_bakeoff.db_inverse_targets import build_inverse_targets


class GeometryAuditTests(unittest.TestCase):
    def test_separate_instances_touch_and_merge(self):
        targets = build_inverse_targets(32, 32, [(3, 3, 7, 7), (7, 3, 11, 7)])
        result = analyze(targets, [("one", "x_tick"), ("two", "x_tick")])
        self.assertEqual(result["direct_contacts"], 1)
        self.assertEqual(result["components_with_multiple_instances"], 1)
        self.assertEqual(result["instances_in_multi_instance_components"], 2)
        self.assertEqual(result["contact_role_pairs"], {"x_tick + x_tick": 1})

    def test_minimum_extent_and_distortion(self):
        targets = build_inverse_targets(32, 32, [(10, 10, 11, 11)])
        result = analyze(targets, [("one", "x_tick")])
        self.assertEqual(result["clamped_both"], 1)
        self.assertEqual(result["clamped_extent_larger_than_source_box"], 1)
        self.assertGreater(result["maximum_clamped_extent_area_to_continuous_inverse_area"], 1)
        self.assertEqual(result["direct_contacts"], 0)

    def test_duplicate_instance_identity_rejected(self):
        targets = build_inverse_targets(32, 32, [(3, 3, 7, 7), (7, 3, 11, 7)])
        with self.assertRaisesRegex(ValueError, "distinct"):
            analyze(targets, [("same", "x_tick"), ("same", "x_tick")])

    def test_counterfactual_detects_contact_caused_by_minimum_extent(self):
        targets = build_inverse_targets(32, 32, [(10, 10, 11, 11), (13, 10, 14, 11)])
        result = analyze(targets, [("one", "x_tick"), ("two", "x_tick")])
        self.assertEqual(result["direct_contacts"], 1)
        self.assertEqual(result["contacts_involving_clamp"], 1)
        self.assertEqual(result["contacts_remaining_without_minimum_extent"], 0)
