# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from score_workflow_crop_trace import compare_case


class WorkflowCropTraceScoreTests(unittest.TestCase):
    def test_reports_crop_difference_and_effective_tall_orientation(self):
        polygon = {"bounds": {"width": 5, "height": 10}}
        detection = {"region_id": "r", "orientation_degrees": 0, "polygon": polygon}
        crop = {"region_id": "r", "width": 320, "height": 48, "crop_sha256": "one",
                "alternatives": [{"text": "8", "confidence": .9}]}
        panel = {"panel_id": "p", "status": "failed", "error": "review", "failure_code": "recalibrate",
                 "centers_before_classification": [], "markers": [],
                 "detection_trace": [{"raw_detector_regions": [detection]}], "recognition_trace": [crop],
                 "ocr_tick_regions": [{"region_id": "r", "text": "8", "polygon": polygon}]}
        trace = {"recognition_traced": True, "truth_read": False, "rows": [{"id": "s", "panels": [panel]}]}
        previous = {"rows": [{"panels": [panel]}]}
        item = {"id": "a", "source_id": "s", "panel_id": "p", "region_id": "r"}
        direct = {"crop_width": 320, "crop_height": 48, "crop_sha256": "two", "text": "18"}
        result = compare_case(item, "18", trace, previous, direct)
        self.assertEqual(result["effective_orientation_degrees"], -90)
        self.assertFalse(result["same_crop_sha256"])
        self.assertTrue(result["direct_exact"])
        self.assertFalse(result["final_workflow_exact"])
        self.assertTrue(result["same_status_and_error"])

    def test_missing_raw_detector_region_is_rejected(self):
        panel = {"panel_id": "p", "detection_trace": [], "recognition_trace": [], "ocr_tick_regions": []}
        trace = {"recognition_traced": True, "truth_read": False, "rows": [{"id": "s", "panels": [panel]}]}
        previous = {"rows": [{"panels": [panel]}]}
        with self.assertRaises(ValueError):
            compare_case({"id": "a", "source_id": "s", "panel_id": "p", "region_id": "r"},
                         "1", trace, previous, {})


if __name__ == "__main__":
    unittest.main()
