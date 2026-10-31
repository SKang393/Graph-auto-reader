# SPDX-License-Identifier: Apache-2.0
"""Synthetic in-memory renderer producer contract tests."""
import pytest

from tools.MarkerArtifactAssociationV1.association import associate_source
from tools.MarkerArtifactAssociationV1.producer import (
    EXPECTED_NUMPY_VERSION,
    EXPECTED_PILLOW_VERSION,
    produce_legend_symbol_visibility,
    verify_renderer_bundle,
)


def test_fixed_renderer_bundle_and_synthetic_single_glyph_producer():
    bundle = verify_renderer_bundle()
    assert bundle["schema"] == "graphreader.legend-ablation-renderer-bundle.v1"
    assert bundle["pillow_version"] == EXPECTED_PILLOW_VERSION
    assert bundle["numpy_version"] == EXPECTED_NUMPY_VERSION
    assert len(bundle["sources"]) >= 10

    capture = produce_legend_symbol_visibility(seed=20820000, expected_split="train")
    assert capture["split"] == "train"
    assert capture["renderer_bundle"]["sha256"] == bundle["sha256"]
    assert capture["artifact_truth"]["kind"] == "legend_symbol"
    assert capture["visibility_inputs"]["expected_binding"]["target_draw_id"] == capture["suppressed_renderer_call_id"]
    assert capture["visibility_inputs"]["expected_binding"]["matching_draw_ids"] == [capture["suppressed_renderer_call_id"]]
    assert capture["visible_changed_pixels"] > 0
    assert capture["artifact_truth"]["visibility_evidence"]["outside_box_changed_pixel_count"] == 0

    binding = capture["visibility_inputs"]["expected_binding"]
    artifact = capture["artifact_truth"]
    result = associate_source(
        source_id=capture["scene_id"], expected_split="train", capture_status="completed",
        panel_status_counts={"completed": 1, "failed": 0, "not_processed": 0},
        source_width=binding["width"], source_height=binding["height"],
        candidates=[{
            "candidate_id": "synthetic-detector-center",
            "center": artifact["center"],
            "coordinate_space": "original_source_pixels",
            "detector_candidate": True,
            "symbol_input": False,
        }],
        truth_markers=[], artifact_truths=[artifact], generic_overlays=[],
        source_raster_bytes=capture["source_raster_bytes"],
        visibility_inputs_by_truth={artifact["truth_id"]: capture["visibility_inputs"]},
        capture_manifest_key=capture["capture_manifest_key"],
    )
    assert result["candidate_status_counts"] == {"matched_artifact_review_only": 1}
    assert result["artifact_truth_status_counts"] == {"matched_artifact_review_only": 1}
    assert result["negative_training_rows"] == result["trainable_rows"] == 0
    assert result["training_approved"] is False


def test_producer_rejects_nontrain_and_dev_seed_before_rendering():
    with pytest.raises(ValueError, match="train-only"):
        produce_legend_symbol_visibility(seed=20820000, expected_split="dev")
    with pytest.raises(ValueError, match="outside the fixed synthetic train recipe"):
        produce_legend_symbol_visibility(seed=20830000, expected_split="train")
