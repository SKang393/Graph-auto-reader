# SPDX-License-Identifier: Apache-2.0
"""Synthetic-only regression for the exact V5 train raster plus overlay."""
from hashlib import sha256

from PIL import Image

from tools.MarkerArtifactAssociationV1.association import associate_source
from tools.MarkerArtifactAssociationV1.full_capture_producer import (
    EXPECTED_CAPTURE,
    produce_v5_train_overlay_glyph_visibility,
    verify_train_capture_bundle,
)


def test_v5_overlay_ablation_reproduces_fixed_capture_and_preserves_overlay():
    bundle = verify_train_capture_bundle()
    assert bundle["train_corpus_builder_binding_sha256"] == (
        "f095bac6f76a9a2d444869ba4d75538e76237c399d3b98997c97292b4cebebd4")

    capture = produce_v5_train_overlay_glyph_visibility(seed=20820000, expected_split="train")
    assert sha256(capture["source_raster_bytes"]).hexdigest() == EXPECTED_CAPTURE["source_raster_sha256"]
    assert capture["source_id"] == "f9b0902a-1f41-7b53-bbef-6c3ff54bc94b"
    assert capture["scene_sha256"] == EXPECTED_CAPTURE["scene_sha256"]
    assert capture["annotation_sha256"] == EXPECTED_CAPTURE["annotation_sha256"]
    assert capture["overlay"]["cue"] is True
    assert capture["overlay"]["center"] == [125, 116]
    assert capture["overlay"]["preserved_pixel_difference_intersection"] is False
    assert capture["artifact_truth"]["glyph_id"] == EXPECTED_CAPTURE["glyph_id"]
    assert capture["suppressed_renderer_call_id"] == "renderer_call:20"
    assert capture["visible_changed_pixels"] == 44

    width, height = EXPECTED_CAPTURE["width"], EXPECTED_CAPTURE["height"]
    original = Image.frombytes("RGB", (width, height), capture["visibility_inputs"]["rendered_rgb"])
    ablated = Image.frombytes("RGB", (width, height), capture["visibility_inputs"]["ablated_rgb"])
    # This fixed cue region is disjoint from the legend glyph and must survive unchanged.
    cue_box = tuple(capture["overlay"]["cue_box"])
    assert original.crop(cue_box).tobytes() == ablated.crop(cue_box).tobytes()

    artifact = capture["artifact_truth"]
    result = associate_source(
        source_id=capture["source_id"], expected_split="train", capture_status="completed",
        panel_status_counts={"completed": 1, "failed": 0, "not_processed": 0},
        source_width=width, source_height=height,
        candidates=[{"candidate_id": "synthetic-fixture-center", "center": artifact["center"],
                     "coordinate_space": "original_source_pixels", "detector_candidate": True,
                     "symbol_input": False}],
        truth_markers=[], artifact_truths=[artifact], generic_overlays=[],
        source_raster_bytes=capture["source_raster_bytes"],
        visibility_inputs_by_truth={artifact["truth_id"]: capture["visibility_inputs"]},
        capture_manifest_key=capture["capture_manifest_key"],
    )
    assert result["candidate_status_counts"] == {"matched_artifact_review_only": 1}
    assert result["artifact_truth_status_counts"] == {"matched_artifact_review_only": 1}
    assert result["negative_training_rows"] == result["trainable_rows"] == 0
    assert result["training_approved"] is False


def test_v5_producer_rejects_wrong_seed_split_and_raster_before_association():
    import pytest

    with pytest.raises(ValueError, match="train-only"):
        produce_v5_train_overlay_glyph_visibility(seed=20820000, expected_split="dev")
    with pytest.raises(ValueError, match="one fixed V5 train capture"):
        produce_v5_train_overlay_glyph_visibility(seed=20820012, expected_split="train")
