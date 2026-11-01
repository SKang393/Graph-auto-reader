# SPDX-License-Identifier: Apache-2.0
"""Truth-blind relaxed-width proposal preflight and paired P3/P5 probe evaluation."""

from __future__ import annotations

from collections import defaultdict
from hashlib import sha256
from pathlib import Path
from datetime import datetime, timezone
import importlib.util
import json
import stat
import sys
from typing import Any

import numpy as np

ROOT = Path(__file__).resolve().parents[6]
HERE = Path(__file__).resolve().parent
CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/relaxed_width_probe_v1/relaxed_width_probe_v2.json"
DIAG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_recall_v1/diagnose_misses.py"
DIAG_SHA = "32662ad05c220de6a9a5fce451f5101baab32ce4ac6156f1ba4b58d3266b834a"
DIAG_REPORT_REL = "artifacts/stroke-seeded-xtick-short-tick-recall-v5/miss_diagnosis.json"
DIAG_REPORT_SHA = "10f1282c134285070c9fcbc29d09fb8d2c46d1fe27ccac2d1c31d9592077d1d8"
PROBE_MANIFEST_REL = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json"
PROBE_MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
RAW_SCANNER_REL = "artifacts/stroke-seeded-xtick-v1-ink-p2-vs-curated-mix-v2/scanner_proposals.json"
RAW_SCANNER_SHA = "0297966c41243bd4a27a0c1fa0aa7e849fc8beb5fa7a5dc177fc7e9029cb6081"
P5_EVALUATOR_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/wide_proposal_v1/fine_tune_p5/evaluate_p5_probe.py"
P5_EVALUATOR_SHA = "1dd3ada4bd9fdd9de33b71c72c604c05b8fe637c8561b822e82834c413f455b4"
OUTPUT_REL = "artifacts/stroke-seeded-xtick-v1-relaxed-width-p3-p5-probe-v2"
PREFLIGHT_REL = OUTPUT_REL + "/preflight.json"
CANDIDATES_REL = OUTPUT_REL + "/candidate_rows.json"
PAIRED_REL = OUTPUT_REL + "/paired_report.json"
THRESHOLD = 0.5
WIDE_CLUSTER_LIMIT = 1_000_000
EXPECTED_BASELINE_ROWS = 365
EXPECTED_RELAXED_ROWS = 442
EXPECTED_VISIBLE_DENOMINATOR = 181


class RelaxedWidthError(RuntimeError):
    pass


def digest(raw: bytes) -> str:
    return sha256(raw).hexdigest()


def read_project(relative: str, maximum: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or maximum < 1:
        raise RelaxedWidthError("invalid bounded project path")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise RelaxedWidthError(f"reparse path rejected: {relative}")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise RelaxedWidthError(f"non-directory path component: {relative}")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > maximum):
            raise RelaxedWidthError(f"input is not bounded regular file: {relative}")
    with current.open("rb") as stream:
        raw = stream.read(maximum + 1)
    if len(raw) > maximum:
        raise RelaxedWidthError(f"input exceeds bound: {relative}")
    return raw


def load_exact(relative: str, expected: str, module_name: str, maximum: int = 4_000_000):
    raw = read_project(relative, maximum)
    if digest(raw) != expected:
        raise RelaxedWidthError(f"pinned source changed: {relative}")
    path = ROOT / relative
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None:
        raise RelaxedWidthError(f"cannot load pinned source: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    if digest(read_project(relative, maximum)) != expected:
        raise RelaxedWidthError(f"source changed during load: {relative}")
    return module


def validate_runtime(runtime: dict[str, Any]) -> None:
    if (runtime.get("device") != "cuda:0" or runtime.get("cpu_fallback") is not False
            or runtime.get("device_name") != "AMD Radeon RX 6600 XT"):
        raise RelaxedWidthError("paired inference requires RX 6600 XT with CPU fallback disabled")


def assert_baseline_parity(observed: list[dict[str, Any]], frozen: list[dict[str, Any]]) -> None:
    if len(observed) != len(frozen):
        raise RelaxedWidthError("frozen baseline proposal count differs from exact scan")
    for actual, expected in zip(observed, frozen):
        geometry = actual["center"] + actual["line"]
        frozen_geometry = expected["center"] + expected["line"]
        if any(abs(float(a) - float(b)) > 1e-6 for a, b in zip(geometry, frozen_geometry)):
            raise RelaxedWidthError("frozen baseline proposal geometry mismatch")
        for key in ("dark_pixel_count", "cluster_width_pixels", "longest_dark_run"):
            if actual[key] != expected[key]:
                raise RelaxedWidthError(f"frozen baseline proposal stat mismatch: {key}")


def globalize(proposal: dict[str, Any], x0: int, y0: int) -> dict[str, Any]:
    result = dict(proposal)
    result["center"] = [float(proposal["center"][0]) + x0, float(proposal["center"][1]) + y0]
    result["line"] = [float(proposal["line"][0]) + x0, float(proposal["line"][1]) + y0,
                      float(proposal["line"][2]) + x0, float(proposal["line"][3]) + y0]
    return result


def quarantine(rows: list[dict[str, Any]], p3_hashes: set[str], p5_hashes: set[str],
               holdout_hashes: set[str]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    eligible, rejected = [], []
    for row in rows:
        reasons = []
        image_hash = row["crop_sha256"]
        if image_hash in p3_hashes:
            reasons.append("exact_P3_training_crop_hash_overlap")
        if image_hash in p5_hashes:
            reasons.append("exact_P5_training_crop_hash_overlap")
        if image_hash in holdout_hashes:
            reasons.append("exact_P1_internal_holdout_crop_hash_metadata_overlap")
        if reasons:
            rejected.append({"proposal_id": row["proposal_id"], "crop_sha256": image_hash, "reasons": reasons})
        else:
            eligible.append(row)
    return eligible, rejected


def holdout_hashes_from_metadata(train_manifest: dict[str, Any], holdout_groups: set[str]) -> set[str]:
    return {str(row["image_sha256"]) for row in train_manifest["records"]
            if row.get("source_group_id") in holdout_groups and isinstance(row.get("image_sha256"), str)}


def validate_training_hash_sets(p3_hashes: set[str], p5_hashes: set[str],
                                expected_p3: int = 446, expected_p5: int = 465) -> None:
    if len(p3_hashes) != expected_p3 or len(p5_hashes) != expected_p5 or not p3_hashes <= p5_hashes:
        raise RelaxedWidthError("P3/P5 train crop hash counts or subset relation differ from pinned audit")


def source_overlap_sets(p5_groups: set[str], holdout_groups: set[str],
                        train_sources: list[dict[str, Any]], probe_sources: list[dict[str, Any]]) -> dict[str, list[str]]:
    train_meta = {row["source_id"]: row for row in train_sources}
    probe_meta = {row["source_id"]: row for row in probe_sources}
    holdout_meta = {row["source_id"]: row for row in train_sources if row["source_id"] in holdout_groups}
    train_identity = {train_meta[group]["source_identity_sha256"] for group in p5_groups}
    train_panel = {train_meta[group]["reconstructed_full_panel_pixel_sha256"] for group in p5_groups}
    holdout_identity = {row["source_identity_sha256"] for row in holdout_meta.values()}
    holdout_panel = {row["reconstructed_full_panel_pixel_sha256"] for row in holdout_meta.values()}
    probe_identity = {row["source_identity_sha256"] for row in probe_meta.values()}
    probe_panel = {row["source_pixel_sha256"] for row in probe_meta.values()}
    return {"p5_train_source_id_overlap": sorted(p5_groups & set(probe_meta)),
        "p1_holdout_source_id_overlap": sorted(holdout_groups & set(probe_meta)),
        "p5_train_source_identity_overlap": sorted(train_identity & probe_identity),
        "p5_train_full_panel_pixel_hash_overlap": sorted(train_panel & probe_panel),
        "p1_holdout_source_identity_overlap": sorted(holdout_identity & probe_identity),
        "p1_holdout_full_panel_pixel_hash_overlap": sorted(holdout_panel & probe_panel)}


def serializable_candidate_rows(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    return [{key: value for key, value in row.items() if key != "pixels"} for row in rows]


def _candidate_rows(context: dict[str, Any], scan_module: Any, p3_module: Any,
                    expected_counts: tuple[int, int, int] = (EXPECTED_BASELINE_ROWS, EXPECTED_RELAXED_ROWS, 24)
                    ) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    scanner = context["scanner"]
    manifest = context["probe_manifest"]
    raw_by_source = {entry["source_id"]: entry for entry in scanner["source_results"]}
    source_by_id = {entry["source_id"]: entry for entry in manifest["sources"]}
    if set(raw_by_source) != set(source_by_id):
        raise RelaxedWidthError("scanner source set differs from pinned probe manifest")
    rows: list[dict[str, Any]] = []
    baseline_count = 0
    panels_seen = 0
    source_receipts = []
    for source_id in sorted(source_by_id):
        image = context["source_images"][source_id]
        source = source_by_id[source_id]
        image_sha = digest(image.tobytes())
        if image_sha != source["source_pixel_sha256"]:
            raise RelaxedWidthError(f"full panel image hash changed: {source_id}")
        source_result = raw_by_source[source_id]
        if source_result["expected_source_pixel_sha256"] != image_sha:
            raise RelaxedWidthError(f"frozen scanner source hash changed: {source_id}")
        for panel in source_result["panels"]:
            panels_seen += 1
            x0, y0, x1, y1 = (int(value) for value in panel["bounds"])
            gray = np.asarray(image.crop((x0, y0, x1, y1)).convert("L"), dtype=np.uint8)
            axis = panel["selected_x_axis_line"]
            local_axis = [axis[0] - x0, axis[1] - y0, axis[2] - x0, axis[3] - y0]
            baseline_local, _ = scan_module.scan(gray, local_axis)
            baseline_global = [globalize(proposal, x0, y0) for proposal in baseline_local]
            assert_baseline_parity(baseline_global, panel["ink_proposals"])
            baseline_count += len(baseline_global)
            wide, _ = scan_module.scan(gray, local_axis, max_cluster_width=WIDE_CLUSTER_LIMIT)
            for index, local in enumerate(wide):
                proposal = globalize(local, x0, y0)
                crop_png, pixels = p3_module.crop_bytes(image, proposal["center"])
                rows.append({"proposal_id": f"relaxed-width-v2:{source_id}:{panel['panel_id']}:{index}",
                    "source_id": source_id, "panel_id": panel["panel_id"], "proposal_index": index,
                    "proposal": proposal, "crop_sha256": digest(crop_png),
                    "needs_review": bool(panel["needs_review"]), "pixels": pixels})
        source_receipts.append({"source_id": source_id, "source_pixel_sha256": image_sha,
            "panel_count": len(source_result["panels"])})
    if (baseline_count, len(rows), panels_seen) != expected_counts:
        raise RelaxedWidthError("truth-blind scan proposal/panel counts differ from pinned comparison")
    return rows, {"source_count": len(source_by_id), "panel_count": panels_seen,
        "baseline_parity_proposal_count": baseline_count, "relaxed_width_proposal_count": len(rows),
        "full_panel_hashes": source_receipts}


def _metadata_overlap(context: dict[str, Any], p4_data: dict[str, Any], p3_hashes: set[str]) -> dict[str, Any]:
    p5_hashes = {str(row["image_sha256"]) for row in p4_data["rows"]}
    holdout_hashes = holdout_hashes_from_metadata(context["train_manifest"], set(context["holdout_groups"]))
    validate_training_hash_sets(p3_hashes, p5_hashes)
    return {"p3_train_hash_count": len(p3_hashes), "p3_train_hash_set_sha256": digest(
                json.dumps(sorted(p3_hashes), separators=(",", ":")).encode()),
            "p5_train_hash_count": len(p5_hashes), "p5_train_hash_set_sha256": digest(
                json.dumps(sorted(p5_hashes), separators=(",", ":")).encode()),
            "p3_hashes_are_subset_of_p5": True, "holdout_metadata_hash_count": len(holdout_hashes),
            "p3_hashes": p3_hashes, "p5_hashes": p5_hashes, "holdout_hashes": holdout_hashes}


def generate_preflight(config: dict[str, Any], config_sha: str, runner_sha: str):
    p5_eval = load_exact(P5_EVALUATOR_REL, P5_EVALUATOR_SHA, "relaxed_width_pinned_p5_evaluator")
    bound, _old_audit, context, _old_common = p5_eval.load_context()
    p3_eval = bound["p3_evaluator"]
    scan_module = load_exact(DIAG_REL, DIAG_SHA, "relaxed_width_pinned_truth_blind_scanner")
    diagnostic = json.loads(read_project(DIAG_REPORT_REL, 2_000_000))
    if diagnostic["wide_cluster_rule_comparison"]["metrics"]["proposals"] != EXPECTED_RELAXED_ROWS:
        raise RelaxedWidthError("pinned relaxed-width count differs from diagnostic report")
    rows, source_summary = _candidate_rows(context, scan_module, p3_eval)
    p3_hashes = set(context["train_hash_labels"])
    metadata = _metadata_overlap(context, bound["p4_data"], p3_hashes)
    eligible, quarantined = quarantine(rows, metadata["p3_hashes"], metadata["p5_hashes"], metadata["holdout_hashes"])
    duplicate_rows: dict[str, list[str]] = defaultdict(list)
    for row in rows:
        duplicate_rows[row["crop_sha256"]].append(row["proposal_id"])
    audit = {"schema": "graphreader.relaxed-width-p3-p5-overlap-audit.v2",
        "proposal_rows": len(rows), "unique_crop_hashes": len(duplicate_rows),
        "duplicate_crop_rows_preserved": len(rows) - len(duplicate_rows),
        "p3_train_crop_hash_count": metadata["p3_train_hash_count"],
        "p5_train_crop_hash_count": metadata["p5_train_hash_count"],
        "p3_train_hashes_subset_of_p5": True,
        "p3_train_overlap_rows": sum(row["crop_sha256"] in metadata["p3_hashes"] for row in rows),
        "p5_train_overlap_rows": sum(row["crop_sha256"] in metadata["p5_hashes"] for row in rows),
        "holdout_metadata_overlap_rows": sum(row["crop_sha256"] in metadata["holdout_hashes"] for row in rows),
        "quarantined_rows": len(quarantined), "eligible_rows": len(eligible),
        "quarantine": quarantined,
        "source_id_overlap": [], "source_identity_overlap": [], "full_panel_hash_overlap": [],
        "private_or_sealed_inputs_read": False, "holdout_image_or_annotation_bytes_read": False}
    p3_ids = set(context["train_groups"])
    probe_ids = {s["source_id"] for s in context["probe_manifest"]["sources"]}
    if p3_ids & probe_ids:
        raise RelaxedWidthError("probe source ids overlap P3 training ids")
    p5_ids = set(bound["p4_data"]["groups"])
    holdout_ids = set(context["holdout_groups"])
    if holdout_ids & probe_ids:
        raise RelaxedWidthError("probe source ids overlap P1 holdout ids")
    audit["source_id_overlap"] = sorted(p3_ids & probe_ids)
    audit.update(source_overlap_sets(p5_ids, holdout_ids, context["train_manifest"]["source_groups"],
        context["probe_manifest"]["sources"]))
    if any(audit[k] for k in ("p5_train_source_id_overlap", "p1_holdout_source_id_overlap",
            "p5_train_source_identity_overlap", "p5_train_full_panel_pixel_hash_overlap",
            "p1_holdout_source_identity_overlap", "p1_holdout_full_panel_pixel_hash_overlap")):
        raise RelaxedWidthError("train/holdout source or panel identity overlaps probe")
    manifest = {"schema": "graphreader.relaxed-width-truth-blind-proposals.v2",
        "scanner_source_sha256": DIAG_SHA, "scanner_variant": {"maximum_cluster_width_px": WIDE_CLUSTER_LIMIT,
            "only_changed_setting": "maximum cluster width increased from 8 to 1000000 pixels"},
        "probe_manifest_sha256": PROBE_MANIFEST_SHA, "raw_baseline_proposals_sha256": RAW_SCANNER_SHA,
        "baseline_parity_proposal_count": source_summary["baseline_parity_proposal_count"],
        "source_summary": source_summary,
        "crop_geometry": "pinned P1 materializer crop at proposal center x and center y minus 2px",
        "rows": serializable_candidate_rows(rows)}
    manifest_raw = (json.dumps(manifest, sort_keys=True, separators=(",", ":")) + "\n").encode()
    audit_raw = (json.dumps(audit, sort_keys=True, separators=(",", ":")) + "\n").encode()
    receipt = {"schema": "graphreader.relaxed-width-p3-p5-preflight.v2",
        "status": "ready-for-paired-rx6600xt-inference",
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "runner_sha256": runner_sha, "config_sha256": config_sha,
        "scanner_diagnostic_sha256": DIAG_SHA, "scanner_comparison_report_sha256": DIAG_REPORT_SHA,
        "probe_manifest_sha256": PROBE_MANIFEST_SHA, "raw_baseline_proposals_sha256": RAW_SCANNER_SHA,
        "p5_evaluator_sha256": P5_EVALUATOR_SHA,
        "p3_checkpoint_sha256": bound["p3_evaluator"].P3_CHECKPOINT_SHA,
        "p5_checkpoint_sha256": digest(bound["p5_checkpoint"]),
        "p5_training_report_sha256": digest(read_project(p5_eval.P5_REPORT_REL, 1_000_000)),
        "threshold": THRESHOLD, "visible_truth_denominator_expected_after_inference": EXPECTED_VISIBLE_DENOMINATOR,
        "rows": {"baseline_parity": source_summary["baseline_parity_proposal_count"],
            "relaxed_width_total": len(rows), "unique_crop_hashes": len(duplicate_rows),
            "eligible_after_hash_quarantine": len(eligible), "quarantined": len(quarantined),
            "duplicate_rows_preserved": True},
        "overlap_audit_sha256": digest(audit_raw), "candidate_manifest_sha256": digest(manifest_raw),
        "runtime": "CPU-only preflight; no model inference or training performed",
        "truth_read": False, "holdout_bytes_read": False, "private_or_sealed_inputs_read": False,
        "production_activation": False, "status_note": "P3/P5 inference may run only via project RX 6600 XT launcher after external GPU job completes",
        "config_recipe_id": config["recipe_id"]}
    return receipt, manifest_raw, audit_raw, rows, eligible, quarantined, context, bound, p5_eval


def preflight() -> dict[str, Any]:
    source_rel = Path(__file__).resolve().relative_to(ROOT).as_posix()
    runner_raw = read_project(source_rel, 4_000_000)
    runner_sha = digest(runner_raw)
    cfg_raw = read_project(CONFIG_REL, 65_536)
    config = json.loads(cfg_raw)
    expected = {"schema": "graphreader.relaxed-width-p3-p5-probe-config.v2",
        "recipe_id": "truth-blind-relaxed-width-paired-frozen-p3-p5-v2",
        "evaluator_sha256": runner_sha, "scanner_diagnostic_sha256": DIAG_SHA,
        "scanner_comparison_report_sha256": DIAG_REPORT_SHA,
        "probe_manifest_sha256": PROBE_MANIFEST_SHA, "baseline_scanner_proposals_sha256": RAW_SCANNER_SHA,
        "p5_evaluator_sha256": P5_EVALUATOR_SHA, "threshold": THRESHOLD,
        "maximum_cluster_width_px": WIDE_CLUSTER_LIMIT, "expected_baseline_rows": EXPECTED_BASELINE_ROWS,
        "expected_relaxed_rows": EXPECTED_RELAXED_ROWS, "expected_visible_denominator": EXPECTED_VISIBLE_DENOMINATOR,
        "training_performed": False, "holdout_image_or_annotation_reads_allowed": False,
        "private_or_sealed_reads_allowed": False, "production_activation": False,
        "output_relative": OUTPUT_REL, "preflight_relative": PREFLIGHT_REL,
        "candidate_manifest_relative": CANDIDATES_REL, "paired_report_relative": PAIRED_REL}
    if config != expected:
        raise RelaxedWidthError("predeclared relaxed-width config/source identity mismatch")
    cfg_sha = digest(cfg_raw)
    if (ROOT / OUTPUT_REL).exists():
        raise RelaxedWidthError("relaxed-width output exists; refusing overwrite")
    receipt, manifest_raw, audit_raw, rows, eligible, quarantined, context, bound, p5_eval = generate_preflight(config, cfg_sha, runner_sha)
    out = ROOT / OUTPUT_REL
    out.mkdir(parents=True, exist_ok=False)
    (out / "candidate_rows.json").write_bytes(manifest_raw)
    (out / "overlap_audit.json").write_bytes(audit_raw)
    (out / "preflight.json").write_text(json.dumps(receipt, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    return receipt


def _metric(panels: list[dict[str, Any]], key: str) -> dict[str, Any]:
    visible = sum(p["visible_truth_count"] for p in panels)
    proposals = sum(p[key]["usable_detected_x_stroke_count"] for p in panels)
    matched = sum(p[key]["matched_count"] for p in panels)
    no_visible = [p for p in panels if p["visible_truth_count"] == 0]
    return {"visible_truth_ticks": visible, "proposals": proposals, "true_matches": matched,
        "unmatched_proposals": proposals - matched,
        "precision": matched / proposals if proposals else 0.0,
        "recall": matched / visible if visible else 0.0,
        "no_visible_panels": len(no_visible),
        "false_proposals_on_no_visible_panels": sum(p[key]["usable_detected_x_stroke_count"] for p in no_visible),
        "no_visible_panels_with_false_proposals": sum(p[key]["usable_detected_x_stroke_count"] > 0 for p in no_visible),
        "axis_uncertain_panels": sum(bool(p["needs_review"]) for p in panels),
        "proposals_on_axis_uncertain_panels": sum(p[key]["usable_detected_x_stroke_count"] for p in panels if p["needs_review"])}


def run_inference() -> dict[str, Any]:
    """Paired model inference. Invoke only after the parent confirms GPU availability."""
    preflight_raw = read_project(PREFLIGHT_REL, 1_000_000)
    saved = json.loads(preflight_raw)
    cfg_raw = read_project(CONFIG_REL, 65_536)
    config = json.loads(cfg_raw)
    source_rel = Path(__file__).resolve().relative_to(ROOT).as_posix()
    runner_sha = digest(read_project(source_rel, 4_000_000))
    current, manifest_raw, audit_raw, rows, eligible, quarantined, context, bound, p5_eval = generate_preflight(
        config, digest(cfg_raw), runner_sha)
    previous = dict(saved)
    recomputed = dict(current)
    previous.pop("created_utc", None)
    recomputed.pop("created_utc", None)
    saved_manifest = read_project(CANDIDATES_REL, 4_000_000)
    saved_audit = read_project(OUTPUT_REL + "/overlap_audit.json", 1_000_000)
    if (previous != recomputed or digest(manifest_raw) != saved["candidate_manifest_sha256"]
            or digest(audit_raw) != saved["overlap_audit_sha256"]
            or digest(saved_manifest) != saved["candidate_manifest_sha256"]
            or digest(saved_audit) != saved["overlap_audit_sha256"]):
        raise RelaxedWidthError("preflight source/data/overlap bindings changed before inference")
    p3_values, p3_runtime = bound["p3_evaluator"]._infer_p3(eligible)
    p5_values, p5_runtime = p5_eval.p5_scores(bound["p4_trainer"], bound["p5_config"],
        bound["p5_report"], bound["p5_checkpoint"], bound["p4_data"], eligible)
    if len(p3_values) != len(eligible) or len(p5_values) != len(eligible):
        raise RelaxedWidthError("P3/P5 did not score the same eligible candidate rows")
    validate_runtime(p3_runtime["runtime"])
    validate_runtime(p5_runtime["runtime"])
    # Only after both frozen models finish inference do we open synthetic target geometry.
    truth_helper = p5_eval.load_exact(
        "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/opencv_hough_minline_diagnostic/run_hough_min5.py",
        "ee3b0c35639f567ec6bdaa705ba0ce1e7b797d95743ad431651338444f1cc979", "relaxed_width_truth_geometry")
    matcher = p5_eval.load_exact(
        "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/axis_stroke_alignment/run_axis_alignment.py",
        "b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de", "relaxed_width_axis_matcher")
    truth_by_panel = {}
    for source_id, rendered in context["rendered_sources"].items():
        truth_by_panel.update(truth_helper.source_truths(source_id, rendered["panels"]))
    if sum(t["source_stroke_visible"] for truths in truth_by_panel.values() for t in truths) != EXPECTED_VISIBLE_DENOMINATOR:
        raise RelaxedWidthError("visible truth denominator changed after paired inference")
    scan_by_panel = {p["panel_id"]: p for s in context["scanner"]["source_results"] for p in s["panels"]}
    row_by_panel: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in eligible:
        row_by_panel[row["panel_id"]].append(row)
    score_by_id = {r["proposal_id"]: (s3, s5) for r, s3, s5 in zip(eligible, p3_values, p5_values)}
    panels = []
    for source in context["probe_manifest"]["sources"]:
        rendered = context["rendered_sources"][source["source_id"]]
        for rp in rendered["panels"]:
            panel_id = rp["panel_id"]
            scan_panel = scan_by_panel[panel_id]
            visible = [t for t in truth_by_panel[panel_id] if t["source_stroke_visible"]]
            panel_rows = row_by_panel[panel_id]
            panel = {"source_id": source["source_id"], "panel_id": panel_id,
                "visible_truth_count": len(visible), "needs_review": bool(scan_panel["needs_review"])}
            for key in ("raw_scan", "p3", "p5"):
                selected = [r for r in panel_rows if key == "raw_scan" or score_by_id[r["proposal_id"]][0 if key == "p3" else 1] >= THRESHOLD]
                detections = [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in selected]
                panel[key] = matcher.match_panel(visible, detections)
            panels.append(panel)
    by_source = {}
    for source_id in sorted({p["source_id"] for p in panels}):
        subset = [p for p in panels if p["source_id"] == source_id]
        by_source[source_id] = {key: _metric(subset, key) for key in ("raw_scan", "p3", "p5")}
    report = {"schema": "graphreader.relaxed-width-p3-p5-paired-report.v2",
        "status": "completed-exploratory-consumed-synthetic-probe",
        "acceptance_status": "consumed synthetic development evidence only; not acceptance",
        "production_activation": False, "training_performed": False, "threshold": THRESHOLD,
        "proposal_counts": {"generated": len(rows), "eligible": len(eligible), "quarantined": len(quarantined),
            "duplicate_rows_preserved": True},
        "visible_truth_denominator": EXPECTED_VISIBLE_DENOMINATOR,
        "metrics": {key: _metric(panels, key) for key in ("raw_scan", "p3", "p5")},
        "per_source_metrics": by_source,
        "runtime": {"p3": p3_runtime, "p5": p5_runtime},
        "inputs": {"preflight_sha256": digest(preflight_raw), "candidate_manifest_sha256": digest(
            read_project(CANDIDATES_REL, 4_000_000)), "overlap_audit_sha256": digest(
            read_project(OUTPUT_REL + "/overlap_audit.json", 1_000_000)),
            "p3_checkpoint_sha256": p3_runtime["checkpoint_sha256"], "p5_checkpoint_sha256": p5_runtime["checkpoint_sha256"]},
        "truth_read_after_all_model_inference": True, "private_or_sealed_inputs_read": False}
    out = ROOT / PAIRED_REL
    if out.exists():
        raise RelaxedWidthError("paired report exists; refusing overwrite")
    out.write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    return report


def _load_bound_context():
    p5_eval = load_exact(P5_EVALUATOR_REL, P5_EVALUATOR_SHA, "relaxed_width_pinned_p5_evaluator_run")
    bound, _audit, context, _common = p5_eval.load_context()
    return bound, context, p5_eval


def _candidate_rows_for_inference(manifest: dict[str, Any]):
    # Reconstruct pixels from pinned source recipes rather than trusting cached crop bytes.
    bound, context, _p5_eval = _load_bound_context()
    scan_module = load_exact(DIAG_REL, DIAG_SHA, "relaxed_width_pinned_truth_blind_scanner_run")
    _p3_eval = bound["p3_evaluator"]
    rows, summary = _candidate_rows(context, scan_module, _p3_eval)
    current = serializable_candidate_rows(rows)
    if current != manifest["rows"]:
        raise RelaxedWidthError("regenerated relaxed-width proposal/crop identities differ from preflight")
    return rows, summary


def main() -> int:
    if "--preflight" in sys.argv:
        print(json.dumps(preflight(), sort_keys=True))
        return 0
    if "--run-inference" in sys.argv:
        print(json.dumps(run_inference(), sort_keys=True))
        return 0
    raise RelaxedWidthError("choose --preflight or --run-inference explicitly")


if __name__ == "__main__":
    raise SystemExit(main())
