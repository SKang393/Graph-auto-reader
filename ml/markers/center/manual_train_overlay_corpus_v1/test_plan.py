# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import hashlib
import unittest

from ml.markers.center.manual_train_overlay_corpus_v1.plan import (
    CorpusPlanError,
    _component_id,
    _components,
    build_plan,
)

MANIFEST_SHA = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57"
PILOT_SHA = "eac5d357a7440537d209e2c22e2af957e06d4edc39e2036ac69b03d427a26243"
LABEL_SCOPE = "curve-point-centers-only; marker-glyph-correspondence-unverified; no-negative-labels"


def _hex(index: int) -> str:
    return hashlib.sha256(f"manual-overlay-corpus-fixture-{index}".encode()).hexdigest()


def _manifest() -> dict:
    rows = []
    for index in range(104):
        study_index = index % 22
        rows.append({
            "caseId": _hex(index),
            "studyId": _hex(1000 + study_index),
            "projectSha256": _hex(2000 + index),
            "embeddedImageSha256": _hex(3000 + study_index),
            "imageWidth": 320,
            "imageHeight": 240,
            "labelingScope": LABEL_SCOPE,
            "curvePointCount": 2,
            "curvePoints": [{"x": 120.0 + index % 10, "y": 110.0}, {"x": 170.0 + index % 10, "y": 140.0}],
        })
    return {
        "schema": "graphreader.manual-train-only-source-capture.v1",
        "status": "completed_train_only_source_capture_unapproved",
        "split": "train",
        "counts": {"train_projects": 104, "train_project_payloads_opened": 104, "development_project_payloads_opened": 0},
        "scope": {"development_payload_reads": 0, "sealed_payload_reads": 0},
        "rows": rows,
    }


def _pilot_plan(rows: list[dict]) -> dict:
    groups = _components(rows)
    return {
        "schema": "graphreader.manual-train-marker-overlay-pilot-plan.v1",
        "source_capture_manifest_sha256": MANIFEST_SHA,
        "sources": [{"component_sha256": _component_id(group)} for group in groups[:2]],
    }


class ExpansionPlanTests(unittest.TestCase):
    def test_components_join_exact_image_across_studies(self) -> None:
        rows = _manifest()["rows"][:]
        rows[0] = dict(rows[0], studyId=_hex(9999), embeddedImageSha256=_hex(9998))
        self.assertEqual(len(_components(rows)), 23)
        rows[0] = dict(rows[0], embeddedImageSha256=rows[1]["embeddedImageSha256"])
        self.assertEqual(len(_components(rows)), 22)

    def test_frozen_plan_excludes_pilot_groups_and_keeps_holdout_disjoint(self) -> None:
        manifest = _manifest()
        pilot = _pilot_plan(manifest["rows"])
        first = build_plan(manifest, MANIFEST_SHA, pilot, PILOT_SHA)
        second = build_plan(manifest, MANIFEST_SHA, pilot, PILOT_SHA)
        self.assertEqual(first, second)
        self.assertEqual(first["source_component_count"], 22)
        self.assertEqual(first["excluded_pilot_component_count"], 2)
        self.assertEqual(len(first["source_rows"]), 20)
        self.assertEqual(first["accounting"]["selected_build"] + first["accounting"]["selected_holdout"], 20)
        self.assertGreater(first["accounting"]["selected_build"], 0)
        self.assertGreater(first["accounting"]["selected_holdout"], 0)
        self.assertEqual(len({row["component_sha256"] for row in first["source_rows"]}), 20)
        self.assertEqual(len({row["image_sha256"] for row in first["source_rows"]}), 20)
        excluded = {row["component_sha256"] for row in pilot["sources"]}
        self.assertTrue(excluded.isdisjoint({row["component_sha256"] for row in first["source_rows"]}))
        self.assertEqual(len(first["recipes"]), 10)
        self.assertEqual(sum(row["label"] == "injected_positive_only" for row in first["recipes"]), 5)
        self.assertEqual(sum(row["label"] == "injected_distractor_only" for row in first["recipes"]), 5)
        self.assertIn("not official dev", first["split_claim"])

    def test_plan_fails_closed_on_changed_train_or_pilot_identity(self) -> None:
        manifest = _manifest()
        pilot = _pilot_plan(manifest["rows"])
        with self.assertRaises(CorpusPlanError):
            build_plan(manifest, "0" * 64, pilot, PILOT_SHA)
        with self.assertRaises(CorpusPlanError):
            build_plan(manifest, MANIFEST_SHA, pilot, "0" * 64)
        manifest["rows"][0]["labelingScope"] = "curve points are marker centers"
        with self.assertRaises(CorpusPlanError):
            build_plan(manifest, MANIFEST_SHA, pilot, PILOT_SHA)


if __name__ == "__main__":
    unittest.main()
