import unittest

from PIL import Image

from ml.ocr.stroke_seeded_xtick_v1 import materialize
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_p2_filter_v1.run_ink_p2_filter import (
    crop_box_to_original, filter_proposal,
)


class InkP2FilterTests(unittest.TestCase):
    def test_thresholds_are_fixed_and_top_score_is_required(self):
        self.assertTrue(filter_proposal(0.25, 0.25))
        self.assertFalse(filter_proposal(0.249, 0.25))
        self.assertTrue(filter_proposal(0.5, 0.5))
        self.assertFalse(filter_proposal(None, 0.25))
        with self.assertRaises(ValueError):
            filter_proposal(0.99, 0.4)

    def test_detector_box_inverse_maps_to_source_pixel_frame(self):
        image = Image.new("L", (300, 200), 255)
        _, transform = materialize._crop_and_resize(image, 100, 50)
        source_box = (80.0, 55.0, 90.0, 65.0)
        crop_box = materialize._map_box(source_box, transform)
        self.assertIsNotNone(crop_box)
        normalized = tuple(crop_box[i] / (256.0 if i % 2 == 0 else 96.0) for i in range(4))
        restored = crop_box_to_original(normalized, transform)
        self.assertIsNotNone(restored)
        for actual, expected in zip(restored, source_box):
            self.assertAlmostEqual(actual, expected, places=6)

    def test_padding_only_box_is_not_mapped_to_source(self):
        image = Image.new("L", (300, 200), 255)
        _, transform = materialize._crop_and_resize(image, 100, 50)
        self.assertIsNone(crop_box_to_original((0.8, 0.1, 0.95, 0.3), transform))

    def test_scanner_transport_has_no_truth_or_annotation_fields(self):
        image = Image.new("RGB", (100, 60), "white")
        source = {"source_id": "synthetic-source", "source_pixel_sha256": "0" * 64}
        panel = {"panel_id": "00000000-0000-0000-0000-000000000001", "box": [5, 4, 80, 40]}
        payload = common.build_source_input(source, [], [panel], image)
        self.assertEqual(set(payload), {"source_id", "expected_source_pixel_sha256", "width", "height",
                                        "image_png_base64", "image_png_sha256", "panels"})
        self.assertNotIn("truth", str(payload).casefold())
        self.assertNotIn("annotation", str(payload).casefold())


if __name__ == "__main__":
    unittest.main()
