# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Measure descriptive pre-OCR preservation on actual synthetic runtime planes.

Text support here is dark source ink inside independently regenerated glyph
boxes. This is an explicitly labeled proxy, not exact glyph-pixel truth or a
production acceptance gate. No threshold, model, or weight is selected here.
"""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import time

import numpy as np

from score_family_ocr import (
    EvidenceError, REPOSITORY_ROOT, _load_object, _regenerate,
    _require_artifact_path, _sha256_bytes, _sha256_file,
    _truth_regions, _validate_manifest, _validate_report,
)


def _plane(directory: Path, record: dict, dtype: str, count: int) -> np.ndarray:
    name = record["file"]
    if not isinstance(name, str) or Path(name).name != name:
        raise EvidenceError("Plane reference must be a local basename")
    path = _require_artifact_path(directory / name, REPOSITORY_ROOT, "plane")
    payload = path.read_bytes()
    if _sha256_bytes(payload) != record["sha256"] or len(payload) != record["byte_count"]:
        raise EvidenceError("Plane bytes do not match the runtime report")
    values = np.frombuffer(payload, dtype=dtype)
    if len(values) != count or not np.isfinite(values).all():
        raise EvidenceError("Plane has invalid dimensions or nonfinite values")
    if dtype == "<f4" and ((values < 0).any() or (values > 1).any()):
        raise EvidenceError("Descriptive scores must remain in [0,1]")
    return values


def score(manifest_path: Path, report_path: Path, output_path: Path) -> dict:
    started = time.perf_counter()
    manifest_path = _require_artifact_path(manifest_path, REPOSITORY_ROOT, "manifest")
    report_path = _require_artifact_path(report_path, REPOSITORY_ROOT, "report")
    output_path = _require_artifact_path(output_path, REPOSITORY_ROOT, "output")
    if output_path.exists():
        raise EvidenceError("Prior diagnostic output cannot be replaced")
    manifest, manifest_bytes = _load_object(manifest_path, "manifest")
    report, report_bytes = _load_object(report_path, "report")
    split, seed, images = _validate_manifest(manifest, manifest_path)
    cases = _validate_report(report, _sha256_bytes(manifest_bytes), images)
    regenerated = _regenerate(split, seed, images)
    totals = dict(text_box_ink=0, retained_text_box_ink=0, retained_ink=0,
                  suppressed_ink=0, text_box_ink_suppressed=0, geometry_excluded_ink=0)
    analyzed = 0
    for image in images:
        identity = image["image_sha256"]
        case = cases[identity]
        diagnostic = case.get("pre_ocr_diagnostic")
        if diagnostic is None:
            continue
        if diagnostic.get("applied_to_ocr") is not False or diagnostic.get("production_approved") is not False:
            raise EvidenceError("Pre-OCR evidence must remain an unapplied diagnostic")
        width, height = image["width"], image["height"]
        count = width * height
        directory = report_path.parent / identity
        gray = _plane(directory, diagnostic["source_gray"], "u1", count).reshape(height, width)
        geometry = _plane(directory, diagnostic["geometry_excluded_ink"], "u1", count).reshape(height, width) != 0
        marker = _plane(directory, diagnostic["marker_like"], "<f4", count).reshape(height, width)
        connector = _plane(directory, diagnostic["thin_connector"], "<f4", count).reshape(height, width)
        text_boxes = np.zeros((height, width), dtype=bool)
        _, annotation = regenerated[identity]
        boxes, _ = _truth_regions(annotation)
        for box in boxes:
            left, top = max(0, math.floor(box.left)), max(0, math.floor(box.top))
            right, bottom = min(width, math.ceil(box.right)), min(height, math.ceil(box.bottom))
            text_boxes[top:bottom, left:right] = True
        # Fixed diagnostic operating point inherited from the focused unit
        # fixtures. It is never searched or enabled for OCR by this evaluator.
        ink = gray < 230
        eligible = ink & ~geometry
        suppressed = np.maximum(marker, connector) >= 0.5
        text = eligible & text_boxes
        totals["text_box_ink"] += int(text.sum())
        totals["retained_text_box_ink"] += int((text & ~suppressed).sum())
        totals["retained_ink"] += int((eligible & ~suppressed).sum())
        totals["suppressed_ink"] += int((eligible & suppressed).sum())
        totals["text_box_ink_suppressed"] += int((text & suppressed).sum())
        totals["geometry_excluded_ink"] += int((ink & geometry).sum())
        analyzed += 1
    if analyzed == 0:
        raise EvidenceError("No runtime structural planes were available")
    result = {
        "schema": "graphreader.synthetic-family-structure-proxy-diagnostic.v1",
        "scope": "synthetic train/dev descriptive diagnosis only",
        "split": split, "case_count": len(images), "analyzed_count": analyzed,
        "missing_structure_case_count": len(images) - analyzed,
        "production_approved": False, "training_input_ready": False,
        "acceptance_gate_satisfied": False, "private_reads": 0,
        "sealed_reads": 0, "optimizer_steps": 0,
        "input_manifest_sha256": _sha256_bytes(manifest_bytes),
        "runtime_report_sha256": _sha256_bytes(report_bytes),
        "runtime_planes_hash_verified": True, "regenerated_png_hash_verified": True,
        "evaluator_sha256": _sha256_file(Path(__file__).resolve()),
        "diagnostic_threshold": 0.5, "source_ink_gray_less_than": 230,
        "counts": totals,
        "proxy_text_preservation_precision": totals["retained_text_box_ink"] / max(1, totals["retained_ink"]),
        "proxy_text_preservation_recall": totals["retained_text_box_ink"] / max(1, totals["text_box_ink"]),
        "limitations": [
            "Glyph boxes contain gaps, degradation, and possibly overlapping graph ink; this is not exact text-pixel truth.",
            "The whole-image runtime currently identifies one axis pair even when several panels are present.",
            "Missing structural cases remain explicit and cannot produce a passing acceptance status.",
            "These scores neither replace OCR region/recognition metrics nor authorize suppression in Production.",
        ],
        "elapsed_milliseconds": round((time.perf_counter() - started) * 1000, 3),
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=2, sort_keys=True)
        stream.write("\n")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-manifest", type=Path, required=True)
    parser.add_argument("--runtime-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        result = score(args.input_manifest, args.runtime_report, args.output)
    except (EvidenceError, OSError, ValueError, KeyError, TypeError) as exception:
        print(json.dumps({"status": "rejected", "error": str(exception)}))
        return 1
    print(json.dumps({key: result[key] for key in (
        "analyzed_count", "proxy_text_preservation_precision", "proxy_text_preservation_recall",
        "acceptance_gate_satisfied")}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
