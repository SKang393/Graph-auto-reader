# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Apply frozen P3 to baseline and relaxed-width proposals on consumed synthetic probe."""

from __future__ import annotations

from collections import defaultdict
from hashlib import sha256
from pathlib import Path
import importlib.util
import json
import math
import sys
from typing import Any

import numpy as np

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
HERE = Path(__file__).resolve().parent
EVALUATOR_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/evaluate_p3_probe.py"
EVALUATOR_SHA = "263506da81caaa40f75cf6cb8c8b06e55c77b9a19e7a2b88af420a311e87337f"
SCAN_DIAGNOSTIC_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_recall_v1/diagnose_misses.py"
SCAN_DIAGNOSTIC_SHA = "32662ad05c220de6a9a5fce451f5101baab32ce4ac6156f1ba4b58d3266b834a"
RELAXED_REPORT_REL = "artifacts/stroke-seeded-xtick-short-tick-recall-v5/miss_diagnosis.json"
RELAXED_REPORT_SHA = "10f1282c134285070c9fcfbc29d09fb8d2c46d1fe27ccac2d1c31d9592077d1d8"
BASELINE_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-probe-filter-v1/report.json"
BASELINE_REPORT_SHA = "0cfe59debc97b64a20a2b2a1b53e4394e72c25f50d3b58c50581864a52750ac0"
BASELINE_SCORE_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-probe-filter-v1/proposal_scores.json"
BASELINE_SCORE_SHA = "c8f3adfb1c090ec57257064ba315c2b59d50bba49a93d23145f5071e6cfc50fc"
PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-probe-filter-v1-preflight-v2.json"
PREFLIGHT_SHA = "7fffab0c37e4747f351f086abfef955bcf0afc55092778feb6f25abaa e4c35ae".replace(" ", "")
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-p3-widecluster-probe-v2"
OUTPUT_PREFLIGHT_REL = "artifacts/stroke-seeded-xtick-v1-p3-widecluster-probe-v2-preflight.json"
CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/p3_widecluster_v1/p3_widecluster_eval.json"
CONFIG_SHA = "ba0bd0ee5e08497b02b435ac96aa2feabc7cca530f0446711daf97d1f09ecf24"
PROBE_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
SCANNER_SHA = "0297966c41243bd4a27a0c1fa0aa7e849fc8beb5fa7a5dc177fc7e9029cb6081"
P3_CHECKPOINT_SHA = "8b1f8ce7d9897e3db19e71a4f21576b92e51ae56a90aa1fec9fb1df364820c92"
P3_CONFIG_SHA = "927b93c88d7e61dc505516995b72f2235aa7fc8824e5fc54b45ae6741c3c5717"
P3_RUNNER_SHA = "b7c0b381f7d58be23ac8d1b139a18a8467fed9e34c320421765063dbe76c118d"
TRAIN_MANIFEST_SHA = "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672"
THRESHOLD = 0.5
WIDE_CLUSTER_LIMIT = 1_000_000


class PairEvaluationError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def pinned_bytes(relative: str, expected: str | None, limit: int) -> bytes:
    path = ROOT / relative
    if path.is_symlink() or not path.resolve(strict=True).is_relative_to(ROOT.resolve()):
        raise PairEvaluationError(f"unsafe or external pinned input: {relative}")
    raw = path.read_bytes()
    if len(raw) > limit or (expected is not None and digest(raw) != expected):
        raise PairEvaluationError(f"pinned bytes differ: {relative}")
    return raw


def load_module(relative: str, name: str, expected_sha: str):
    raw = pinned_bytes(relative, expected_sha, 5_000_000)
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    if spec is None:
        raise PairEvaluationError(f"cannot load pinned module: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    exec(compile(raw, str(ROOT / relative), "exec"), module.__dict__)
    return module


def expected_config() -> dict[str, Any]:
    return {"schema": "graphreader.p3-wide-cluster-probe-pair.v1",
        "recipe_id": "p3-filter-baseline-vs-unbounded-cluster-width-v1",
        "probe_manifest_sha256": PROBE_SHA, "scanner_proposals_sha256": SCANNER_SHA,
        "p3_checkpoint_sha256": P3_CHECKPOINT_SHA, "p3_config_sha256": P3_CONFIG_SHA,
        "p3_runner_sha256": P3_RUNNER_SHA, "p3_train_manifest_sha256": TRAIN_MANIFEST_SHA,
        "baseline_p3_report_sha256": BASELINE_REPORT_SHA,
        "baseline_p3_score_rows_sha256": BASELINE_SCORE_SHA,
        "relaxed_proposal_report_sha256": RELAXED_REPORT_SHA,
        "scan_diagnostic_source_sha256": SCAN_DIAGNOSTIC_SHA,
        "threshold": THRESHOLD, "wide_cluster_limit_px": WIDE_CLUSTER_LIMIT,
        "output_relative": OUTPUT_REL, "preflight_relative": OUTPUT_PREFLIGHT_REL,
        "crop_geometry": "pinned P1 crop materializer at proposal center x and center y minus 2px",
        "quarantine": ["exact P3 training crop hash", "P1 holdout crop hash metadata"],
        "truth_read_after_inference_only": True, "p1_holdout_files_opened": False,
        "training_performed": False, "production_activation": False}


def make_relaxed_rows(context: dict[str, Any], evaluator: Any, scanner_diag: Any) -> tuple[list[dict[str, Any]], int]:
    scanner = context["scanner"]
    raw_rows: list[dict[str, Any]] = []
    baseline_count = 0
    for source_result in scanner["source_results"]:
        source_id = source_result["source_id"]
        image = context["source_images"][source_id]
        for panel in source_result["panels"]:
            panel_id = panel["panel_id"]
            x0, y0, x1, y1 = (int(v) for v in panel["bounds"])
            axis = panel["selected_x_axis_line"]
            local_axis = [axis[0] - x0, axis[1] - y0, axis[2] - x0, axis[3] - y0]
            gray = np.asarray(image.crop((x0, y0, x1, y1)).convert("L"), dtype=np.uint8)
            base_local, _ = scanner_diag.scan(gray, local_axis)
            wide_local, _ = scanner_diag.scan(gray, local_axis, max_cluster_width=WIDE_CLUSTER_LIMIT)

            def to_global(proposals: list[dict[str, Any]]) -> list[dict[str, Any]]:
                return [{**p, "center": [p["center"][0] + x0, p["center"][1] + y0],
                    "line": [p["line"][0] + x0, p["line"][1] + y0,
                        p["line"][2] + x0, p["line"][3] + y0]} for p in proposals]

            base_global = to_global(base_local)
            if len(base_global) != len(panel["ink_proposals"]):
                raise PairEvaluationError(f"baseline scanner count mismatch: {panel_id}")
            for actual, expected in zip(base_global, panel["ink_proposals"]):
                geometry_error = max(abs(a - b) for a, b in zip(
                    actual["center"] + actual["line"], expected["center"] + expected["line"]))
                if (geometry_error > 1e-6
                        or any(actual[k] != expected[k] for k in
                            ("cluster_width_pixels", "dark_pixel_count", "longest_dark_run"))):
                    raise PairEvaluationError(f"baseline scanner proposal parity mismatch: {panel_id}")
            baseline_count += len(base_global)
            for index, proposal in enumerate(to_global(wide_local)):
                png, pixels = evaluator.crop_bytes(image, proposal["center"])
                raw_rows.append({"proposal_id": f"wide:{source_id}:{panel_id}:{index}",
                    "source_id": source_id, "panel_id": panel_id, "proposal_index": index,
                    "proposal": proposal, "crop_sha256": digest(png), "pixels": pixels})
    if baseline_count != 365 or len(raw_rows) != 442:
        raise PairEvaluationError(f"scanner proposal inventory changed: baseline={baseline_count}, wide={len(raw_rows)}")
    return raw_rows, baseline_count


def split_rows(evaluator: Any, rows: list[dict[str, Any]], training_hashes: set[str],
               holdout_hashes: set[str]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    eligible, quarantined = evaluator.quarantine_rows(rows, training_hashes, holdout_hashes)
    if len(eligible) + len(quarantined) != len(rows):
        raise PairEvaluationError("eligible/quarantine row accounting is incomplete")
    eligible_ids = [r["proposal_id"] for r in eligible]
    quarantine_ids = [r["proposal_id"] for r in quarantined]
    if len(eligible_ids) != len(set(eligible_ids)) or set(eligible_ids) & set(quarantine_ids):
        raise PairEvaluationError("candidate proposal identity collision across admission groups")
    return eligible, quarantined


def panel_metrics(panels: list[dict[str, Any]], key: str) -> dict[str, Any]:
    visible = sum(p["visible_truth_count"] for p in panels)
    proposals = sum(p[key]["usable_detected_x_stroke_count"] for p in panels)
    matches = sum(p[key]["matched_count"] for p in panels)
    unmatched = sum(p[key]["unmatched_detected_count"] for p in panels)
    no_visible = [p for p in panels if p["visible_truth_count"] == 0]
    return {"visible_truth_ticks": visible, "proposals": proposals, "true_matches": matches,
        "unmatched_proposals": unmatched, "precision": matches / proposals if proposals else 0.0,
        "recall": matches / visible if visible else 0.0,
        "no_visible_tick_panels": len(no_visible),
        "false_proposals_on_no_visible_panels": sum(p[key]["usable_detected_x_stroke_count"] for p in no_visible),
        "no_visible_panels_with_proposals": sum(p[key]["usable_detected_x_stroke_count"] > 0 for p in no_visible),
        "axis_uncertain_panels": sum(bool(p["needs_review"]) for p in panels)}


def run_preflight() -> dict[str, Any]:
    config_raw = pinned_bytes(CONFIG_REL, CONFIG_SHA, 65_536)
    config = json.loads(config_raw)
    if config != expected_config():
        raise PairEvaluationError("paired evaluation config differs from the pinned recipe")
    evaluator = load_module(EVALUATOR_REL, "p3_pair_pinned_evaluator", EVALUATOR_SHA)
    scanner_diag = load_module(SCAN_DIAGNOSTIC_REL, "p3_pair_pinned_scanner_diag", SCAN_DIAGNOSTIC_SHA)
    old_receipt, baseline_eligible, context = evaluator.preflight()
    saved = json.loads(pinned_bytes(evaluator.PREFLIGHT_REL, PREFLIGHT_SHA, 500_000))
    if saved != old_receipt:
        raise PairEvaluationError("existing P3 scanner preflight no longer matches pinned inputs")
    if any(old_receipt["source_and_full_panel_overlap_counts"].get(k) for k in
            ("train_source_id", "train_source_identity_sha256", "train_full_panel_pixel_sha256",
             "p1_holdout_source_id", "p1_holdout_source_identity_sha256", "p1_holdout_full_panel_pixel_sha256")):
        raise PairEvaluationError("probe source/full-panel overlap detected")
    train_hashes = set(context["train_hash_labels"])
    holdout_hashes = {str(r["image_sha256"]) for r in context["train_manifest"]["records"]
        if r.get("source_group_id") in context["holdout_groups"] and isinstance(r.get("image_sha256"), str)}
    baseline_eligible_check, baseline_quarantine = split_rows(evaluator, context["all_rows"], train_hashes, holdout_hashes)
    if {r["proposal_id"] for r in baseline_eligible_check} != {r["proposal_id"] for r in baseline_eligible}:
        raise PairEvaluationError("baseline P3 eligible proposal set differs from retained preflight")
    wide_rows, baseline_count = make_relaxed_rows(context, evaluator, scanner_diag)
    wide_eligible, wide_quarantine = split_rows(evaluator, wide_rows, train_hashes, holdout_hashes)
    base_hashes = {r["crop_sha256"] for r in context["all_rows"]}
    wide_hashes = {r["crop_sha256"] for r in wide_rows}
    receipt = {"schema": "graphreader.p3-wide-cluster-paired-preflight.v1",
        "status": "verified-before-inference", "config_sha256": digest(config_raw),
        "runner_sha256": digest(pinned_bytes(Path(__file__).resolve().relative_to(ROOT).as_posix(), None, 4_000_000)),
        "p3_evaluator_sha256": EVALUATOR_SHA, "scan_diagnostic_source_sha256": SCAN_DIAGNOSTIC_SHA,
        "relaxed_scan_report_sha256": RELAXED_REPORT_SHA,
        "p3_model": {"checkpoint_sha256": evaluator.P3_CHECKPOINT_SHA,
            "config_sha256": evaluator.P3_CONFIG_SHA, "runner_sha256": evaluator.P3_RUNNER_SHA,
            "threshold": evaluator.THRESHOLD},
        "probe_manifest_sha256": evaluator.PROBE_MANIFEST_SHA,
        "raw_scanner_proposals_sha256": evaluator.SCANNER_SHA,
        "baseline_p3_report_sha256": BASELINE_REPORT_SHA, "baseline_p3_score_rows_sha256": BASELINE_SCORE_SHA,
        "probe_source_separation": old_receipt["source_and_full_panel_overlap_counts"],
        "p1_holdout_image_or_annotation_files_read": False,
        "baseline_proposals": {"all": len(context["all_rows"]), "eligible": len(baseline_eligible),
            "quarantined": len(baseline_quarantine), "unique_crop_hashes": len(base_hashes),
            "overlapping_p3_training_crop_hashes": len(base_hashes & train_hashes),
            "overlapping_p1_holdout_crop_hashes": len(base_hashes & holdout_hashes)},
        "relaxed_proposals": {"all": len(wide_rows), "eligible": len(wide_eligible),
            "quarantined": len(wide_quarantine), "unique_crop_hashes": len(wide_hashes),
            "overlapping_p3_training_crop_hashes": len(wide_hashes & train_hashes),
            "overlapping_p1_holdout_crop_hashes": len(wide_hashes & holdout_hashes)},
        "eligible_crop_hash_intersection": len({r["crop_sha256"] for r in baseline_eligible} &
            {r["crop_sha256"] for r in wide_eligible}),
        "duplicate_rows_preserved": True, "proposal_truth_read": False,
        "baseline_scanner_reproduced": baseline_count == 365,
        "relaxed_width_limit_px": WIDE_CLUSTER_LIMIT,
        "p3_training_transitive_materializer_provenance_limitation":
            old_receipt["p3_training_transitive_materializer_provenance"],
        "inputs_verified_by_frozen_p3_preflight": True}
    return receipt


def write_preflight() -> dict[str, Any]:
    path = ROOT / OUTPUT_PREFLIGHT_REL
    if path.exists() or path.is_symlink():
        raise PairEvaluationError("paired preflight output exists; refusing overwrite")
    receipt = run_preflight()
    path.write_text(json.dumps(receipt, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    return receipt


def run() -> dict[str, Any]:
    if (ROOT / OUTPUT_REL).exists():
        raise PairEvaluationError("paired evaluation output exists; refusing overwrite")
    expected_receipt = run_preflight()
    saved_receipt_raw = pinned_bytes(OUTPUT_PREFLIGHT_REL, None, 1_000_000)
    if json.loads(saved_receipt_raw) != expected_receipt:
        raise PairEvaluationError("paired preflight receipt changed")

    evaluator = load_module(EVALUATOR_REL, "p3_pair_pinned_evaluator_run", EVALUATOR_SHA)
    scanner_diag = load_module(SCAN_DIAGNOSTIC_REL, "p3_pair_pinned_scanner_diag_run", SCAN_DIAGNOSTIC_SHA)
    _, base_eligible, context = evaluator.preflight()
    train_hashes = set(context["train_hash_labels"])
    holdout_hashes = {str(r["image_sha256"]) for r in context["train_manifest"]["records"]
        if r.get("source_group_id") in context["holdout_groups"] and isinstance(r.get("image_sha256"), str)}
    wide_rows, _ = make_relaxed_rows(context, evaluator, scanner_diag)
    wide_eligible, wide_quarantine = split_rows(evaluator, wide_rows, train_hashes, holdout_hashes)
    baseline_quarantine = split_rows(evaluator, context["all_rows"], train_hashes, holdout_hashes)[1]
    combined = base_eligible + wide_eligible
    scores, runtime = evaluator._infer_p3(combined)
    if len(scores) != len(combined):
        raise PairEvaluationError("P3 score count differs from admitted baseline/relaxed proposals")
    for row, score in zip(combined, scores):
        if not math.isfinite(float(score)):
            raise PairEvaluationError("P3 returned non-finite score")
        row["p3_score"] = float(score)

    old_report = json.loads(pinned_bytes(BASELINE_REPORT_REL, BASELINE_REPORT_SHA, 3_000_000))
    old_scores = json.loads(pinned_bytes(BASELINE_SCORE_REL, BASELINE_SCORE_SHA, 3_000_000))
    previous_by_id = {r["proposal_id"]: r for r in old_scores["rows"]}
    p3_baseline_rows = [r for r in base_eligible if float(r["p3_score"]) >= THRESHOLD]
    for row in base_eligible:
        previous = previous_by_id.get(row["proposal_id"])
        if previous is None or previous.get("p3_score") is None or abs(float(previous["p3_score"]) - row["p3_score"]) > 1e-7:
            raise PairEvaluationError("rerun baseline P3 score differs from retained per-proposal score")
    p3_wide_rows = [r for r in wide_eligible if float(r["p3_score"]) >= THRESHOLD]

    # Ground truth is first consulted only after all truth-blind proposals are generated,
    # crop-hash admission is fixed, and the frozen P3 has scored every eligible row.
    from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
    from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.opencv_hough_minline_diagnostic import run_hough_min5 as truth_helper
    truths: dict[str, list[dict[str, Any]]] = {}
    for source_id, rendered in context["rendered_sources"].items():
        truths.update(truth_helper.source_truths(source_id, rendered["panels"]))
    if sum(t["source_stroke_visible"] for panel_truth in truths.values() for t in panel_truth) != 181:
        raise PairEvaluationError("visible truth count is not the pinned 181");
    raw_by_panel = {p["panel_id"]: p for s in context["scanner"]["source_results"] for p in s["panels"]}
    rows_by_panel: dict[str, dict[str, list[dict[str, Any]]]] = defaultdict(lambda: {"baseline": [], "wide": []})
    for row in context["all_rows"]:
        rows_by_panel[row["panel_id"]]["baseline"].append(row)
    for row in wide_rows:
        rows_by_panel[row["panel_id"]]["wide"].append(row)
    eligible_base_ids = {r["proposal_id"] for r in base_eligible}
    eligible_wide_ids = {r["proposal_id"] for r in wide_eligible}
    panels = []
    for source in context["probe_manifest"]["sources"]:
        source_id = source["source_id"]
        for rendered_panel in context["rendered_sources"][source_id]["panels"]:
            panel_id = rendered_panel["panel_id"]
            panel = raw_by_panel[panel_id]
            visible = [t for t in truths[panel_id] if t["source_stroke_visible"]]
            base_all = rows_by_panel[panel_id]["baseline"]
            wide_all = rows_by_panel[panel_id]["wide"]
            base_p3 = [r for r in base_all if r["proposal_id"] in eligible_base_ids and r["p3_score"] >= THRESHOLD]
            wide_p3 = [r for r in wide_all if r["proposal_id"] in eligible_wide_ids and r["p3_score"] >= THRESHOLD]
            record = {"panel_id": panel_id, "source_id": source_id,
                "visible_truth_count": len(visible), "visible_truth": visible,
                "needs_review": bool(panel["needs_review"]),
                "quarantined_baseline": sum(r["proposal_id"] not in eligible_base_ids for r in base_all),
                "quarantined_wide": sum(r["proposal_id"] not in eligible_wide_ids for r in wide_all)}
            for key, records in (("baseline_raw", base_all), ("baseline_p3", base_p3),
                                 ("relaxed_raw", wide_all), ("relaxed_p3", wide_p3)):
                detections = [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in records]
                record[key] = common.match_panel(visible, detections)
            panels.append(record)

    strategies = ("baseline_raw", "baseline_p3", "relaxed_raw", "relaxed_p3")
    metrics = {key: {"aggregate": panel_metrics(panels, key), "by_source": {}}
        for key in strategies}
    for key in strategies:
        for source_id in sorted({p["source_id"] for p in panels}):
            metrics[key]["by_source"][source_id] = panel_metrics([p for p in panels if p["source_id"] == source_id], key)
    expected_baseline_p3 = old_report["raw_scan_p2_and_p3_metrics"]["p3_filter_eligible"]["aggregate"]
    actual_baseline_p3 = metrics["baseline_p3"]["aggregate"]
    for key, old_key in (("proposals", "proposals"), ("true_matches", "matched_ticks"),
        ("unmatched_proposals", "unmatched_proposals"), ("precision", "precision"), ("recall", "recall")):
        if abs(float(actual_baseline_p3[key]) - float(expected_baseline_p3[old_key])) > 1e-12:
            raise PairEvaluationError(f"baseline P3 metrics fail retained parity for {key}")

    def overlap_labels(rows: list[dict[str, Any]], key: str) -> dict[str, Any]:
        labels: dict[str, set[bool]] = defaultdict(set)
        panel_by_id = {p["panel_id"]: p for p in panels}
        for row in rows:
            matched = panel_by_id[row["panel_id"]][key]["matched_pairs"]
            labels[row["crop_sha256"]].add(any(pair["detection_index"] == row["proposal_index"]
                and pair.get("kind") == "visible_tick" for pair in matched))
        return {"unique_crop_hashes": len(labels),
            "duplicate_hashes": sum(len([r for r in rows if r["crop_sha256"] == h]) > 1 for h in labels),
            "semantic_truth_conflict_hashes": sum(len(v) > 1 for v in labels.values())}

    score_rows = [{"proposal_id": r["proposal_id"], "source_id": r["source_id"],
        "panel_id": r["panel_id"], "proposal_index": r["proposal_index"],
        "crop_sha256": r["crop_sha256"], "p3_score": r.get("p3_score"),
        "eligible": r["proposal_id"] in eligible_base_ids | eligible_wide_ids,
        "strategy": "baseline" if r["proposal_id"].startswith("stroke-seeded") else "relaxed_width"}
        for r in context["all_rows"] + wide_rows]
    receipt_raw = (ROOT / OUTPUT_PREFLIGHT_REL).read_bytes()
    report = {"schema": "graphreader.p3-wide-cluster-paired-report.v1",
        "status": "completed-exploratory-consumed-synthetic-probe-diagnostic",
        "acceptance_status": "consumed synthetic development probe; not formal acceptance",
        "production_activation": False, "training_performed": False,
        "p1_holdout_file_bytes_read": False, "truth_used_after_inference_only": True,
        "inputs": {"preflight_sha256": digest(receipt_raw), "p3_evaluator_source_sha256": EVALUATOR_SHA,
            "scan_diagnostic_source_sha256": SCAN_DIAGNOSTIC_SHA, "p3_checkpoint_sha256": P3_CHECKPOINT_SHA,
            "p3_config_sha256": P3_CONFIG_SHA, "p3_runner_sha256": P3_RUNNER_SHA,
            "probe_manifest_sha256": PROBE_SHA, "raw_scanner_proposals_sha256": SCANNER_SHA,
            "relaxed_proposal_report_sha256": RELAXED_REPORT_SHA,
            "baseline_p3_report_sha256": BASELINE_REPORT_SHA, "baseline_p3_score_rows_sha256": BASELINE_SCORE_SHA,
            "train_manifest_sha256": TRAIN_MANIFEST_SHA},
        "scanner_variants": {"baseline": {"max_cluster_width_px": 8},
            "relaxed_width": {"max_cluster_width_px": WIDE_CLUSTER_LIMIT}},
        "threshold": THRESHOLD, "runtime": runtime["runtime"], "inference": runtime,
        "proposal_admission": {"baseline": {"all": len(context["all_rows"]), "eligible": len(base_eligible),
                "quarantined": len(baseline_quarantine)},
            "relaxed_width": {"all": len(wide_rows), "eligible": len(wide_eligible),
                "quarantined": len(wide_quarantine)},
            "quarantine_reasons_count": {"baseline": sum(len(r["reasons"]) for r in baseline_quarantine),
                "relaxed_width": sum(len(r["reasons"]) for r in wide_quarantine)},
            "same_truth_panels": len(panels), "visible_truth_ticks": sum(p["visible_truth_count"] for p in panels),
            "no_visible_tick_panels": sum(p["visible_truth_count"] == 0 for p in panels),
            "axis_uncertain_panels": sum(p["needs_review"] for p in panels)},
        "metrics": metrics,
        "duplicate_crop_assessment": {"baseline": overlap_labels(context["all_rows"], "baseline_raw"),
            "relaxed_width": overlap_labels(wide_rows, "relaxed_raw")},
        "baseline_p3_parity": {"retained_metrics": expected_baseline_p3,
            "reproduced_metrics": actual_baseline_p3,
            "score_rows_sha256": digest(pinned_bytes(BASELINE_SCORE_REL, BASELINE_SCORE_SHA, 3_000_000)),
            "passed": True},
        "score_rows_sha256": digest(json.dumps(score_rows, sort_keys=True, separators=(",", ":")).encode()),
        "limitations": ["Only the already-consumed synthetic development probe was used; not an acceptance set.",
            "Proposal and crop duplicates are retained and scored row by row; crop-hash overlaps with P3 train or P1 holdout metadata are quarantined.",
            "The prior P3 training run has an unbound transitive materializer provenance limitation, recorded in preflight.",
            "Synthetic proposal performance does not establish real-chart performance or production suitability."],
        "panel_metrics": [{k: v for k, v in p.items() if k != "visible_truth"} for p in panels]}
    output = ROOT / OUTPUT_REL
    output.mkdir(parents=False, exist_ok=False)
    (output / "proposal_scores.json").write_text(json.dumps(score_rows, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    (output / "report.json").write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    return report


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--preflight", action="store_true")
    args = parser.parse_args()
    if args.preflight:
        receipt = write_preflight()
        print(json.dumps({"preflight": OUTPUT_PREFLIGHT_REL,
            "sha256": digest((ROOT / OUTPUT_PREFLIGHT_REL).read_bytes()),
            "baseline": receipt["baseline_proposals"], "relaxed": receipt["relaxed_proposals"]}, indent=2))
    else:
        result = run()
        print(json.dumps({"metrics": {k: v["aggregate"] for k, v in result["metrics"].items()},
            "proposals": result["proposal_admission"]}, indent=2))
