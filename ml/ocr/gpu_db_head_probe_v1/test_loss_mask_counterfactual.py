# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest

import numpy as np

from ml.ocr.gpu_db_head_probe_v1.loss_mask_counterfactual import _footprints, _overlap, evaluate
from ml.ocr.official_bakeoff.db_inverse_targets import build_inverse_targets


class CounterfactualTests(unittest.TestCase):
    def test_merged_instances_are_ignored_and_distant_label_remains(self):
        built = build_inverse_targets(40, 40, [(3, 3, 7, 7), (7, 3, 11, 7), (27, 27, 35, 35)])
        mask, summary = evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("b", "y_tick"), ("c", "x_tick")])
        self.assertEqual(summary["merged_component_seed_instances"], 2)
        self.assertEqual(summary["omitted_instances"], 2)
        self.assertEqual(summary["retained_instances"], 1)
        self.assertEqual(mask[0, 5, 5], 0)
        self.assertEqual(mask[0, 31, 31], 1)

    def test_outgrown_clamp_masks_entire_source_and_target(self):
        built = build_inverse_targets(40, 40, [(10, 10, 11, 11), (27, 27, 35, 35)])
        mask, summary = evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("b", "y_tick")])
        self.assertEqual(summary["outgrown_clamp_seed_instances"], 1)
        self.assertEqual(summary["omitted_instances"], 1)
        r = built.regions[0]
        l, t, right, bottom = r.raster_contour_box
        self.assertFalse(mask[0, t:bottom + 1, l:right + 1].any())
        self.assertEqual(mask[0, 10, 10], 0)
        self.assertEqual(mask[0, 31, 31], 1)

    def test_source_margin_overlap_cascades_to_third_instance(self):
        built = build_inverse_targets(40, 40, [(3, 3, 7, 7), (7, 3, 11, 7), (13, 3, 17, 7)])
        _, summary = evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("b", "y_tick"), ("c", "x_tick")])
        self.assertEqual(summary["merged_component_seed_instances"], 2)
        self.assertEqual(summary["cascade_instances"], 1)
        self.assertEqual(summary["retained_instances"], 0)

    def test_duplicate_ids_rejected(self):
        built = build_inverse_targets(40, 40, [(3, 3, 7, 7), (7, 3, 11, 7)])
        with self.assertRaisesRegex(ValueError, "identity"):
            evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("a", "y_tick")])

    def test_target_footprint_alone_can_trigger_cascade(self):
        # The first minimum-extent target shifts inward at the raster edge.
        built = build_inverse_targets(40, 40, [(0, 10, 1, 11), (5, 10, 10, 16)])
        first_source, first_target = _footprints(built.regions[0], 40, 40)
        second_source, second_target = _footprints(built.regions[1], 40, 40)
        self.assertEqual(built.contacts, ())
        self.assertFalse(_overlap(first_source, second_source))
        self.assertFalse(_overlap(first_source, second_target))
        self.assertFalse(_overlap(first_target, second_target))
        self.assertTrue(_overlap(first_target, second_source))
        _, summary = evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("b", "x_tick")])
        self.assertEqual(summary["outgrown_clamp_seed_instances"], 1)
        self.assertEqual(summary["cascade_instances"], 1)
        self.assertEqual(summary["retained_instances"], 0)

    def test_cascade_reaches_multiple_hops(self):
        built = build_inverse_targets(40, 40, [(0, 10, 1, 11), (5, 10, 10, 16), (13, 10, 18, 16)])
        first, middle, last = [_footprints(region, 40, 40) for region in built.regions]
        self.assertEqual(built.contacts, ())
        self.assertFalse(any(_overlap(a, b) for a in first for b in last))
        self.assertTrue(any(_overlap(a, b) for a in first for b in middle))
        self.assertTrue(any(_overlap(a, b) for a in middle for b in last))
        _, summary = evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("b", "x_tick"), ("c", "x_tick")])
        self.assertEqual(summary["outgrown_clamp_seed_instances"], 1)
        self.assertEqual(summary["cascade_instances"], 2)
        self.assertEqual(summary["retained_instances"], 0)
