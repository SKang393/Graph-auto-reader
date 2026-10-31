#!/usr/bin/env python3
"""Offline classification of unmatched synthetic OCR regions by surviving IDs.

The labels below do not claim a complete runtime lineage. They describe only
the final region ID and whether that same ID is retained in raw evidence.
"""
import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path

IOU_MINIMUM = 0.5  # Same rule as GraphReader.RealAcceptance.Ocr/Program.cs.

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
    print(json.dumps({"status": "pass", "checks": len(cases) + 2}))

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
    for image in sorted((fixture / "sources").glob("*/image.png")):
        annotation_path = image.with_name("annotation.json")
        annotation = load(annotation_path)
        canonical = annotation.get("texts") or [item for panel in annotation.get("panels", []) for item in panel.get("texts", [])]
        # Panels repeat no root list in this fixture. Retain each authored text once.
        seen, texts = set(), []
        for item in canonical:
            key = item.get("text_id", item.get("region_id"))
            if key in seen or not item.get("visible", True): continue
            seen.add(key)
            texts.append({"box": item["box"], "text": item["text"], "role": item["role"]})
        result[digest(image)] = (str(annotation_path.resolve()), texts)
    return result

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
    truth_by_sha = annotations(fixture)
    classifications, panel_counts, details = Counter(), Counter(), []
    truth_count = predicted_count = matched_count = scored_panels = 0
    for stage_path in sorted((run_dir / "stage-evidence").glob("*.json")):
        stage = load(stage_path); observation = stage.get("observation", {}); ocr = observation.get("ocr")
        if not ocr or not ocr.get("succeeded", False): continue
        source_sha = stage["panel_source"]["source"]["image"]["sha256"]
        annotation_path, truths = truth_by_sha[source_sha]
        raw = load(run_dir / "raw-ocr-evidence" / stage_path.name)["observation"]["raw_detector_regions"]
        raw_ids = {item["region_id"] for item in raw}; regions = ocr["regions"]
        assignments = matching([[index for index, truth in enumerate(truths) if iou(region_box(region), truth["box"]) >= IOU_MINIMUM] for region in regions], len(truths))
        scored_panels += 1; truth_count += len(truths); predicted_count += len(regions)
        for region, assignment in zip(regions, assignments):
            if assignment >= 0:
                matched_count += 1; continue
            category = classify_surviving_region_id(region["region_id"], raw_ids); classifications[category] += 1; panel_counts[observation["panel_id"]] += 1
            details.append({"panel_id": observation["panel_id"], "source_image_sha256": source_sha, "panel_input_sha256": ocr["input_sha256"], "region_id": region["region_id"], "surviving_region_id_classification": category, "bounds_xywh": region_box(region), "text": region["text"], "role": region["role"], "confidence": region["confidence"], "annotation": annotation_path})
    output = {"schema": "graphreader.synthetic-ocr-false-positive-surviving-id-classification.v2", "scope": "offline synthetic diagnostic only", "run_report": str((run_dir / "report.json").resolve()), "run_report_sha256": digest(run_dir / "report.json"), "candidate_binding_sha256": report.get("candidate_binding_sha256"), "input_manifest_sha256": report.get("input_manifest_sha256"), "matching": {"algorithm": "maximum_bipartite_iou", "iou_minimum": IOU_MINIMUM}, "counts": {"stage_evidence_files": len(list((run_dir / "stage-evidence").glob("*.json"))), "annotated_panels_scored": scored_panels, "truth_regions": truth_count, "predicted_regions": predicted_count, "geometry_matched_regions": matched_count, "geometry_false_positive_regions": predicted_count - matched_count, "geometry_false_negative_regions": truth_count - matched_count, "false_positives_by_surviving_region_id_classification": dict(sorted(classifications.items()))}, "false_positive_panels": dict(sorted(panel_counts.items())), "false_positive_details": details, **generic_metadata()}
    args.output.parent.mkdir(parents=True, exist_ok=True); args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(output["counts"], indent=2))

if __name__ == "__main__": main()
