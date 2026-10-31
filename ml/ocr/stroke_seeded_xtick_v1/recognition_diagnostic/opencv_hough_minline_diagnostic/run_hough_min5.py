# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Controlled diagnostic-only comparison of Hough minimum line length 12 vs 5 px."""

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
import subprocess
import sys
import time
from typing import Any

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.stroke_seeded_xtick_v1 import materialize as crop_materializer
from ml.ocr.stroke_seeded_xtick_v1 import probe_materialize
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common

CORPUS = Path("artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3")
MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
BASELINE_REPORT = Path("artifacts/stroke-seeded-xtick-v1-axis-stroke-alignment-v4/report.json")
BASELINE_REPORT_SHA = "7a9aa9b5dc6b73de11eac01f856984b55ed537d4614250836022c73973c77de2"


class HoughCompareError(RuntimeError):
    pass


def source_truths(source_id: str, panels: list[dict[str, Any]]) -> dict[str, list[dict[str, Any]]]:
    result = {}
    for panel_index, panel in enumerate(panels):
        tick_rows = [tick for tick in panel.get("ticks", [])
                     if tick.get("axis") == "x" and tick.get("role") == "x_tick"]
        result[panel["panel_id"]] = [
            {
                "case_id": f"{source_id}-p{panel_index}-t{tick_index}",
                "stroke_center_original_xy": [float(tick["center"][0]), float(tick["center"][1])],
                "stroke_line_original_xyxy": [float(tick["line"][0][0]), float(tick["line"][0][1]),
                                               float(tick["line"][1][0]), float(tick["line"][1][1])],
                "kind": "visible_tick" if tick.get("visible") else "invisible_tick",
                "source_stroke_visible": bool(tick.get("visible")),
            }
            for tick_index, tick in enumerate(tick_rows)
        ]
    return result


def run(root: Path, dll: Path, out_path: Path) -> dict[str, Any]:
    started = time.perf_counter()
    manifest_bytes = common.read_pinned(root / CORPUS / "manifest.json", MANIFEST_SHA, 10_000_000)
    manifest = json.loads(manifest_bytes)
    if common.digest(Path(probe_materialize.__file__).read_bytes()) != manifest["generator_sha256"]:
        raise HoughCompareError("probe generator source hash differs from manifest")
    if common.digest(Path(crop_materializer.__file__).read_bytes()) != manifest["crop_materializer_sha256"]:
        raise HoughCompareError("crop materializer source hash differs from manifest")
    baseline_bytes = common.read_pinned(root / BASELINE_REPORT, BASELINE_REPORT_SHA, 2_000_000)
    baseline = json.loads(baseline_bytes)
    if baseline.get("probe_manifest_sha256") != MANIFEST_SHA:
        raise HoughCompareError("frozen baseline is for a different probe")
    frozen_by_panel = {panel["panel_id"]: panel for panel in baseline["panel_results"]}

    source_inputs = []
    truth_by_panel: dict[str, list[dict[str, Any]]] = {}
    source_pixel_receipts = []
    visible_truth_count = invisible_truth_count = 0
    for source in manifest["sources"]:
        scene = crop_materializer.build_train_scene(source["renderer_recipe"])
        image, rendered, _ = crop_materializer.render_train_scene(scene)
        source_sha = common.digest(image.tobytes())
        if source_sha != source["source_pixel_sha256"]:
            raise HoughCompareError(f"source raster hash mismatch: {source['source_id']}")
        if (image.width, image.height) != (rendered["canvas"]["width"], rendered["canvas"]["height"]):
            raise HoughCompareError(f"source raster dimensions mismatch: {source['source_id']}")
        source_truth = source_truths(source["source_id"], rendered["panels"])
        for panel_id, truths in source_truth.items():
            truth_by_panel[panel_id] = truths
            visible_truth_count += sum(t["source_stroke_visible"] for t in truths)
            invisible_truth_count += sum(not t["source_stroke_visible"] for t in truths)
        source_input = common.build_source_input(source, [], rendered["panels"], image)
        source_inputs.append(source_input)
        source_pixel_receipts.append({"source_id": source["source_id"], "source_pixel_sha256": source_sha,
                                      "width": image.width, "height": image.height,
                                      "panel_count": len(rendered["panels"])})
    if visible_truth_count != 181 or invisible_truth_count != 241:
        raise HoughCompareError("visible truth inventory differs from pinned v4 diagnostic")

    process = subprocess.Popen(["dotnet", str(dll), "--stdio"], cwd=root, stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                               encoding="utf-8", errors="strict", bufsize=1)
    assert process.stdin is not None and process.stdout is not None
    outputs = []
    try:
        for source in source_inputs:
            process.stdin.write(json.dumps(source, separators=(",", ":")) + "\n")
            process.stdin.flush()
            line = process.stdout.readline()
            if not line:
                raise HoughCompareError("C# comparison runner ended without a source result")
            outputs.append(json.loads(line))
    finally:
        process.stdin.close()
    stderr = process.stderr.read() if process.stderr else ""
    code = process.wait(timeout=60)
    if code != 0:
        raise HoughCompareError(f"C# runner exit {code}: {stderr[-4000:]}")

    panels = []
    for source in outputs:
        if "error" in source:
            raise HoughCompareError(f"source runner failure {source['source_id']}: {source['error']}")
        expected = next(item["image_png_sha256"] for item in source_inputs if item["source_id"] == source["source_id"])
        if source["png_sha256"] != expected:
            raise HoughCompareError(f"PNG transport hash mismatch for {source['source_id']}")
        for panel in source["panels"]:
            panel["source_id"] = source["source_id"]
            panel_id = panel["panel_id"]
            frozen = frozen_by_panel[panel_id]
            default = panel["default_hough12"]
            if default["status"] != "ok" or frozen["status"] != "ok":
                raise HoughCompareError(f"default detector failed for {panel_id}")
            if len(default["x_axis_ticks"]) != len(frozen["x_axis_ticks"]):
                raise HoughCompareError(f"default baseline tick count changed for {panel_id}")
            for new_tick, frozen_tick in zip(default["x_axis_ticks"], frozen["x_axis_ticks"]):
                if (new_tick["tick_id"] != frozen_tick["tick_id"] or
                        max(abs(a - b) for a, b in zip(new_tick["center"], frozen_tick["center"])) > 1e-6 or
                        max(abs(a - b) for a, b in zip(new_tick["line"], frozen_tick["line"])) > 1e-6):
                    raise HoughCompareError(f"default geometry differs from frozen v4 report for {panel_id}")
            truth = [row for row in truth_by_panel[panel_id] if row["source_stroke_visible"]]
            panel["truth_count"] = len(truth)
            panel["default_metrics"] = common.match_panel(truth, default["x_axis_ticks"])
            panel["hough_min5_metrics"] = common.match_panel(truth, panel["diagnostic_hough5"]["x_axis_ticks"])
            panels.append(panel)

    metrics = {name: summarize_variant(panels, name) for name in ("default_hough12", "diagnostic_hough5")}
    baseline_matched = {pair["case_id"] for panel in panels for pair in panel["default_metrics"]["matched_pairs"]}
    changed_matched = {pair["case_id"] for panel in panels for pair in panel["hough_min5_metrics"]["matched_pairs"]}
    per_source = summarize_sources(panels)
    report = {
        "schema": "graphreader.stroke-seeded-xtick-v1-hough-minline-comparison.v1",
        "recipe_id": "stroke-seeded-xtick-v1-consumed-probe-hough-minline-12-vs-5-diagnostic-v1",
        "acceptance_status": "consumed synthetic diagnostic only; not formal acceptance",
        "probe_manifest_sha256": common.digest(manifest_bytes),
        "frozen_v4_report_sha256": common.digest(baseline_bytes),
        "frozen_v4_runner_sha256": baseline["axis_runner_source_sha256"],
        "comparison_runner_source_sha256": common.digest(Path(__file__).with_name("Program.cs").read_bytes()),
        "comparison_runner_project_sha256": common.digest(Path(__file__).with_name("AxisHoughMinLineDiagnostic.csproj").read_bytes()),
        "comparison_python_sha256": common.digest(Path(__file__).read_bytes()),
        "production_components": {
            "raster_decoder": "GraphReader.App.Integration.Workflow.ProductionRasterFrameDecoder",
            "geometry_detector": "GraphReader.Axis.AxisGeometryDetector",
            "provider": "GraphReader.Axis.OpenCvLineCandidateProvider",
            "default_options": "all defaults, including HoughMinimumLineLengthPixels=12",
            "diagnostic_change": "only HoughMinimumLineLengthPixels=5; all other OpenCvLineCandidateOptions values remain defaults",
            "production_files_or_thresholds_changed": False,
            "ocr_models_loaded": False,
        },
        "sample_counts": {
            "source_groups": len(source_inputs),
            "source_panels": len(panels),
            "visible_truth_strokes": visible_truth_count,
            "invisible_strokes_excluded_from_recall": invisible_truth_count,
            "source_pixel_hashes_verified_before_inference": len(source_pixel_receipts),
            "source_panels_written_to_disk": False,
        },
        "matching_rule": common.MATCH_RECIPE,
        "metrics": metrics,
        "matched_case_changes": {
            "matched_in_both": len(baseline_matched & changed_matched),
            "newly_matched_with_min5": len(changed_matched - baseline_matched),
            "lost_with_min5": len(baseline_matched - changed_matched),
        },
        "source_group_results": per_source,
        "verified_source_rasters": source_pixel_receipts,
        "panel_results": panels,
        "elapsed_rerender_and_inference_seconds": round(time.perf_counter() - started, 6),
        "training_performed": False,
        "dev_sealed_private_inputs_read": False,
        "production_behavior_changed": False,
        "production_approval": False,
        "release_eligible": False,
        "limitations": [
            "This is a diagnostic-only threshold ablation on consumed synthetic development data, not held-out acceptance.",
            "The one-variable change tests Hough candidate generation only; downstream axis-pair selection and tick filters remain unchanged.",
            "The matching tolerance is diagnostic and may classify near misses as false detections.",
        ],
    }
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(report, sort_keys=True, separators=(",", ":")), encoding="utf-8")
    return report


def summarize_variant(panels: list[dict[str, Any]], run_key: str) -> dict[str, Any]:
    metric_key = "default_metrics" if run_key == "default_hough12" else "hough_min5_metrics"
    run_key_field = "default_hough12" if run_key == "default_hough12" else "diagnostic_hough5"
    visible = sum(panel["truth_count"] for panel in panels)
    detected = matched = unmatched_truth = unmatched_detected = duplicate_truth = 0
    no_review = 0
    ambiguity_reasons: dict[str, int] = {}
    by_source: dict[str, dict[str, int]] = {}
    tick_id_prefixes: dict[str, int] = {}
    for panel in panels:
        run = panel[run_key_field]
        metric = panel[metric_key]
        if run["status"] != "ok":
            raise HoughCompareError(f"variant run failed in panel {panel['panel_id']}: {run['failure']}")
        detected += metric["usable_detected_x_stroke_count"]
        matched += metric["matched_count"]
        unmatched_truth += metric["unmatched_truth_count"]
        unmatched_detected += metric["unmatched_detected_count"]
        duplicate_truth += metric["truths_with_multiple_detections_within_tolerance"]
        no_review += int(not run["needs_review"])
        for reason in run["uncertainty_reasons"]:
            ambiguity_reasons[reason] = ambiguity_reasons.get(reason, 0) + 1
        group = by_source.setdefault(panel["source_id"], {"truth": 0, "detected": 0, "matched": 0,
                                                            "unmatched_truth": 0, "unmatched_detected": 0})
        group["truth"] += metric["truth_count"]
        group["detected"] += metric["usable_detected_x_stroke_count"]
        group["matched"] += metric["matched_count"]
        group["unmatched_truth"] += metric["unmatched_truth_count"]
        group["unmatched_detected"] += metric["unmatched_detected_count"]
        for tick in run["x_axis_ticks"]:
            prefix = tick["tick_id"].rsplit("-", 1)[0]
            tick_id_prefixes[prefix] = tick_id_prefixes.get(prefix, 0) + 1
    return {
        "visible_truth_count": visible,
        "usable_detected_count": detected,
        "matched_count": matched,
        "unmatched_truth_count": unmatched_truth,
        "false_detection_count": unmatched_detected,
        "false_detections_per_panel": unmatched_detected / len(panels) if panels else None,
        "precision": matched / detected if detected else 0.0,
        "recall": matched / visible if visible else 0.0,
        "truths_with_multiple_detections_within_tolerance": duplicate_truth,
        "panels_without_needs_review_flag": no_review,
        "panels_with_uncertainty_reasons": len(panels) - no_review,
        "uncertainty_reason_counts": ambiguity_reasons,
        "emitted_tick_id_prefix_counts": tick_id_prefixes,
        "per_source": by_source,
    }


def summarize_sources(panels: list[dict[str, Any]]) -> list[dict[str, Any]]:
    groups: dict[str, list[dict[str, Any]]] = {}
    for panel in panels:
        groups.setdefault(panel["source_id"], []).append(panel)
    return [{"source_id": source_id,
             "default": source_variant_summary(rows, "default_metrics", "default_hough12"),
             "hough_min5": source_variant_summary(rows, "hough_min5_metrics", "diagnostic_hough5")}
            for source_id, rows in sorted(groups.items())]


def source_variant_summary(panels: list[dict[str, Any]], metrics_key: str, run_key: str) -> dict[str, Any]:
    return {"truth": sum(p[metrics_key]["truth_count"] for p in panels),
            "detected": sum(p[metrics_key]["usable_detected_x_stroke_count"] for p in panels),
            "matched": sum(p[metrics_key]["matched_count"] for p in panels),
            "false_detections": sum(p[metrics_key]["unmatched_detected_count"] for p in panels),
            "uncertain_panels": sum(bool(p[run_key]["needs_review"]) for p in panels)}


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: run_hough_min5.py <repo-root> <runner.dll> <report.json>", file=sys.stderr)
        return 2
    report = run(Path(sys.argv[1]).resolve(strict=True), Path(sys.argv[2]).resolve(strict=True),
                 Path(sys.argv[3]).resolve())
    print(json.dumps({"report": sys.argv[3], "metrics": report["metrics"],
                      "matched_case_changes": report["matched_case_changes"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(json.dumps({"status": "failed", "error_type": type(exc).__name__, "error": str(exc)}, file=sys.stderr))
        raise
