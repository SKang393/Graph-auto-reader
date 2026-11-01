# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Produce a provenance-linked correction for reused P2 metric field names."""

from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[5]
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-sourceuniform-60ep-rx6600xt"
ORIGINAL_REL = OUTPUT_REL + "/report.json"
CORRECTED_REL = OUTPUT_REL + "/report-corrected-v2.json"
ORIGINAL_SHA256 = "aa57c136d7a6272ca1d0f3590d8ad7404413fdae85be24db39986a3952f854bf"
MATERIALIZER_REL = "ml/ocr/fragmented_xtick_detector_v2/materialize.py"
CURRENT_MATERIALIZER_SHA256 = "16b48f1a59385181f59b2245fc9ff53816093c55ade4fdc40b2f5bd9eb19a3b7"


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def correct_report(report: dict) -> dict:
    """Rename the reused trainer's active-model P2 fields to P3, without recomputing metrics."""
    corrected = deepcopy(report)
    comparison = corrected["train_only_metrics"]["p1_train_only_comparison"]
    comparison["p3_train_metrics"] = comparison.pop("p2_train_metrics")
    comparison["metric_deltas_p3_minus_p1"] = comparison.pop("metric_deltas_p2_minus_p1")
    for group in comparison["per_source_class_score_comparison"].values():
        for klass in group.values():
            klass["p3_count"] = klass.pop("p2_count")
            klass["p3_mean_score"] = klass.pop("p2_mean_score")
            klass["p3_fraction_at_or_above_0_5"] = klass.pop("p2_fraction_at_or_above_0_5")
    comparison["p1_report_scope_note"] = (
        "The aggregate P1 JSON was parsed, but only train metrics and train-source score summaries were used; "
        "no holdout images, annotations, predictions, or per-case truth were loaded or scored."
    )
    corrected["dependencies"]["all_local_source_dependencies_executed_from_exact_bytes"] = False
    corrected["dependencies"]["unbound_transitive_source_dependency"] = {
        "path": MATERIALIZER_REL,
        "current_source_sha256": CURRENT_MATERIALIZER_SHA256,
        "executed_bytes_sha256": None,
        "executed_from_exact_bytes_proven": False,
        "used_by": "gpu_train.py imports _png_bytes, cases, render_case, source_bundle_sha256",
    }
    corrected["limitations"].append(
        "P3 provenance is incomplete for the transitive project-local dependency "
        "ml/ocr/fragmented_xtick_detector_v2/materialize.py: it was normally imported by the pinned helper, "
        "not source-compiled from separately hashed bytes. Its current file hash is recorded but does not prove "
        "which bytes Python executed. No second training run was performed."
    )
    corrected["report_correction"] = {
        "kind": "field-name and provenance correction only; no training, inference, or metric recomputation",
        "original_report_relative": ORIGINAL_REL,
        "original_report_sha256": ORIGINAL_SHA256,
        "corrected_report_relative": CORRECTED_REL,
        "corrector_source_relative": Path(__file__).resolve().relative_to(ROOT).as_posix(),
        "corrector_source_sha256": digest(Path(__file__).read_bytes()),
        "corrected_field": "train_only_metrics.p1_train_only_comparison",
        "reason": "The reused P2 loop labels its active model result as P2; during P3 the active result was the P3 checkpoint.",
        "canonical_p1_p2_p3_comparison_field": "train_only_comparison",
        "transitive_dependency_provenance_correction": {
            "path": MATERIALIZER_REL,
            "current_source_sha256": CURRENT_MATERIALIZER_SHA256,
            "executed_bytes_hash_is_not_available": True,
        },
    }
    return corrected


def main() -> None:
    original_path = ROOT / ORIGINAL_REL
    original_bytes = original_path.read_bytes()
    if digest(original_bytes) != ORIGINAL_SHA256:
        raise RuntimeError("executed original report identity differs")
    corrected = correct_report(json.loads(original_bytes.decode("utf-8")))
    destination = ROOT / CORRECTED_REL
    if destination.exists():
        raise RuntimeError("corrected report already exists; refusing overwrite")
    destination.write_text(json.dumps(corrected, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"path": CORRECTED_REL, "sha256": digest(destination.read_bytes()),
        "bytes": destination.stat().st_size, "original_sha256": ORIGINAL_SHA256}, sort_keys=True))


if __name__ == "__main__":
    main()
