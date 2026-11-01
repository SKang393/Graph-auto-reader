# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest
import gzip
from pathlib import Path
import struct
import tempfile

import numpy as np

from score import RUN, digest
from stage_score import any_near, local_pattern, read_attribution


class StageScoreTests(unittest.TestCase):
    def test_nearby_stage_checks_use_the_expected_pixel_radius(self):
        points = np.array([[4, 3], [20, 20]], dtype=np.float64)
        self.assertTrue(any_near(points, 0, 0, 5))
        self.assertFalse(any_near(points, 0, 0, 4.99))
        self.assertFalse(any_near(np.empty((0, 2)), 0, 0, 5))

    def test_stage_patterns_do_not_require_nearby_grid_to_precede_decoded_point(self):
        self.assertEqual("decoded_without_pre_nms_nearby", local_pattern(False, True, False, False))
        self.assertEqual("pre_nms_without_final_nearby", local_pattern(True, True, True, False))
        self.assertEqual("final_nearby", local_pattern(False, False, False, True))
        self.assertEqual("no_emitted_proposal_nearby", local_pattern(False, False, False, False))

    def test_attribution_must_reproduce_the_merged_mask(self):
        with tempfile.TemporaryDirectory(dir=RUN) as temporary:
            path = Path(temporary) / "fixture.gz"
            seed = np.array([[0, 1], [0, 0]], dtype="<f4")
            residual = np.array([[0, 0], [0.5, 0]], dtype="<f4")
            bits = np.array([[0, 0], [8, 0]], dtype=np.uint8)
            with gzip.open(path, "wb") as stream:
                stream.write(struct.pack("<III", 0x314D5247, 2, 2))
                stream.write(seed.tobytes())
                stream.write(residual.tobytes())
                stream.write(bits.tobytes())
            planes = np.zeros((3, 2, 2), dtype=np.float32)
            planes[2] = np.maximum(seed, residual)
            actual_seed, actual_residual, actual_bits = read_attribution(path, digest(path), planes)
            np.testing.assert_array_equal(seed, actual_seed)
            np.testing.assert_array_equal(residual, actual_residual)
            np.testing.assert_array_equal(bits, actual_bits)
            planes[2, 0, 0] = 1
            with self.assertRaises(ValueError):
                read_attribution(path, digest(path), planes)


if __name__ == "__main__":
    unittest.main()
