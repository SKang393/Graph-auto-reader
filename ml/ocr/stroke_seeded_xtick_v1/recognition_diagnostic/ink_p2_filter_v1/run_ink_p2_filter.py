# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Filter truth-blind short-ink scan proposals with the frozen P2 crop detector."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
import math
from pathlib import Path
import subprocess
import sys
import time
from typing import Any

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers
from ml.ocr.stroke_seeded_xtick_v1 import materialize, probe_materialize, train_gpu
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.opencv_hough_minline_diagnostic import run_hough_min5 as prior
from ml.ocr.stroke_seeded_xtick_v1 import top_one_seed_analysis

CORPUS = ROOT / "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3"
MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
P2_REPORT = ROOT / "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/report.json"
P2_REPORT_SHA = "cc013b5eda70af9681dbd87cc3e496a668cc29d0dcfba8d0841250782bdcb615"
P2_CHECKPOINT_SHA = "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351"
V4_REPORT_SHA = "7a9aa9b5dc6b73de11eac01f856984b55ed537d4614250836022c73973c77de2"
INK_PROJECT = ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1/ShortTickInkPrototype.csproj"
INK_REPORT = ROOT / "artifacts/stroke-seeded-xtick-v1-short-tick-ink-prototype-v1/report.json"
INK_REPORT_SHA = "95ce064ca9006d1fd1ed5521a0a309b4a3a0b3e18ade084adb191d98a8941455"
OUTPUT = ROOT / "artifacts/stroke-seeded-xtick-v1-ink-p2-filter-v1"


class FilterError(RuntimeError):
    pass


def digest(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def box_iou(a: tuple[float, float, float, float], b: tuple[float, float, float, float]) -> float:
    x0, y0 = max(a[0], b[0]), max(a[1], b[1])
    x1, y1 = min(a[2], b[2]), min(a[3], b[3])
    inter = max(0.0, x1 - x0) * max(0.0, y1 - y0)
    union = max(0.0, a[2] - a[0]) * max(0.0, a[3] - a[1]) + max(0.0, b[2] - b[0]) * max(0.0, b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def crop_box_to_original(box: tuple[float, float, float, float], transform: dict[str, Any]) -> tuple[float, float, float, float] | None:
    """Map model-normalized box through canvas content and exact materializer inverse."""
    left, top, right, bottom = (box[0] * 256.0, box[1] * 96.0, box[2] * 256.0, box[3] * 96.0)
    cx, cy, width, height = transform["content_xywh"]
    left, right = max(left, cx), min(right, cx + width)
    top, bottom = max(top, cy), min(bottom, cy + height)
    if right <= left or bottom <= top:
        return None
    affine = transform["crop_pixel_to_original"]
    matrix = [affine[0], affine[1], affine[2], affine[3], affine[4], affine[5], 0.0, 0.0, 1.0]
    x0, y0, x1, y1 = materialize._apply_box(matrix, (left, top, right, bottom))
    return (x0, y0, x1, y1) if x1 > x0 and y1 > y0 else None


def filter_proposal(score: float | None, threshold: float) -> bool:
    if threshold not in (0.25, 0.5):
        raise ValueError("only predeclared P2 score thresholds .25 and .5 are allowed")
    return score is not None and math.isfinite(score) and score >= threshold


def _summarize(panels: list[dict[str, Any]], key: str) -> dict[str, Any]:
    visible = sum(panel["visible_truth_count"] for panel in panels)
    proposals = matched = unmatched = 0
    no_visible_proposals = no_visible_panel_false = 0
    no_visible_panel_count = 0
    uncertain = 0
    for panel in panels:
        metric = panel[key]
        proposals += metric["usable_detected_x_stroke_count"]
        matched += metric["matched_count"]
        unmatched += metric["unmatched_detected_count"]
        uncertain += int(panel["needs_review"])
        if panel["visible_truth_count"] == 0:
            no_visible_panel_count += 1
            no_visible_panel_false += metric["usable_detected_x_stroke_count"]
            no_visible_proposals += int(metric["usable_detected_x_stroke_count"] > 0)
    return {"visible_truth_ticks": visible, "proposals": proposals, "matched_ticks": matched,
            "unmatched_proposals": unmatched, "precision": matched / proposals if proposals else 0.0,
            "recall": matched / visible if visible else 0.0,
            "uncertain_panels": uncertain,
            "panels_with_no_visible_ticks": no_visible_panel_count,
            "no_visible_tick_panels_with_false_proposals": no_visible_proposals,
            "false_proposals_on_no_visible_tick_panels": no_visible_panel_false}


def _load_model(torch: Any, device: Any, config: dict[str, Any]):
    checkpoint = helpers._bound_read(ROOT, config["checkpoint_path"], "frozen P2 checkpoint", 1_000_000)
    if checkpoint.sha256 != P2_CHECKPOINT_SHA:
        raise FilterError("frozen P2 checkpoint identity changed")
    training_report = helpers._bound_read(ROOT, "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/report.json",
                                         "P2 report", 1_000_000)
    if training_report.sha256 != P2_REPORT_SHA:
        raise FilterError("frozen P2 report identity changed")
    data = torch.load(BytesIO(checkpoint.value), map_location=device, weights_only=True)
    expected = {"recipe_id": "stroke-seeded-xtick-v1-hard-negative-fit-p2",
                "model_identity": config["model_identity"],
                "input_manifest_sha256": config["train_manifest_sha256"], "optimizer_steps": 2760}
    if any(data.get(key) != value for key, value in expected.items()):
        raise FilterError("frozen checkpoint metadata mismatch")
    model = helpers.build_spatial_locator(torch).to(device)
    model.load_state_dict(data["state_dict"], strict=True)
    if any(parameter.device != device for parameter in model.parameters()):
        raise FilterError("P2 model did not remain on RX 6600 XT")
    return model, checkpoint.sha256, training_report.sha256


def _infer(model: Any, pixels: list[np.ndarray], torch: Any, device: Any):
    outputs = []
    model.eval()
    with torch.inference_mode():
        for start in range(0, len(pixels), 32):
            batch = torch.from_numpy(np.stack(pixels[start:start + 32]).astype(np.float32)).unsqueeze(1).to(device)
            if batch.device != device:
                raise FilterError("candidate inference tensor left the RX 6600 XT")
            logits, boxes = model(batch)
            outputs.extend(train_gpu.decode_predictions(logits, boxes))
    torch.cuda.synchronize(device)
    return outputs


def run(dll: Path, output: Path = OUTPUT) -> dict[str, Any]:
    started = time.perf_counter()
    phase = "initialization"
    if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
        raise FilterError("frozen detector inference must use project GPU Python launcher")
    config, config_bytes = top_one_seed_analysis._load_config()
    if output.exists() or output.is_symlink() or output.parent != ROOT / "artifacts":
        raise FilterError("output must be a fresh direct project artifacts child")
    manifest_bytes = common.read_pinned(CORPUS / "manifest.json", MANIFEST_SHA, 10_000_000)
    manifest = json.loads(manifest_bytes)
    if digest(Path(materialize.__file__).read_bytes()) != manifest["crop_materializer_sha256"] or digest(Path(probe_materialize.__file__).read_bytes()) != manifest["generator_sha256"]:
        raise FilterError("probe renderer/materializer source identity changed")
    common.read_pinned(P2_REPORT, P2_REPORT_SHA, 1_000_000)
    v4_bytes = common.read_pinned(ROOT / "artifacts/stroke-seeded-xtick-v1-axis-stroke-alignment-v4/report.json", V4_REPORT_SHA, 2_000_000)
    v4_report = json.loads(v4_bytes)
    frozen_panels = {panel["panel_id"]: panel for panel in v4_report["panel_results"]}
    ink_report_bytes = common.read_pinned(INK_REPORT, INK_REPORT_SHA, 2_000_000)
    ink_report = json.loads(ink_report_bytes)
    if (digest(INK_PROJECT.read_bytes()) != ink_report["runner_project_sha256"] or
            digest((INK_PROJECT.parent / "Program.cs").read_bytes()) != ink_report["runner_source_sha256"]):
        raise FilterError("short-ink scanner source differs from its frozen consumed-probe report")
    dll = dll.resolve(strict=True)
    if not dll.is_relative_to(INK_PROJECT.parent.resolve()):
        raise FilterError("scanner runner DLL is outside its project directory")
    output.mkdir(parents=True, exist_ok=False)
    (output / "started.json").write_text(json.dumps({"status": "started", "phase": phase,
        "started_utc": datetime.now(timezone.utc).isoformat(), "training_performed": False,
        "dev_sealed_private_inputs_read": False}), encoding="utf-8")
    try:
        phase = "rx6600xt-binding"
        import torch
        runtime = helpers.require_rx_6600_xt(torch)
        device = torch.device(str(runtime["device"]))
        phase = "source-rerender-and-scan"
        source_inputs, images_by_source, rendered_by_source = [], {}, {}
        source_receipts = []
        for source in manifest["sources"]:
            recipe = source["renderer_recipe"]
            scene = materialize.build_train_scene(recipe)
            image, rendered, _ = materialize.render_train_scene(scene)
            pixel_sha = digest(image.tobytes())
            if pixel_sha != source["source_pixel_sha256"]:
                raise FilterError(f"pinned source pixel hash mismatch: {source['source_id']}")
            images_by_source[source["source_id"]] = image
            rendered_by_source[source["source_id"]] = rendered
            source_inputs.append(common.build_source_input(source, [], rendered["panels"], image))
            source_receipts.append({"source_id": source["source_id"], "source_pixel_sha256": pixel_sha,
                                    "width": image.width, "height": image.height})
        proc = subprocess.Popen(["dotnet", str(dll), "--stdio"], cwd=ROOT, stdin=subprocess.PIPE,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                encoding="utf-8", errors="strict", bufsize=1)
        assert proc.stdin is not None and proc.stdout is not None
        scan_outputs = []
        try:
            for item in source_inputs:
                proc.stdin.write(json.dumps(item, separators=(",", ":")) + "\n")
                proc.stdin.flush()
                line = proc.stdout.readline()
                if not line:
                    raise FilterError("truth-blind scanner exited without a source result")
                scan_outputs.append(json.loads(line))
        finally:
            proc.stdin.close()
        stderr = proc.stderr.read() if proc.stderr else ""
        if proc.wait(timeout=60) != 0:
            raise FilterError(stderr[-4000:])
        proposal_rows = []
        default_by_panel = {}
        scan_by_panel = {}
        for source_result in scan_outputs:
            if "error" in source_result:
                raise FilterError(source_result["error"])
            expected_png = next(item["image_png_sha256"] for item in source_inputs if item["source_id"] == source_result["source_id"])
            if source_result["png_sha256"] != expected_png:
                raise FilterError("scanner transport PNG hash mismatch")
            for panel in source_result["panels"]:
                panel_id = panel["panel_id"]
                frozen = frozen_panels[panel_id]["x_axis_ticks"]
                default = [t for t in panel["default_x_axis_ticks"] if t["usable_for_ocr_tick_seed"]]
                if len(default) != len(frozen) or any(a["tick_id"] != b["tick_id"] or
                    max(abs(x-y) for x,y in zip(a["center"],b["center"])) > 1e-6 or
                    max(abs(x-y) for x,y in zip(a["line"],b["line"])) > 1e-6 for a,b in zip(default,frozen)):
                    raise FilterError(f"production default parity failed for {panel_id}")
                default_by_panel[panel_id] = default
                scan_by_panel[panel_id] = panel["ink_proposals"]
                for index, proposal in enumerate(panel["ink_proposals"]):
                    source_id = source_result["source_id"]
                    center_x, center_y = (float(v) for v in proposal["center"])
                    crop, transform = materialize._crop_and_resize(images_by_source[source_id], center_x, center_y - 2.0)
                    proposal_rows.append({"source_id": source_id, "panel_id": panel_id, "proposal_index": index,
                                          "proposal": proposal, "transform": transform,
                                          "pixels": np.asarray(crop, dtype=np.float32) / 255.0})
        phase = "p2-crop-inference"
        model, checkpoint_sha, p2_sha = _load_model(torch, device, config)
        predictions = _infer(model, [row["pixels"] for row in proposal_rows], torch, device)
        if len(predictions) != len(proposal_rows):
            raise FilterError("P2 prediction count differs from truth-blind scan proposal count")
        for row, pred in zip(proposal_rows, predictions):
            top = pred[0] if pred else None
            score, box = top if top is not None else (None, None)
            row["top_score"] = score
            row["top_box_normalized_crop"] = list(box) if box is not None else None
            row["top_box_original"] = list(crop_box_to_original(box, row["transform"])) if box is not None and crop_box_to_original(box, row["transform"]) is not None else None
        phase = "post-inference-truth-scoring"
        # Truth is deliberately reconstructed only after all proposals and P2 scores are fixed.
        truths_by_panel: dict[str, list[dict[str, Any]]] = {}
        tick_id_by_case: dict[str, str] = {}
        glyph_boxes: dict[tuple[str, str], tuple[float, float, float, float]] = {}
        visible_truth = invisible_truth = 0
        for source in manifest["sources"]:
            source_id = source["source_id"]
            rendered = rendered_by_source[source_id]
            source_truth = prior.source_truths(source_id, rendered["panels"])
            for panel_index, panel in enumerate(rendered["panels"]):
                panel_id = panel["panel_id"]
                truths_by_panel[panel_id] = source_truth[panel_id]
                source_ticks = [tick for tick in panel.get("ticks", [])
                                if tick.get("axis") == "x" and tick.get("role") == "x_tick"]
                for tick_index, tick in enumerate(source_ticks):
                    tick_id_by_case[f"{source_id}-p{panel_index}-t{tick_index}"] = str(tick["tick_id"])
                visible_truth += sum(t["source_stroke_visible"] for t in source_truth[panel_id])
                invisible_truth += sum(not t["source_stroke_visible"] for t in source_truth[panel_id])
            for row in materialize.materialize_recipe(source["renderer_recipe"]):
                for target in row["targets"]:
                    if target["text"] == row["expected_tick_label"]:
                        glyph_boxes[(row["source_panel_id"], row["tick_id"])] = tuple(float(v) for v in target["glyph_mask_xyxy"])
        if (visible_truth, invisible_truth) != (181, 241):
            raise FilterError("consumed probe truth inventory changed")
        panels = []
        default_reordered = []
        scan_metrics_flat = []
        for source in scan_outputs:
            for raw_panel in source["panels"]:
                pid = raw_panel["panel_id"]
                visible = [t for t in truths_by_panel[pid] if t["source_stroke_visible"]]
                panel = {"panel_id": pid, "source_id": source["source_id"],
                         "visible_truth_count": len(visible), "needs_review": raw_panel["needs_review"],
                         "uncertainty_reasons": raw_panel["uncertainty_reasons"]}
                panel["production_default"] = common.match_panel(visible, default_by_panel[pid])
                panel["scan_alone"] = common.match_panel(visible, scan_by_panel[pid])
                by_panel_rows = [r for r in proposal_rows if r["panel_id"] == pid]
                for threshold in (0.25, 0.5):
                    key = "detector_filtered_scan_" + str(threshold).replace(".", "")
                    survivor_rows = [r for r in by_panel_rows if filter_proposal(r["top_score"], threshold)]
                    survivors = [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]}
                                 for r in survivor_rows]
                    panel[key] = common.match_panel(visible, survivors)
                    panel[key + "_survivor_rows"] = survivor_rows
                panels.append(panel)
                default_reordered.append(panel["production_default"])
                scan_metrics_flat.append(panel["scan_alone"])
        box_scores = {"score_thresholds": {}}
        for threshold in (0.25, 0.5):
            accepted = [r for r in proposal_rows if filter_proposal(r["top_score"], threshold)]
            metric_pairs = []
            for panel in panels:
                metric_key = "detector_filtered_scan_" + str(threshold).replace(".", "")
                survivor_rows = panel[metric_key + "_survivor_rows"]
                for pair in panel[metric_key]["matched_pairs"]:
                    row = survivor_rows[pair["detection_index"]]
                    target = glyph_boxes.get((panel["panel_id"], tick_id_by_case[pair["case_id"]]))
                    pred_box = row["top_box_original"]
                    if target is not None and pred_box is not None:
                        metric_pairs.append(box_iou(tuple(pred_box), target))
            box_scores["score_thresholds"][str(threshold)] = {
                "accepted_proposals": len(accepted), "rejected_by_score_or_missing_box": len(proposal_rows) - len(accepted),
                "matched_tick_pairs_with_label_box": len(metric_pairs),
                "matched_tick_top_box_mean_iou_to_glyph": sum(metric_pairs) / len(metric_pairs) if metric_pairs else None,
                "matched_tick_top_box_iou_ge_0_5": sum(v >= 0.5 for v in metric_pairs),
            }
        for panel in panels:
            panel.pop("detector_filtered_scan_025_survivor_rows", None)
            panel.pop("detector_filtered_scan_05_survivor_rows", None)
        report = {
            "schema": "graphreader.stroke-seeded-xtick-v1-ink-p2-filter-diagnostic.v1",
            "recipe_id": "truth-blind-short-ink-scan-filtered-by-frozen-p2-v1",
            "status": "completed-exploratory-synthetic-diagnostic",
            "acceptance_status": "consumed synthetic probe only; not formal acceptance",
            "config_sha256": digest(config_bytes), "evaluator_sha256": digest(Path(__file__).read_bytes()),
            "probe_manifest_sha256": digest(manifest_bytes), "p2_checkpoint_sha256": checkpoint_sha,
            "p2_training_report_sha256": p2_sha, "p2_training_config_sha256": config["p2_training_config_sha256"],
            "p2_runner_sha256": config["p2_runner_sha256"], "frozen_v4_axis_report_sha256": digest(v4_bytes),
            "frozen_ink_scan_report_sha256": digest(ink_report_bytes),
            "ink_scanner_project_sha256": digest(INK_PROJECT.read_bytes()),
            "ink_scanner_source_sha256": digest((INK_PROJECT.parent / "Program.cs").read_bytes()),
            "runtime": runtime, "training_performed": False, "optimizer_steps": 0,
            "probe_source_pixel_hashes_verified": source_receipts,
            "proposal_contract": {"truth_sent_to_scanner": False, "truth_sent_to_cropper_or_detector": False,
                "proposal_source_frame": "original full-panel pixel coordinates",
                "crop_recipe": "P1 materializer _crop_and_resize at proposal center x and proposal center y minus 2 pixels; original-to-256x96 transform recorded per crop",
                "filter": "retain proposal iff its single highest-confidence valid P2 decoded box reaches fixed score cutoff; no GT use in filtering",
                "score_thresholds": [0.25, 0.5], "box_iou_threshold_for_scoring_only": 0.5},
            "counts": {"source_groups": len(source_inputs), "panels": len(panels), "visible_truth_ticks": visible_truth,
                "invisible_truth_ticks_excluded_from_recall": invisible_truth, "scan_proposals": len(proposal_rows),
                "p2_top_box_missing": sum(r["top_score"] is None for r in proposal_rows)},
            "metrics": {"production_default": _summarize(panels, "production_default"),
                        "scan_alone": _summarize(panels, "scan_alone"),
                        "detector_filtered_scan_025": _summarize(panels, "detector_filtered_scan_025"),
                        "detector_filtered_scan_05": _summarize(panels, "detector_filtered_scan_05"),
                        "p2_top_box_glyph_localization": box_scores},
            "panel_results": panels,
            "elapsed_seconds": round(time.perf_counter() - started, 6),
            "dev_sealed_private_inputs_read": False, "production_behavior_changed": False,
            "production_approval": False, "release_eligible": False,
            "limitations": ["The consumed synthetic diagnostic is already used for development, not untouched acceptance.",
                "P2 predicts the tick label box within a crop; scan stroke proposals are assessed against visible tick stroke geometry, while P2 box IoU is separately compared with glyph bounds after inference.",
                "The scan prototype is known to propose grid, border, and text-stroke confusers; detector filtering may still accept these when a nearby glyph yields high objectness.",
                "No recognizer or end-to-end exact-text score is measured here."]}
        (output / "report.json").write_text(json.dumps(report, sort_keys=True, separators=(",", ":")), encoding="utf-8")
        return report
    except Exception as error:
        (output / "failure.json").write_text(json.dumps({"status": "failed", "phase": phase,
            "error_type": type(error).__name__, "error": str(error), "evaluator_sha256": digest(Path(__file__).read_bytes()),
            "training_performed": False, "dev_sealed_private_inputs_read": False}, sort_keys=True), encoding="utf-8")
        raise


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: run_ink_p2_filter.py SCANNER_DLL")
    result = run(Path(sys.argv[1]))
    print(json.dumps(result["metrics"], indent=2))
