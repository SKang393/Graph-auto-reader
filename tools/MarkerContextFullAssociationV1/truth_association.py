# SPDX-License-Identifier: Apache-2.0
"""Post-freeze v5 truth conversion and review-only association.

This module accepts sidecar bytes, not a path or reader callback. A caller must
authenticate and durably freeze the capture identity and 2 px rule before it
opens the real sidecar and supplies bytes here.
"""
import hashlib
import json


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def build_truth_by_source(sidecar_raw, expected_sha256, protocol_sources, classifier):
    if not isinstance(sidecar_raw, bytes) or digest(sidecar_raw) != expected_sha256:
        raise ValueError("truth sidecar digest differs")
    if not isinstance(protocol_sources, list) or len(protocol_sources) != 64:
        raise ValueError("full-train protocol source inventory differs")
    by_image = {row.get("image_sha256"): row for row in protocol_sources}
    by_source = {row.get("source_id"): row for row in protocol_sources}
    if len(by_image) != 64 or len(by_source) != 64 or any(not isinstance(key, str) for key in by_image):
        raise ValueError("protocol source identities are missing or duplicated")

    try:
        sidecar = json.loads(sidecar_raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError("truth sidecar is not valid JSON") from error
    rows = sidecar.get("sources") if isinstance(sidecar, dict) else None
    if (sidecar.get("schema") != "marker-context-train-truth-v5" or sidecar.get("mode") != "full" or
        sidecar.get("training_approved") is not False or not isinstance(rows, list) or len(rows) != 64):
        raise ValueError("v5 full-train truth sidecar scope differs")

    truth_by_source = {}
    seen_images = set()
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("truth source row is invalid")
        image_sha = row.get("image_sha256")
        expected = by_image.get(image_sha)
        if expected is None or image_sha in seen_images:
            raise ValueError("truth image inventory differs or duplicates")
        seen_images.add(image_sha)
        scene, annotation, overlay = row.get("scene"), row.get("annotation"), row.get("overlay")
        if not all(isinstance(value, dict) for value in (scene, annotation, overlay)):
            raise ValueError("truth scene, annotation, or overlay is missing")
        scene_id = expected.get("scene_id")
        scene_sha = digest(canonical(scene))
        profile = annotation.get("contextual_negative_profile")
        if (row.get("case_id") != expected.get("case_id") or scene.get("scene_id") != scene_id or
            scene_sha != expected.get("scene_sha256") or annotation.get("scene_id") != scene_id or
            not isinstance(profile, dict) or profile.get("source_scene_canonical_sha256") != scene_sha or
            overlay.get("scene_id") != scene_id or overlay.get("scene_sha256") != scene_sha or
            overlay.get("split") != "train" or overlay.get("status") != "review_only" or
            overlay.get("optimization_allowed") is not False or overlay.get("materialized") is not True):
            raise ValueError("truth scene/annotation/overlay provenance differs")

        classified = classifier(annotation, expected_split="train")
        if classified.get("scene_id") != scene_id or classified.get("optimization_allowed") is not False:
            raise ValueError("train annotation classification identity differs")
        markers, overlays = [], []
        for record in classified.get("records", []):
            center, kind, source_id = record.get("center"), record.get("bin"), record.get("source_id")
            if (not isinstance(center, list) or len(center) != 2 or
                any(isinstance(value, bool) or not isinstance(value, (int, float)) for value in center) or
                not isinstance(source_id, str) or not source_id):
                raise ValueError("classified truth record geometry/identity differs")
            if kind in ("connected_positive", "isolated_positive"):
                markers.append({"truth_id": source_id, "center": center,
                                "connection": "connected" if kind == "connected_positive" else "isolated"})
            elif kind == "contextual_negative":
                overlays.append({"center": center})
            else:
                raise ValueError("unsupported classified truth bin")

        overlay_center = overlay.get("center")
        if (not isinstance(overlay_center, list) or len(overlay_center) != 2 or
            any(isinstance(value, bool) or not isinstance(value, (int, float)) for value in overlay_center)):
            raise ValueError("in-plot overlay center differs")
        if not (0 <= overlay_center[0] < 1200 and 0 <= overlay_center[1] < 350):
            raise ValueError("in-plot overlay center falls outside the authenticated source raster")
        overlays.append({"center": overlay_center})
        source_id = expected["source_id"]
        if source_id not in by_source or source_id in truth_by_source:
            raise ValueError("truth source identity duplicates or is unbound")
        truth_by_source[source_id] = {"markers": markers, "overlays": overlays}

    if seen_images != set(by_image) or set(truth_by_source) != set(by_source):
        raise ValueError("truth denominator differs from the fixed 64-source inventory")
    return truth_by_source


def associate_truth(sidecar_raw, expected_sha256, protocol_sources, capture_sources, classifier, association):
    """Return summary-only associations. Inputs must already be authenticated."""
    truth_by_source = build_truth_by_source(sidecar_raw, expected_sha256, protocol_sources, classifier)
    result = association.associate_full(capture_sources, truth_by_source)
    if (result.get("source_count") != 64 or result.get("trainable_rows") != 0 or
        result.get("negative_training_rows") != 0 or result.get("training_approved") is not False or
        result.get("optimizer_steps") != 0 or
        result.get("match_tolerance_px") != 2.0 or
        result.get("match_metric") != "euclidean_original_source_pixels_inclusive"):
        raise ValueError("review-only association result contract differs")
    return result


def associate_after_freeze(freeze_record, persist_freeze, truth_reader, expected_truth_sha256,
                           protocol_sources, capture_sources, classifier, association):
    """Orchestrate real-sidecar conversion after a caller durably persists freeze bytes.

    `persist_freeze` is required to create the caller's fresh, project-contained
    freeze receipt with exclusive creation and fsync. The sidecar reader is not
    invoked until that callback returns successfully.
    """
    if not callable(persist_freeze) or not callable(truth_reader):
        raise ValueError("freeze writer and truth reader callbacks are required")
    if (not isinstance(freeze_record, dict) or
        freeze_record.get("schema") != "graphreader.marker-context-full-train-association-pretruth-freeze.v1" or
        freeze_record.get("source_count") != 64 or freeze_record.get("match_tolerance_px") != 2.0 or
        freeze_record.get("match_metric") != "euclidean_original_source_pixels_inclusive" or
        freeze_record.get("truth_content_read") is not False or
        freeze_record.get("training_approved") is not False or freeze_record.get("optimizer_steps") != 0):
        raise ValueError("pretruth freeze record differs from the fixed association contract")
    freeze_raw = canonical(freeze_record)
    persist_freeze(freeze_raw)
    sidecar_raw = truth_reader()
    if not isinstance(sidecar_raw, bytes) or digest(sidecar_raw) != expected_truth_sha256:
        raise ValueError("truth sidecar digest differs after pretruth freeze")
    return associate_truth(sidecar_raw, expected_truth_sha256, protocol_sources,
                           capture_sources, classifier, association)
