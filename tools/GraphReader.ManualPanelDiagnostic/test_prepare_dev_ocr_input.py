# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

from prepare_dev_ocr_input import crop_rows


class DevelopmentOcrInputTests(unittest.TestCase):
    @staticmethod
    def parity():
        return {"scope": "train-and-dev-no-label-truth", "truth_read": False,
                "optimizer_steps": 0, "sealed_payload_reads": 0,
                "rows": [{"id": f"dev:dev-proposal-{i:04d}",
                          "image_path": f"artifacts/manual-axis-ocr-diagnostic-v1/dev-axis-capture-v1/images/{i % 15}.png",
                          "image_sha256": f"{i % 15:064x}", "box": [1, 2, 3, 4],
                          "expected_connected": True, "private_label": "not an input"}
                         for i in range(134)]}

    def test_only_crop_identity_and_geometry_are_retained(self):
        rows = crop_rows(self.parity())
        self.assertEqual(len(rows), 134)
        self.assertEqual(set(rows[0]), {"id", "image_path", "image_sha256", "box_xyxy"})

    def test_sealed_or_training_scope_violation_is_rejected(self):
        source = self.parity()
        source["sealed_payload_reads"] = 1
        with self.assertRaises(ValueError):
            crop_rows(source)


if __name__ == "__main__":
    unittest.main()
