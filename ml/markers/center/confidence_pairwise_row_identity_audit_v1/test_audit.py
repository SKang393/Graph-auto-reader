# SPDX-License-Identifier: Apache-2.0
"""Small synthetic-fixture and optional pinned train-metadata checks."""
from __future__ import annotations

import unittest
from unittest.mock import patch

from . import audit


def _record(sample: str, coords: list[list[float]], positive: int, negative: int) -> dict:
    return {
        "split": "train", "sample_id": sample,
        "original_raster_sha256": "1" * 64, "tensor_sha256": "2" * 64,
        "patches_sha256": "3" * 64, "labels_sha256": "4" * 64,
        "selected_proposal_coordinates": coords,
        "positive_rows": positive, "negative_rows": negative,
    }


class RowIdentityAuditTests(unittest.TestCase):
    def test_scene_ranges_preserve_record_and_local_candidate_order(self) -> None:
        records = [_record("a" * 64, [[1, 2], [3, 4]], 1, 1),
                   _record("b" * 64, [[5, 6]], 0, 1)]
        ranges = audit._records_to_ranges(
            "coverage", records, coordinate_ids=True, expected=(3, 1, 2), expected_scenes=2
        )
        self.assertEqual([item["row_start"] for item in ranges], [0, 2])
        self.assertEqual([item["row_count"] for item in ranges], [2, 1])
        self.assertEqual(ranges[0]["candidate_coordinates"], [[1, 2], [3, 4]])
        self.assertEqual(ranges[1]["scene_id"], f"coverage/train/sample-{'b' * 64}")

    def test_nontrain_or_missing_row_identity_fails_closed(self) -> None:
        record = _record("a" * 64, [[1, 2]], 1, 0)
        record["split"] = "dev"
        with self.assertRaisesRegex(ValueError, "non-train"):
            audit._records_to_ranges("coverage", [record], coordinate_ids=True,
                                     expected=(1, 1, 0), expected_scenes=1)
        record = _record("a" * 64, [[1, 2]], 1, 0)
        record["sample_id"] = None
        with self.assertRaisesRegex(ValueError, "sample ID"):
            audit._records_to_ranges("coverage", [record], coordinate_ids=True,
                                     expected=(1, 1, 0), expected_scenes=1)

    def test_actual_pinned_train_metadata_when_available(self) -> None:
        required = [audit.ROOT / path for path in (
            audit.V35_REPORT, audit.V37_REPORT, audit.V26_COVERAGE, audit.V27_SELECTION,
            audit.SHAPE_MANIFEST, audit.SHAPE_ROWS, audit.CROWDED_MANIFEST,
            audit.CROWDED_ROWS, audit.MINED_MANIFEST, audit.MINED_SELECTION,
        )]
        if any(not path.exists() for path in required):
            self.skipTest("pinned local synthetic train metadata is absent")
        report = audit.build_report()
        self.assertEqual(report["status"], "train_identity_reconstructed_from_metadata")
        self.assertEqual(report["identity_row_count"], 276618)
        self.assertEqual(report["feature_group_rows"], {
            "component": 35838, "family": 9053, "coverage": 42747,
            "crowded": 182614, "crowded_mined_negative": 6366,
        })
        self.assertFalse(report["scope"]["development_payloads_opened"])
        self.assertEqual(len(report["groups"]["component"]), 167)
        self.assertEqual(len(report["groups"]["family"]), 28)
        self.assertEqual(len(report["groups"]["coverage"]), 2160)
        self.assertEqual(len(report["groups"]["crowded"]), 3456)
        self.assertEqual(len(report["groups"]["crowded_mined_negative"]), 195)
        mined = {(row["scope"], row["scene_index"]): row
                 for row in report["groups"]["crowded_mined_negative"]}
        for scope in ("component", "family"):
            for row in report["groups"][scope]:
                self.assertEqual(row["scene_tensor_sha256"],
                                 mined[(scope, row["scene_index"])]["scene_tensor_sha256"])


if __name__ == "__main__":
    unittest.main()
