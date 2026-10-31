# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Focused synthetic fixtures for candidate label curation."""
from __future__ import annotations

import json
import hashlib
import math
import unittest

from ml.markers.candidate_label_curation_v1.curator import (
    CANDIDATE_STATUSES,
    INPUT_PATHS,
    ROOT,
    SOURCE_PATHS,
    _apply_cross_source_tensor_conflicts,
    _mutual_unique_links,
    _marker_truth_rows,
    _train_corpus_scene_sha256,
    curate_source,
    derive_family_id,
    family_split_plan,
    CROP_BILINEAR_SUPPORT_MARGIN_PX,
    _intersects_half_extent,
    _validate_overlay_record,
    write_new_report,
)


def h(char: str) -> str:
    return char * 64


def source_fixture() -> dict:
    return {
        "source_id": "synthetic-source-a",
        "scene_id": "scene-a",
        "scene_sha256": h("a"),
        "image_sha256": h("b"),
        "panel_id": "panel-a",
        "panel_result_sha256": h("c"),
        "family_id": h("d"),
        "source_split": "train",
        "markers": [{"truth_id": "marker-a", "kind": "connected", "center": [10, 10]}],
        "glyphs": [{
            "glyph_id": "glyph-a", "center": [30, 10],
            "association_status": "matched_artifact_review_only",
            "visibility_proof_sha256": h("e"),
            "rendered_rgb_sha256": h("f"),
            "ablated_rgb_sha256": h("0"),
            "visible_changed_pixels": 8,
        }],
        "overlay_center": [100, 100],
        "overlay_boxes": [],
        "candidates": [
            {"candidate_id": "candidate-marker", "center": [10.5, 10], "radius": 1,
             "source_status": "review_positive_connected", "truth_id": "marker-a",
             "glyph_id": None, "visibility_proof_sha256": None,
             "local_tensor_sha256": h("1"), "wide_tensor_sha256": h("2")},
            {"candidate_id": "candidate-glyph", "center": [30.5, 10], "radius": 1,
             "source_status": "review_visible_legend_glyph", "truth_id": None,
             "glyph_id": "glyph-a", "visibility_proof_sha256": h("e"),
             "local_tensor_sha256": h("3"), "wide_tensor_sha256": h("4")},
            {"candidate_id": "candidate-unknown", "center": [60, 60], "radius": 1,
             "source_status": "unknown_unmatched", "truth_id": None,
             "glyph_id": None, "visibility_proof_sha256": None,
             "local_tensor_sha256": h("5"), "wide_tensor_sha256": h("6")},
            {"candidate_id": "candidate-overlay", "center": [70, 70], "radius": 1,
             "source_status": "excluded_generic_overlay", "truth_id": None,
             "glyph_id": None, "visibility_proof_sha256": None,
             "local_tensor_sha256": h("7"), "wide_tensor_sha256": h("8")},
        ],
    }


class CurationTests(unittest.TestCase):
    def test_marker_foreign_key_preserves_classifier_typed_id(self) -> None:
        records = [{"id": "marker:marker-uuid", "source_id": "marker-uuid",
                    "center": [25.0, 14.5], "bin": "isolated_positive"}]
        self.assertEqual(_marker_truth_rows(records), [{
            "truth_id": "marker:marker-uuid", "center": [25.0, 14.5], "kind": "isolated",
        }])

    def test_scene_identity_digest_matches_train_builder_unicode_json(self) -> None:
        scene = {"scene_id": "synthetic", "font": "Noto Sans μ", "items": [1, 2]}
        corpus_bytes = json.dumps(scene, sort_keys=True, separators=(",", ":"),
                                  allow_nan=False).encode("utf-8")
        self.assertEqual(_train_corpus_scene_sha256(scene), hashlib.sha256(corpus_bytes).hexdigest())
        self.assertNotEqual(_train_corpus_scene_sha256(scene), hashlib.sha256(
            json.dumps(scene, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
                        allow_nan=False).encode("utf-8")).hexdigest())

    def test_mutual_unique_is_two_sided_and_uses_frozen_tolerance(self) -> None:
        candidates = [{"center": [10, 10]}, {"center": [11, 10]}]
        refs = [{"center": [10, 10]}]
        candidate_links, reverse = _mutual_unique_links(candidates, refs)
        self.assertEqual(candidate_links, [[0], [0]])
        self.assertEqual(reverse, [[0, 1]])

    def test_positive_visible_glyph_and_unknown_overlay_are_separated(self) -> None:
        result = curate_source(source_fixture())
        rows = {row["candidate_id"]: row for row in result["rows"]}
        self.assertEqual(rows["candidate-marker"]["proposed_label"], "plot_marker")
        self.assertEqual(rows["candidate-glyph"]["proposed_label"], "visible_legend_symbol")
        self.assertIsNone(rows["candidate-unknown"]["proposed_label"])
        self.assertIn("unknown_is_not_a_negative_label", rows["candidate-unknown"]["label_evidence"])
        self.assertIsNone(rows["candidate-overlay"]["proposed_label"])
        self.assertIn("excluded_generic_overlay_is_excluded_by_source_review",
                      rows["candidate-overlay"]["collision_reasons"])

    def test_ambiguous_marker_glyph_collision_is_never_labeled(self) -> None:
        source = source_fixture()
        source["glyphs"][0]["center"] = [11, 10]
        result = curate_source(source)
        marker = next(row for row in result["rows"] if row["candidate_id"] == "candidate-marker")
        self.assertIsNone(marker["proposed_label"])
        self.assertIn("marker_glyph_center_collision", marker["collision_reasons"])

    def test_duplicate_candidates_cannot_share_marker_truth(self) -> None:
        source = source_fixture()
        duplicate = dict(source["candidates"][0])
        duplicate.update(candidate_id="candidate-marker-duplicate", local_tensor_sha256=h("9"),
                         wide_tensor_sha256=h("a"))
        source["candidates"].append(duplicate)
        result = curate_source(source)
        rows = {row["candidate_id"]: row for row in result["rows"]}
        for key in ("candidate-marker", "candidate-marker-duplicate"):
            self.assertIsNone(rows[key]["proposed_label"])
            self.assertIn("multiple_candidates_share_marker_truth", rows[key]["collision_reasons"])

    def test_visibility_proof_must_have_pixels_and_distinct_render(self) -> None:
        source = source_fixture()
        glyph = source["glyphs"][0]
        glyph["visible_changed_pixels"] = 0
        result = curate_source(source)
        row = next(row for row in result["rows"] if row["candidate_id"] == "candidate-glyph")
        self.assertIsNone(row["proposed_label"])
        self.assertIn("glyph_ablation_identity_or_visibility_proof_mismatch", row["collision_reasons"])

    def test_any_visible_glyph_collision_blocks_positive_even_if_glyph_unmatched(self) -> None:
        source = source_fixture()
        source["glyphs"][0]["center"] = [11, 10]
        source["glyphs"][0]["association_status"] = "unmatched_review_only"
        result = curate_source(source)
        row = next(row for row in result["rows"] if row["candidate_id"] == "candidate-marker")
        self.assertIsNone(row["proposed_label"])
        self.assertIn("marker_glyph_center_collision", row["collision_reasons"])

    def test_overlay_crop_contamination_blocks_positive(self) -> None:
        source = source_fixture()
        source["overlay_center"] = [20, 10]
        result = curate_source(source)
        row = next(row for row in result["rows"] if row["candidate_id"] == "candidate-marker")
        self.assertIsNone(row["proposed_label"])
        self.assertTrue(any(reason.startswith("wide_crop_intersects_synthetic_overlay")
                            for reason in row["collision_reasons"]))

    def test_fractional_sampler_boundary_includes_bilinear_neighbor_pixel(self) -> None:
        center_x, half = 10.375, 12.0
        last_sample = center_x - half + half / 32 + 31 * (2 * half / 32)
        self.assertEqual(last_sample, 22.0)
        bilinear_neighbor = math.floor(last_sample) + 1
        self.assertEqual(bilinear_neighbor, 23)
        cue = (22.6, 9.0, 23.4, 11.0)
        self.assertFalse(_intersects_half_extent([center_x, 10], half, cue))
        self.assertTrue(_intersects_half_extent([center_x, 10],
                                                half + CROP_BILINEAR_SUPPORT_MARGIN_PX, cue))

    def test_overlay_cue_crop_contamination_blocks_positive(self) -> None:
        source = source_fixture()
        source["overlay_boxes"] = [[8, 8, 14, 14]]
        result = curate_source(source)
        row = next(row for row in result["rows"] if row["candidate_id"] == "candidate-marker")
        self.assertIsNone(row["proposed_label"])
        self.assertIn("wide_crop_intersects_synthetic_overlay_cue", row["collision_reasons"])

    def test_nontrain_source_is_rejected(self) -> None:
        source = source_fixture()
        source["source_split"] = "dev"
        with self.assertRaisesRegex(ValueError, "fixed train split"):
            curate_source(source)

    def test_duplicate_annotation_truth_ids_fail_closed(self) -> None:
        source = source_fixture()
        source["markers"].append(dict(source["markers"][0]))
        with self.assertRaisesRegex(ValueError, "marker truth IDs"):
            curate_source(source)

    def test_duplicate_exact_two_view_pair_is_removed_across_sources(self) -> None:
        first = curate_source(source_fixture())
        second_source = source_fixture()
        second_source["source_id"] = "synthetic-source-b"
        second_source["scene_id"] = "scene-b"
        second_source["family_id"] = h("9")
        second = curate_source(second_source)
        first["rows"][0]["local_tensor_sha256"] = second["rows"][1]["local_tensor_sha256"]
        first["rows"][0]["wide_tensor_sha256"] = second["rows"][1]["wide_tensor_sha256"]
        count = _apply_cross_source_tensor_conflicts([first, second])
        self.assertEqual(count, 3)
        self.assertIsNone(first["rows"][0]["proposed_label"])
        self.assertIsNone(first["rows"][1]["proposed_label"])
        self.assertIsNone(second["rows"][1]["proposed_label"])

    def test_labeled_pair_repeated_by_unknown_candidate_is_ambiguous(self) -> None:
        source = source_fixture()
        source["candidates"][2]["local_tensor_sha256"] = source["candidates"][0]["local_tensor_sha256"]
        source["candidates"][2]["wide_tensor_sha256"] = source["candidates"][0]["wide_tensor_sha256"]
        result = curate_source(source)
        marker = next(row for row in result["rows"] if row["candidate_id"] == "candidate-marker")
        self.assertIsNone(marker["proposed_label"])

    def test_shared_local_tensor_with_different_wide_context_is_not_duplicate(self) -> None:
        source = source_fixture()
        source["candidates"][2]["local_tensor_sha256"] = source["candidates"][0]["local_tensor_sha256"]
        result = curate_source(source)
        marker = next(row for row in result["rows"] if row["candidate_id"] == "candidate-marker")
        self.assertEqual(marker["proposed_label"], "plot_marker")

    def test_family_split_refuses_recipe_independence_claims(self) -> None:
        one = curate_source(source_fixture())
        blocked = family_split_plan([one])
        self.assertEqual(blocked["status"], "blocked_no_assigned_validation_partition")
        self.assertEqual(blocked["training_eligible_rows"], 0)
        self.assertEqual(blocked["assigned_train_family_ids"], [])
        self.assertEqual(blocked["assigned_validation_family_ids"], [])
        self.assertTrue(all(not row["training_eligible"] and row["assigned_partition"] is None
                            for row in one["rows"]))

    def test_same_scene_group_cannot_cross_family_splits(self) -> None:
        first = curate_source(source_fixture())
        second_fixture = source_fixture()
        second_fixture["source_id"] = "another-source-same-scene"
        second_fixture["family_id"] = h("9")
        second = curate_source(second_fixture)
        with self.assertRaisesRegex(ValueError, "scene-level source group spans"):
            family_split_plan([first, second])

    def test_forged_family_relabels_only_assign_review_partition_never_eligibility(self) -> None:
        sources = []
        for index, family in enumerate((h("a"), h("b"), h("c"), h("d"), h("e"))):
            source = source_fixture()
            source["source_id"] = f"source-{index}"
            source["scene_id"] = f"scene-{index}"
            source["family_id"] = family
            curated = curate_source(source)
            sources.append(curated)
        result = family_split_plan(sources)
        self.assertEqual(result["status"], "assigned_signature_partition_review_only")
        self.assertFalse(result["independence_verified"])
        self.assertFalse(set(result["assigned_train_family_ids"]) & set(result["assigned_validation_family_ids"]))
        self.assertEqual(result["training_eligible_rows"], 0)
        self.assertTrue(all(not row["training_eligible"] and
                            row["assigned_partition"] == ("validation" if source["family_id"] in result["assigned_validation_family_ids"] else "train")
                            for source in sources for row in source["rows"]))

    def test_overlay_visibility_box_contract_fails_closed(self) -> None:
        base = {"schema": "marker-context-inplot-overlay-v1", "split": "train",
                "scene_id": "scene-a", "scene_sha256": h("a"), "panel_id": "panel-a",
                "status": "review_only", "materialized": True,
                "optimization_allowed": False, "semantic_non_data": True,
                "center": [20, 20], "cue_visible": True, "cue_text": "note",
                "cue_box": [28, 13, 32, 19]}
        center, boxes = _validate_overlay_record(base, scene_id="scene-a", scene_sha256=h("a"),
                                                  scene_panel_ids={"panel-a"}, width=100, height=100)
        self.assertEqual(center, [20.0, 20.0])
        self.assertEqual(boxes, [[28.0, 13.0, 32.0, 19.0]])
        invisible = {**base, "cue_visible": False, "cue_text": None, "cue_box": None}
        self.assertEqual(_validate_overlay_record(invisible, scene_id="scene-a", scene_sha256=h("a"),
                                                  scene_panel_ids={"panel-a"}, width=100, height=100),
                         ([20.0, 20.0], []))
        for invalid in (
            {**base, "cue_visible": 1},
            {**base, "cue_box": None},
            {**base, "cue_box": [98, 13, 102, 19]},
            {**base, "cue_visible": False},
            {**base, "cue_visible": False, "cue_text": None, "cue_box": [28, 13, 32, 19]},
        ):
            with self.subTest(invalid=invalid):
                with self.assertRaises(ValueError):
                    _validate_overlay_record(invalid, scene_id="scene-a", scene_sha256=h("a"),
                                             scene_panel_ids={"panel-a"}, width=100, height=100)

    def test_every_emitted_unknown_or_excluded_status_stays_unlabeled(self) -> None:
        statuses = (
            "excluded_symbol_input", "unknown_capture_incomplete",
            "unknown_duplicate_candidate_center", "unknown_ambiguous_collision",
            "unknown_unmatched_visible_glyph", "unknown_unmatched",
        )
        for status in statuses:
            source = source_fixture()
            candidate = source["candidates"][2]
            candidate["source_status"] = status
            candidate["center"] = [10.5, 10]
            result = curate_source(source)
            row = next(item for item in result["rows"] if item["candidate_id"] == "candidate-unknown")
            self.assertIsNone(row["proposed_label"], status)
            self.assertFalse(row["training_eligible"], status)

    def test_family_signature_ignores_recipe_cell_but_binds_renderer_choices(self) -> None:
        a = {"families": {key: {"key": value} for key, value in
                           {"renderer": "vector", "font": "sans", "marker": "circle"}.items()},
             "recipe_cell": 0}
        b = {"families": {key: {"key": value} for key, value in
                           {"renderer": "vector", "font": "sans", "marker": "circle"}.items()},
             "recipe_cell": 15}
        c = {"families": {key: {"key": value} for key, value in
                           {"renderer": "camera", "font": "serif", "marker": "square"}.items()},
             "recipe_cell": 15}
        self.assertEqual(derive_family_id(a), derive_family_id(b))
        self.assertNotEqual(derive_family_id(a), derive_family_id(c))

    def test_protocol_and_fixed_project_paths_are_present(self) -> None:
        protocol_path = ROOT / "ml/markers/candidate_label_curation_v1/curation_protocol-v1.json"
        protocol = json.loads(protocol_path.read_text(encoding="utf-8"))
        self.assertEqual(protocol["scope"], "synthetic_train_only_no_training")
        self.assertEqual(set(protocol["candidate_statuses"]), set(CANDIDATE_STATUSES))
        self.assertIn("ensure_ascii=true", protocol["scene_identity_digest"])
        self.assertIn("marker:<marker_uuid>", protocol["annotation_foreign_keys"])
        self.assertIs(protocol["training_approved"], False)
        self.assertIs(protocol["dev_input_supplied"], False)
        self.assertEqual(set(INPUT_PATHS), {
            "batch_report", "batch_receipt", "complete_receipt", "identity_metadata",
            "raster_manifest", "producer_binding", "capture_report", "truth_sidecar",
            "artifact_report", "artifact_receipt",
        })
        for relative in SOURCE_PATHS.values():
            self.assertTrue((ROOT / relative).is_file(), relative)
        binding_schema = json.loads((ROOT / "ml/markers/candidate_label_curation_v1/input-binding.schema.json").read_text(encoding="utf-8"))
        self.assertEqual(binding_schema["properties"]["schema"]["const"],
                         "graphreader.marker-label-curation-input-binding.v1")
        self.assertEqual(set(binding_schema["properties"]["inputs"]["properties"]), set(INPUT_PATHS))
        self.assertEqual(set(binding_schema["properties"]["source_bindings"]["properties"]),
                         set(SOURCE_PATHS))

    def test_writer_rejects_nonfixed_output_before_writing(self) -> None:
        with self.assertRaisesRegex(ValueError, "fixed fresh destination"):
            write_new_report(ROOT / "artifacts/goal22-runs/other", {"status": "blocked"})


if __name__ == "__main__":
    unittest.main()
