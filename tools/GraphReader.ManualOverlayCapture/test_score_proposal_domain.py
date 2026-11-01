# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest

import numpy as np

from score_proposal_domain import emitted_centers, summarize_patch


class ProposalDomainTests(unittest.TestCase):
    def test_runtime_emission_is_separate_from_ink_support(self):
        planes = np.zeros((3, 40, 40), dtype=np.float32)
        planes[0] = 1
        planes[0, 20, 20] = 0
        emitted = summarize_patch(planes, 20, 20, (0, 0, 40, 40), {(20, 20)})
        withheld = summarize_patch(planes, 20, 20, (0, 0, 40, 40), set())
        self.assertEqual((1, 1), (emitted["ink_supported_positions"], emitted["emitted_positions"]))
        self.assertEqual(1, withheld["supported_not_emitted"])
        self.assertEqual(0, withheld["emitted_positions"])

    def test_rejects_emission_without_ink_and_patch_outside_controlled_area(self):
        planes = np.zeros((3, 40, 40), dtype=np.float32)
        planes[0] = 1
        with self.assertRaisesRegex(ValueError, "without mirrored ink support"):
            summarize_patch(planes, 20, 20, (0, 0, 40, 40), {(20, 20)})
        with self.assertRaisesRegex(ValueError, "escapes controlled region"):
            summarize_patch(planes, 4, 4, (0, 0, 40, 40), set())

    def test_rejects_duplicate_and_out_of_bounds_runtime_centers(self):
        point = {"x": 20, "y": 20, "is_finite": True}
        stage = {"schema": "graphreader.manual-marker-stage-evidence.v1",
                 "emitted_proposals": [point, point], "counters": {"emitted_proposals": 2}}
        with self.assertRaisesRegex(ValueError, "duplicate emitted proposal"):
            emitted_centers(stage, 40, 40)
        stage["emitted_proposals"] = [{"x": 40, "y": 20, "is_finite": True}]
        stage["counters"]["emitted_proposals"] = 1
        with self.assertRaisesRegex(ValueError, "invalid or duplicate"):
            emitted_centers(stage, 40, 40)


if __name__ == "__main__":
    unittest.main()
