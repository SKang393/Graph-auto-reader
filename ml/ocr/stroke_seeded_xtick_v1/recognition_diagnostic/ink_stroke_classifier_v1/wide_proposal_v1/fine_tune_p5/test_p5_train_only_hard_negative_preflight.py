import importlib.util
from pathlib import Path
import sys
import unittest

MODULE_PATH = Path(__file__).with_name("p5_train_only_hard_negative_preflight.py")
SPEC = importlib.util.spec_from_file_location("p5_preflight_test_module", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class P5HardNegativeTests(unittest.TestCase):
    def test_selects_only_negative_training_rows_at_frozen_threshold(self):
        rows = [
            {"image_sha256": "a", "source_group_ids": ["g1"], "label": 0},
            {"image_sha256": "b", "source_group_ids": ["g2"], "label": 1},
            {"image_sha256": "c", "source_group_ids": ["g1"], "label": 0},
        ]
        selected = MODULE.select_hard_negative_rows(rows, [0.8, 0.99, 0.5], 2)
        self.assertEqual([row["image_sha256"] for row in selected], ["a", "c"])
        self.assertTrue(all(row["label"] == 0 for row in selected))

    def test_count_mismatch_fails_closed(self):
        rows = [{"image_sha256": "a", "source_group_ids": ["g"], "label": 1}]
        with self.assertRaisesRegex(MODULE.P5PreflightError, "expected 14"):
            MODULE.select_hard_negative_rows(rows, [0.99], 14)

    def test_malformed_score_fails_closed(self):
        rows = [{"image_sha256": "a", "source_group_ids": ["g"], "label": 0}]
        with self.assertRaisesRegex(MODULE.P5PreflightError, "non-probability"):
            MODULE.select_hard_negative_rows(rows, [1.2], 0)

    def test_rx_binding_rejects_cpu_or_wrong_launcher(self):
        runtime = {"device_name": "AMD Radeon RX 6600 XT", "device": "cuda:0", "cpu_fallback": False}
        MODULE.check_rx6600xt(runtime, sys.executable, sys.executable)
        with self.assertRaisesRegex(MODULE.P5PreflightError, "RX 6600 XT"):
            MODULE.check_rx6600xt({**runtime, "device": "cpu"}, sys.executable, sys.executable)
        with self.assertRaisesRegex(MODULE.P5PreflightError, "RX 6600 XT"):
            MODULE.check_rx6600xt(runtime, sys.executable, sys.executable + ".other")


if __name__ == "__main__":
    unittest.main()
