# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score raw detection separately from assembled OCR in a native synthetic run.

This is development evidence, not private/sealed admission. Inference artifacts
are authenticated before separately authored synthetic truth is opened. Failed
sources and every truth remain in the denominators.
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from hashlib import sha256
import json
import math
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

import score_full_ocr_candidate_v2 as metric
from ml.policy.evidence_policy import tier1_acceptance_bars

METRIC_HASHES = {
    "score_full_ocr_candidate_v2.py": "8b879664c33e83f2aaec0e637f4f0f4df0f9701fe04ac43867672dde2e662656",
    "score_official_head_candidate.py": "719bb18c30821cdd44b65fc9ede38f6d111fb3631e07c7c69d98d1116aa575a2",
}


class EvidenceError(ValueError):
    """Evidence is incomplete, inconsistent, or outside synthetic scope."""


def require(condition, message):
    if not condition:
        raise EvidenceError(message)


def digest(path):
    return sha256(path.read_bytes()).hexdigest()


def reference(root, path):
    return {"path": path.resolve().relative_to(root.resolve()).as_posix(), "sha256": digest(path)}


def authenticated_path(root, ref, *, base=None):
    path = ((base or root) / ref.get("path", ref.get("file", ""))).resolve()
    require(path.is_relative_to(root.resolve()) and path.is_file(), "missing or outside-root evidence")
    if base is not None:
        require(path.is_relative_to(base.resolve()), "evidence escaped its execution directory")
    require(digest(path) == ref["sha256"], f"evidence checksum mismatch: {path.name}")
    return path


def read(root, ref, *, base=None):
    return json.loads(authenticated_path(root, ref, base=base).read_text(encoding="utf-8-sig"))


def validate_scope(report):
    require(report.get("schema") == "graphreader.real-acceptance-frozen-synthetic-report.v1"
            and report.get("scope") == "local-synthetic-frozen-candidate-diagnostic", "wrong evidence scope")
    for key in ("private_corpus_access", "sealed_corpus_access", "truth_consumed_by_inference",
                "production_approved", "model_selection_performed"):
        require(report.get(key) is False, f"forbidden or missing scope declaration: {key}")
    require(report.get("raw_ocr_observation_enabled") is True, "raw detector observations are required")


def validate_envelope(record, report):
    require(record.get("synthetic_only") is True, "observation is not synthetic-only")
    for key in ("private_corpus_access", "sealed_corpus_access"):
        require(record.get(key) is False, "private/sealed observation is forbidden")
    for key in ("candidate_binding_sha256", "input_manifest_sha256"):
        require(record.get(key) == report[key], "observation belongs to another run")


def predictions(regions, panel_id, source_hash, crop, *, raw):
    x, y, width, height = crop
    result, ids = [], set()
    for region in regions:
        ident = region["region_id"]
        require(isinstance(ident, str) and ident and ident not in ids, "duplicate/empty region identity")
        ids.add(ident)
        require(region.get("coordinate_space") == "original_pixels", "wrong region coordinate space")
        points = region["polygon"]["points"]
        require(len(points) >= 3 and all(
            isinstance(p.get(k), (int, float)) and math.isfinite(p[k])
            and 0 <= p[k] <= bound for p in points for k, bound in (("x", width), ("y", height))),
            "invalid region polygon")
        box = metric.Box(min(p["x"] for p in points) + x, min(p["y"] for p in points) + y,
                         max(p["x"] for p in points) + x, max(p["y"] for p in points) + y)
        require(box.right > box.left and box.bottom > box.top, "empty region polygon")
        if raw:
            confidence = region.get("detection_confidence")
            require(isinstance(confidence, (int, float)) and math.isfinite(confidence)
                    and 0 <= confidence <= 1, "invalid detector confidence")
            text, role = "", "other"
        else:
            text, role = region["text"], region["role"].lower()
            require(isinstance(text, str) and role in metric.RUNTIME_ROLES, "invalid OCR text/role")
        result.append(metric.FullTextPrediction(panel_id + "\n" + ident, source_hash, box, text, role))
    return result


def authenticate_runtime(root, report_ref):
    report_path = authenticated_path(root, report_ref)
    folder = report_path.parent
    report = read(root, report_ref)
    validate_scope(report)
    frozen = folder / "frozen-inputs"
    binding = read(root, {"path": str(frozen / "candidate-binding.json"),
                          "sha256": report["candidate_binding_sha256"]})
    inputs = read(root, {"path": str(frozen / "input-manifest.json"),
                         "sha256": report["input_manifest_sha256"]})
    protocol = read(root, binding["protocol"])
    require(binding["protocol"]["sha256"] == report["protocol_sha256"] == inputs["protocol_sha256"],
            "protocol identity mismatch")
    for key in ("managed_files", "native_files"):
        require(binding[key], f"missing runtime binding: {key}")
        for item in binding[key]:
            authenticated_path(root, item)
    for key in ("ocr_detection", "ocr_recognition", "marker_center", "marker_classifier"):
        model = binding[key].get("synthetic_candidate", binding[key])
        for kind in ("payload", "manifest"):
            authenticated_path(root, model[kind])
    # The protocol is authenticated here; its stage admission is a separate gate.
    require(isinstance(protocol, dict), "invalid protocol")
    sources = {s["sha256"]: s for s in inputs["sources"]}
    cases = report["cases"]
    require(len(sources) == len(inputs["sources"]) == len(cases) == report["source_count"] > 0,
            "missing or duplicate source")
    require(set(sources) == {c["image_sha256"] for c in cases}, "source/case mismatch")
    for source_hash, source in sources.items():
        authenticated_path(root, {"path": source["relative_path"], "sha256": source_hash})
        authenticated_path(root, {"path": str(frozen / "sources" / (source_hash + ".png")),
                                  "sha256": source_hash})
    panels, crop_ids = {}, set()
    assembled = defaultdict(list)
    for entry in report["calibration_diagnostic_files"]:
        record = read(root, entry, base=folder)
        validate_envelope(record, report)
        observation, source = record["observation"], record["panel_source"]
        panel_id = observation["panel_id"]
        source_hash = source["source"]["image"]["sha256"]
        require(panel_id == entry["panel_id"] and panel_id not in panels and source_hash in sources,
                "duplicate or unbound calibration panel")
        crop = tuple(source["encoded_crop_in_source_pixels"][k] for k in ("x", "y", "width", "height"))
        x, y, width, height = crop
        require(all(isinstance(v, (int, float)) and math.isfinite(v) for v in crop)
                and x >= 0 and y >= 0 and width > 0 and height > 0
                and x + width <= sources[source_hash]["width"]
                and y + height <= sources[source_hash]["height"], "invalid source crop")
        require(source["panel_to_source_matrix"] == [1, 0, x, 0, 1, y, 0, 0, 1], "unsupported crop mapping")
        require((source_hash, crop) not in crop_ids, "duplicate source crop")
        crop_ids.add((source_hash, crop))
        ocr = observation["ocr"]
        require(ocr["panel_id"] == panel_id and ocr["input_sha256"] == source["panel_image_sha256"],
                "OCR input identity mismatch")
        panels[panel_id] = (source_hash, crop, source["panel_image_sha256"])
        assembled[source_hash].extend(predictions(ocr["regions"], panel_id, source_hash, crop, raw=False))
    raw_predictions, seen = defaultdict(list), set()
    for entry in report["raw_ocr_diagnostic_files"]:
        record = read(root, entry, base=folder)
        validate_envelope(record, report)
        require(record.get("schema") == "graphreader.synthetic-workflow-raw-ocr-observation.v1",
                "wrong raw observation schema")
        observed = record["observation"]
        panel_id = observed["panel_id"]
        require(panel_id == entry["panel_id"] and panel_id in panels and panel_id not in seen,
                "duplicate or unbound raw panel")
        seen.add(panel_id)
        source_hash, crop, input_hash = panels[panel_id]
        require(observed.get("supplied_regions") is False, "supplied regions cannot stand in for model output")
        require(observed["input_sha256"] == entry["input_sha256"] == input_hash
                and (observed["width"], observed["height"]) == crop[2:], "raw input identity mismatch")
        raw_predictions[source_hash].extend(predictions(
            observed["raw_detector_regions"], panel_id, source_hash, crop, raw=True))
    require(seen == set(panels) and panels, "missing raw panel observations")
    require(set(sources) == {p[0] for p in panels.values()}, "a source has no OCR observations")
    return report, sources, panels, {"raw_detector": raw_predictions, "assembled_ocr": assembled}


def load_truths(root, generator_ref, sources, panels):
    generator = read(root, generator_ref)
    require(generator.get("private_reads") == generator.get("sealed_reads") == 0, "non-synthetic generator")
    require(len(generator["sources"]) == len(sources)
            and {s["image"]["sha256"] for s in generator["sources"]} == set(sources), "truth source mismatch")
    result, seen, split_sources = {"train": [], "validation": []}, set(), {}
    panel_counts = Counter(p[0] for p in panels.values())
    for source in generator["sources"]:
        source_hash = source["image"]["sha256"]
        require(source["split"] in ("train", "dev"), "sealed/private truth is forbidden")
        split = "validation" if source["split"] == "dev" else "train"
        require(source_hash not in split_sources, "duplicate or overlapping split source")
        split_sources[source_hash] = split
        authenticated_path(root, source["image"])
        annotation, scene = read(root, source["annotation"]), read(root, source["scene"])
        require(len(scene["panels"]) == panel_counts[source_hash], "missing or extra physical panel")
        for row in metric.family_scenes._records(annotation, "texts"):
            if row.get("visible", True) is False or not row.get("text", "").strip() or row.get("rendered_pixel_box") is None:
                continue
            ident = sha256((source_hash + "\n" + row["text_id"]).encode()).hexdigest()
            require(ident not in seen, "duplicate truth identity")
            seen.add(ident)
            result[split].append(metric.FullTextTruth(ident, source_hash,
                metric.Box(*metric.family_scenes._box(row)), row["text"], row["role"], metric._canonical_role(row["role"])))
    return result, split_sources


def score_geometry(truths, predictions_by_source, source_ids):
    matched = 0
    by_role = defaultdict(lambda: {"truths": 0, "matched": 0})
    for source_hash in sorted(source_ids):
        rows = [t for t in truths if t.source_sha256 == source_hash]
        pairs = metric._maximum_cardinality_pairs(predictions_by_source.get(source_hash, ()), rows)
        matched_ids = {truth_index for _, truth_index in pairs}
        matched += len(pairs)
        for index, truth in enumerate(rows):
            by_role[truth.expected_runtime_role]["truths"] += 1
            by_role[truth.expected_runtime_role]["matched"] += index in matched_ids
    count = sum(len(predictions_by_source.get(s, ())) for s in source_ids)
    return {"truth_regions": len(truths), "predicted_regions": count, "matched_regions": matched,
            "false_positives": count - matched, "false_negatives": len(truths) - matched,
            "precision": matched / count if count else 0.0,
            "recall": matched / len(truths) if truths else 0.0,
            "intersection_over_union_minimum": metric.MATCH_IOU_MINIMUM, "by_role": dict(by_role)}


def score(root, report_ref, generator_ref):
    started = time.perf_counter()
    for filename, expected in METRIC_HASHES.items():
        require(digest(Path(__file__).with_name(filename)) == expected, "frozen metric source changed")
    report, sources, panels, observations = authenticate_runtime(root, report_ref)
    truths, split_sources = load_truths(root, generator_ref, sources, panels)
    bars = tier1_acceptance_bars()
    results = {}
    for split, rows in truths.items():
        source_ids = {s for s, role in split_sources.items() if role == split}
        raw = score_geometry(rows, observations["raw_detector"], source_ids)
        assembled_geometry = score_geometry(rows, observations["assembled_ocr"], source_ids)
        assembled = metric._score_split(rows, {s: tuple(observations["assembled_ocr"].get(s, ())) for s in source_ids})
        results[split] = {"sources": len(source_ids), "raw_detector": raw,
                          "assembled_geometry": assembled_geometry, "assembled_ocr": assembled,
                          "bar_checks": {
            "raw_detection_precision": raw["precision"] >= bars["text_region_detection_precision_minimum"],
            "raw_detection_recall": raw["recall"] >= bars["text_region_detection_recall_minimum"],
            "assembled_detection_precision": assembled_geometry["precision"] >= bars["text_region_detection_precision_minimum"],
            "assembled_detection_recall": assembled_geometry["recall"] >= bars["text_region_detection_recall_minimum"],
            "recognition_exact": assembled["recognition_exact_accuracy"] >= bars["recognition_exact_match_minimum"],
            "character_error_rate": assembled["character_error_rate"] <= bars["character_error_rate_maximum"],
            "role_accuracy": assembled["role_accuracy"] >= bars["role_accuracy_minimum"],
        }}
    return {"schema": "graphreader.native-workflow-separated-ocr-score.v1",
            "status": "synthetic_development_diagnostic_only", "workflow_report": report_ref,
            "generator_report": generator_ref, "scorer": reference(root, Path(__file__)),
            "metric_sources": {name: digest(Path(__file__).with_name(name)) for name in METRIC_HASHES},
            "acceptance_bars": reference(root, root / "ml/policy/acceptance-bars.json"),
            "source_count": len(sources), "observed_panels": len(panels),
            "retained_failed_sources": sum(c["status"] != "completed" for c in report["cases"]),
            "full_source_truth_count": sum(map(len, truths.values())), "metrics": results,
            "all_source_truths_retained": True, "raw_geometry_is_not_assembled_geometry": True,
            "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0, "inference_rerun": False,
            "production_approved": False, "stage_admission_granted": False,
            "elapsed_seconds": time.perf_counter() - started}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workflow-report", type=Path, required=True)
    parser.add_argument("--workflow-report-sha256", required=True)
    parser.add_argument("--generator-report", type=Path, required=True)
    parser.add_argument("--generator-report-sha256", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = score(ROOT, {"path": str(args.workflow_report), "sha256": args.workflow_report_sha256},
                   {"path": str(args.generator_report), "sha256": args.generator_report_sha256})
    output = (ROOT / args.output).resolve()
    require(output.is_relative_to(ROOT), "output must stay within repository")
    with output.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, allow_nan=False)
        stream.write("\n")
    print(json.dumps({"status": result["status"], "panels": result["observed_panels"],
                      "truths": result["full_source_truth_count"], "seconds": result["elapsed_seconds"]}))


if __name__ == "__main__":
    main()
