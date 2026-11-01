# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fit-only truth-derived constant center calibration, evaluated source-disjoint."""

from __future__ import annotations

from collections import Counter, defaultdict
from hashlib import sha256
import json
import math
from pathlib import Path
from statistics import median
from typing import Any

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_train_miss_v1 import scan_train_miss as scan
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1 import curate_train_proposals as curation

RUN_ROOT = scan.ROOT / "artifacts/stroke-seeded-xtick-v1-scan-train-miss-v2"
RUN_REPORT_SHA = "a08da3e040112e4476777c7c7d4e21b4882e1f32e1f2a37209bebd5b84984e83"
FIT_PROPOSALS_SHA = "e3d1476b8264e61e35c1187e5d7851cd5abf2f5ff30e889043a8aa0d9eee99ca"
OUTPUT_ROOT = scan.ROOT / "artifacts/stroke-seeded-xtick-v1-scan-center-position-v1"
SEED_DOMAIN = "graphreader-scan-center-position-validation-v1|"
MAX_CALIBRATION_DISTANCE_PX = 12.0
VALIDATION_COMPONENT_COUNT = 4


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def freeze_component_split(components: list[list[str]], fit_ids: list[str],
                           positive_rows_by_source: dict[str, int],
                           previous_validation_ids: set[str]) -> tuple[list[str], list[str], list[list[str]]]:
    """Choose four hash-ordered whole source components before reading annotations."""
    fit = set(fit_ids)
    normalized = [sorted(set(group) & fit) for group in components]
    normalized = [group for group in normalized if group]
    flattened = [source for group in normalized for source in group]
    if set(flattened) != fit or len(flattened) != len(set(flattened)):
        raise ValueError("fit components must partition all fit source IDs exactly once")
    ordered = sorted(normalized, key=lambda group: sha256((SEED_DOMAIN + "|".join(group)).encode()).hexdigest())
    if len(ordered) <= VALIDATION_COMPONENT_COUNT:
        raise ValueError("not enough components to create a source-disjoint train/validation split")
    validation_groups = ordered[:VALIDATION_COMPONENT_COUNT]
    validation_ids = sorted(source for group in validation_groups for source in group)
    training_ids = sorted(fit - set(validation_ids))
    if set(validation_ids) & set(training_ids) or set(validation_ids) & previous_validation_ids:
        raise ValueError("new center-rule validation split overlaps train or prior validation sources")
    if sum(positive_rows_by_source.get(source, 0) for source in validation_ids) < 20:
        raise ValueError("frozen four-component validation split has fewer than 20 positive rows")
    if sum(positive_rows_by_source.get(source, 0) for source in training_ids) < 20:
        raise ValueError("center-rule training split has fewer than 20 positive rows")
    return training_ids, validation_ids, validation_groups


def load_truths(source_ids: list[str], v6: dict[str, Any]) -> dict[tuple[str, str], list[dict[str, Any]]]:
    result: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    allowed = set(source_ids)
    for ref in v6["records"]:
        if ref["source_id"] not in allowed or ref["split"] != "train" or ref["kind"] != "positive":
            continue
        payload = scan.read_pinned(scan.ROOT / scan.V6_REL / ref["annotation_path"], ref["annotation_sha256"], 65_536)
        row = json.loads(payload)
        if row.get("source_id") != ref["source_id"] or row.get("split") != "train":
            raise ValueError("positive annotation does not match its pinned training source")
        result[(row["source_id"], row["source_panel_id"])].append({
            "tick_id": row["tick_id"], "stroke_center_original_xy": row["stroke_center_original_xy"],
            "stroke_line_original_xyxy": row["stroke_line_original_xyxy"], "visible": True,
            "ground_truth_target_unambiguous": True,
        })
    return result


def calibrate_translation(outputs: list[dict[str, Any]], truths: dict[tuple[str, str], list[dict[str, Any]]],
                          training_ids: list[str]) -> dict[str, Any]:
    """Use mutual nearest truth/proposal pairs within 12px, then componentwise median residual."""
    dxs: list[float] = []
    dys: list[float] = []
    source_pairs: Counter[str] = Counter()
    for source in outputs:
        source_id = source["source_id"]
        if source_id not in training_ids:
            continue
        for panel in source["panels"]:
            ts = truths.get((source_id, panel["panel_id"]), [])
            ps = panel["ink_proposals"]
            if not ts or not ps:
                continue
            t_nearest: dict[int, tuple[float, int]] = {}
            p_nearest: dict[int, tuple[float, int]] = {}
            for ti, truth in enumerate(ts):
                tx, ty = map(float, truth["stroke_center_original_xy"])
                candidates = [(math.hypot(float(p["center"][0]) - tx, float(p["center"][1]) - ty), pi)
                              for pi, p in enumerate(ps)]
                t_nearest[ti] = min(candidates)
            for pi, proposal in enumerate(ps):
                px, py = map(float, proposal["center"])
                candidates = [(math.hypot(px - float(t["stroke_center_original_xy"][0]),
                                          py - float(t["stroke_center_original_xy"][1])), ti)
                              for ti, t in enumerate(ts)]
                p_nearest[pi] = min(candidates)
            for ti, (distance, pi) in t_nearest.items():
                if distance > MAX_CALIBRATION_DISTANCE_PX or p_nearest[pi][1] != ti:
                    continue
                tx, ty = map(float, ts[ti]["stroke_center_original_xy"])
                px, py = map(float, ps[pi]["center"])
                dxs.append(tx - px)
                dys.append(ty - py)
                source_pairs[source_id] += 1
    if len(dxs) < 20:
        raise ValueError(f"too few unambiguous mutual-nearest train pairs for calibration: {len(dxs)}")
    return {"method": "componentwise median truth-minus-center over mutual nearest pairs within 12px",
            "max_pair_distance_px": MAX_CALIBRATION_DISTANCE_PX, "pair_count": len(dxs),
            "pair_count_by_source": dict(sorted(source_pairs.items())),
            "dx_px": median(dxs), "dy_px": median(dys),
            "runtime_inputs": "fixed dx/dy only; no labels or truth are used at runtime"}


def shifted(proposals: list[dict[str, Any]], dx: float, dy: float) -> list[dict[str, Any]]:
    result = []
    for proposal in proposals:
        row = dict(proposal)
        row["center"] = [float(proposal["center"][0]) + dx, float(proposal["center"][1]) + dy]
        row["line"] = [float(proposal["line"][0]) + dx, float(proposal["line"][1]) + dy,
                       float(proposal["line"][2]) + dx, float(proposal["line"][3]) + dy]
        result.append(row)
    return result


def score(outputs: list[dict[str, Any]], truths: dict[tuple[str, str], list[dict[str, Any]]],
          source_ids: list[str], correction: tuple[float, float]) -> dict[str, Any]:
    dx, dy = correction
    tp = fp = ambiguous = proposals = positive_truths = no_truth_panels = no_truth_proposals = 0
    per_source: dict[str, dict[str, int]] = {}
    for source in outputs:
        source_id = source["source_id"]
        if source_id not in source_ids:
            continue
        counts = Counter()
        for panel in source["panels"]:
            ts = truths.get((source_id, panel["panel_id"]), [])
            ps = shifted(panel["ink_proposals"], dx, dy)
            assignments = curation.assign_proposals(ps, ts)
            p_tp = sum(row["state"] == "unique_visible_tick" for row in assignments)
            p_fp = sum(row["state"] == "no_ground_truth_tick_nearby" for row in assignments)
            p_amb = sum(row["state"] == "ambiguous" for row in assignments)
            tp += p_tp; fp += p_fp; ambiguous += p_amb; proposals += len(ps); positive_truths += len(ts)
            counts["tp"] += p_tp; counts["fp"] += p_fp; counts["ambiguous"] += p_amb
            if not ts:
                no_truth_panels += 1; no_truth_proposals += len(ps)
                counts["no_truth_panel_proposals"] += len(ps)
        per_source[source_id] = dict(counts)
    return {"source_ids": list(source_ids), "positive_truths": positive_truths,
            "proposals": proposals, "true_matches": tp, "false_proposals": fp,
            "ambiguous_proposals": ambiguous,
            "precision_excluding_ambiguous": tp / (tp + fp) if tp + fp else None,
            "recall": tp / positive_truths if positive_truths else None,
            "no_visible_truth_panels": no_truth_panels, "proposals_on_no_visible_truth_panels": no_truth_proposals,
            "per_source": per_source}


def run() -> dict[str, Any]:
    run_bytes = scan.read_pinned(RUN_ROOT / "report.json", RUN_REPORT_SHA, 2_000_000)
    run_report = json.loads(run_bytes)
    proposal_bytes = scan.read_pinned(RUN_ROOT / "fit_truth_blind_proposals.json", FIT_PROPOSALS_SHA, 2_000_000)
    outputs = json.loads(proposal_bytes)
    v6_bytes = scan.read_pinned(scan.ROOT / scan.V6_MANIFEST_REL, scan.V6_MANIFEST_SHA, 5_000_000)
    v6 = json.loads(v6_bytes)
    # The V6 fit split includes a known failed scan source; only successfully scored sources have proposals.
    fit_ids = list(run_report["fit_sources_scored"])
    if not set(fit_ids).issubset(set(run_report["split"]["fit_source_ids"])):
        raise ValueError("scored source IDs escape the frozen fit split")
    if set(fit_ids) != {row["source_id"] for row in outputs}:
        raise ValueError("saved truth-blind proposals do not exactly cover frozen fit sources")
    positive_counts = Counter(row["source_id"] for row in v6["records"]
                              if row["source_id"] in fit_ids and row["split"] == "train" and row["kind"] == "positive")
    train_ids, validation_ids, validation_groups = freeze_component_split(
        run_report["split"]["fit_components"], fit_ids, positive_counts,
        set(run_report["split"]["validation_source_ids"]))
    # Load training labels only, estimate correction, then freeze the rule before validation labels are read.
    train_truths = load_truths(train_ids, v6)
    calibration = calibrate_translation(outputs, train_truths, train_ids)
    OUTPUT_ROOT.mkdir(parents=True, exist_ok=False)
    frozen = {"schema": "graphreader.scan-center-position-rule.v1", "rule_id": "global_median_center_translation_v1",
              "scanner_width_px": 8, "all_other_scanner_settings": "unchanged from frozen width-8 baseline",
              "split_policy": f"first {VALIDATION_COMPONENT_COUNT} whole components by SHA256(domain|sorted source IDs); domain={SEED_DOMAIN}",
              "fit_source_ids": train_ids, "new_validation_source_ids": validation_ids,
              "new_validation_components": validation_groups, "calibration": calibration,
              "validation_labels_opened": False,
              "source_hashes": {"prior_run_report": digest(run_bytes), "fit_truth_blind_proposals": digest(proposal_bytes),
                                "v6_train_manifest": digest(v6_bytes),
                                "scanner_runner": digest(Path(scan.__file__).read_bytes()),
                                "curation_helper": digest(Path(curation.__file__).read_bytes()),
                                "experiment_source": digest(Path(__file__).read_bytes())}}
    frozen_path = OUTPUT_ROOT / "frozen_rule.json"
    frozen_bytes = json.dumps(frozen, sort_keys=True, separators=(",", ":")).encode()
    frozen_path.write_bytes(frozen_bytes)
    # Only now load the new validation annotations.
    validation_truths = load_truths(validation_ids, v6)
    baseline = score(outputs, validation_truths, validation_ids, (0.0, 0.0))
    adjusted = score(outputs, validation_truths, validation_ids,
                      (float(calibration["dx_px"]), float(calibration["dy_px"])))
    report = {"schema": "graphreader.scan-center-position-evaluation.v1",
              "acceptance_status": "exploratory internal synthetic validation only",
              "frozen_rule_sha256": digest(frozen_bytes), "frozen_rule": frozen,
              "validation_labels_hashes": {"annotation_sha256": sha256("".join(sorted(
                  ref["annotation_sha256"] for ref in v6["records"] if ref["source_id"] in validation_ids
                  and ref["kind"] == "positive" and ref["split"] == "train")).encode()).hexdigest()},
              "baseline": score(outputs, validation_truths, validation_ids, (0.0, 0.0)),
              "center_corrected": adjusted,
              "correction_delta_from_baseline": {key: adjusted[key] - baseline[key]
                  for key in ("true_matches", "false_proposals", "ambiguous_proposals", "proposals_on_no_visible_truth_panels")},
              "source_disjoint_new_validation": not bool(set(train_ids) & set(validation_ids)),
              "prior_validation_sources_unread": True,
              "truth_handling": "Fit labels estimated fixed dx/dy; new validation labels loaded only after frozen_rule.json was written. No prior validation, consumed dev, sealed, or private labels were read.",
              "scanner_rerun": False, "training_performed": False, "gpu_used": False,
              "production_behavior_changed": False}
    report_path = OUTPUT_ROOT / "report.json"
    report_path.write_text(json.dumps(report, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    return report


if __name__ == "__main__":
    print(json.dumps(run(), indent=2))
