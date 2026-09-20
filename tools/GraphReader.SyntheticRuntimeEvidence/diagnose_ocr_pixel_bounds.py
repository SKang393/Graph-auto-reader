# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Test one pixel-bounds hypothesis against frozen synthetic train/dev evidence.

Only empty outer margins of existing effective boxes may be removed. All dark
pixels are retained, including noise and nontext structures. No proposal is
created or deleted. This is geometry diagnosis, not recognition or approval.
"""
from __future__ import annotations

import argparse
from collections import Counter
from hashlib import sha256
import json
import math
from pathlib import Path
import sys
import time

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

INPUTS = {
    "score": (
        "artifacts/goal22-runs/ocr-inside-plot-runtime-v1/full-score-v1.json",
        "31c15dd06dd4dd22d85f352ad65a8b58c44891748dd7f2f4f85382b0226954b4",
    ),
    "evaluation": (
        "artifacts/goal22-runs/ocr-inside-plot-runtime-v1/application-evaluation-v1/report.json",
        "15a28150ef5d8258ebfef94ed3867a331a305c5aa696b61c371ab3f77869665f",
    ),
    "request": (
        "artifacts/goal22-runs/ocr-v45-evaluation-inputs-v1/historical-evaluation-request.json",
        "5875c407ac3bcacc1d9f843f72fb12ca7b61d08484227e468d906dc50f4bb615",
    ),
    "preflight": (
        "artifacts/goal22-runs/ocr-text-extent-preflight/preflight-report.json",
        "23afd6d3163c84998586b54e72ef28d46cf80c561ff2a63449a005b48188cc53",
    ),
}
SOURCES = {
    "tools/GraphReader.SyntheticRuntimeEvidence/diagnose_ocr_line_assembly_v44.py":
        "6c9c981bc7bd0160158b705e19ddb1192d0ea1ee8ee061e40d1aaa23122e8a80",
    "tools/GraphReader.SyntheticRuntimeEvidence/score_full_ocr_candidate_v2.py":
        "8b879664c33e83f2aaec0e637f4f0f4df0f9701fe04ac43867672dde2e662656",
    "ml/ocr/official_bakeoff/production_head_inputs.py":
        "51800bcddfd9fc02c538bba04015312f94fb88113ea14c563616db58a61cff90",
    "ml/ocr/component_context_detector_v7/dataset.py":
        "96bebccedc58404e369a1ac3ef6fe3d4d8baa657872543f8958ed73e902d595f",
    "src/GraphReader.Ocr/ConnectedComponentTextRegionDetector.cs":
        "c77e65eb53bb9d15bc94b2956a96b87cfdcdd79509cbf87d2beada3b29afc3c4",
}


def foreground_threshold(gray: np.ndarray) -> int:
    """Reuse the current C# component detector's full-image mean rule."""
    if gray.ndim != 2 or gray.dtype != np.uint8 or gray.size == 0:
        raise ValueError("A nonempty Gray8 panel is required")
    return min(224, max(32, round(float(gray.mean()) * 0.80)))


def tighten_box(gray: np.ndarray, box: tuple[float, ...], threshold: int) -> tuple[float, ...]:
    if gray.ndim != 2 or gray.dtype != np.uint8 or gray.size == 0:
        raise ValueError("A nonempty Gray8 panel is required")
    if len(box) != 4 or not all(math.isfinite(value) for value in box):
        raise ValueError("A finite rectangle is required")
    left, top, right, bottom = box
    height, width = gray.shape
    if not (0 <= left < right <= width and 0 <= top < bottom <= height):
        raise ValueError("Box must remain inside the original panel")
    if isinstance(threshold, bool) or not isinstance(threshold, int) or not 0 <= threshold <= 255:
        raise ValueError("Invalid Gray8 threshold")
    x0, y0, x1, y1 = math.floor(left), math.floor(top), math.ceil(right), math.ceil(bottom)
    yy, xx = np.nonzero(gray[y0:y1, x0:x1] <= threshold)
    if not len(xx):
        return box
    return (
        max(left, float(x0 + xx.min())),
        max(top, float(y0 + yy.min())),
        min(right, float(x0 + xx.max() + 1)),
        min(bottom, float(y0 + yy.max() + 1)),
    )


def _read(relative: str, digest: str, bindings: dict[str, str]) -> bytes:
    path = (ROOT / relative).resolve()
    if ROOT not in path.parents or len(digest) != 64:
        raise ValueError("Evidence path or checksum is invalid")
    payload = path.read_bytes()
    if sha256(payload).hexdigest() != digest:
        raise ValueError(f"Evidence changed: {relative}")
    bindings[relative] = digest
    return payload


def _geometry_summary(metric, truths, predictions):
    matched = set()
    predicted_count = 0
    matched_count = 0
    for source in sorted({truth.source_sha256 for truth in truths} | set(predictions)):
        source_truths = [truth for truth in truths if truth.source_sha256 == source]
        source_predictions = predictions.get(source, ())
        pairs = metric._maximum_cardinality_pairs(source_predictions, source_truths)
        predicted_count += len(source_predictions)
        matched_count += len(pairs)
        matched.update(source_truths[index].truth_id for _, index in pairs)
    return {
        "truth_count": len(truths), "predicted_count": predicted_count,
        "true_positives": matched_count,
        "false_positives": predicted_count - matched_count,
        "false_negatives": len(truths) - matched_count,
        "precision": matched_count / max(1, predicted_count),
        "recall": matched_count / max(1, len(truths)),
    }, matched


def run(output: Path) -> dict:
    started = time.perf_counter()
    allowed = ROOT / "artifacts/goal22-runs/ocr-pixel-bounds-diagnostic"
    output = output.resolve()
    if allowed not in output.parents or output.exists():
        raise ValueError("Use a new output file under the local pixel-bounds diagnostic directory")
    bindings: dict[str, str] = {}
    docs = {key: json.loads(_read(path, digest, bindings)) for key, (path, digest) in INPUTS.items()}
    for path, digest in SOURCES.items():
        _read(path, digest, bindings)
    source_bindings = docs["score"]["inputs"]["source_bindings"]
    for group in ("active_frozen_scorers", "active_frozen_metric_dependencies"):
        for path, digest in source_bindings[group].items():
            _read(path, digest, bindings)
    own_path = Path(__file__).resolve().relative_to(ROOT).as_posix()
    test_path = Path(__file__).with_name("test_diagnose_ocr_pixel_bounds.py").relative_to(ROOT).as_posix()
    for relative in (own_path, test_path):
        bindings[relative] = sha256((ROOT / relative).read_bytes()).hexdigest()

    import diagnose_ocr_line_assembly_v44 as assembly
    from ml.ocr.official_bakeoff.production_head_inputs import _decode_production_pixels
    metric = assembly.metric
    evaluation = docs["evaluation"]
    if (evaluation["private_data"] or evaluation["sealed_data"] or
            evaluation["truth_used_by_runtime"] or evaluation["optimizer_steps"] != 0 or
            evaluation["panel_count"] != 37 or evaluation["completed_panel_count"] != 37):
        raise ValueError("Unexpected evaluation scope or panel denominator")
    requests = {row["panel_id"]: row for row in docs["request"]["panels"]}
    panels = evaluation["panels"]
    if len(requests) != 37 or len(panels) != 37 or {row["panel_id"] for row in panels} != set(requests):
        raise ValueError("Panel inventory changed")
    before = {"train": {}, "validation": {}}
    after = {"train": {}, "validation": {}}
    panel_counts = []
    for panel in panels:
        request = requests[panel["panel_id"]]
        if (panel["status"] != "completed" or panel["split"] != request["split"] or
                panel["source_sha256"] != request["source_sha256"]):
            raise ValueError("Panel status or source identity changed")
        descriptor = request["panel_png"]
        encoded = _read(descriptor["path"], descriptor["sha256"], bindings)
        if len(encoded) != descriptor["byte_count"]:
            raise ValueError("Panel byte count changed")
        gray, bgr = _decode_production_pixels(encoded, panel["width"], panel["height"])
        if (sha256(gray.tobytes()).hexdigest() != panel["original_gray_sha256"] or
                sha256(bgr.tobytes()).hexdigest() != panel["original_bgr_sha256"]):
            raise ValueError("Decoded pixels differ from the actual application inputs")
        threshold = foreground_threshold(gray)
        changed = 0
        for region in panel["effective_regions"]:
            bounds = assembly._box_from_polygon(region["panel_polygon"])
            box = (bounds.left, bounds.top, bounds.right, bounds.bottom)
            tightened = tighten_box(gray, box, threshold)
            changed += tightened != box
            source = panel["source_sha256"]
            region_id = panel["panel_id"] + ":" + region["region_id"]
            baseline = assembly._box_from_polygon(region["source_polygon"])
            mapped_baseline = assembly._map_box(bounds, panel["panel_to_source_matrix"])
            if any(abs(getattr(baseline, name) - getattr(mapped_baseline, name)) > 1e-6
                   for name in ("left", "top", "right", "bottom")):
                raise ValueError("Original-pixel coordinate mapping changed")
            trial = assembly._map_box(metric.Box(*tightened), panel["panel_to_source_matrix"])
            for target, current in ((before, baseline), (after, trial)):
                target[panel["split"]].setdefault(source, []).append(
                    metric.FullTextPrediction(region_id, source, current, "", ""))
        panel_counts.append({"panel_id": panel["panel_id"], "split": panel["split"],
                             "threshold": threshold, "box_count": len(panel["effective_regions"]),
                             "tightened_count": changed})

    # Derivation above cannot consult truth, recognized strings, roles, or metrics.
    for descriptor in (docs["preflight"]["train_text_truth"],
                       docs["preflight"]["historical_dev"]["synthetic_truth"]):
        _read(descriptor["path"], descriptor["sha256"], bindings)
    truths = assembly._load_truths(docs["preflight"], docs["score"])
    results = {}
    for split, split_truths in truths.items():
        baseline, old_matches = _geometry_summary(metric, split_truths, before[split])
        trial, new_matches = _geometry_summary(metric, split_truths, after[split])
        saved = docs["score"]["effective_region_geometry"][split]
        for key in ("true_positives", "false_positives", "false_negatives"):
            if baseline[key] != saved[key]:
                raise ValueError(f"{split} baseline differs from the saved full score")
        recovered = new_matches - old_matches
        lost = old_matches - new_matches
        results[split] = {
            "baseline": baseline, "pixel_bounds_trial": trial,
            "recovered_truth_count": len(recovered), "lost_truth_count": len(lost),
            "recovered_by_role": dict(sorted(Counter(t.generator_role for t in split_truths if t.truth_id in recovered).items())),
            "lost_by_role": dict(sorted(Counter(t.generator_role for t in split_truths if t.truth_id in lost).items())),
        }
    bars = docs["score"]["acceptance_bar_reference"]
    _read(bars["path"], bars["sha256"], bindings)
    for relative, digest in bindings.items():
        _read(relative, digest, {})
    result = {
        "schema": "graphreader.ocr-pixel-bounds-diagnostic.v1",
        "status": "geometry_diagnostic_only_unapproved",
        "synthetic_only": True, "private_data": False, "sealed_data": False,
        "model_inference": False, "optimizer_steps": 0, "new_model_revision": False,
        "production_approval": False, "parameter_sweeps": 0,
        "rule": "Retain every pixel at or below the existing full-panel component threshold; trim only empty outer margins of existing boxes; preserve empty boxes.",
        "truth_used_to_derive_bounds": False,
        "recognition_or_role_improvement_measured": False,
        "unchanged_truth_denominators": {"train": 709, "validation": 183},
        "source_bindings": bindings, "panels": panel_counts, "results": results,
        "acceptance_bar_reference": {"path": bars["path"], "sha256": bars["sha256"],
            "precision_minimum": bars["text_region_detection_precision_minimum"],
            "recall_minimum": bars["text_region_detection_recall_minimum"], "descriptive_only": True},
        "limitations": ["No new OCR execution; changed crops require actual runtime validation.",
                        "Dark artifacts and noise are retained; this trial does not segment letters or separate merged labels.",
                        "Axis-aligned enclosing bounds are compared; arbitrary-angle production behavior is not verified."],
        "elapsed_milliseconds": (time.perf_counter() - started) * 1000,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2, allow_nan=False)
        handle.write("\n")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = run(args.output)
    print(json.dumps({"status": report["status"], "results": report["results"],
                      "elapsed_milliseconds": report["elapsed_milliseconds"]}, indent=2))
