# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare one highest-score detection per unambiguous stroke-seeded crop."""

from __future__ import annotations

from collections import Counter
from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys
from typing import Any

import numpy as np

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers
from ml.ocr.stroke_seeded_xtick_v1 import probe_evaluate_gpu, probe_materialize, train_gpu


CONFIG = ROOT / "ml" / "ocr" / "stroke_seeded_xtick_v1" / "top_one_seed_p2.json"
P2_CONFIG = ROOT / "ml" / "ocr" / "stroke_seeded_xtick_v1" / "training" / "p2.json"
_RUN = {"phase": "starting", "inference_batches": 0}


class TopOneAnalysisError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def seed_crop_disposition(kind: str, trainable: bool, target_count: int,
                          expected_label: str | None, exclusion_reasons: list[str]) -> str:
    """Ambiguous, excluded, or inconsistent targets are review-only, never negatives."""
    if not trainable or exclusion_reasons or kind == "excluded":
        return "needs-review"
    if kind == "positive" and target_count == 1 and isinstance(expected_label, str) and expected_label:
        return "score"
    if kind == "background_only" and target_count == 0 and expected_label is None:
        return "score"
    return "needs-review"


def top_one_metrics(predictions: list[list[tuple[float, tuple[float, float, float, float]]]],
                    targets: list[tuple[float, float, float, float] | None],
                    score_thresholds: list[float], match_threshold: float) -> dict[str, Any]:
    if len(predictions) != len(targets):
        raise ValueError("prediction/seed target counts differ")
    positives = sum(target is not None for target in targets)
    backgrounds = len(targets) - positives
    metrics: dict[str, Any] = {
        "image_count": len(targets), "positive_count": positives, "background_only_count": backgrounds,
        "detections_per_seed_maximum": 1, "match_iou_threshold": match_threshold,
        "score_threshold_metrics": {},
    }
    for threshold in score_thresholds:
        tp = fp = fn = bg_fp = 0
        top1_matched = selected = 0
        for row_predictions, truth in zip(predictions, targets):
            detection = row_predictions[0] if row_predictions and row_predictions[0][0] >= threshold else None
            if detection is not None:
                selected += 1
                if truth is None:
                    fp += 1
                    bg_fp += 1
                elif train_gpu.box_iou(detection[1], truth) >= match_threshold:
                    tp += 1
                    top1_matched += 1
                else:
                    fp += 1
                    fn += 1
            elif truth is not None:
                fn += 1
        metrics["score_threshold_metrics"][str(threshold)] = {
            "true_positives": tp, "false_positives": fp, "false_negatives": fn,
            "selected_detections": selected,
            "precision": tp / max(1, tp + fp), "recall": tp / max(1, tp + fn),
            "false_positives_per_image": fp / max(1, len(targets)),
            "false_positives_per_background_image": bg_fp / max(1, backgrounds),
            "top_confidence_box_recall": top1_matched / max(1, positives),
        }
    return metrics


def _load_config() -> tuple[dict[str, Any], bytes]:
    bound = helpers._bound_read(ROOT, "ml/ocr/stroke_seeded_xtick_v1/top_one_seed_p2.json",
                                "top-one seed analysis config", 65_536)
    config = helpers._parse_json(bound, "top-one seed analysis config")
    exact = {
        "schema": "graphreader.stroke-seeded-xtick-v1-top-one-seed-analysis-config.v1",
        "recipe_id": "stroke-seeded-xtick-v1-p2-top-one-per-seed-analysis-v1",
        "checkpoint_path": "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/checkpoint.pt",
        "checkpoint_sha256": "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351",
        "p2_training_report_path": "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/report.json",
        "p2_training_report_sha256": "cc013b5eda70af9681dbd87cc3e496a668cc29d0dcfba8d0841250782bdcb615",
        "p2_training_config_sha256": "1ae28371439c7106631337c12db8f1661f7152778532628db8b3163c6b4cd5e5",
        "p2_runner_sha256": "dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6",
        "train_manifest_path": "artifacts/stroke-seeded-xtick-v1-train-verified-v6/manifest.json",
        "train_manifest_sha256": "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a",
        "probe_manifest_path": "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json",
        "probe_manifest_sha256": "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba",
        "probe_evaluation_report_path": "artifacts/stroke-seeded-xtick-v1-p2-source-disjoint-probe-eval-v2/report.json",
        "probe_evaluation_report_sha256": "97932d6f2b12debdd009014d2cbcfd5ff63db8754ccc664079df2551d7242340",
        "model_identity": "stroke-seeded-xtick-spatial-locator-rf31-logwh-hard-negative-p2",
        "score_thresholds": [0.25, 0.5], "nms_iou_threshold": 0.5, "match_iou_threshold": 0.5,
        "maximum_detections_per_image": 100,
        "required_device_name_substring": "RX 6600 XT", "cpu_fallback_allowed": False,
        "optimizer_execution_authorized": False, "dev_input_allowed": False,
        "sealed_input_allowed": False, "private_input_allowed": False,
        "planned_output_root": "artifacts/stroke-seeded-xtick-v1-p2-top-one-seed-analysis-v1",
        "acceptance_status": "exploratory analysis only",
    }
    for key, value in exact.items():
        if config.get(key) != value:
            raise TopOneAnalysisError(f"top-one config mismatch: {key}")
    if config.get("expected_analyzer_sha256") != digest(Path(__file__).read_bytes()):
        raise TopOneAnalysisError("top-one analyzer source hash changed")
    return config, bound.value


def _assert_baseline_reproduced(actual: dict[str, Any], expected: dict[str, Any], label: str) -> None:
    for key in ("image_count", "positive_count", "background_only_count"):
        if actual.get(key) != expected.get(key):
            raise TopOneAnalysisError(f"{label} NMS baseline differs at {key}")
    for threshold in ("0.25", "0.5"):
        measured = actual["score_threshold_metrics"][threshold]
        frozen = expected["score_threshold_metrics"][threshold]
        for key in ("true_positives", "false_positives", "false_negatives"):
            if measured[key] != frozen[key]:
                raise TopOneAnalysisError(f"{label} NMS baseline differs at {threshold}:{key}")
        for key in ("precision", "recall", "false_positives_per_image",
                    "false_positives_per_background_image", "top_confidence_box_recall"):
            if abs(float(measured[key]) - float(frozen[key])) > 1e-9:
                raise TopOneAnalysisError(f"{label} NMS baseline differs at {threshold}:{key}")
    for key in ("top_confidence_box_mean_iou_on_positives", "top_confidence_box_median_iou_on_positives"):
        if abs(float(actual[key]) - float(expected[key])) > 1e-8:
            raise TopOneAnalysisError(f"{label} NMS baseline differs at {key}")


def _read_exclusion_summary(manifest: dict[str, Any], relative_root: Path) -> dict[str, Any]:
    counts: Counter[str] = Counter()
    rows = 0
    for row in manifest["records"]:
        if row.get("kind") != "excluded":
            continue
        rows += 1
        bound = helpers._bound_read(relative_root, row["annotation_path"],
                                    f"excluded crop annotation {row['case_id']}", 65_536)
        if bound.sha256 != row.get("annotation_sha256"):
            raise TopOneAnalysisError("excluded crop annotation hash changed")
        annotation = helpers._parse_json(bound, "excluded crop annotation")
        for reason in annotation.get("exclusion_reasons", []):
            counts[str(reason)] += 1
    return {"needs_review_crop_count": rows, "reasons": dict(sorted(counts.items()))}


def _check_seed_contract(manifest: dict[str, Any], corpus_root: Path,
                         admitted_case_ids: set[str]) -> dict[str, Any]:
    seen_seed_keys: set[tuple[str, str, str]] = set()
    admitted = needs_review = 0
    for row in manifest["records"]:
        bound = helpers._bound_read(corpus_root, row["annotation_path"],
                                    f"seed annotation {row['case_id']}", 65_536)
        if bound.sha256 != row.get("annotation_sha256"):
            raise TopOneAnalysisError("seed annotation hash changed")
        annotation = helpers._parse_json(bound, "seed annotation")
        reasons = annotation.get("exclusion_reasons", [])
        targets = annotation.get("targets", [])
        disposition = seed_crop_disposition(
            str(row["kind"]), bool(row.get("trainable_single_box")), len(targets),
            annotation.get("expected_tick_label"), reasons)
        in_admission = row["case_id"] in admitted_case_ids
        if in_admission != (disposition == "score"):
            raise TopOneAnalysisError("single-seed admission disagrees with manifest exclusion/target state")
        if disposition == "needs-review":
            needs_review += 1
            continue
        tick_id = annotation.get("tick_id")
        panel_id = annotation.get("source_panel_id")
        if not isinstance(tick_id, str) or not isinstance(panel_id, str):
            raise TopOneAnalysisError("admitted crop does not identify one stroke seed")
        seed_key = (str(annotation["source_id"]), panel_id, tick_id)
        if seed_key in seen_seed_keys:
            raise TopOneAnalysisError("multiple admitted crops reuse one source-panel tick seed")
        seen_seed_keys.add(seed_key)
        admitted += 1
    if admitted != len(admitted_case_ids):
        raise TopOneAnalysisError("seed-key count differs from admitted crop rows")
    return {"one_crop_per_seed": True, "admitted_seed_count": admitted,
            "needs_review_or_excluded_crop_count": needs_review}


def _infer(model: Any, pixels: np.ndarray, device: Any, torch: Any) -> list[list[tuple[float, tuple[float, float, float, float]]]]:
    predictions = []
    model.eval()
    with torch.inference_mode():
        for start in range(0, len(pixels), 32):
            batch = torch.from_numpy(pixels[start:start + 32]).unsqueeze(1).to(device)
            if batch.device != device:
                raise TopOneAnalysisError("inference tensor left the RX 6600 XT")
            logits, boxes = model(batch)
            predictions.extend(train_gpu.decode_predictions(logits, boxes))
    torch.cuda.synchronize(device)
    return predictions


def _run_analysis(config: dict[str, Any], config_bytes: bytes, output: Path) -> dict[str, Any]:
    if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
        raise TopOneAnalysisError("frozen model inference must use the project GPU Python launcher")
    import torch

    _RUN["phase"] = "rx6600xt-binding"
    runtime = helpers.require_rx_6600_xt(torch)
    device = torch.device(runtime["device"])
    _RUN["phase"] = "frozen-source-admission"
    checkpoint_bound = helpers._bound_read(ROOT, config["checkpoint_path"], "frozen P2 checkpoint", 1_000_000)
    p2_report_bound = helpers._bound_read(ROOT, config["p2_training_report_path"], "P2 training report", 1_000_000)
    probe_report_bound = helpers._bound_read(ROOT, config["probe_evaluation_report_path"], "consumed P2 probe report", 1_000_000)
    if checkpoint_bound.sha256 != config["checkpoint_sha256"] or p2_report_bound.sha256 != config["p2_training_report_sha256"] or probe_report_bound.sha256 != config["probe_evaluation_report_sha256"]:
        raise TopOneAnalysisError("frozen P2 model or comparison report bytes changed")
    training_report = helpers._parse_json(p2_report_bound, "P2 training report")
    probe_report = helpers._parse_json(probe_report_bound, "consumed probe evaluation report")
    train_config_bound = helpers._bound_read(ROOT, "ml/ocr/stroke_seeded_xtick_v1/training/p2.json", "P2 training config", 65_536)
    if train_config_bound.sha256 != config["p2_training_config_sha256"] or training_report.get("trainer_sha256") != config["p2_runner_sha256"]:
        raise TopOneAnalysisError("P2 training config or trainer identity changed")
    if (probe_report.get("checkpoint_sha256") != checkpoint_bound.sha256
            or probe_report.get("probe_manifest_sha256") != config["probe_manifest_sha256"]
            or probe_report.get("train_manifest_sha256") != config["train_manifest_sha256"]):
        raise TopOneAnalysisError("probe report does not bind the frozen P2 checkpoint/corpus")
    checkpoint = torch.load(BytesIO(checkpoint_bound.value), map_location=device, weights_only=True)
    for key, value in {"recipe_id": "stroke-seeded-xtick-v1-hard-negative-fit-p2",
                       "model_identity": config["model_identity"],
                       "input_manifest_sha256": config["train_manifest_sha256"],
                       "optimizer_steps": 2760}.items():
        if checkpoint.get(key) != value:
            raise TopOneAnalysisError(f"frozen checkpoint metadata mismatch: {key}")
    model = helpers.build_spatial_locator(torch).to(device)
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    if any(parameter.device != device for parameter in model.parameters()):
        raise TopOneAnalysisError("model did not remain on the RX 6600 XT")

    _RUN["phase"] = "train-and-consumed-probe-admission"
    p2_config, p2_config_bytes = train_gpu.load_config(P2_CONFIG)
    if digest(p2_config_bytes) != config["p2_training_config_sha256"]:
        raise TopOneAnalysisError("P2 config identity changed during analysis")
    train = train_gpu.load_train_inputs(p2_config)
    train_manifest = train["manifest"]
    # Input loader already bound the exact V6 annotations; resolve its pinned corpus for seed/exclusion checks.
    train_corpus = ROOT / Path(p2_config["train_manifest_path"]).parent
    train_seed_contract = _check_seed_contract(train_manifest, train_corpus,
                                               {row["case_id"] for row in train["rows"]})
    train_exclusions = _read_exclusion_summary(train_manifest, train_corpus)
    _RUN["phase"] = "probe-admission"
    probe_config, _ = probe_evaluate_gpu.load_config()
    probe = probe_evaluate_gpu.load_probe_rows(probe_config)
    probe_case_ids = {row["case_id"] for row in probe["rows"]}
    probe_corpus = ROOT / Path(probe_config["probe_manifest_path"]).parent
    probe_seed_contract = _check_seed_contract(probe["manifest"], probe_corpus, probe_case_ids)
    probe_exclusions = {"needs_review_crop_count": probe["counts"]["excluded"],
                        "reasons": probe["excluded_reasons"],
                        "training_image_hash_collision_crops_excluded": probe["manifest"]["duplicate_train_image_exclusion_count"],
                        "non_affine_source_recipes_excluded": len(probe["manifest"]["excluded_source_recipes"])}

    _RUN["phase"] = "gpu-inference"
    train_pixels = np.stack(train["pixels"]).astype(np.float32)
    train_predictions = _infer(model, train_pixels, device, torch)
    _RUN["inference_batches"] = (len(train_predictions) + 31) // 32
    probe_predictions = _infer(model, probe["pixels"], device, torch)
    _RUN["inference_batches"] += (len(probe_predictions) + 31) // 32
    thresholds = list(config["score_thresholds"])
    train_targets = list(train["boxes"])
    probe_targets = list(probe["boxes"])
    train_nms = train_gpu.image_detection_metrics(
        train_predictions, train_targets, thresholds, float(config["nms_iou_threshold"]),
        float(config["match_iou_threshold"]), int(config["maximum_detections_per_image"]))
    probe_nms = train_gpu.image_detection_metrics(
        probe_predictions, probe_targets, thresholds, float(config["nms_iou_threshold"]),
        float(config["match_iou_threshold"]), int(config["maximum_detections_per_image"]))
    _assert_baseline_reproduced(train_nms, training_report["train_fit_after_image_metrics"], "V6 train-fit")
    _assert_baseline_reproduced(probe_nms, probe_report["probe_image_metrics"], "consumed source-disjoint probe")
    train_one = top_one_metrics(train_predictions, train_targets, thresholds, float(config["match_iou_threshold"]))
    probe_one = top_one_metrics(probe_predictions, probe_targets, thresholds, float(config["match_iou_threshold"]))
    report = {
        "schema": "graphreader.stroke-seeded-xtick-v1-p2-top-one-seed-analysis.v1",
        "status": "completed-unregistered-exploratory-analysis",
        "recipe_id": config["recipe_id"], "acceptance_status": config["acceptance_status"],
        "config_sha256": digest(config_bytes), "analyzer_sha256": digest(Path(__file__).read_bytes()),
        "checkpoint_path": config["checkpoint_path"], "checkpoint_sha256": checkpoint_bound.sha256,
        "p2_training_report_sha256": p2_report_bound.sha256,
        "probe_evaluation_report_sha256": probe_report_bound.sha256,
        "train_manifest_sha256": config["train_manifest_sha256"],
        "probe_manifest_sha256": config["probe_manifest_sha256"],
        "runtime": runtime, "optimizer_steps": 0, "training_performed": False,
        "seed_contract": {"train_fit": train_seed_contract, "consumed_probe": probe_seed_contract},
        "excluded_or_needs_review": {"train_fit": train_exclusions, "consumed_probe": probe_exclusions},
        "metric_contract": {"top_one": config["top_one_rule"], "score_thresholds": thresholds,
            "nms_comparison_iou_threshold": config["nms_iou_threshold"],
            "match_iou_threshold": config["match_iou_threshold"],
            "nms_maximum_detections_per_crop": config["maximum_detections_per_image"]},
        "train_fit_nms_baseline": train_nms, "train_fit_top_one_per_seed": train_one,
        "consumed_probe_nms_baseline": probe_nms, "consumed_probe_top_one_per_seed": probe_one,
        "comparison_limit": "The prior source-disjoint probe is consumed exploratory synthetic data, not untouched acceptance. Top-one is a postprocessing analysis of a single-box-per-seed model; no P1 on-probe comparison or product acceptance is claimed.",
        "dev_sealed_private_inputs_read": False, "production_approval": False, "release_eligible": False,
        "finished_utc": datetime.now(timezone.utc).isoformat(),
    }
    (output / "report.json").write_bytes(canonical_json(report))
    return report


def run() -> dict[str, Any]:
    _RUN.update(phase="starting", inference_batches=0)
    config, config_bytes = _load_config()
    output = ROOT / config["planned_output_root"]
    if output.parent != ROOT / "artifacts" or output.exists() or output.is_symlink():
        raise TopOneAnalysisError("top-one output must be a fresh project artifacts directory")
    helpers._checked_chain(ROOT, output.parent, "top-one output parent")
    output.mkdir(parents=True, exist_ok=False)
    (output / "started.json").write_bytes(canonical_json({
        "status": "started", "recipe_id": config["recipe_id"], "config_sha256": digest(config_bytes),
        "analyzer_sha256": digest(Path(__file__).read_bytes()),
        "started_utc": datetime.now(timezone.utc).isoformat(), "training_performed": False,
    }))
    try:
        return _run_analysis(config, config_bytes, output)
    except Exception as error:
        (output / "failure.json").write_bytes(canonical_json({
            "schema": "graphreader.stroke-seeded-xtick-v1-top-one-analysis-failure.v1", "status": "failed",
            "recipe_id": config.get("recipe_id"), "config_sha256": digest(config_bytes),
            "analyzer_sha256": digest(Path(__file__).read_bytes()), "failure_type": type(error).__name__,
            "failure_message": str(error), "phase": _RUN["phase"], "inference_batches": _RUN["inference_batches"],
            "optimizer_steps": 0, "dev_sealed_private_inputs_read": False,
        }))
        raise


if __name__ == "__main__":
    print(json.dumps(run(), sort_keys=True, indent=2))
