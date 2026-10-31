# SPDX-License-Identifier: Apache-2.0
"""Train-only, review-only matching for explicit legend-symbol artifacts.

This module consumes already authenticated source-pixel records. It never reads
files, annotations, images, model output, or split manifests. A caller must bind
those inputs and must obtain per-glyph visibility evidence from a trusted render
and single-glyph ablation before passing a legend-symbol record here.
"""
from collections import Counter
import hashlib
import json
import math
from pathlib import Path
import struct
import zlib

TOLERANCE_PX = 2.0
HEX = set("0123456789abcdef")
CAPTURE_MANIFEST_SHA256 = "e674cd5ccf74843e6ae09ee9ed1f2c96f9d938cc5e9b45dce7455721919c22a0"


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def _digest(value):
    return hashlib.sha256(value).hexdigest()


def _strict_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("capture manifest contains duplicate fields")
        result[key] = value
    return result


def _read_capture_manifest():
    path = Path(__file__).absolute().with_name("capture_manifest.json")
    for item in reversed((path, *path.parents)):
        info = item.lstat()
        if item.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("capture manifest path contains a link or reparse point")
    raw = path.read_bytes()
    if _digest(raw) != CAPTURE_MANIFEST_SHA256:
        raise ValueError("immutable capture manifest SHA-256 differs")
    manifest = json.loads(raw, object_pairs_hook=_strict_object)
    if (not isinstance(manifest, dict) or
            set(manifest) != {"schema", "captures"} or
            manifest["schema"] != "graphreader.legend-capture-manifest.v1" or
            not isinstance(manifest["captures"], dict)):
        raise ValueError("immutable capture manifest schema differs")
    return manifest


def _verify_capture_entry(*, capture_manifest_key, source_id, source_width, source_height,
                          source_raster_bytes, artifact_truths):
    manifest = _read_capture_manifest()
    entry = manifest["captures"].get(capture_manifest_key)
    if not isinstance(entry, dict):
        raise ValueError("capture key is absent from the immutable manifest")
    if set(entry) != {
            "split", "source_id", "seed", "scene_sha256", "annotation_sha256",
            "source_raster_sha256", "renderer_bundle_sha256", "producer_source_sha256",
            "font_sha256", "width", "height", "glyphs"}:
        raise ValueError("capture manifest entry fields differ")
    if (entry["split"] != "train" or entry["source_id"] != source_id or
            entry["width"] != source_width or entry["height"] != source_height or
            _digest(source_raster_bytes) != entry["source_raster_sha256"]):
        raise ValueError("caller capture identity differs from the immutable manifest")
    for field in ("scene_sha256", "annotation_sha256", "source_raster_sha256",
                  "renderer_bundle_sha256", "producer_source_sha256", "font_sha256"):
        _sha(entry[field], f"capture manifest {field}")
    from .producer import verify_renderer_bundle
    producer_path = Path(__file__).absolute().with_name("producer.py")
    producer_info = producer_path.lstat()
    if producer_path.is_symlink() or getattr(producer_info, "st_file_attributes", 0) & 0x400:
        raise ValueError("capture producer path contains a link or reparse point")
    if _digest(producer_path.read_bytes()) != entry["producer_source_sha256"]:
        raise ValueError("producer source differs from the immutable capture manifest")
    current_bundle = verify_renderer_bundle()
    if current_bundle["sha256"] != entry["renderer_bundle_sha256"]:
        raise ValueError("producer renderer bundle differs from the immutable capture manifest")
    if current_bundle["sources"].get("ml/synthetic/public_fonts_v1/assets/NotoSans-Regular.ttf") != entry["font_sha256"]:
        raise ValueError("font asset differs from the immutable capture manifest")
    if any(glyph.get("expected_binding", {}).get("scene_sha256") != entry["scene_sha256"] or
           glyph.get("expected_binding", {}).get("annotation_sha256") != entry["annotation_sha256"]
           for glyph in entry["glyphs"].values() if isinstance(glyph, dict)):
        raise ValueError("capture manifest glyph bindings conflict with source identity")
    if not isinstance(entry["glyphs"], dict) or not artifact_truths:
        raise ValueError("capture manifest has no typed glyph records")
    truth_ids = {item.get("truth_id") for item in artifact_truths if isinstance(item, dict)}
    if truth_ids != set(entry["glyphs"]):
        raise ValueError("artifact truth identities differ from the immutable capture manifest")
    return entry


def _sha(value, name):
    if (not isinstance(value, str) or len(value) != 64 or
            any(char not in HEX for char in value)):
        raise ValueError(f"{name} must be lowercase SHA-256")


def _point(point, width, height, name):
    if (not isinstance(point, (list, tuple)) or len(point) != 2 or
            any(isinstance(value, bool) or not isinstance(value, (int, float)) or
                not math.isfinite(value) for value in point)):
        raise ValueError(f"{name} must be a finite source-pixel point")
    x, y = float(point[0]), float(point[1])
    if not (0 <= x < width and 0 <= y < height):
        raise ValueError(f"{name} falls outside source pixels")
    return x, y


def _box(box, width, height):
    if (not isinstance(box, (list, tuple)) or len(box) != 4 or
            any(isinstance(value, bool) or not isinstance(value, (int, float)) or
                not math.isfinite(value) for value in box)):
        raise ValueError("glyph_box must contain four finite source-pixel values")
    x, y, w, h = map(float, box)
    if w <= 0 or h <= 0 or not (0 <= x < x + w <= width and 0 <= y < y + h <= height):
        raise ValueError("glyph_box falls outside source pixels")
    return x, y, w, h


def _distance(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def _png_dimensions(source_raster_bytes):
    """Read and CRC-check the PNG IHDR used to bind source raster dimensions."""
    if not isinstance(source_raster_bytes, bytes) or len(source_raster_bytes) < 33:
        raise ValueError("original source raster must be PNG bytes")
    if source_raster_bytes[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("original source raster must be PNG bytes")
    length = struct.unpack(">I", source_raster_bytes[8:12])[0]
    chunk_type = source_raster_bytes[12:16]
    data = source_raster_bytes[16:29]
    crc = struct.unpack(">I", source_raster_bytes[29:33])[0]
    if (length != 13 or chunk_type != b"IHDR" or
            zlib.crc32(chunk_type + data) & 0xFFFFFFFF != crc):
        raise ValueError("original source raster PNG IHDR is invalid")
    width, height = struct.unpack(">II", data[:8])
    if width <= 0 or height <= 0:
        raise ValueError("original source raster PNG dimensions are invalid")
    return width, height


_VISIBILITY_BINDING_FIELDS = {
    "scene_sha256", "annotation_sha256", "source_reference", "glyph_id", "center",
    "glyph_box", "width", "height", "source_raster_sha256", "rendered_rgb_sha256",
    "renderer_bundle_sha256", "target_draw_id", "matching_draw_ids",
}


def _validate_visibility_binding(binding, source_raster_bytes):
    if not isinstance(binding, dict) or set(binding) != _VISIBILITY_BINDING_FIELDS:
        raise ValueError("visibility binding fields differ from the frozen schema")
    if not isinstance(source_raster_bytes, bytes):
        raise ValueError("original source raster must be immutable bytes")
    width, height = binding["width"], binding["height"]
    if type(width) is not int or type(height) is not int or width <= 0 or height <= 0:
        raise ValueError("visibility binding dimensions are invalid")
    for field in ("scene_sha256", "annotation_sha256", "source_raster_sha256",
                  "rendered_rgb_sha256", "renderer_bundle_sha256"):
        _sha(binding[field], field)
    if _digest(source_raster_bytes) != binding["source_raster_sha256"]:
        raise ValueError("original source raster bytes differ from the bound SHA-256")
    if _png_dimensions(source_raster_bytes) != (width, height):
        raise ValueError("original source raster dimensions differ from the binding")
    if not isinstance(binding["source_reference"], str) or not binding["source_reference"]:
        raise ValueError("source_reference is required")
    if not isinstance(binding["glyph_id"], str) or not binding["glyph_id"]:
        raise ValueError("glyph_id is required")
    if (not isinstance(binding["target_draw_id"], str) or not binding["target_draw_id"] or
            binding["matching_draw_ids"] != [binding["target_draw_id"]]):
        raise ValueError("single-glyph ablation draw identity is not unique")
    _point(binding["center"], width, height, "glyph center")
    _box(binding["glyph_box"], width, height)
    return width, height


def build_visibility_evidence(*, source_raster_bytes, rendered_rgb, ablated_rgb, binding):
    """Create visibility evidence after checking source and ablation pixel bytes.

    `binding` must come from a separately authenticated source/renderer record,
    not from an evidence record. The helper verifies its source raster and
    rendered-plane hashes, then computes the glyph-only pixel diff. This does
    not independently sign or validate the provenance of that external record.
    Pixel bounds use inclusive right/bottom coverage from the renderer audit.
    """
    width, height = _validate_visibility_binding(binding, source_raster_bytes)
    if (not isinstance(rendered_rgb, bytes) or not isinstance(ablated_rgb, bytes) or
            len(rendered_rgb) != width * height * 3 or
            len(ablated_rgb) != width * height * 3):
        raise ValueError("visibility images must be equal-size packed RGB byte rasters")
    if _digest(rendered_rgb) != binding["rendered_rgb_sha256"]:
        raise ValueError("rendered RGB bytes differ from the bound SHA-256")
    x, y, w, h = _box(binding["glyph_box"], width, height)
    x0, y0 = math.floor(x), math.floor(y)
    x1, y1 = min(width, math.ceil(x + w) + 1), min(height, math.ceil(y + h) + 1)
    changed = 0
    outside = 0
    for pixel in range(width * height):
        offset = pixel * 3
        if rendered_rgb[offset:offset + 3] != ablated_rgb[offset:offset + 3]:
            changed += 1
            px, py = pixel % width, pixel // width
            if not (x0 <= px < x1 and y0 <= py < y1):
                outside += 1
    if changed == 0 or outside != 0:
        raise ValueError("legend glyph has no uniquely attributable visible pixels")
    proof = {
        "schema": "graphreader.legend-glyph-visibility-proof.v1",
        "method": "single_glyph_ablation_rgb_diff",
        "scene_sha256": binding["scene_sha256"],
        "annotation_sha256": binding["annotation_sha256"],
        "source_reference": binding["source_reference"],
        "glyph_id": binding["glyph_id"],
        "center": [float(binding["center"][0]), float(binding["center"][1])],
        "glyph_box": [x, y, w, h],
        "width": width,
        "height": height,
        "source_raster_sha256": binding["source_raster_sha256"],
        "rendered_rgb_sha256": _digest(rendered_rgb),
        "renderer_bundle_sha256": binding["renderer_bundle_sha256"],
        "target_draw_id": binding["target_draw_id"],
        "matching_draw_ids": list(binding["matching_draw_ids"]),
        "changed_pixel_count": changed,
        "outside_box_changed_pixel_count": outside,
        "ablated_rgb_sha256": _digest(ablated_rgb),
    }
    proof["proof_sha256"] = _digest(_canonical(proof))
    return proof


def verify_visibility_evidence(evidence, *, source_raster_bytes, rendered_rgb,
                              ablated_rgb, expected_binding):
    """Recompute evidence from current bytes and an external expected binding."""
    if not isinstance(evidence, dict):
        raise ValueError("legend artifact lacks per-glyph visibility evidence")
    supplied = dict(evidence)
    supplied_digest = supplied.pop("proof_sha256", None)
    if supplied_digest != _digest(_canonical(supplied)):
        raise ValueError("legend visibility proof digest differs")
    rebuilt = build_visibility_evidence(
        source_raster_bytes=source_raster_bytes,
        rendered_rgb=rendered_rgb,
        ablated_rgb=ablated_rgb,
        binding=expected_binding,
    )
    if evidence != rebuilt:
        raise ValueError("visibility proof does not match bound raster bytes and glyph identity")
    return rebuilt


def _validate_artifacts(artifacts, width, height, *, source_raster_bytes,
                        visibility_inputs_by_truth, capture_entry):
    if not isinstance(artifacts, list):
        raise ValueError("artifacts must be a list")
    if visibility_inputs_by_truth is None and not artifacts:
        visibility_inputs_by_truth = {}
    if not isinstance(visibility_inputs_by_truth, dict):
        raise ValueError("visibility raster inputs must be a truth-ID map")
    ids = []
    points = []
    if artifacts and not isinstance(capture_entry, dict):
        raise ValueError("artifact association requires an immutable capture entry")
    for item in artifacts:
        if not isinstance(item, dict) or item.get("kind") != "legend_symbol":
            raise ValueError("only typed legend_symbol records can be artifact truths")
        truth_id, reference = item.get("truth_id"), item.get("source_reference")
        if not isinstance(truth_id, str) or not truth_id or not isinstance(reference, str) or not reference:
            raise ValueError("artifact truth identity is missing")
        ids.append(truth_id)
        center = _point(item.get("center"), width, height, "artifact center")
        box = _box(item.get("glyph_box"), width, height)
        if not (box[0] <= center[0] <= box[0] + box[2] and
                box[1] <= center[1] <= box[1] + box[3]):
            raise ValueError("artifact center falls outside its glyph box")
        scene_sha, annotation_sha = item.get("scene_sha256"), item.get("annotation_sha256")
        _sha(scene_sha, "artifact scene_sha256")
        _sha(annotation_sha, "artifact annotation_sha256")
        glyph_id = item.get("glyph_id")
        if not isinstance(glyph_id, str) or not glyph_id:
            raise ValueError("artifact glyph_id is required")
        raster_inputs = visibility_inputs_by_truth.get(truth_id)
        if not isinstance(raster_inputs, dict) or set(raster_inputs) != {
                "expected_binding", "rendered_rgb", "ablated_rgb"}:
            raise ValueError("legend artifact lacks bound raster and ablation bytes")
        binding = raster_inputs["expected_binding"]
        manifest_glyph = capture_entry["glyphs"].get(truth_id)
        if not isinstance(manifest_glyph, dict) or set(manifest_glyph) != {
                "kind", "source_reference", "glyph_id", "center", "glyph_box", "expected_binding"}:
            raise ValueError("artifact glyph is absent from the immutable capture manifest")
        if (item.get("kind") != manifest_glyph["kind"] or reference != manifest_glyph["source_reference"] or
                glyph_id != manifest_glyph["glyph_id"] or list(item["center"]) != manifest_glyph["center"] or
                list(item["glyph_box"]) != manifest_glyph["glyph_box"] or
                binding != manifest_glyph["expected_binding"]):
            raise ValueError("artifact or caller binding differs from the immutable capture manifest")
        if (not isinstance(binding, dict) or binding.get("scene_sha256") != scene_sha or
                binding.get("annotation_sha256") != annotation_sha or
                binding.get("source_reference") != reference or binding.get("glyph_id") != glyph_id or
                binding.get("center") != list(item["center"]) or
                binding.get("glyph_box") != list(item["glyph_box"]) or
                binding.get("width") != width or binding.get("height") != height):
            raise ValueError("external visibility binding differs from artifact identity")
        evidence = verify_visibility_evidence(
            item.get("visibility_evidence"),
            source_raster_bytes=source_raster_bytes,
            rendered_rgb=raster_inputs["rendered_rgb"],
            ablated_rgb=raster_inputs["ablated_rgb"],
            expected_binding=binding,
        )
        points.append(center)
    if set(visibility_inputs_by_truth) != set(ids):
        raise ValueError("visibility raster inputs contain missing or unreferenced truth IDs")
    if len(ids) != len(set(ids)):
        raise ValueError("artifact truth IDs must be unique")
    return points


def associate_source(*, source_id, expected_split, capture_status, panel_status_counts,
                     source_width, source_height, candidates, truth_markers, artifact_truths,
                     generic_overlays, source_raster_bytes=None,
                     visibility_inputs_by_truth=None, capture_manifest_key=None):
    """Aggregate one source; only unique negative-only matches get artifact status."""
    if expected_split != "train":
        raise ValueError("this diagnostic accepts only the fixed train split")
    if not isinstance(source_id, str) or not source_id:
        raise ValueError("source_id is required")
    if (type(source_width) is not int or type(source_height) is not int or
            source_width <= 0 or source_height <= 0):
        raise ValueError("source dimensions are invalid")
    if capture_status not in ("completed", "failed"):
        raise ValueError("capture_status is invalid")
    if (not isinstance(panel_status_counts, dict) or
            set(panel_status_counts) != {"completed", "failed", "not_processed"} or
            any(type(panel_status_counts.get(key)) is not int or panel_status_counts[key] < 0
                for key in ("completed", "failed", "not_processed")) or
            sum(panel_status_counts.values()) == 0):
        raise ValueError("panel accounting is invalid")
    capture_complete = capture_status == "completed" and not any(
        panel_status_counts[key] for key in ("failed", "not_processed"))

    if not isinstance(candidates, list) or not isinstance(truth_markers, list):
        raise ValueError("candidate and marker inventories must be lists")
    candidate_ids = [item.get("candidate_id") if isinstance(item, dict) else None for item in candidates]
    marker_ids = [item.get("truth_id") if isinstance(item, dict) else None for item in truth_markers]
    if (any(not isinstance(value, str) or not value for value in candidate_ids) or
            len(candidate_ids) != len(set(candidate_ids))):
        raise ValueError("candidate IDs must be unique and nonempty")
    if (any(not isinstance(value, str) or not value for value in marker_ids) or
            len(marker_ids) != len(set(marker_ids))):
        raise ValueError("marker truth IDs must be unique and nonempty")

    candidate_points = []
    for item in candidates:
        if item.get("coordinate_space") != "original_source_pixels":
            raise ValueError("candidate coordinate space must be original source pixels")
        if type(item.get("symbol_input")) is not bool:
            raise ValueError("symbol_input must be explicit")
        if type(item.get("detector_candidate")) is not bool:
            raise ValueError("detector_candidate must be explicit")
        candidate_points.append(_point(item.get("center"), source_width, source_height,
                                        "candidate center"))

    marker_points = []
    for item in truth_markers:
        if item.get("coordinate_space") != "original_source_pixels" or item.get("connection") not in ("connected", "isolated"):
            raise ValueError("marker truth role or coordinate space differs")
        marker_points.append(_point(item.get("center"), source_width, source_height, "marker center"))
    if artifact_truths and not isinstance(source_raster_bytes, bytes):
        raise ValueError("original source raster bytes are required for artifact verification")
    capture_entry = None
    if artifact_truths:
        if not isinstance(capture_manifest_key, str) or not capture_manifest_key:
            raise ValueError("artifact association requires an immutable capture manifest key")
        capture_entry = _verify_capture_entry(
            capture_manifest_key=capture_manifest_key, source_id=source_id,
            source_width=source_width, source_height=source_height,
            source_raster_bytes=source_raster_bytes, artifact_truths=artifact_truths,
        )
    artifact_points = _validate_artifacts(
        artifact_truths, source_width, source_height,
        source_raster_bytes=source_raster_bytes,
        visibility_inputs_by_truth=visibility_inputs_by_truth,
        capture_entry=capture_entry,
    )
    if not isinstance(generic_overlays, list):
        raise ValueError("generic_overlays must be a list")
    overlay_points = [_point(item.get("center") if isinstance(item, dict) else None,
                              source_width, source_height, "generic overlay center")
                      for item in generic_overlays]

    candidate_counts = Counter()
    marker_counts = Counter()
    artifact_counts = Counter()
    duplicates = set()
    exact_points = {}
    for index, point in enumerate(candidate_points):
        exact_points.setdefault(point, []).append(index)
    for indices in exact_points.values():
        if len(indices) > 1:
            duplicates.update(indices)

    overlay_candidates = {
        index for index, candidate in enumerate(candidates)
        if not candidate["detector_candidate"] or candidate["symbol_input"] or any(
            _distance(candidate_points[index], overlay) <= TOLERANCE_PX for overlay in overlay_points)
    }
    eligible = [index for index in range(len(candidates))
                if index not in duplicates and index not in overlay_candidates and capture_complete]

    # Joint links prevent a positive/artifact collision from becoming a negative.
    truth_refs = ([('marker', index) for index in range(len(truth_markers))] +
                  [('artifact', index) for index in range(len(artifact_truths))])
    truth_points = marker_points + artifact_points
    links_by_candidate, links_by_truth = {}, {}
    for candidate_index in eligible:
        for truth_index, truth_point in enumerate(truth_points):
            if _distance(candidate_points[candidate_index], truth_point) <= TOLERANCE_PX:
                links_by_candidate.setdefault(candidate_index, []).append(truth_index)
                links_by_truth.setdefault(truth_index, []).append(candidate_index)

    ambiguous_candidates = set()
    ambiguous_truths = set()
    for candidate_index, truth_indices in links_by_candidate.items():
        if len(truth_indices) > 1:
            ambiguous_candidates.add(candidate_index)
            ambiguous_truths.update(truth_indices)
        for truth_index in truth_indices:
            if len(links_by_truth.get(truth_index, [])) > 1:
                ambiguous_candidates.add(candidate_index)
                ambiguous_truths.add(truth_index)
                ambiguous_candidates.update(links_by_truth[truth_index])

    candidate_statuses = []
    for index, candidate in enumerate(candidates):
        if not capture_complete:
            status = "not_observed_capture_incomplete"
        elif not candidate["detector_candidate"]:
            status = "review_non_detector_input"
        elif candidate["symbol_input"]:
            status = "review_symbol_input"
        elif index in overlay_candidates:
            status = "review_overlay"
        elif index in duplicates:
            status = "review_duplicate"
        elif index in ambiguous_candidates:
            status = "review_ambiguous"
        elif len(links_by_candidate.get(index, [])) == 1:
            kind, truth_index = truth_refs[links_by_candidate[index][0]]
            if kind == "artifact":
                status = "matched_artifact_review_only"
            else:
                status = ("review_connected_diagnostic" if
                          truth_markers[truth_index]["connection"] == "connected" else "review_isolated")
        else:
            status = "review_unmatched"
        candidate_statuses.append(status)
        candidate_counts[status] += 1

    for truth_index, marker in enumerate(truth_markers):
        linked = links_by_truth.get(truth_index, [])
        if not capture_complete:
            status = "not_observed_capture_incomplete"
        elif any(_distance(marker_points[truth_index], overlay) <= TOLERANCE_PX for overlay in overlay_points):
            status = "review_overlay_conflict"
        elif truth_index in ambiguous_truths:
            status = "review_ambiguous"
        elif len(linked) == 1 and candidate_statuses[linked[0]] in ("review_connected_diagnostic", "review_isolated"):
            status = ("matched_connected_review_only" if marker["connection"] == "connected"
                      else "matched_isolated_review_only")
        else:
            status = "unmatched_review_only"
        marker_counts[status] += 1

    for artifact_index, artifact in enumerate(artifact_truths):
        truth_index = len(truth_markers) + artifact_index
        linked = links_by_truth.get(truth_index, [])
        if not capture_complete:
            status = "not_observed_capture_incomplete"
        elif any(_distance(artifact_points[artifact_index], overlay) <= TOLERANCE_PX for overlay in overlay_points):
            status = "review_overlay_conflict"
        elif truth_index in ambiguous_truths:
            status = "review_ambiguous"
        elif len(linked) == 1 and candidate_statuses[linked[0]] == "matched_artifact_review_only":
            status = "matched_artifact_review_only"
        else:
            status = "unmatched_review_only"
        artifact_counts[status] += 1

    return {
        "candidate_count": len(candidates),
        "candidate_status_counts": dict(sorted(candidate_counts.items())),
        "positive_truth_count": len(truth_markers),
        "positive_truth_status_counts": dict(sorted(marker_counts.items())),
        "artifact_truth_count": len(artifact_truths),
        "artifact_truth_status_counts": dict(sorted(artifact_counts.items())),
        "generic_overlay_count": len(generic_overlays),
        "match_tolerance_px": TOLERANCE_PX,
        "match_metric": "euclidean_original_source_pixels_inclusive",
        "negative_training_rows": 0,
        "trainable_rows": 0,
        "training_approved": False,
        "optimizer_steps": 0,
    }


def associate_full(*, expected_split, sources, truth_by_source):
    """Aggregate exactly 64 authenticated train sources, with no case rows."""
    if expected_split != "train":
        raise ValueError("this diagnostic is train-only")
    if not isinstance(sources, list) or len(sources) != 64 or not isinstance(truth_by_source, dict) or len(truth_by_source) != 64:
        raise ValueError("expected exactly 64 train capture and truth sources")
    source_ids = [row.get("source_id") if isinstance(row, dict) else None for row in sources]
    if (any(not isinstance(value, str) or not value for value in source_ids) or
            len(source_ids) != len(set(source_ids)) or set(source_ids) != set(truth_by_source)):
        raise ValueError("train source identities differ")
    totals = {
        "candidate_status_counts": Counter(),
        "positive_truth_status_counts": Counter(),
        "artifact_truth_status_counts": Counter(),
    }
    source_count = 0
    for source in sources:
        truth = truth_by_source[source["source_id"]]
        if not isinstance(truth, dict):
            raise ValueError("source truth record is invalid")
        result = associate_source(
            source_id=source["source_id"], expected_split=expected_split,
            capture_status=source["capture_status"], panel_status_counts=source["panel_status_counts"],
            source_width=source["source_width"], source_height=source["source_height"],
            candidates=source["candidates"], truth_markers=truth.get("markers"),
            artifact_truths=truth.get("artifacts"), generic_overlays=truth.get("generic_overlays"),
            source_raster_bytes=source.get("source_raster_bytes"),
            visibility_inputs_by_truth=source.get("visibility_inputs_by_truth"),
            capture_manifest_key=source.get("capture_manifest_key"),
        )
        source_count += 1
        for key in totals:
            totals[key].update(result[key])
    return {
        "schema": "graphreader.train-legend-artifact-association.v1",
        "split": "train",
        "source_count": source_count,
        "candidate_status_counts": dict(sorted(totals["candidate_status_counts"].items())),
        "positive_truth_status_counts": dict(sorted(totals["positive_truth_status_counts"].items())),
        "artifact_truth_status_counts": dict(sorted(totals["artifact_truth_status_counts"].items())),
        "match_tolerance_px": TOLERANCE_PX,
        "match_metric": "euclidean_original_source_pixels_inclusive",
        "negative_training_rows": 0,
        "trainable_rows": 0,
        "training_approved": False,
        "optimizer_steps": 0,
    }
