#!/usr/bin/env python3
"""Offline classification of unmatched synthetic OCR regions by surviving IDs.

Requires Pillow from the existing ml/synthetic/requirements.txt.

The labels below do not claim a complete runtime lineage. They describe only
the final region ID and whether that same ID is retained in raw evidence.
"""
import argparse
import hashlib
import json
import math
from collections import Counter
from pathlib import Path
from io import BytesIO
from PIL import Image

IOU_MINIMUM = 0.5  # Same rule as GraphReader.RealAcceptance.Ocr/Program.cs.
IDENTITY_MATRIX = (1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)

def load(path):
    return json.loads(path.read_text(encoding="utf-8"))

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def iou(a, b):
    ax, ay, aw, ah = a; bx, by, bw, bh = b
    left, top = max(ax, bx), max(ay, by)
    right, bottom = min(ax + aw, bx + bw), min(ay + ah, by + bh)
    intersection = max(0.0, right - left) * max(0.0, bottom - top)
    union = aw * ah + bw * bh - intersection
    return intersection / union if union else 0.0

def matching(edges, truth_count):
    owner = [-1] * truth_count
    def assign(prediction, seen):
        for truth in edges[prediction]:
            if seen[truth]: continue
            seen[truth] = True
            if owner[truth] < 0 or assign(owner[truth], seen):
                owner[truth] = prediction
                return True
        return False
    for prediction in range(len(edges)):
        assign(prediction, [False] * truth_count)
    result = [-1] * len(edges)
    for truth, prediction in enumerate(owner):
        if prediction >= 0: result[prediction] = truth
    return result

def region_box(region):
    bounds = region["polygon"]["bounds"]
    return [bounds["x"], bounds["y"], bounds["width"], bounds["height"]]

def positive_int(value):
    return isinstance(value, int) and not isinstance(value, bool) and value > 0

def finite_number(value):
    if not isinstance(value, (int, float)) or isinstance(value, bool): return False
    try: return math.isfinite(value)
    except OverflowError: return False

def exact_number(value, expected):
    return finite_number(value) and value == expected

def valid_box(box, width, height):
    return (isinstance(box, list) and len(box) == 4 and all(finite_number(value) for value in box) and
            box[2] > 0 and box[3] > 0 and box[0] >= 0 and box[1] >= 0 and
            box[0] + box[2] <= width and box[1] + box[3] <= height)

def valid_prediction_regions(regions, width, height):
    if not isinstance(regions, list): return False
    try:
        return all(region.get("coordinate_space") == "original_pixels" and
                   valid_box(region_box(region), width, height) for region in regions)
    except (AttributeError, KeyError, TypeError):
        return False

def decoded_image_size(path):
    with Image.open(path) as decoded:
        decoded.load()
        return decoded.size

def classify_surviving_region_id(region_id, raw_ids):
    if region_id in raw_ids: return "raw_detector_id_retained"
    prefixes = {
        "header-glyph-complete:": "header_glyph_complete_id",
        "header-separated-": "header_separation_id",
        "participant-word:": "participant_word_id",
        "participant-lane:": "participant_lane_id",
        "tick-lane:": "tick_lane_id",
        "bracket-caption-": "bracket_caption_id",
    }
    return next((value for prefix, value in prefixes.items() if region_id.startswith(prefix)), "unrecognized_surviving_id")

def identity_matrix(matrix):
    return isinstance(matrix, list) and len(matrix) == len(IDENTITY_MATRIX) and all(
        finite_number(value) and abs(value - expected) <= 1e-12
        for value, expected in zip(matrix, IDENTITY_MATRIX))

def full_source_identity_panel(stage, annotation, image_size):
    """Return panel-scoped truth only where the common pixel frame is explicit.

    The offline score cannot safely project a whole-source annotation into an
    arbitrary detected/cropped panel. It therefore permits only the simple,
    auditable case: one authored panel occupying the complete source canvas,
    and a stage record showing the same complete source with identity maps.
    """
    if not isinstance(stage, dict) or not isinstance(annotation, dict) or not isinstance(image_size, tuple):
        return None, "malformed_or_non_numeric_geometry"
    panels = annotation.get("panels")
    canvas = annotation.get("canvas", {})
    panel_source = stage.get("panel_source", {})
    if not isinstance(canvas, dict) or not isinstance(panel_source, dict):
        return None, "malformed_or_non_numeric_geometry"
    source_root = panel_source.get("source", {})
    if not isinstance(source_root, dict): return None, "malformed_or_non_numeric_geometry"
    source = source_root.get("image", {})
    requested = panel_source.get("requested_crop_in_source_pixels", {})
    encoded = panel_source.get("encoded_crop_in_source_pixels", {})
    if not isinstance(source, dict) or not isinstance(requested, dict) or not isinstance(encoded, dict) or annotation.get("coordinate_space") != "original_pixels" or not isinstance(panels, list) or len(panels) != 1:
        return None, "panel_or_coordinate_frame_not_explicitly_compatible"
    panel = panels[0]
    if not isinstance(panel, dict) or panel.get("coordinate_space") != "original_pixels":
        return None, "panel_or_coordinate_frame_not_explicitly_compatible"
    width, height = source.get("width"), source.get("height")
    if not positive_int(width) or not positive_int(height):
        return None, "malformed_or_non_numeric_geometry"
    if image_size != (width, height) or not exact_number(canvas.get("width"), width) or not exact_number(canvas.get("height"), height):
        return None, "decoded_image_dimensions_do_not_match_geometry"
    full = {"x": 0, "y": 0, "width": width, "height": height}
    panel_box = panel.get("box")
    if not isinstance(panel_box, list) or len(panel_box) != 4 or any(
            not finite_number(value)
            for value in panel_box):
        return None, "malformed_or_non_numeric_geometry"
    if any(abs(value - expected) > 1e-12 for value, expected in zip(panel_box, (0, 0, width, height))):
        return None, "panel_or_coordinate_frame_not_explicitly_compatible"
    if any(not exact_number(box.get(key), value) for box in (requested, encoded) for key, value in full.items()):
        return None, "crop_does_not_match_full_source"
    if not identity_matrix(panel_source.get("source_to_panel_matrix")) or not identity_matrix(panel_source.get("panel_to_source_matrix")):
        return None, "panel_or_coordinate_frame_not_explicitly_compatible"
    if "texts" not in panel or not isinstance(panel["texts"], list): return None, "malformed_or_non_numeric_geometry"
    try:
        truths = [{"box": item["box"], "text": item["text"], "role": item["role"]}
                  for item in panel["texts"] if isinstance(item, dict) and item.get("visible", True)]
    except (KeyError, TypeError, OverflowError):
        return None, "malformed_or_non_numeric_geometry"
    if any(not isinstance(item, dict) for item in panel["texts"]):
        return None, "malformed_or_non_numeric_geometry"
    if any(not valid_box(item["box"], width, height) for item in truths):
        return None, "malformed_or_non_numeric_geometry"
    return truths, None

def ocr_input_matches_full_source(ocr, source_sha):
    return isinstance(ocr, dict) and isinstance(ocr.get("input_sha256"), str) and ocr["input_sha256"] == source_sha

def source_sha_from_stage(stage):
    try:
        value = stage["panel_source"]["source"]["image"]["sha256"]
    except (KeyError, TypeError):
        return None
    return value if isinstance(value, str) and len(value) == 64 else None

def register_annotation(by_digest, ambiguous, image_digest, value):
    if image_digest in by_digest:
        ambiguous.add(image_digest)
        return
    by_digest[image_digest] = value

def self_test():
    raw = {"participant-word:raw"}
    cases = {
        "participant-word:raw": "raw_detector_id_retained",
        "participant-word:test": "participant_word_id",
        "participant-lane:test": "participant_lane_id",
        "header-glyph-complete:test": "header_glyph_complete_id",
        "other:test": "unrecognized_surviving_id",
    }
    actual = {region_id: classify_surviving_region_id(region_id, raw) for region_id in cases}
    if actual != cases:
        raise AssertionError({"expected": cases, "actual": actual})
    generic = generic_metadata()
    forbidden = {"private_corpus_access", "sealed_corpus_access", "production_approved"}
    if forbidden.intersection(generic) or any("Twenty of 21" in item for item in generic["limitations"]):
        raise AssertionError(generic)
    annotation = {"coordinate_space": "original_pixels", "canvas": {"width": 100, "height": 50},
        "panels": [{"coordinate_space": "original_pixels", "box": [0, 0, 100, 50],
            "texts": [{"box": [1, 2, 3, 4], "text": "1", "role": "x_tick"}]}]}
    stage = {"panel_source": {"source": {"image": {"width": 100, "height": 50}},
        "requested_crop_in_source_pixels": {"x": 0, "y": 0, "width": 100, "height": 50},
        "encoded_crop_in_source_pixels": {"x": 0, "y": 0, "width": 100, "height": 50},
        "source_to_panel_matrix": list(IDENTITY_MATRIX), "panel_to_source_matrix": list(IDENTITY_MATRIX)}}
    if full_source_identity_panel(stage, annotation, (100, 50)) != (annotation["panels"][0]["texts"], None):
        raise AssertionError("eligible single-panel identity case was not accepted")
    if full_source_identity_panel(stage, annotation, (99, 50))[1] != "decoded_image_dimensions_do_not_match_geometry":
        raise AssertionError("decoded dimension mismatch must be explicitly skipped")
    bad_source = {"panel_source": {**stage["panel_source"], "source": []}}
    if full_source_identity_panel(bad_source, annotation, (100, 50))[0] is not None:
        raise AssertionError("non-dict source must be explicitly skipped")
    missing_texts = {**annotation, "panels": [{key: value for key, value in annotation["panels"][0].items() if key != "texts"}]}
    if full_source_identity_panel(stage, missing_texts, (100, 50))[0] is not None:
        raise AssertionError("missing texts must be explicitly skipped")
    if finite_number(10 ** 10000):
        raise AssertionError("oversized integer must not reach geometry arithmetic")
    bool_crop = {"panel_source": {**stage["panel_source"], "requested_crop_in_source_pixels": {**stage["panel_source"]["requested_crop_in_source_pixels"], "x": False}}}
    if full_source_identity_panel(bool_crop, annotation, (100, 50))[0] is not None:
        raise AssertionError("boolean crop coordinate must be explicitly skipped")
    annotation["panels"].append(annotation["panels"][0])
    if full_source_identity_panel(stage, annotation, (100, 50))[0] is not None:
        raise AssertionError("multi-panel annotation must not be geometry-scored")
    annotation["panels"] = [annotation["panels"][0]]
    annotation["panels"][0]["box"] = [1, 0, 99, 50]
    if full_source_identity_panel(stage, annotation, (100, 50))[0] is not None:
        raise AssertionError("partial annotation panel must not be geometry-scored")
    annotation["panels"][0]["box"] = [0, 0, 100, 50]
    stage["panel_source"]["source_to_panel_matrix"][0] = 0.9
    if full_source_identity_panel(stage, annotation, (100, 50))[0] is not None:
        raise AssertionError("nonidentity transform must not be geometry-scored")
    stage["panel_source"]["source_to_panel_matrix"] = list(IDENTITY_MATRIX)
    stage["panel_source"]["requested_crop_in_source_pixels"]["x"] = 1
    if full_source_identity_panel(stage, annotation, (100, 50))[0] is not None:
        raise AssertionError("crop mismatch must not be geometry-scored")
    stage["panel_source"]["requested_crop_in_source_pixels"]["x"] = 0
    annotation["panels"][0]["box"] = ["bad", 0, 100, 50]
    if full_source_identity_panel(stage, annotation, (100, 50))[1] != "malformed_or_non_numeric_geometry":
        raise AssertionError("malformed geometry must be explicitly skipped")
    if ocr_input_matches_full_source({"input_sha256": "other"}, "source"):
        raise AssertionError("OCR input mismatch must not be geometry-scored")
    by_digest, ambiguous = {}, set()
    register_annotation(by_digest, ambiguous, "digest-a", "first")
    register_annotation(by_digest, ambiguous, "digest-a", "second")
    if by_digest["digest-a"] != "first" or ambiguous != {"digest-a"}:
        raise AssertionError("duplicate digest must be retained as ambiguous, never overwritten")
    if positive_int(True) or positive_int(0) or not valid_box([0, 0, 1, 1], 1, 1) or valid_box([0, 0, 0, 1], 1, 1):
        raise AssertionError("dimension or bounding-box validation failed")
    valid = BytesIO(); Image.new("L", (2, 3)).save(valid, format="PNG")
    valid.seek(0)
    with Image.open(valid) as decoded:
        decoded.load()
        if decoded.size != (2, 3): raise AssertionError("decoded dimensions mismatch")
    truncated = BytesIO(valid.getvalue()[:20])
    try:
        with Image.open(truncated) as decoded: decoded.load()
    except Exception:
        pass
    else:
        raise AssertionError("truncated image must fail full decode")
    if source_sha_from_stage({"panel_source": {}}) is not None:
        raise AssertionError("malformed stage source must not produce a digest")
    print(json.dumps({"status": "pass", "checks": len(cases) + 19}))

def generic_metadata():
    return {
        "limitations": [
            "This classifies surviving region IDs only; it does not prove complete runtime lineage.",
            "This scores only the supplied run directory; it does not establish workflow, release, or production acceptance.",
            "Truth is used only by this offline scorer, never runtime inference.",
        ],
        "model_inference_runs_by_this_tool": 0,
    }

def annotations(fixture):
    result = {}
    ambiguous = set()
    for image in sorted((fixture / "sources").glob("*/image.png")):
        annotation_path = image.with_name("annotation.json")
        annotation = load(annotation_path)
        image_digest = digest(image)
        try:
            image_size = decoded_image_size(image)
        except Exception as exc:
            raise ValueError(f"Fixture image failed full decode: {image}") from exc
        register_annotation(result, ambiguous, image_digest,
            (str(annotation_path.resolve()), annotation, image_size))
    return result, ambiguous

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-dir", type=Path)
    parser.add_argument("--fixture-dir", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        if args.run_dir or args.fixture_dir or args.output:
            parser.error("--self-test cannot be combined with run inputs")
        self_test()
        return
    if not args.run_dir or not args.fixture_dir or not args.output:
        parser.error("--run-dir, --fixture-dir, and --output are required")
    run_dir, fixture = args.run_dir.resolve(), args.fixture_dir.resolve()
    report = load(run_dir / "report.json")
    truth_by_sha, ambiguous_source_digests = annotations(fixture)
    classifications, panel_counts, details = Counter(), Counter(), []
    truth_count = predicted_count = matched_count = scored_panels = 0
    skipped_stages = Counter()
    for stage_path in sorted((run_dir / "stage-evidence").glob("*.json")):
        stage = load(stage_path)
        observation = stage.get("observation", {}) if isinstance(stage, dict) else {}
        ocr = observation.get("ocr") if isinstance(observation, dict) else None
        if not isinstance(ocr, dict) or not ocr.get("succeeded", False):
            skipped_stages["ocr_not_succeeded_or_malformed"] += 1
            continue
        source_sha = source_sha_from_stage(stage)
        if source_sha is None:
            skipped_stages["malformed_or_non_numeric_geometry"] += 1
            continue
        if source_sha in ambiguous_source_digests:
            skipped_stages["ambiguous_duplicate_source_digest"] += 1
            continue
        annotation_pair = truth_by_sha.get(source_sha)
        if annotation_pair is None:
            skipped_stages["source_annotation_not_found"] += 1
            continue
        annotation_path, annotation, image_size = annotation_pair
        truths, skip_reason = full_source_identity_panel(stage, annotation, image_size)
        if truths is None:
            skipped_stages[skip_reason] += 1
            continue
        if not ocr_input_matches_full_source(ocr, source_sha):
            skipped_stages["ocr_input_does_not_match_full_source"] += 1
            continue
        source_width, source_height = stage["panel_source"]["source"]["image"]["width"], stage["panel_source"]["source"]["image"]["height"]
        if not valid_prediction_regions(ocr.get("regions"), source_width, source_height):
            skipped_stages["malformed_or_non_numeric_geometry"] += 1
            continue
        raw = load(run_dir / "raw-ocr-evidence" / stage_path.name)["observation"]["raw_detector_regions"]
        raw_ids = {item["region_id"] for item in raw}; regions = ocr["regions"]
        assignments = matching([[index for index, truth in enumerate(truths) if iou(region_box(region), truth["box"]) >= IOU_MINIMUM] for region in regions], len(truths))
        scored_panels += 1; truth_count += len(truths); predicted_count += len(regions)
        for region, assignment in zip(regions, assignments):
            if assignment >= 0:
                matched_count += 1; continue
            category = classify_surviving_region_id(region["region_id"], raw_ids); classifications[category] += 1; panel_counts[observation["panel_id"]] += 1
            details.append({"panel_id": observation["panel_id"], "source_image_sha256": source_sha, "panel_input_sha256": ocr["input_sha256"], "region_id": region["region_id"], "surviving_region_id_classification": category, "bounds_xywh": region_box(region), "text": region["text"], "role": region["role"], "confidence": region["confidence"], "annotation": annotation_path})
    output = {"schema": "graphreader.synthetic-ocr-false-positive-surviving-id-classification.v3", "scope": "offline synthetic diagnostic only", "run_report": str((run_dir / "report.json").resolve()), "run_report_sha256": digest(run_dir / "report.json"), "candidate_binding_sha256": report.get("candidate_binding_sha256"), "input_manifest_sha256": report.get("input_manifest_sha256"), "matching": {"algorithm": "maximum_bipartite_iou", "iou_minimum": IOU_MINIMUM, "eligibility": "single annotated panel, full-source crop, matching OCR input SHA, and explicit identity source-panel transforms"}, "counts": {"stage_evidence_files": len(list((run_dir / "stage-evidence").glob("*.json"))), "geometry_scored_panels": scored_panels, "geometry_skipped_stages": dict(sorted(skipped_stages.items())), "truth_regions": truth_count, "predicted_regions": predicted_count, "geometry_matched_regions": matched_count, "geometry_false_positive_regions": predicted_count - matched_count, "geometry_false_negative_regions": truth_count - matched_count, "false_positives_by_surviving_region_id_classification": dict(sorted(classifications.items()))}, "false_positive_panels": dict(sorted(panel_counts.items())), "false_positive_details": details, **generic_metadata()}
    args.output.parent.mkdir(parents=True, exist_ok=True); args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(output["counts"], indent=2))

if __name__ == "__main__": main()
