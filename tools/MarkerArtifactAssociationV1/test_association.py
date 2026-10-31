# SPDX-License-Identifier: Apache-2.0
"""Synthetic-only contract tests; no corpus, annotation, or model reads."""
import hashlib
import json
import struct
import zlib
from copy import deepcopy

import pytest

from tools.MarkerArtifactAssociationV1.association import (
    associate_full,
    associate_source,
    build_visibility_evidence,
)
from tools.MarkerArtifactAssociationV1.fixture_support import train_fixture


def sha(value):
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def png(width, height, rgb=(120, 130, 140)):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    scanlines = b"".join(b"\x00" + bytes(rgb) * width for _ in range(height))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) +
            chunk(b"IDAT", zlib.compress(scanlines)) + chunk(b"IEND", b""))


SOURCE_RASTER = png(8, 8)
CAPTURE = train_fixture()


def visibility(scene=None, annotation=None, reference="legend:0", glyph_id="glyph-0",
               center=(3, 3), box=(2, 2, 2, 2)):
    rendered = bytearray([255] * (8 * 8 * 3))
    ablated = bytearray(rendered)
    offset = (3 * 8 + 3) * 3
    rendered[offset:offset + 3] = b"\x00\x00\x00"
    binding = {
        "scene_sha256": scene or sha("scene"),
        "annotation_sha256": annotation or sha("annotation"),
        "source_reference": reference,
        "glyph_id": glyph_id,
        "center": list(center),
        "glyph_box": list(box),
        "width": 8,
        "height": 8,
        "source_raster_sha256": hashlib.sha256(SOURCE_RASTER).hexdigest(),
        "rendered_rgb_sha256": hashlib.sha256(rendered).hexdigest(),
        "renderer_bundle_sha256": sha("synthetic renderer bundle"),
        "target_draw_id": f"renderer_call:synthetic:{glyph_id}",
        "matching_draw_ids": [f"renderer_call:synthetic:{glyph_id}"],
    }
    inputs = {"expected_binding": binding, "rendered_rgb": bytes(rendered), "ablated_rgb": bytes(ablated)}
    evidence = build_visibility_evidence(
        source_raster_bytes=SOURCE_RASTER,
        rendered_rgb=inputs["rendered_rgb"],
        ablated_rgb=inputs["ablated_rgb"],
        binding=inputs["expected_binding"],
    )
    return evidence, inputs


def artifact():
    item = deepcopy(CAPTURE["artifact_truth"])
    item["_visibility_inputs"] = deepcopy(CAPTURE["visibility_inputs"])
    return item


def candidate(candidate_id, center, *, detector=True, symbol=False):
    return {
        "candidate_id": candidate_id,
        "center": list(center),
        "coordinate_space": "original_source_pixels",
        "detector_candidate": detector,
        "symbol_input": symbol,
    }


def run(candidates, markers=(), artifacts=(), overlays=(), *, status="completed", panel_counts=None, split="train"):
    artifact_items = []
    visibility_inputs = {}
    for artifact_item in artifacts:
        item = dict(artifact_item)
        inputs = item.pop("_visibility_inputs", None)
        artifact_items.append(item)
        if inputs is not None:
            visibility_inputs[item["truth_id"]] = inputs
    return associate_source(
        source_id=CAPTURE["scene_id"],
        expected_split=split,
        capture_status=status,
        panel_status_counts=panel_counts or {"completed": 1, "failed": 0, "not_processed": 0},
        source_width=CAPTURE["visibility_inputs"]["expected_binding"]["width"],
        source_height=CAPTURE["visibility_inputs"]["expected_binding"]["height"],
        candidates=list(candidates),
        truth_markers=list(markers),
        artifact_truths=artifact_items,
        generic_overlays=list(overlays),
        source_raster_bytes=CAPTURE["source_raster_bytes"] if artifact_items else None,
        visibility_inputs_by_truth=visibility_inputs,
        capture_manifest_key=CAPTURE["capture_manifest_key"] if artifact_items else None,
    )


def test_unique_visible_legend_match_is_review_only_and_never_trainable():
    item = artifact()
    result = run([candidate("c1", item["center"])], artifacts=[item])
    assert result["candidate_status_counts"] == {"matched_artifact_review_only": 1}
    assert result["artifact_truth_status_counts"] == {"matched_artifact_review_only": 1}
    assert result["negative_training_rows"] == result["trainable_rows"] == 0
    assert result["training_approved"] is False
    assert result["optimizer_steps"] == 0


def test_connected_and_isolated_legitimate_markers_remain_positive_review():
    result = run(
        [candidate("connected", (10, 10)), candidate("isolated", (100, 100))],
        markers=[
            {"truth_id": "m1", "center": [10, 10], "connection": "connected",
             "coordinate_space": "original_source_pixels"},
            {"truth_id": "m2", "center": [100, 100], "connection": "isolated",
             "coordinate_space": "original_source_pixels"},
        ],
    )
    assert result["candidate_status_counts"] == {
        "review_connected_diagnostic": 1,
        "review_isolated": 1,
    }
    assert result["positive_truth_status_counts"] == {
        "matched_connected_review_only": 1,
        "matched_isolated_review_only": 1,
    }
    assert result["artifact_truth_count"] == 0
    assert result["negative_training_rows"] == 0


def test_unmatched_proposal_is_unknown_not_negative():
    result = run([candidate("unmatched", (1100, 320))])
    assert result["candidate_status_counts"] == {"review_unmatched": 1}
    assert result["artifact_truth_count"] == 0
    assert result["negative_training_rows"] == 0


def test_joint_two_pixel_positive_artifact_collision_is_ambiguous():
    item = artifact()
    x, y = item["center"]
    marker = {"truth_id": "m1", "center": [x - 1, y], "connection": "connected",
              "coordinate_space": "original_source_pixels"}
    result = run([candidate("c1", (x, y))], markers=[marker], artifacts=[item])
    assert result["candidate_status_counts"] == {"review_ambiguous": 1}
    assert result["positive_truth_status_counts"] == {"review_ambiguous": 1}
    assert result["artifact_truth_status_counts"] == {"review_ambiguous": 1}
    assert result["negative_training_rows"] == 0


def test_many_to_one_artifact_collision_is_ambiguous():
    x, y = CAPTURE["artifact_truth"]["center"]
    result = run([candidate("c1", (x, y)), candidate("c2", (x + 0.5, y))], artifacts=[artifact()])
    assert result["candidate_status_counts"] == {"review_ambiguous": 2}
    assert result["artifact_truth_status_counts"] == {"review_ambiguous": 1}


def test_generic_overlay_suppression_is_not_reclassified_as_artifact_negative():
    center = CAPTURE["artifact_truth"]["center"]
    result = run([candidate("c1", center)], artifacts=[artifact()], overlays=[{"center": center}])
    assert result["candidate_status_counts"] == {"review_overlay": 1}
    assert result["artifact_truth_status_counts"] == {"review_overlay_conflict": 1}
    assert result["negative_training_rows"] == 0


def test_symbol_and_non_detector_inputs_cannot_become_artifact_matches():
    result = run([
        candidate("symbol", CAPTURE["artifact_truth"]["center"], symbol=True),
        candidate("other", CAPTURE["artifact_truth"]["center"], detector=False),
    ], artifacts=[artifact()])
    assert result["candidate_status_counts"] == {
        "review_non_detector_input": 1,
        "review_symbol_input": 1,
    }
    assert result["artifact_truth_status_counts"] == {"unmatched_review_only": 1}


def test_exact_duplicate_candidate_centers_are_review_only():
    center = CAPTURE["artifact_truth"]["center"]
    result = run([candidate("c1", center), candidate("c2", center)], artifacts=[artifact()])
    assert result["candidate_status_counts"] == {"review_duplicate": 2}
    assert result["artifact_truth_status_counts"] == {"unmatched_review_only": 1}


def test_visibility_requires_single_glyph_pixels_inside_glyph_box():
    evidence, inputs = visibility()
    rendered = bytearray([255] * (8 * 8 * 3))
    ablated = bytearray(rendered)
    rendered[0:3] = b"\x00\x00\x00"
    binding = dict(inputs["expected_binding"])
    binding["rendered_rgb_sha256"] = hashlib.sha256(rendered).hexdigest()
    with pytest.raises(ValueError, match="uniquely attributable"):
        build_visibility_evidence(
            source_raster_bytes=SOURCE_RASTER, rendered_rgb=bytes(rendered),
            ablated_rgb=bytes(ablated), binding=binding,
        )
    binding = dict(inputs["expected_binding"])
    binding["matching_draw_ids"] = ["glyph-0", "glyph-0"]
    with pytest.raises(ValueError, match="draw identity is not unique"):
        build_visibility_evidence(
            source_raster_bytes=SOURCE_RASTER, rendered_rgb=inputs["rendered_rgb"],
            ablated_rgb=inputs["ablated_rgb"], binding=binding,
        )
    assert evidence["changed_pixel_count"] == 1


def test_unbound_or_forged_visibility_evidence_fails_closed():
    item = artifact()
    item["visibility_evidence"]["changed_pixel_count"] = 0
    with pytest.raises(ValueError, match="digest differs"):
        run([candidate("c1", item["center"])], artifacts=[item])
    item = artifact()
    item["scene_sha256"] = sha("other-scene")
    with pytest.raises(ValueError, match="external visibility binding differs"):
        run([candidate("c1", item["center"])], artifacts=[item])
    with pytest.raises(ValueError, match="lacks per-glyph"):
        item = artifact()
        item["visibility_evidence"] = None
        run([candidate("c1", item["center"])], artifacts=[item])


def test_self_consistent_forged_proof_is_rejected_against_actual_raster_bytes():
    item = artifact()
    evidence = item["visibility_evidence"]
    evidence["changed_pixel_count"] = 99
    evidence.pop("proof_sha256")
    evidence["proof_sha256"] = hashlib.sha256(
        json.dumps(evidence, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
    ).hexdigest()
    with pytest.raises(ValueError, match="does not match bound raster bytes"):
        run([candidate("c1", item["center"])], artifacts=[item])


def test_source_raster_and_rendered_plane_hashes_are_rechecked_at_association():
    item = artifact()
    inputs = item["_visibility_inputs"]
    inputs["rendered_rgb"] = bytes([0]) * len(inputs["rendered_rgb"])
    with pytest.raises(ValueError, match="rendered RGB bytes differ"):
        run([candidate("c1", item["center"])], artifacts=[item])

    item = artifact()
    inputs = item["_visibility_inputs"]
    inputs["ablated_rgb"] = inputs["rendered_rgb"]
    with pytest.raises(ValueError, match="no uniquely attributable visible pixels"):
        run([candidate("c1", item["center"])], artifacts=[item])

    item = artifact()
    artifact_items = [dict(item)]
    artifact_items[0].pop("_visibility_inputs")
    visibility_inputs = {item["truth_id"]: item["_visibility_inputs"]}
    with pytest.raises(ValueError, match="caller capture identity differs"):
        associate_source(
            source_id=CAPTURE["scene_id"], expected_split="train", capture_status="completed",
            panel_status_counts={"completed": 1, "failed": 0, "not_processed": 0},
            source_width=CAPTURE["visibility_inputs"]["expected_binding"]["width"],
            source_height=CAPTURE["visibility_inputs"]["expected_binding"]["height"],
            candidates=[candidate("c1", item["center"])],
            truth_markers=[], artifact_truths=artifact_items, generic_overlays=[],
            source_raster_bytes=CAPTURE["source_raster_bytes"] + b"tampered",
            visibility_inputs_by_truth=visibility_inputs,
            capture_manifest_key=CAPTURE["capture_manifest_key"],
        )


def test_consistently_forged_caller_binding_is_rejected_by_immutable_manifest():
    item = artifact()
    raster_inputs = item["_visibility_inputs"]
    original_png = CAPTURE["source_raster_bytes"]
    insert_at = original_png.rfind(b"\x00\x00\x00\x00IEND")
    assert insert_at >= 0
    text_payload = b"Comment\x00forged-but-valid-png-metadata"
    text_chunk = (struct.pack(">I", len(text_payload)) + b"tEXt" + text_payload +
                  struct.pack(">I", zlib.crc32(b"tEXt" + text_payload) & 0xFFFFFFFF))
    forged_png = original_png[:insert_at] + text_chunk + original_png[insert_at:]
    binding = dict(raster_inputs["expected_binding"])
    binding["scene_sha256"] = sha("forged scene")
    binding["annotation_sha256"] = sha("forged annotation")
    binding["source_raster_sha256"] = hashlib.sha256(forged_png).hexdigest()
    forged_evidence = build_visibility_evidence(
        source_raster_bytes=forged_png,
        rendered_rgb=raster_inputs["rendered_rgb"],
        ablated_rgb=raster_inputs["ablated_rgb"],
        binding=binding,
    )
    item["scene_sha256"] = binding["scene_sha256"]
    item["annotation_sha256"] = binding["annotation_sha256"]
    item["visibility_evidence"] = forged_evidence
    item["_visibility_inputs"] = {
        **raster_inputs,
        "expected_binding": binding,
    }
    with pytest.raises(ValueError, match="caller capture identity differs"):
        associate_source(
            source_id=CAPTURE["scene_id"], expected_split="train", capture_status="completed",
            panel_status_counts={"completed": 1, "failed": 0, "not_processed": 0},
            source_width=binding["width"], source_height=binding["height"],
            candidates=[candidate("forged-candidate", item["center"])], truth_markers=[],
            artifact_truths=[{key: value for key, value in item.items() if not key.startswith("_")}],
            generic_overlays=[], source_raster_bytes=forged_png,
            visibility_inputs_by_truth={item["truth_id"]: item["_visibility_inputs"]},
            capture_manifest_key=CAPTURE["capture_manifest_key"],
        )


def test_original_raster_dimensions_and_png_identity_are_bound():
    _, inputs = visibility()
    binding = dict(inputs["expected_binding"])
    binding["width"] = 9
    inputs["expected_binding"] = binding
    with pytest.raises(ValueError, match="dimensions differ"):
        build_visibility_evidence(
            source_raster_bytes=SOURCE_RASTER,
            rendered_rgb=inputs["rendered_rgb"],
            ablated_rgb=inputs["ablated_rgb"],
            binding=binding,
        )

    bad_crc = bytearray(SOURCE_RASTER)
    bad_crc[29] ^= 1
    evidence, inputs = visibility()
    binding = dict(inputs["expected_binding"])
    binding["source_raster_sha256"] = hashlib.sha256(bad_crc).hexdigest()
    with pytest.raises(ValueError, match="IHDR is invalid"):
        build_visibility_evidence(
            source_raster_bytes=bytes(bad_crc), rendered_rgb=inputs["rendered_rgb"],
            ablated_rgb=inputs["ablated_rgb"], binding=binding,
        )


def test_incomplete_capture_and_nontrain_split_never_emit_matches():
    incomplete = run([candidate("c1", (3, 3))], artifacts=[artifact()],
                     status="failed", panel_counts={"completed": 0, "failed": 1, "not_processed": 0})
    assert incomplete["candidate_status_counts"] == {"not_observed_capture_incomplete": 1}
    assert incomplete["artifact_truth_status_counts"] == {"not_observed_capture_incomplete": 1}
    with pytest.raises(ValueError, match="fixed train split"):
        run([candidate("c1", (3, 3))], artifacts=[artifact()], split="dev")


def test_full_aggregate_requires_exact_train_source_identity_and_stays_review_only():
    sources = []
    truth = {}
    for index in range(64):
        source_id = f"s{index:02d}"
        sources.append({
            "source_id": source_id,
            "capture_status": "completed",
            "panel_status_counts": {"completed": 1, "failed": 0, "not_processed": 0},
            "source_width": 8,
            "source_height": 8,
            "candidates": [],
        })
        truth[source_id] = {"markers": [], "artifacts": [], "generic_overlays": []}
    report = associate_full(expected_split="train", sources=sources, truth_by_source=truth)
    assert report["source_count"] == 64
    assert report["schema"] == "graphreader.train-legend-artifact-association.v1"
    assert report["negative_training_rows"] == report["trainable_rows"] == 0
    assert report["training_approved"] is False
    assert report["optimizer_steps"] == 0
    with pytest.raises(ValueError, match="source identities differ"):
        wrong_truth = dict(truth)
        wrong_truth.pop("s00")
        wrong_truth["other"] = {"markers": [], "artifacts": [], "generic_overlays": []}
        associate_full(expected_split="train", sources=sources, truth_by_source=wrong_truth)
    with pytest.raises(ValueError, match="exactly 64"):
        associate_full(expected_split="train", sources=sources, truth_by_source={})


def test_panel_accounting_requires_exact_nonnegative_integer_statuses():
    with pytest.raises(ValueError, match="panel accounting"):
        run([], panel_counts={"completed": 1, "failed": 0, "not_processed": 0, "ignored": 0})
    with pytest.raises(ValueError, match="panel accounting"):
        run([], panel_counts={"completed": 1, "failed": -1, "not_processed": 0})
