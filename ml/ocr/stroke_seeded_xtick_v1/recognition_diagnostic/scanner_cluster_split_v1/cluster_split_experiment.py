# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fit-only local support-valley splitting experiment for width-8 ink clusters."""

from __future__ import annotations

from collections import Counter, defaultdict
from hashlib import sha256
import json
import math
from pathlib import Path
from typing import Any

from PIL import Image

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.scanner_train_miss_v1 import scan_train_miss as scan
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1 import curate_train_proposals as curation

RUN_ROOT = scan.ROOT / "artifacts/stroke-seeded-xtick-v1-scan-train-miss-v2"
RUN_REPORT_SHA = "a08da3e040112e4476777c7c7d4e21b4882e1f32e1f2a37209bebd5b84984e83"
FIT_PROPOSALS_SHA = "e3d1476b8264e61e35c1187e5d7851cd5abf2f5ff30e889043a8aa0d9eee99ca"
CENTER_REPORT = scan.ROOT / "artifacts/stroke-seeded-xtick-v1-scan-center-position-v1/report.json"
CENTER_REPORT_SHA = "4957523a6d955f3cb6194daa9d294b9b45af99a19a9d8780f27875dfca2a56d7"
OUTPUT_ROOT = scan.ROOT / "artifacts/stroke-seeded-xtick-v1-scanner-cluster-split-v1"
SPLIT_DOMAIN = "graphreader-scanner-cluster-split-validation-v1|"
WIDTH = 8
PROMINENCE_CANDIDATES = (1, 2, 3, 4)
TARGET_VALIDATION_FRACTION = 0.20
MINIMUM_VALIDATION_COMPONENTS = 3
MAX_PRECISION_DROP = 0.02


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def same_proposals(left: list[dict[str, Any]], right: list[dict[str, Any]]) -> bool:
    if len(left) != len(right):
        return False
    for a, b in zip(left, right):
        for key in ("center", "line"):
            if len(a[key]) != len(b[key]) or max((abs(float(x) - float(y)) for x, y in zip(a[key], b[key])), default=0) > 1e-6:
                return False
        for key in ("cluster_width_pixels", "dark_pixel_count", "longest_dark_run"):
            if a[key] != b[key]:
                return False
    return True


def choose_groups(components: list[list[str]], fit_sources: set[str], excluded_sources: set[str],
                  positive_rows: dict[str, int]) -> tuple[list[str], list[str], list[list[str]]]:
    """Freeze whole-component validation groups by salted ID hash and manifest counts."""
    eligible: list[list[str]] = []
    flattened: list[str] = []
    excluded_component_sources: set[str] = set()
    for component in components:
        all_members = set(component)
        members = sorted(all_members & fit_sources)
        if all_members & excluded_sources:
            # Drop the entire linked component so duplicates cannot cross into the new split.
            excluded_component_sources.update(members)
            continue
        if members:
            eligible.append(members)
            flattened.extend(members)
    if set(flattened) != fit_sources - excluded_component_sources or len(flattened) != len(set(flattened)):
        raise ValueError("eligible components do not partition the remaining scored sources")
    target = math.ceil(TARGET_VALIDATION_FRACTION * sum(positive_rows.get(source, 0) for source in flattened))
    ordered = sorted(eligible, key=lambda group: sha256((SPLIT_DOMAIN + "|".join(group)).encode()).hexdigest())
    validation_components: list[list[str]] = []
    validation_positive = 0
    for group in ordered:
        if len(validation_components) >= MINIMUM_VALIDATION_COMPONENTS and validation_positive >= target:
            break
        validation_components.append(group)
        validation_positive += sum(positive_rows.get(source, 0) for source in group)
    validation_sources = sorted(source for group in validation_components for source in group)
    training_sources = sorted(set(flattened) - set(validation_sources))
    if validation_positive < 20 or sum(positive_rows.get(source, 0) for source in training_sources) < 20:
        raise ValueError("deterministic split lacks enough positive targets for train and validation")
    if set(training_sources) & set(validation_sources):
        raise ValueError("source leakage in cluster-splitting split")
    return training_sources, validation_sources, validation_components


def profile_clusters(image: Image.Image, axis_full: list[float], bounds: list[int]) -> tuple[Image.Image, list[float], list[tuple[float, ...]], list[list[dict[str, int]]]]:
    """Capture the frozen width-8 scan's qualifying per-pixel support clusters."""
    left, top, right, bottom = bounds
    axis = [float(axis_full[0]) - left, float(axis_full[1]) - top,
            float(axis_full[2]) - left, float(axis_full[3]) - top]
    dx, dy = axis[2] - axis[0], axis[3] - axis[1]
    length = math.hypot(dx, dy)
    if not math.isfinite(length) or length <= 8:
        return image.crop((left, top, right, bottom)).convert("L"), axis, (0.0, 0.0, 0.0, 0.0), []
    ux, uy = dx / length, dy / length
    if ux < 0:
        ux, uy = -ux, -uy
    nx, ny = -uy, ux
    if ny < 0:
        nx, ny = -nx, -ny
    frame = image.crop((left, top, right, bottom)).convert("L")
    pixels = frame.load()
    clusters: list[list[dict[str, int]]] = []
    along = 4
    while along <= length - 4:
        dark_count = longest = run = 0
        previous = False
        for offset in range(-12, 13):
            if abs(offset) <= 2:
                run = 0
                previous = False
                continue
            x, y = int(round(axis[0] + ux * along + nx * offset)), int(round(axis[1] + uy * along + ny * offset))
            dark = 0 <= x < frame.width and 0 <= y < frame.height and pixels[x, y] < 200
            if dark:
                dark_count += 1
                run = run + 1 if previous else 1
                longest = max(longest, run)
            else:
                run = 0
            previous = dark
        if dark_count >= 3 and longest >= 2:
            row = {"along": along, "dark_count": dark_count, "longest_run": longest}
            if not clusters or along - clusters[-1][-1]["along"] > 1:
                clusters.append([])
            clusters[-1].append(row)
        along += 1
    return frame, axis, (ux, uy, nx, ny), clusters


def emit_segment(frame: Image.Image, axis: list[float], basis: tuple[float, ...], hits: list[dict[str, int]],
                 left: int, top: int) -> dict[str, Any] | None:
    if not hits:
        return None
    ux, uy, nx, ny = basis
    width = hits[-1]["along"] - hits[0]["along"] + 1
    if width > WIDTH:
        return None
    along = hits[len(hits) // 2]["along"]
    pixels = frame.load()
    offsets = []
    for offset in range(-12, 13):
        if abs(offset) <= 2:
            continue
        x, y = int(round(axis[0] + ux * along + nx * offset)), int(round(axis[1] + uy * along + ny * offset))
        if 0 <= x < frame.width and 0 <= y < frame.height and pixels[x, y] < 200:
            offsets.append(offset)
    if len(offsets) < 3:
        return None
    start_offset, end_offset = min(0, min(offsets)), max(0, max(offsets))
    if end_offset - start_offset < 4:
        return None
    start = [axis[0] + ux * along + nx * start_offset + left,
             axis[1] + uy * along + ny * start_offset + top]
    end = [axis[0] + ux * along + nx * end_offset + left,
           axis[1] + uy * along + ny * end_offset + top]
    return {"center": [(start[0] + end[0]) / 2, (start[1] + end[1]) / 2],
            "line": [*start, *end], "cluster_width_pixels": width,
            "dark_pixel_count": sum(hit["dark_count"] for hit in hits),
            "longest_dark_run": max(hit["longest_run"] for hit in hits)}


def split_wide_cluster(hits: list[dict[str, int]], min_prominence: int, max_width: int = WIDTH) -> list[list[dict[str, int]]]:
    """Recursively split a wide cluster at its strongest qualifying support valley."""
    width = hits[-1]["along"] - hits[0]["along"] + 1
    if width <= max_width:
        return [hits]
    counts = [hit["dark_count"] for hit in hits]
    choices = []
    for cut in range(2, len(hits) - 1):
        left_hits, right_hits = hits[:cut], hits[cut:]
        left_width = left_hits[-1]["along"] - left_hits[0]["along"] + 1
        right_width = right_hits[-1]["along"] - right_hits[0]["along"] + 1
        if left_width < 2 or right_width < 2:
            continue
        # A cut lies between adjacent qualifying samples; the weaker sample is the valley score.
        valley = min(counts[cut - 1], counts[cut])
        prominence = min(max(counts[:cut]), max(counts[cut:])) - valley
        if prominence >= min_prominence:
            choices.append((-prominence, max(left_width, right_width), cut))
    if not choices:
        return []
    _, _, cut = min(choices)
    left = split_wide_cluster(hits[:cut], min_prominence, max_width)
    right = split_wide_cluster(hits[cut:], min_prominence, max_width)
    return left + right


def trace_proposals(image: Image.Image, axis_full: list[float], bounds: list[int], prominence: int | None) -> tuple[list[dict[str, Any]], dict[str, int]]:
    left, top, _, _ = bounds
    frame, axis, basis, clusters = profile_clusters(image, axis_full, bounds)
    proposals: list[dict[str, Any]] = []
    wide_rejected = split_count = split_segments = 0
    for cluster in clusters:
        width = cluster[-1]["along"] - cluster[0]["along"] + 1
        if width <= WIDTH:
            segment_groups = [cluster]
        else:
            wide_rejected += 1
            segment_groups = split_wide_cluster(cluster, prominence) if prominence is not None else []
            if len(segment_groups) > 1:
                split_count += 1
                split_segments += len(segment_groups)
        for segment in segment_groups:
            proposal = emit_segment(frame, axis, basis, segment, left, top)
            if proposal is not None:
                proposals.append(proposal)
    return proposals, {"wide_clusters": wide_rejected, "clusters_split": split_count,
                       "split_segments": split_segments}


def load_truths(source_ids: list[str], data: dict[str, dict[str, Any]], refs: list[dict[str, Any]]) -> dict[tuple[str, str], list[dict[str, Any]]]:
    result = {}
    for source_id in source_ids:
        rows = scan._load_annotations(source_id, refs)
        image, rendered = data[source_id]["image"], data[source_id]["rendered"]
        for panel_index, panel in enumerate(rendered["panels"]):
            truths, _ = scan._truths_for_panel(rows, panel, image, panel_index)
            result[(source_id, panel["panel_id"])] = truths
    return result


def evaluate(outputs: list[dict[str, Any]], truth_by_panel: dict[tuple[str, str], list[dict[str, Any]]],
             source_ids: list[str], prominence: int | None) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    total = Counter()
    per_source: dict[str, Counter[str]] = defaultdict(Counter)
    rows = []
    for source in outputs:
        source_id = source["source_id"]
        if source_id not in source_ids:
            continue
        for panel in source["panels"]:
            if "ink_proposals" in panel:
                proposals, trace = panel["ink_proposals"], panel.get("trace", {})
            else:
                proposals, trace = trace_proposals(panel["image"], panel["axis"], panel["bounds"], prominence)
            truths = truth_by_panel[(source_id, panel["panel_id"])]
            assignments = curation.assign_proposals(proposals, truths)
            matches = sum(row["state"] == "unique_visible_tick" for row in assignments)
            fp = sum(row["state"] == "no_ground_truth_tick_nearby" for row in assignments)
            ambiguous = sum(row["state"] == "ambiguous" for row in assignments)
            target_count = sum(bool(t["ground_truth_target_unambiguous"]) for t in truths)
            matched_targets = sum(row["state"] == "unique_visible_tick" and row["truth_index"] is not None
                                  and bool(truths[row["truth_index"]]["ground_truth_target_unambiguous"])
                                  for row in assignments)
            row = {"source_id": source_id, "panel_id": panel["panel_id"], "needs_review": panel["needs_review"],
                   "target_truths": target_count, "visible_stroke_truths": len(truths),
                   "matched_targets": matched_targets, "true_matches": matches,
                   "false_proposals": fp, "ambiguous_proposals": ambiguous,
                   "proposals": len(proposals), "trace": trace}
            rows.append(row)
            for key, value in (("targets", target_count), ("matched_targets", matched_targets),
                               ("visible_strokes", len(truths)), ("true_matches", matches),
                               ("false_proposals", fp), ("ambiguous", ambiguous), ("proposals", len(proposals)),
                               ("no_truth_panel_proposals", len(proposals) if not truths else 0)):
                total[key] += value
                per_source[source_id][key] += value
    target_precision = total["matched_targets"] / total["proposals"] if total["proposals"] else 0.0
    target_recall = total["matched_targets"] / total["targets"] if total["targets"] else 0.0
    return ({"source_ids": source_ids, "visible_text_targets": total["targets"],
             "matched_text_targets": total["matched_targets"], "visible_stroke_truths": total["visible_strokes"],
             "true_matches": total["true_matches"], "false_proposals": total["false_proposals"],
             "ambiguous_proposals": total["ambiguous"], "proposals": total["proposals"],
             "precision_including_ambiguous": total["true_matches"] / total["proposals"] if total["proposals"] else 0.0,
             "text_target_precision": target_precision, "text_target_recall": target_recall,
             "text_target_f1": 2 * target_precision * target_recall / (target_precision + target_recall)
                 if target_precision + target_recall else 0.0,
             "no_visible_truth_panels": sum(r["visible_stroke_truths"] == 0 for r in rows),
             "proposals_on_no_visible_truth_panels": total["no_truth_panel_proposals"],
             "per_source": {source: dict(values) for source, values in sorted(per_source.items())}}, rows)


def scan_truth_blind(source_ids: list[str], source_map: dict[str, dict[str, Any]],
                     raster_map: dict[str, dict[str, Any]], base_outputs: list[dict[str, Any]]) -> tuple[list[dict[str, Any]], dict[str, dict[str, Any]]]:
    _, data = scan._truth_blind_inputs(source_ids, source_map, raster_map)
    data_rows, outputs_by_id = [], {row["source_id"]: row for row in base_outputs}
    for source_id in source_ids:
        base = outputs_by_id[source_id]
        output_panels = []
        image, rendered = data[source_id]["image"], data[source_id]["rendered"]
        for baseline_panel in base["panels"]:
            panel = rendered["panels"][int(baseline_panel["panel_index"])]
            if panel["panel_id"] != baseline_panel["panel_id"]:
                raise ValueError("source panel identity changed during train-only reconstruction")
            bounds = scan.common.transformed_panel_bounds(panel, image.size)
            output_panels.append({"panel_id": panel["panel_id"], "panel_index": baseline_panel["panel_index"],
                "axis": baseline_panel["selected_x_axis_line"], "bounds": bounds,
                "needs_review": baseline_panel["needs_review"],
                "uncertainty_reasons": baseline_panel["uncertainty_reasons"], "image": image})
        data_rows.append({"source_id": source_id, "panels": output_panels})
    return data_rows, data


def run() -> dict[str, Any]:
    pinned_helper_bindings = scan._source_bindings()
    run_bytes = scan.read_pinned(RUN_ROOT / "report.json", RUN_REPORT_SHA, 2_000_000)
    run_report = json.loads(run_bytes)
    proposal_bytes = scan.read_pinned(RUN_ROOT / "fit_truth_blind_proposals.json", FIT_PROPOSALS_SHA, 2_000_000)
    base_outputs = json.loads(proposal_bytes)
    center_bytes = scan.read_pinned(CENTER_REPORT, CENTER_REPORT_SHA, 2_000_000)
    center_report = json.loads(center_bytes)
    v6_bytes = scan.read_pinned(scan.ROOT / scan.V6_MANIFEST_REL, scan.V6_MANIFEST_SHA, 5_000_000)
    v6 = json.loads(v6_bytes)
    curation_bytes = scan.read_pinned(scan.ROOT / scan.CURATION_REL / "manifest.json", scan.CURATION_MANIFEST_SHA, 2_000_000)
    curation_manifest = json.loads(curation_bytes)
    fit_sources = {row["source_id"] for row in base_outputs}
    previous_holdouts = set(run_report["split"]["validation_source_ids"]) | set(center_report["frozen_rule"]["new_validation_source_ids"])
    positive_counts = Counter(row["source_id"] for row in v6["records"]
        if row["source_id"] in fit_sources and row["split"] == "train" and row["kind"] == "positive")
    train_ids, validation_ids, validation_components = choose_groups(
        run_report["split"]["fit_components"], fit_sources, previous_holdouts, positive_counts)
    source_map = {row["source_id"]: row for row in v6["sources"]}
    raster_map = {row["source_id"]: row for row in curation_manifest["source_groups"]}
    train_outputs, train_data = scan_truth_blind(train_ids, source_map, raster_map, base_outputs)
    train_truths = load_truths(train_ids, train_data, v6["records"])
    # Baseline parity is checked independently on every admitted panel before candidate selection.
    for source in train_outputs:
        base = next(row for row in base_outputs if row["source_id"] == source["source_id"])
        for panel in source["panels"]:
            actual = scan.scan_panel(panel["image"], panel["axis"], panel["bounds"], WIDTH)
            expected = next(row for row in base["panels"] if row["panel_id"] == panel["panel_id"])["ink_proposals"]
            trace, _ = trace_proposals(panel["image"], panel["axis"], panel["bounds"], None)
            if not same_proposals(actual, expected) or not same_proposals(actual, trace):
                raise ValueError(f"frozen width-8 trace parity failed for {source['source_id']}/{panel['panel_id']}")
    baseline_train, _ = evaluate(train_outputs, train_truths, train_ids, None)
    candidate_train = {}
    for prominence in PROMINENCE_CANDIDATES:
        candidate_train[prominence], _ = evaluate(train_outputs, train_truths, train_ids, prominence)
    eligible = [(metrics["text_target_f1"], metrics["text_target_precision"], prominence)
                for prominence, metrics in candidate_train.items()
                if metrics["text_target_precision"] >= baseline_train["text_target_precision"] - MAX_PRECISION_DROP]
    selected = max(eligible)[2] if eligible else None
    selected_rule = {"rule_id": "support_valley_recursive_split_v1" if selected is not None else "frozen_width8_baseline_no_split",
        "scanner_width_px": WIDTH, "other_scanner_settings_changed": False,
        "min_support_prominence": selected, "candidate_prominence_values": list(PROMINENCE_CANDIDATES),
        "selection": "maximize train text-target F1 with precision no more than 0.02 below train baseline; then precision; then larger prominence",
        "fit_source_ids": train_ids, "new_validation_source_ids": validation_ids,
        "new_validation_components": validation_components, "excluded_previous_holdout_source_ids": sorted(previous_holdouts),
        "train_baseline": baseline_train, "train_candidate_metrics": candidate_train,
        "validation_labels_opened": False,
        "input_hashes": {"prior_run_report": digest(run_bytes), "fit_truth_blind_proposals": digest(proposal_bytes),
            "center_rule_report": digest(center_bytes), "v6_train_manifest": digest(v6_bytes),
            "curation_manifest": digest(curation_bytes), "scanner_source": digest(Path(scan.__file__).read_bytes()),
            "curation_source": digest(Path(curation.__file__).read_bytes()),
            "experiment_source": digest(Path(__file__).read_bytes()),
            "pinned_executed_helper_bindings": pinned_helper_bindings}}
    OUTPUT_ROOT.mkdir(parents=True, exist_ok=False)
    frozen_bytes = json.dumps(selected_rule, sort_keys=True, separators=(",", ":")).encode()
    (OUTPUT_ROOT / "frozen_rule.json").write_bytes(frozen_bytes)
    # Generate only truth-blind validation outputs before reading those labels.
    validation_outputs, validation_data = scan_truth_blind(validation_ids, source_map, raster_map, base_outputs)
    val_proposals = []
    baseline_val_proposals = []
    base_by_id = {row["source_id"]: row for row in base_outputs}
    for source in validation_outputs:
        for panel in source["panels"]:
            candidate, trace = trace_proposals(panel["image"], panel["axis"], panel["bounds"], selected)
            baseline = scan.scan_panel(panel["image"], panel["axis"], panel["bounds"], WIDTH)
            expected = next(row for row in base_by_id[source["source_id"]]["panels"]
                            if row["panel_id"] == panel["panel_id"])["ink_proposals"]
            if not same_proposals(baseline, expected):
                raise ValueError("validation width-8 proposal parity failed")
            val_proposals.append({"source_id": source["source_id"], "panel_id": panel["panel_id"],
                                  "candidate": candidate, "trace": trace})
            baseline_val_proposals.append({"source_id": source["source_id"], "panel_id": panel["panel_id"],
                                           "proposals": baseline})
    blind_bytes = json.dumps({"baseline": baseline_val_proposals, "candidate": val_proposals},
                             sort_keys=True, separators=(",", ":")).encode()
    (OUTPUT_ROOT / "validation_truth_blind_proposals.json").write_bytes(blind_bytes)
    # Open the new validation annotations only after the rule and truth-blind predictions are frozen.
    validation_truths = load_truths(validation_ids, validation_data, v6["records"])
    candidate_map = {(row["source_id"], row["panel_id"]): row["candidate"] for row in val_proposals}
    baseline_map = {(row["source_id"], row["panel_id"]): row["proposals"] for row in baseline_val_proposals}
    val_output_shape = [{"source_id": source["source_id"], "panels": [
        {**panel, "ink_proposals": candidate_map[(source["source_id"], panel["panel_id"])]}
        for panel in source["panels"]]} for source in validation_outputs]
    baseline_output_shape = [{"source_id": source["source_id"], "panels": [
        {**panel, "ink_proposals": baseline_map[(source["source_id"], panel["panel_id"])]}
        for panel in source["panels"]]} for source in validation_outputs]
    baseline_val, baseline_rows = evaluate(baseline_output_shape, validation_truths, validation_ids, None)
    candidate_val, candidate_rows = evaluate(val_output_shape, validation_truths, validation_ids, None)
    report = {"schema": "graphreader.scanner-cluster-split-evaluation.v1",
        "acceptance_status": "exploratory internal synthetic validation only",
        "frozen_rule_sha256": digest(frozen_bytes), "truth_blind_validation_proposals_sha256": digest(blind_bytes),
        "frozen_rule": selected_rule, "validation_baseline": baseline_val,
        "validation_candidate": candidate_val,
        "validation_per_source": {"baseline": baseline_val["per_source"], "candidate": candidate_val["per_source"]},
        "validation_per_panel": {"baseline": baseline_rows, "candidate": candidate_rows},
        "source_disjoint_from_candidate_fit": not bool(set(train_ids) & set(validation_ids)),
        "prior_validation_groups_excluded_without_opening_labels": sorted(previous_holdouts),
        "truth_handling": "Candidate rule selected on fit component labels only and frozen before the new validation annotations were read; proposals use rendered pixels and selected axis only. No prior validation, consumed dev, sealed or private labels read.",
        "scanner_rerun": "pixel scan replay on newly reconstructed train and new internal validation sources; axis model and baseline width-8 C# outputs were not rerun",
        "training_performed": False, "gpu_used": False, "production_behavior_changed": False}
    (OUTPUT_ROOT / "report.json").write_text(json.dumps(report, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    return report


if __name__ == "__main__":
    print(json.dumps(run(), indent=2))
