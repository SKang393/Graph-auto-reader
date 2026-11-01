# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Pair frozen P2 and mixed checkpoints on truth-blind scanner proposals."""

from __future__ import annotations

from collections import Counter
from datetime import datetime, timezone
from hashlib import sha256
import json
from pathlib import Path
import subprocess
import sys
import time
from typing import Any, Mapping, Sequence

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[6]
sys.path.insert(0, str(ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers
from ml.ocr.stroke_seeded_xtick_v1 import materialize, probe_evaluate_gpu, probe_materialize, train_gpu as p2
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_p2_filter_v1 import run_ink_p2_filter as prior_filter
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.opencv_hough_minline_diagnostic import run_hough_min5 as prior
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.train_proposal_curation_v1 import evaluate_paired_probe as pair_probe

HERE = Path(__file__).resolve().parent
CONFIG = HERE / "paired_ink_eval_v2.json"
OUTPUT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-ink-p2-vs-curated-mix-v2"
CORPUS_RELATIVE = "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3"
INK_RELATIVE = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/short_tick_ink_prototype_v1"
DLL_RELATIVE = INK_RELATIVE + "/bin/Release/net10.0-windows/ShortTickInkPrototype.dll"
FILTER_REPORT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-ink-p2-filter-v1/report.json"
INK_REPORT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-short-tick-ink-prototype-v1/report.json"
V4_REPORT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-axis-stroke-alignment-v4/report.json"
_RUN = {"phase": "starting", "inference_batches_per_model": 0}


class PairedInkEvaluationError(RuntimeError):
    pass


def digest(value: bytes) -> str:
    return sha256(value).hexdigest()


def canonical(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _bound_json(relative: str, label: str, limit: int) -> tuple[dict[str, Any], bytes, str]:
    bound = helpers._bound_read(ROOT, relative, label, limit)
    return helpers._parse_json(bound, label), bound.value, bound.sha256


def load_config() -> tuple[dict[str, Any], bytes, dict[str, Any]]:
    relative = CONFIG.relative_to(ROOT).as_posix()
    config, raw, _ = _bound_json(relative, "paired ink evaluation config", 65_536)
    expected = {
        "schema": "graphreader.stroke-seeded-xtick-v1-paired-ink-eval-config.v1",
        "recipe_id": "truth-blind-ink-scan-p2-vs-curated-mix-source-disjoint-diagnostic-v2",
        "probe_manifest_sha256": "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba",
        "train_manifest_sha256": "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a",
        "probe_generator_sha256": "c0a6d0559bdd384a84785926fa4dbf0230814f0663b55eff978f5ee32ff6a7a6",
        "crop_materializer_sha256": "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d",
        "probe_evaluator_sha256": "df9e804facd370b0540bc35c3e2165dd703fbbf19a49ddbd8978717d96e09077",
        "paired_crop_evaluator_sha256": "d45bb19d1bfc8ed801dd80ce3471153c7e5b315df0832eabeab46bb51b5758bc",
        "paired_crop_config_sha256": "d474a588105d2c5cbb5e72a38f6ebe178236d0f061646d9d7d8808312fa62e23",
        "scanner_project_sha256": "7a6f7599b67f6e09425cc8168c894e0f2263f66e91bb2c254f593409a5ec3632",
        "scanner_source_sha256": "40b104c23f0b60ca029dbe596dbd46117211d4f32b13837679380e4da47a8780",
        "scanner_binary_sha256": "7c77eaa5c1949d5f7e22c35b32d4751e24c6a2bed7e8664b77a50b032ebaa831",
        "prior_filter_evaluator_sha256": "2ae9bd965ca7f0d399045ebe831473ca85ed274f0ac4567d005eb03dcc55e1b5",
        "prior_filter_report_sha256": "3a81674fa9655e96c298f4094105b8c6d41d5f0991428f26e6bdd4ded3b7a5ef",
        "ink_report_sha256": "95ce064ca9006d1fd1ed5521a0a309b4a3a0b3e18ade084adb191d98a8941455",
        "v4_axis_report_sha256": "7a9aa9b5dc6b73de11eac01f856984b55ed537d4614250836022c73973c77de2",
        "axis_alignment_helper_sha256": "b64c353ce919143e83e4bea380f7f36b11175a51547b30a137d42f2402efb3de",
        "truth_geometry_helper_sha256": "ee3b0c35639f567ec6bdaa705ba0ce1e7b797d95743ad431651338444f1cc979",
        "model_builder_helper_sha256": "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5",
        "decode_metrics_helper_sha256": "dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6",
        "current_hard_negative_helper_sha256": "c670f43b21c56672321e248f3969c4b8e2eaa2bfd8f97f55fcdc6f20128f510f",
        "score_thresholds": [0.25, 0.5], "box_match_iou_threshold": 0.5,
        "crop_batch_size": 32, "required_launcher": "tools/gpu-training/Run-GpuPython.ps1",
        "required_device_name_substring": "RX 6600 XT", "cpu_fallback_allowed": False,
        "optimizer_execution_authorized": False, "dev_input_allowed": False,
        "sealed_input_allowed": False, "private_input_allowed": False,
        "production_approval": False, "release_eligible": False,
        "planned_output_root": OUTPUT_RELATIVE,
        "expected_evaluator_sha256": digest(Path(__file__).read_bytes()),
    }
    for key, value in expected.items():
        if config.get(key) != value:
            raise PairedInkEvaluationError(f"paired ink evaluation config mismatch: {key}")
    source_hashes = {
        "probe_materializer": (probe_materialize.__file__, config["probe_generator_sha256"]),
        "crop_materializer": (materialize.__file__, config["crop_materializer_sha256"]),
        "probe_evaluator": (probe_evaluate_gpu.__file__, config["probe_evaluator_sha256"]),
        "paired_crop_evaluator": (pair_probe.__file__, config["paired_crop_evaluator_sha256"]),
        "model_builder": (helpers.__file__, config["model_builder_helper_sha256"]),
        "decoder_metrics": (p2.__file__, config["decode_metrics_helper_sha256"]),
        "axis_helper": (common.__file__, config["axis_alignment_helper_sha256"]),
        "truth_helper": (prior.__file__, config["truth_geometry_helper_sha256"]),
    }
    for name, (path, expected_sha) in source_hashes.items():
        if digest(Path(path).read_bytes()) != expected_sha:
            raise PairedInkEvaluationError(f"pinned inference helper changed: {name}")
    ink_root = ROOT / INK_RELATIVE
    if digest((ink_root / "ShortTickInkPrototype.csproj").read_bytes()) != config["scanner_project_sha256"]:
        raise PairedInkEvaluationError("pinned ink scanner project changed")
    if digest((ink_root / "Program.cs").read_bytes()) != config["scanner_source_sha256"]:
        raise PairedInkEvaluationError("pinned ink scanner source changed")
    if digest((ROOT / DLL_RELATIVE).read_bytes()) != config["scanner_binary_sha256"]:
        raise PairedInkEvaluationError("pinned ink scanner binary changed")
    pair_config, _ = pair_probe.load_config()
    return config, raw, pair_config


def _scan_truth_blind(manifest: Mapping[str, Any], dll: Path) -> tuple[
        list[dict[str, Any]], dict[str, Any], dict[str, Any], list[dict[str, Any]], dict[str, Any], list[dict[str, Any]]]:
    source_inputs: list[dict[str, Any]] = []
    images: dict[str, Image.Image] = {}
    rendered_sources: dict[str, Any] = {}
    source_receipts = []
    for source in manifest["sources"]:
        source_id = source["source_id"]
        scene = materialize.build_train_scene(source["renderer_recipe"])
        image, rendered, _ = materialize.render_train_scene(scene)
        pixel_sha = digest(image.tobytes())
        if pixel_sha != source["source_pixel_sha256"]:
            raise PairedInkEvaluationError(f"probe full-panel pixel hash mismatch: {source_id}")
        images[source_id] = image
        rendered_sources[source_id] = rendered
        payload = common.build_source_input(source, [], rendered["panels"], image)
        if set(payload) != {"source_id", "expected_source_pixel_sha256", "width", "height",
                            "image_png_base64", "image_png_sha256", "panels"} or "truth" in json.dumps(payload).lower():
            raise PairedInkEvaluationError("scanner transport is not truth blind")
        source_inputs.append(payload)
        source_receipts.append({"source_id": source_id, "source_pixel_sha256": pixel_sha,
                                "width": image.width, "height": image.height,
                                "panel_count": len(rendered["panels"])})
    proc = subprocess.Popen(["dotnet", str(dll), "--stdio"], cwd=ROOT, stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                            encoding="utf-8", errors="strict", bufsize=1)
    assert proc.stdin is not None and proc.stdout is not None
    outputs: list[dict[str, Any]] = []
    try:
        for payload in source_inputs:
            proc.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
            proc.stdin.flush()
            line = proc.stdout.readline()
            if not line:
                raise PairedInkEvaluationError("scanner exited without a source output")
            outputs.append(json.loads(line))
    finally:
        proc.stdin.close()
    stderr = proc.stderr.read() if proc.stderr else ""
    if proc.wait(timeout=60) != 0:
        raise PairedInkEvaluationError(f"scanner process failed: {stderr[-4000:]}")
    if len(outputs) != len(source_inputs):
        raise PairedInkEvaluationError("scanner output source count differs from inputs")
    scan_by_panel: dict[str, list[dict[str, Any]]] = {}
    default_by_panel: dict[str, list[dict[str, Any]]] = {}
    frozen_v4, _, _ = _bound_json(V4_REPORT_RELATIVE, "pinned V4 axis report", 2_000_000)
    frozen_panels = {panel["panel_id"]: panel for panel in frozen_v4["panel_results"]}
    rows: list[dict[str, Any]] = []
    for source_input, output in zip(source_inputs, outputs):
        if output.get("source_id") != source_input["source_id"] or "error" in output:
            raise PairedInkEvaluationError(f"scanner source response invalid: {output.get('error')}")
        if output.get("png_sha256") != source_input["image_png_sha256"]:
            raise PairedInkEvaluationError("scanner transport PNG hash mismatch")
        source_id = output["source_id"]
        for panel in output["panels"]:
            panel_id = panel["panel_id"]
            if panel_id not in frozen_panels:
                raise PairedInkEvaluationError(f"unknown scanner panel id: {panel_id}")
            default = [t for t in panel["default_x_axis_ticks"] if t["usable_for_ocr_tick_seed"]]
            frozen = frozen_panels[panel_id]["x_axis_ticks"]
            if len(default) != len(frozen) or any(
                    a["tick_id"] != b["tick_id"]
                    or max(abs(x-y) for x, y in zip(a["center"], b["center"])) > 1e-6
                    or max(abs(x-y) for x, y in zip(a["line"], b["line"])) > 1e-6
                    for a, b in zip(default, frozen)):
                raise PairedInkEvaluationError(f"production-default axis parity failed: {panel_id}")
            default_by_panel[panel_id] = default
            proposals = panel["ink_proposals"]
            scan_by_panel[panel_id] = proposals
            for proposal_index, proposal in enumerate(proposals):
                cx, cy = (float(v) for v in proposal["center"])
                crop, transform = materialize._crop_and_resize(images[source_id], cx, cy - 2.0)
                rows.append({"proposal_id": f"{source_id}:{panel_id}:{proposal_index}",
                    "source_id": source_id, "panel_id": panel_id, "proposal_index": proposal_index,
                    "proposal": proposal, "transform": transform,
                    "pixels": np.asarray(crop, dtype=np.float32) / 255.0})
    return rows, scan_by_panel, default_by_panel, source_receipts, rendered_sources, outputs


def _infer_pair(models: Sequence[Any], rows: list[dict[str, Any]], torch: Any, device: Any, batch_size: int) -> None:
    for model in models:
        model.eval()
    with torch.inference_mode():
        for start in range(0, len(rows), batch_size):
            batch = torch.from_numpy(np.stack([r["pixels"] for r in rows[start:start + batch_size]]).astype(np.float32)).unsqueeze(1).to(device)
            if batch.device != device:
                raise PairedInkEvaluationError("proposal crop tensor left RX 6600 XT")
            for key, model in zip(("p2", "mixed"), models):
                logits, boxes = model(batch)
                decoded = p2.decode_predictions(logits, boxes)
                for row, predictions in zip(rows[start:start + batch_size], decoded):
                    top = predictions[0] if predictions else None
                    score, box = top if top is not None else (None, None)
                    row[f"{key}_score"] = score
                    row[f"{key}_box_normalized_crop"] = list(box) if box is not None else None
                    mapped = prior_filter.crop_box_to_original(box, row["transform"]) if box is not None else None
                    row[f"{key}_box_original"] = list(mapped) if mapped is not None else None
                _RUN["inference_batches_per_model"] += 1
    torch.cuda.synchronize(device)


def _glyph_index(manifest: Mapping[str, Any], rendered_sources: Mapping[str, Any]) -> tuple[
        dict[str, list[dict[str, Any]]], dict[tuple[str, str], tuple[float, float, float, float]], dict[str, str], int, int]:
    truths_by_panel: dict[str, list[dict[str, Any]]] = {}
    glyph_boxes: dict[tuple[str, str], tuple[float, float, float, float]] = {}
    tick_id_by_case: dict[str, str] = {}
    visible = invisible = 0
    for source in manifest["sources"]:
        source_id = source["source_id"]
        rendered = rendered_sources[source_id]
        source_truth = prior.source_truths(source_id, rendered["panels"])
        for panel_index, panel in enumerate(rendered["panels"]):
            panel_id = panel["panel_id"]
            truths_by_panel[panel_id] = source_truth[panel_id]
            ticks = [t for t in panel.get("ticks", []) if t.get("axis") == "x" and t.get("role") == "x_tick"]
            for tick_index, tick in enumerate(ticks):
                tick_id_by_case[f"{source_id}-p{panel_index}-t{tick_index}"] = str(tick["tick_id"])
            visible += sum(t["source_stroke_visible"] for t in source_truth[panel_id])
            invisible += sum(not t["source_stroke_visible"] for t in source_truth[panel_id])
        for crop_row in materialize.materialize_recipe(source["renderer_recipe"]):
            for target in crop_row["targets"]:
                if target["text"] == crop_row["expected_tick_label"]:
                    glyph_boxes[(crop_row["source_panel_id"], crop_row["tick_id"])] = tuple(float(v) for v in target["glyph_mask_xyxy"])
    return truths_by_panel, glyph_boxes, tick_id_by_case, visible, invisible


def evaluate(config: Mapping[str, Any], config_bytes: bytes, pair_config: Mapping[str, Any], output: Path) -> dict[str, Any]:
    if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
        raise PairedInkEvaluationError("inference must run via project GPU Python launcher")
    import torch
    runtime = helpers.require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    _RUN["phase"] = "paired-checkpoint-admission"
    model_p2, model_mixed, refs = pair_probe._load_checkpoint_pair(pair_config, device, torch)
    manifest_bound = helpers._bound_read(ROOT, f"{CORPUS_RELATIVE}/manifest.json", "pinned source-disjoint probe manifest", 10_000_000)
    if manifest_bound.sha256 != config["probe_manifest_sha256"]:
        raise PairedInkEvaluationError("source-disjoint probe manifest changed")
    manifest = helpers._parse_json(manifest_bound, "source-disjoint probe manifest")
    if (manifest.get("generator_sha256") != config["probe_generator_sha256"]
            or manifest.get("crop_materializer_sha256") != config["crop_materializer_sha256"]
            or manifest.get("train_separation", {}).get("compared_to_manifest_sha256") != config["train_manifest_sha256"]
            or any(manifest.get("train_separation", {}).get("overlap_counts", {}).values())):
        raise PairedInkEvaluationError("probe source-disjointness or renderer identity changed")
    _RUN["phase"] = "truth-blind-scanner-proposals"
    dll = (ROOT / DLL_RELATIVE).resolve(strict=True)
    ink_root = (ROOT / INK_RELATIVE).resolve(strict=True)
    if not dll.is_relative_to(ink_root):
        raise PairedInkEvaluationError("scanner DLL is outside the pinned project")
    rows, scan_by_panel, default_by_panel, source_receipts, rendered_sources, scanner_outputs = _scan_truth_blind(manifest, dll)
    scanner_payload = {"schema": "graphreader.truth-blind-ink-proposals.v1",
        "probe_manifest_sha256": manifest_bound.sha256, "scanner_binary_sha256": config["scanner_binary_sha256"],
        "source_pixel_hashes": source_receipts, "source_results": scanner_outputs,
        "truth_sent_to_scanner": False}
    scanner_bytes = canonical(scanner_payload)
    scanner_file = output / "scanner_proposals.json"
    scanner_file.write_bytes(scanner_bytes)
    scanner_hash = digest(scanner_bytes)
    if len(rows) != 365:
        raise PairedInkEvaluationError(f"scanner proposal count differs from prior run: {len(rows)}")
    _RUN["phase"] = "paired-gpu-inference"
    _infer_pair((model_p2, model_mixed), rows, torch, device, int(config["crop_batch_size"]))
    scored_proposals = [{"proposal_id": r["proposal_id"], "source_id": r["source_id"],
        "panel_id": r["panel_id"], "proposal_index": r["proposal_index"],
        "p2_score": r["p2_score"], "p2_box_normalized_crop": r["p2_box_normalized_crop"],
        "p2_box_original": r["p2_box_original"], "mixed_score": r["mixed_score"],
        "mixed_box_normalized_crop": r["mixed_box_normalized_crop"],
        "mixed_box_original": r["mixed_box_original"]} for r in rows]
    scored_bytes = canonical({"schema": "graphreader.paired-ink-proposal-scores.v1",
        "scanner_proposals_sha256": scanner_hash, "rows": scored_proposals})
    (output / "paired_proposal_scores.json").write_bytes(scored_bytes)
    # Ground truth is reconstructed only after both models have scored every proposal.
    _RUN["phase"] = "post-inference-truth-scoring"
    truths_by_panel, glyph_boxes, tick_case_ids, visible, invisible = _glyph_index(manifest, rendered_sources)
    if (visible, invisible) != (181, 241):
        raise PairedInkEvaluationError("probe visible/invisible tick inventory changed")
    panels = []
    # Keep scanner uncertainty from its raw output, never infer it from truth.
    scanner_panel_map = {p["panel_id"]: p for out in scanner_outputs for p in out["panels"]}
    for source in manifest["sources"]:
        source_id = source["source_id"]
        rendered = rendered_sources[source_id]
        for rendered_panel in rendered["panels"]:
            panel_id = rendered_panel["panel_id"]
            raw_panel = scanner_panel_map[panel_id]
            visible_truth = [t for t in truths_by_panel[panel_id] if t["source_stroke_visible"]]
            panel = {"panel_id": panel_id, "source_id": source_id,
                "visible_truth_count": len(visible_truth), "needs_review": raw_panel["needs_review"],
                "uncertainty_reasons": raw_panel["uncertainty_reasons"]}
            panel["production_default"] = common.match_panel(visible_truth, default_by_panel[panel_id])
            panel["scan_alone"] = common.match_panel(visible_truth, scan_by_panel[panel_id])
            panel_rows = [r for r in rows if r["panel_id"] == panel_id]
            for model_key in ("p2", "mixed"):
                for threshold in config["score_thresholds"]:
                    suffix = str(threshold).replace(".", "")
                    key = f"{model_key}_filtered_scan_{suffix}"
                    survivors = [r for r in panel_rows if prior_filter.filter_proposal(r[f"{model_key}_score"], float(threshold))]
                    panel[key] = common.match_panel(visible_truth,
                        [{"center": r["proposal"]["center"], "line": r["proposal"]["line"]} for r in survivors])
                    panel[key + "_survivors"] = survivors
            panels.append(panel)
    all_panels = panels
    scan_only = prior_filter._summarize(all_panels, "scan_alone")
    default_metrics = prior_filter._summarize(all_panels, "production_default")
    metrics: dict[str, Any] = {"production_default": default_metrics, "scan_alone": scan_only,
                               "score_thresholds": {}}
    source_metrics: dict[str, Any] = {}
    box_scores: dict[str, Any] = {"p2": {}, "mixed": {}}
    for threshold in config["score_thresholds"]:
        suffix = str(threshold).replace(".", "")
        p2_metrics = prior_filter._summarize(all_panels, f"p2_filtered_scan_{suffix}")
        mixed_metrics = prior_filter._summarize(all_panels, f"mixed_filtered_scan_{suffix}")
        p2_boxes = _box_metric_for(all_panels, rows, "p2", float(threshold), glyph_boxes, tick_case_ids)
        mixed_boxes = _box_metric_for(all_panels, rows, "mixed", float(threshold), glyph_boxes, tick_case_ids)
        metrics["score_thresholds"][str(threshold)] = {"p2": p2_metrics, "mixed": mixed_metrics}
        box_scores["p2"][str(threshold)] = p2_boxes
        box_scores["mixed"][str(threshold)] = mixed_boxes
        for source in manifest["sources"]:
            source_id = source["source_id"]
            subset = [panel for panel in all_panels if panel["source_id"] == source_id]
            source_metrics.setdefault(source_id, {})[str(threshold)] = {
                "p2": prior_filter._summarize(subset, f"p2_filtered_scan_{suffix}"),
                "mixed": prior_filter._summarize(subset, f"mixed_filtered_scan_{suffix}"),
            }
    prior_report, _, prior_report_sha = _bound_json(FILTER_REPORT_RELATIVE, "frozen P2 ink-filter report", 2_000_000)
    p2_parity = {}
    for threshold, suffix in (("0.25", "025"), ("0.5", "05")):
        p2_parity[threshold] = {
            "proposal_match_metrics_equal": metrics["score_thresholds"][threshold]["p2"] == prior_report["metrics"][f"detector_filtered_scan_{suffix}"],
            "glyph_box_metrics_equal": box_scores["p2"][threshold] == prior_report["metrics"]["p2_top_box_glyph_localization"]["score_thresholds"][threshold],
        }
    if not all(v["proposal_match_metrics_equal"] and v["glyph_box_metrics_equal"] for v in p2_parity.values()):
        raise PairedInkEvaluationError(f"reconstructed P2 scanner-filter metrics failed parity: {p2_parity}")
    summary = {
        "source_group_count": len(manifest["sources"]), "panel_count": len(all_panels),
        "visible_truth_ticks": visible, "invisible_truth_ticks_excluded_from_recall": invisible,
        "scan_proposals": len(rows),
        "p2_top_box_missing": sum(r["p2_score"] is None for r in rows),
        "mixed_top_box_missing": sum(r["mixed_score"] is None for r in rows),
        "axis_uncertain_panel_count": sum(bool(p["needs_review"]) for p in all_panels),
        "axis_uncertainty_reasons": dict(sorted(Counter(reason for p in all_panels for reason in p["uncertainty_reasons"]).items())),
        "panels_with_no_visible_ticks": sum(p["visible_truth_count"] == 0 for p in all_panels),
    }
    _drop_survivor_scratch(all_panels)
    report = {
        "schema": "graphreader.stroke-seeded-xtick-v1-paired-ink-filter-report.v1",
        "status": "completed-unregistered-consumed-exploratory-synthetic-diagnostic",
        "recipe_id": config["recipe_id"], "acceptance_status": "consumed synthetic diagnostic only; not formal acceptance",
        "config_sha256": digest(config_bytes), "evaluator_sha256": digest(Path(__file__).read_bytes()),
        "probe_manifest_sha256": manifest_bound.sha256,
        "probe_source_pixel_hashes_verified": source_receipts,
        "source_disjointness": manifest["train_separation"],
        "scanner_proposal_contract": {"truth_sent_to_scanner": False, "truth_sent_to_cropper_or_detector": False,
            "proposal_frame": "original full-panel pixels",
            "crop_geometry": "materialize._crop_and_resize at proposal center x and y minus 2 px; P1 transform preserved",
            "filter_rule": "per model, retain the highest-confidence valid decoded crop box only when its score reaches .25 or .5",
            "score_thresholds": config["score_thresholds"], "box_iou_scoring_threshold": 0.5},
        "scanner_identity": {"project_sha256": config["scanner_project_sha256"],
            "source_sha256": config["scanner_source_sha256"], "binary_sha256": config["scanner_binary_sha256"],
            "raw_proposal_artifact": "scanner_proposals.json", "raw_proposal_sha256": scanner_hash,
            "proposal_scores_artifact": "paired_proposal_scores.json",
            "proposal_scores_sha256": digest(scored_bytes),
            "prior_filter_report_sha256": prior_report_sha,
            "raw_scanner_output_was_not_retained_by_prior_report": True,
            "reconstructed_with_exact_existing_project_binary": True},
        "frozen_p2_filter_reproduction": {"prior_report_sha256": prior_report_sha,
            "threshold_parity": p2_parity, "exact_parity_passed": True},
        "frozen_model_references": refs,
        "training_dependency_provenance_limitation": {
            "message": "Training report artifacts did not bind execution-time bytes of the imported model-building and hard-negative helper files; current helper hashes are recorded but do not prove the helper bytes used during training.",
            "unbound": [
                {"path": "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py", "current_sha256": config["model_builder_helper_sha256"]},
                {"path": "ml/ocr/fragmented_xtick_detector_v4/train.py", "current_sha256": config["current_hard_negative_helper_sha256"]},
            ]},
        "runtime": runtime, "training_performed": False, "optimizer_steps": 0,
        "inference_batches_per_model": _RUN["inference_batches_per_model"],
        "counts": summary, "metrics": metrics, "p2_top_box_glyph_localization": box_scores,
        "probe_admission": {"candidate_source_recipes": manifest["candidate_source_count"],
            "admitted_source_groups": len(manifest["sources"]),
            "excluded_source_recipes": manifest["excluded_source_recipes"],
            "excluded_duplicate_crop_rows": manifest["duplicate_train_image_exclusion_count"],
            "all_admitted_scanner_proposals_inferred": len(scored_proposals)},
        "metrics_by_source": source_metrics, "panel_results": all_panels,
        "dev_sealed_private_inputs_read": False, "production_behavior_changed": False,
        "production_approval": False, "release_eligible": False,
        "limitations": ["This is a consumed source-disjoint synthetic diagnostic, not acceptance or real-chart validation.",
            "The previous P2 report retained aggregate metrics but not raw scanner proposals; proposals were reconstructed using the exact pinned scanner binary and P2 metrics were required to match the prior report.",
            "The scanner has known grid, border, and nearby-text confusers; axis uncertainty is retained and reported.",
            "Training helper execution-time source bytes were not pinned by the completed model reports."],
        "finished_utc": datetime.now(timezone.utc).isoformat(),
    }
    (output / "report.json").write_bytes(canonical(report))
    return report


def _box_metric_for(panels: list[dict[str, Any]], rows: list[dict[str, Any]], model_key: str, threshold: float,
                    glyph_boxes: Mapping[tuple[str, str], tuple[float, float, float, float]],
                    tick_case_ids: Mapping[str, str]) -> dict[str, Any]:
    suffix = str(threshold).replace(".", "")
    key = f"{model_key}_filtered_scan_{suffix}"
    ious = []
    for panel in panels:
        survivors = panel[key + "_survivors"]
        for pair in panel[key]["matched_pairs"]:
            row = survivors[pair["detection_index"]]
            tick_id = tick_case_ids.get(pair["case_id"])
            target = glyph_boxes.get((panel["panel_id"], tick_id)) if tick_id is not None else None
            pred = row.get(f"{model_key}_box_original")
            if target is not None and pred is not None:
                ious.append(prior_filter.box_iou(tuple(pred), target))
    accepted = [r for r in rows if prior_filter.filter_proposal(r[f"{model_key}_score"], threshold)]
    return {"accepted_proposals": len(accepted),
            "rejected_by_score_or_missing_box": len(rows) - len(accepted),
            "matched_tick_pairs_with_label_box": len(ious),
            "matched_tick_top_box_mean_iou_to_glyph": sum(ious) / len(ious) if ious else None,
            "matched_tick_top_box_iou_ge_0_5": sum(iou >= 0.5 for iou in ious)}


def _drop_survivor_scratch(panels: list[dict[str, Any]]) -> None:
    """Remove large in-memory crop rows after metrics, before report JSON encoding."""
    for panel in panels:
        for model_key in ("p2", "mixed"):
            for threshold in (0.25, 0.5):
                key = f"{model_key}_filtered_scan_{str(threshold).replace('.', '')}_survivors"
                panel.pop(key, None)


def run() -> dict[str, Any]:
    _RUN.update(phase="starting", inference_batches_per_model=0)
    started = time.perf_counter()
    config, config_bytes, pair_config = load_config()
    output = ROOT / OUTPUT_RELATIVE
    if output.exists() or output.is_symlink() or output.parent != ROOT / "artifacts":
        raise PairedInkEvaluationError("planned output must be a fresh direct project artifact child")
    helpers._checked_chain(ROOT, output.parent, "paired ink output parent")
    output.mkdir(parents=False, exist_ok=False)
    (output / "started.json").write_bytes(canonical({"status": "started", "recipe_id": config["recipe_id"],
        "config_sha256": digest(config_bytes), "evaluator_sha256": digest(Path(__file__).read_bytes()),
        "started_utc": datetime.now(timezone.utc).isoformat(), "training_performed": False,
        "optimizer_steps": 0, "truth_scoring_started": False}))
    try:
        result = evaluate(config, config_bytes, pair_config, output)
        result["elapsed_seconds"] = round(time.perf_counter() - started, 6)
        (output / "report.json").write_bytes(canonical(result))
        return result
    except Exception as error:
        (output / "failure.json").write_bytes(canonical({"status": "failed", "recipe_id": config["recipe_id"],
            "config_sha256": digest(config_bytes), "evaluator_sha256": digest(Path(__file__).read_bytes()),
            "phase": _RUN["phase"], "failure_type": type(error).__name__, "failure_message": str(error),
            "optimizer_steps": 0, "dev_sealed_private_inputs_read": False,
            "failed_utc": datetime.now(timezone.utc).isoformat()}))
        raise


if __name__ == "__main__":
    print(json.dumps(run(), sort_keys=True, indent=2))
