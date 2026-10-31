# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import unittest
from hashlib import sha256
from pathlib import Path

import numpy as np

from ml.ocr.gpu_db_head_probe_v1 import conflict_pixel_counterfactual as subject
from ml.ocr.gpu_db_head_probe_v1.conflict_pixel_counterfactual import evaluate
from ml.ocr.official_bakeoff.db_inverse_targets import build_inverse_targets


class ConflictPixelTests(unittest.TestCase):
    def test_overlap_and_two_pixel_margin_remove_shared_support(self):
        built = build_inverse_targets(40, 40, [(3, 3, 9, 9), (6, 3, 12, 9)])
        mask, result = evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("b", "y_tick")])
        self.assertEqual(result["original_contacts_by_kind"], {"overlap": 1})
        self.assertEqual(result["shared_target_pixels_before_margin"], 4)
        self.assertEqual(result["unresolved_original_direct_contacts_by_kind"], {})
        self.assertEqual(result["roles"]["x_tick"]["instances_with_positive_after"], 1)
        self.assertEqual(result["roles"]["y_tick"]["instances_with_positive_after"], 1)
        self.assertEqual(mask[0, 5, 5], 1)
        self.assertEqual(mask[0, 5, 6], 0)
        self.assertEqual(mask[0, 5, 10], 0)
        self.assertEqual(mask[0, 5, 11], 1)

    def test_touch_without_overlap_remains_reported(self):
        built = build_inverse_targets(40, 40, [(3, 3, 9, 9), (7, 3, 13, 9)])
        mask, result = evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("b", "x_tick")])
        self.assertEqual(result["original_contacts_by_kind"], {"touch": 1})
        self.assertEqual(result["unresolved_original_direct_contacts_by_kind"], {"touch": 1})
        self.assertEqual(result["instance_pairs_sharing_retained_connected_component"], 1)
        self.assertEqual(result["excluded_tensor_pixels_including_margin"], 0)
        self.assertTrue(mask.all())

    def test_existing_mask_and_outgrown_clamp_are_retained(self):
        built = build_inverse_targets(40, 40, [(10, 10, 11, 11)])
        original = np.ones_like(built.target)
        original[0, 0, 0] = 0
        mask, result = evaluate(built, original, [("a", "x_tick")])
        np.testing.assert_array_equal(mask, original)
        self.assertEqual(result["outgrown_clamped_instances_not_targeted"], 1)

    def test_duplicate_ids_are_rejected(self):
        built = build_inverse_targets(40, 40, [(3, 3, 9, 9), (6, 3, 12, 9)])
        with self.assertRaisesRegex(ValueError, "identity"):
            evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("a", "y_tick")])

    def test_border_overlap_margin_is_clipped(self):
        built = build_inverse_targets(40, 40, [(0, 0, 1, 1), (1, 0, 2, 1)])
        mask, result = evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("b", "y_tick")])
        self.assertEqual(result["original_contacts_by_kind"], {"overlap": 1})
        self.assertEqual(mask[0, 0, 0], 0)
        self.assertEqual(mask[0, 5, 5], 0)
        self.assertEqual(mask[0, 6, 6], 1)
        self.assertEqual(mask[0, 39, 39], 1)

    def test_triple_overlap_counts_unique_shared_pixels_and_combines_margins(self):
        built = build_inverse_targets(40, 40, [(3, 3, 9, 9), (4, 3, 10, 9), (5, 3, 11, 9)])
        mask, result = evaluate(built, np.ones_like(built.target), [("a", "x_tick"), ("b", "y_tick"), ("c", "legend")])
        self.assertEqual(result["original_contacts_by_kind"], {"overlap": 3})
        coverage = np.zeros((40, 40), dtype=np.uint8)
        for region in built.regions:
            left, top, right, bottom = region.raster_contour_box
            coverage[top:bottom + 1, left:right + 1] += 1
        self.assertEqual(result["shared_target_pixels_before_margin"], int(np.count_nonzero(coverage >= 2)))
        # The pairwise overlap rectangles are [6..8], [7..8], and [7..9] in x,
        # and [5..8] in y, so their two-pixel margins form [4..11] x [3..10].
        expected = np.ones((40, 40), dtype=np.uint8)
        expected[3:11, 4:12] = 0
        np.testing.assert_array_equal(mask[0], expected)

    def test_dependency_metadata_mismatches_fail_closed(self):
        target_sha = sha256(Path(subject.targets.__file__).read_bytes()).hexdigest()
        script_sha = sha256(Path(subject.target_geometry_audit.__file__).read_bytes()).hexdigest()
        prior = {"opencv_version": subject.cv2.__version__, "dependency_source_sha256": {"inverse_targets": target_sha}}
        audit = {"opencv_version": subject.cv2.__version__, "preflight_report_sha256": subject.tensor_parity.PREFLIGHT_SHA,
                 "inverse_targets_source_sha256": target_sha, "script_sha256": script_sha}
        self.assertEqual(subject._validate_dependencies(prior, audit), target_sha)
        for field in ("preflight_report_sha256", "inverse_targets_source_sha256", "opencv_version", "script_sha256"):
            wrong = dict(audit)
            wrong[field] = "incorrect"
            with self.subTest(field=field), self.assertRaises(ValueError):
                subject._validate_dependencies(prior, wrong)
        wrong_prior = {"opencv_version": subject.cv2.__version__, "dependency_source_sha256": {"inverse_targets": "incorrect"}}
        with self.assertRaisesRegex(ValueError, "Preflight target source"):
            subject._validate_dependencies(wrong_prior, audit)
        wrong_prior = {"opencv_version": "incorrect", "dependency_source_sha256": {"inverse_targets": target_sha}}
        with self.assertRaisesRegex(ValueError, "OpenCV version"):
            subject._validate_dependencies(wrong_prior, audit)
