# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest
from hashlib import sha256

import cv2
import numpy as np

from patches import lattice_near, proposal_patch


class ProposalPatchTests(unittest.TestCase):
    def test_three_planes_match_scalar_runtime_copy_including_padding(self):
        planes = np.ones((3, 20, 21), dtype=np.float32)
        planes[0] = np.arange(420, dtype=np.float32).reshape(20, 21) / 420
        planes[1] = np.float32(0.25)
        planes[2] = np.float32(0.75)
        actual = proposal_patch(planes, 0, 0)
        expected = np.zeros((3, 33, 33), dtype=np.float32)
        for py in range(33):
            for px in range(33):
                x, y = px - 16, py - 16
                if 0 <= x < 21 and 0 <= y < 20:
                    expected[0, py, px] = 1 - planes[0, y, x]
                    expected[1, py, px] = planes[1, y, x]
                    expected[2, py, px] = planes[2, y, x]
        np.testing.assert_array_equal(expected, actual)

    def test_empty_ink_is_not_an_emitted_proposal(self):
        self.assertIsNone(proposal_patch(np.array([np.ones((20, 20)), np.zeros((20, 20)), np.zeros((20, 20))], dtype=np.float32), 4, 4))

    def test_nearby_points_use_four_pixel_lattice(self):
        self.assertEqual([(4, 4), (8, 4), (4, 8), (8, 8)], lattice_near(6, 6, 20, 20))
        with self.assertRaises(ValueError):
            proposal_patch(np.zeros((3, 20, 20), dtype=np.float32), 5, 4)

    def test_matches_existing_csharp_runtime_patch_parity_fixture(self):
        height, width = 40, 48
        y, x = np.indices((height, width))
        planes = np.zeros((3, height, width), dtype=np.float32)
        planes[1] = ((x + 2 * y) % 17).astype(np.float32) / np.float32(16)
        planes[2] = ((3 * x + y) % 19).astype(np.float32) / np.float32(18)
        polygon = np.array([[12, 32], [36, 32], [40, 8], [16, 8]], dtype=np.float32)
        coordinates = [(gx, gy) for gy in range(0, height, 4) for gx in range(0, width, 4)
                       if cv2.pointPolygonTest(polygon, (float(gx), float(gy)), True) >= -16]
        self.assertEqual(118, len(coordinates))
        self.assertEqual("b4734b37d85bd2fda0b667560722d055da487cdf5bbfd7a5e4158a96f14fafb9",
                         sha256(np.asarray(coordinates, dtype="<f4").tobytes()).hexdigest())
        patches = np.stack([proposal_patch(planes, gx, gy) for gx, gy in coordinates])
        self.assertEqual("d5f564e5e002abbf6dbb430f805380d39b1d5bf3ee98c58f2353feda71c331b7",
                         sha256(patches.astype("<f4", copy=False).tobytes()).hexdigest())


if __name__ == "__main__":
    unittest.main()
