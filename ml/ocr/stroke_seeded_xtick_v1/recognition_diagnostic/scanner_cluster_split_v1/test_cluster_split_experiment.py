# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

import unittest
from pathlib import Path

from PIL import Image, ImageDraw

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_cluster_split_v1 import cluster_split_experiment as exp
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_train_miss_v1 import scan_train_miss as scan


class ClusterSplitExperimentTests(unittest.TestCase):
    def test_new_split_preserves_components_and_excludes_prior_holdouts(self):
        components = [["a", "a-dup"], ["old-val", "old-val-linked"], ["b"], ["c"], ["d"], ["e"], ["f"]]
        fit = {source for group in components for source in group}
        counts = {source: 8 for source in fit}
        train, validation, groups = exp.choose_groups(components, fit, {"old-val"}, counts)
        self.assertGreaterEqual(len(groups), 3)
        self.assertFalse(set(train) & set(validation))
        self.assertTrue(any(set(group) == {"a", "a-dup"} for group in groups) or
                        not ({"a", "a-dup"} & set(validation)))
        self.assertNotIn("old-val", train + validation)
        self.assertNotIn("old-val-linked", train + validation)

    def test_support_valley_splits_wide_profile_without_truth(self):
        counts = [4, 4, 3, 4, 6, 6, 3, 4, 4, 4]
        hits = [{"along": index + 4, "dark_count": value, "longest_run": 3}
                for index, value in enumerate(counts)]
        segments = exp.split_wide_cluster(hits, min_prominence=1)
        self.assertGreaterEqual(len(segments), 2)
        self.assertTrue(all(segment[-1]["along"] - segment[0]["along"] + 1 <= exp.WIDTH
                            for segment in segments))

    def test_flat_wide_profile_is_not_fabricated_into_proposals(self):
        hits = [{"along": index + 4, "dark_count": 4, "longest_run": 3} for index in range(10)]
        self.assertEqual([], exp.split_wide_cluster(hits, min_prominence=1))

    def test_width8_trace_preserves_frozen_pixel_scan(self):
        image = Image.new("L", (100, 60), 255)
        draw = ImageDraw.Draw(image)
        draw.line((10, 40, 90, 40), fill=0)
        draw.line((40, 35, 40, 45), fill=0)
        actual = scan.scan_panel(image, [10, 40, 90, 40], [0, 0, 100, 60], exp.WIDTH)
        traced, _ = exp.trace_proposals(image, [10, 40, 90, 40], [0, 0, 100, 60], None)
        self.assertEqual(actual, traced)
        epsilon = [dict(actual[0], center=[actual[0]["center"][0] + 1e-8, actual[0]["center"][1]])]
        self.assertTrue(exp.same_proposals(actual[:1], epsilon))

    def test_new_runner_has_no_consumed_dev_or_private_inputs(self):
        source = Path(exp.__file__).read_text(encoding="utf-8")
        self.assertNotIn("stroke-seeded-xtick-v1-p2-probe-corpus-v3", source)
        self.assertNotIn("sealed_manifest", source)
        self.assertIn("validation_truth_blind_proposals.json", source)


if __name__ == "__main__":
    unittest.main()
