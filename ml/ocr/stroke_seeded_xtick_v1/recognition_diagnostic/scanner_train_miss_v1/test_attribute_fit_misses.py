# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest
from pathlib import Path

from PIL import Image, ImageDraw

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_train_miss_v1 import attribute_fit_misses as attr
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_train_miss_v1 import scan_train_miss as scan


class FitMissAttributionTests(unittest.TestCase):
    def test_detailed_default_scan_matches_frozen_pixel_scan(self):
        image = Image.new("L", (100, 60), 255)
        draw = ImageDraw.Draw(image)
        draw.line((10, 40, 90, 40), fill=0)
        draw.line((40, 35, 40, 45), fill=0)
        detailed, clusters = attr.scan_with_rejections(image, [10, 40, 90, 40], [0, 0, 100, 60], 8)
        frozen = scan.scan_panel(image, [10, 40, 90, 40], [0, 0, 100, 60], 8)
        self.assertEqual(frozen, detailed)
        self.assertEqual(["accepted"], [row["reason"] for row in clusters])

    def test_oversized_cluster_is_attributed_to_width_gate(self):
        truth = [{"tick_id": "t", "stroke_center_original_xy": [40.0, 40.0],
            "stroke_line_original_xyxy": [40.0, 36.0, 40.0, 44.0],
            "expected_tick_label": "1", "visible": True,
            "ground_truth_target_unambiguous": True}]
        clusters = [{"along_start": 29, "along_end": 31, "along_center": 30,
                     "width": 10, "reason": "cluster_width_exceeded"}]
        misses = attr.attribute_misses(truth, [], clusters, [10, 40, 90, 40], False,
                                       Image.new("L", (100, 60), 255), [0, 0, 100, 60])
        self.assertEqual(1, len(misses))
        self.assertEqual("cluster_width_exceeded", misses[0]["closest_stage_reason"])

    def test_fit_only_inputs_do_not_name_consumed_probe_or_dev_corpus(self):
        source = Path(attr.__file__).read_text(encoding="utf-8")
        self.assertNotIn("stroke-seeded-xtick-v1-p2-probe-corpus-v3", source)
        self.assertIn("fit_truth_blind_proposals.json", source)


if __name__ == "__main__":
    unittest.main()
