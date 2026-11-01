# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Paired GPU-only detector evaluation on the already-consumed synthetic probe."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys
from typing import Any, Mapping, Sequence

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers
from ml.ocr.stroke_seeded_xtick_v1 import probe_evaluate_gpu, train_gpu as p2

CONFIG = Path(__file__).resolve().parent / "paired_probe_eval_v1.json"
OUTPUT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-p2-vs-curated-mix-probe-eval-v1"
_RUN = {"phase": "starting", "inference_batches_per_model": 0}


class PairedEvaluationError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _bound_json(path: str, label: str, limit: int) -> tuple[Any, bytes, str]:
    bound = helpers._bound_read(ROOT, path, label, limit)
    return helpers._parse_json(bound, label), bound.value, bound.sha256


def top_one_metrics(predictions: Sequence[Sequence[tuple[float, tuple[float, float, float, float]]]],
                    targets: Sequence[tuple[float, float, float, float] | None],
                    score_thresholds: Sequence[float], match_iou_threshold: float) -> dict[str, Any]:
    """Score one highest-confidence valid box per row after the score gate."""
    if len(predictions) != len(targets):
        raise ValueError("prediction and target row counts differ")
    results: dict[str, Any] = {}
    for threshold in score_thresholds:
        tp = fp = fn = bg_fp = top1_matched = 0
        for detections, truth in zip(predictions, targets):
            candidate = detections[0] if detections else None
            accepted = candidate if candidate is not None and candidate[0] >= threshold else None
            if truth is None:
                if accepted is not None:
                    fp += 1
                    bg_fp += 1
                continue
            if accepted is not None and p2.box_iou(accepted[1], truth) >= match_iou_threshold:
                tp += 1
                top1_matched += 1
            else:
                fn += 1
                if accepted is not None:
                    fp += 1
        image_count = len(targets)
        positive_count = sum(target is not None for target in targets)
        background_count = image_count - positive_count
        results[str(threshold)] = {
            "true_positives": tp, "false_positives": fp, "false_negatives": fn,
            "precision": tp / max(1, tp + fp), "recall": tp / max(1, positive_count),
            "false_positives_per_image": fp / max(1, image_count),
            "false_positives_per_background_image": bg_fp / max(1, background_count),
            "background_false_positives": bg_fp, "top_one_matched_positive_count": top1_matched,
        }
    return results


def load_config() -> tuple[dict[str, Any], bytes]:
    rel = CONFIG.relative_to(ROOT).as_posix()
    config, raw, config_hash = _bound_json(rel, "paired probe evaluation config", 65_536)
    expected = {
        "schema": "graphreader.stroke-seeded-xtick-v1-paired-probe-evaluation-config.v1",
        "recipe_id": "stroke-seeded-xtick-v1-p2-vs-curated-mix-source-disjoint-diagnostic-v1",
        "probe_config_sha256": "d6c74a492f10af9966e10abab2cc114eaae0cd228100b6243ae123f8286ec54f",
        "probe_manifest_path": "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json",
        "probe_manifest_sha256": "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba",
        "probe_evaluator_sha256": "df9e804facd370b0540bc35c3e2165dd703fbbf19a49ddbd8978717d96e09077",
        "probe_generator_sha256": "c0a6d0559bdd384a84785926fa4dbf0230814f0663b55eff978f5ee32ff6a7a6",
        "crop_materializer_sha256": "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d",
        "inference_model_helper_sha256": "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5",
        "inference_decode_metrics_sha256": "dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6",
        "current_training_model_helper_sha256": "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5",
        "current_training_hard_negative_helper_sha256": "c670f43b21c56672321e248f3969c4b8e2eaa2bfd8f97f55fcdc6f20128f510f",
        "train_manifest_sha256": "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a",
        "p2_checkpoint_path": "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/checkpoint.pt",
        "p2_checkpoint_sha256": "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351",
        "p2_training_report_path": "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/report.json",
        "p2_training_report_sha256": "cc013b5eda70af9681dbd87cc3e496a668cc29d0dcfba8d0841250782bdcb615",
        "p2_evaluation_report_sha256": "97932d6f2b12debdd009014d2cbcfd5ff63db8754ccc664079df2551d7242340",
        "p2_config_sha256": "1ae28371439c7106631337c12db8f1661f7152778532628db8b3163c6b4cd5e5",
        "p2_trainer_sha256": "dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6",
        "mixed_checkpoint_path": "artifacts/stroke-seeded-xtick-v1-proposal-curation-mix-v1-rx6600xt/checkpoint.pt",
        "mixed_checkpoint_sha256": "da1b72d949cfd8ca198ec18a9c346f069acef55e6ac64a2ef9e9cb2bcf402e63",
        "mixed_training_report_path": "artifacts/stroke-seeded-xtick-v1-proposal-curation-mix-v1-rx6600xt/report.json",
        "mixed_training_report_sha256": "4997c1c47f238f39a2fef7308d72fe70c7070e0bee8427a0608fe21c019917f9",
        "mixed_training_config_sha256": "b1bf8a4e723e75db6849992f3632b7f7235f4c4a15ae7b40440d8a5d3d143543",
        "mixed_training_runner_sha256": "91fd4b3ae7e43bfe152c4a72b6ee15a2b3352e3a27cc9fa88fd36c6a1469ed3f",
        "curation_manifest_sha256": "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672",
        "score_thresholds": [0.25, 0.5], "nms_iou_threshold": 0.5, "match_iou_threshold": 0.5,
        "maximum_detections_per_image": 100, "inference_batch_size": 32,
        "required_launcher": "tools/gpu-training/Run-GpuPython.ps1",
        "required_device_name_substring": "RX 6600 XT", "cpu_fallback_allowed": False,
        "optimizer_execution_authorized": False, "dev_input_allowed": False,
        "sealed_input_allowed": False, "private_input_allowed": False,
        "production_approval": False, "release_eligible": False,
        "planned_output_root": OUTPUT_RELATIVE,
        "acceptance_status": "consumed exploratory synthetic diagnostic only",
        "expected_evaluator_sha256": digest(Path(__file__).read_bytes()),
    }
    for key, value in expected.items():
        if config.get(key) != value:
            raise PairedEvaluationError(f"paired evaluation config mismatch: {key}")
    if digest(Path(probe_evaluate_gpu.__file__).read_bytes()) != config["probe_evaluator_sha256"]:
        raise PairedEvaluationError("pinned P2 probe evaluator source changed")
    if digest(Path(probe_evaluate_gpu.probe_materialize.__file__).read_bytes()) != config["probe_generator_sha256"]:
        raise PairedEvaluationError("pinned probe generator source changed")
    if digest(Path(helpers.__file__).read_bytes()) != config["inference_model_helper_sha256"]:
        raise PairedEvaluationError("pinned model-building helper source changed")
    if digest(Path(p2.__file__).read_bytes()) != config["inference_decode_metrics_sha256"]:
        raise PairedEvaluationError("pinned decode/metrics source changed")
    hard_negative_helper = ROOT / "ml/ocr/fragmented_xtick_detector_v4/train.py"
    if digest(hard_negative_helper.read_bytes()) != config["current_training_hard_negative_helper_sha256"]:
        raise PairedEvaluationError("current hard-negative helper source differs from recorded audit identity")
    return config, raw


def _load_checkpoint_pair(config: Mapping[str, Any], device: Any, torch: Any) -> tuple[Any, Any, dict[str, Any]]:
    p2_checkpoint_bound = helpers._bound_read(ROOT, config["p2_checkpoint_path"], "frozen P2 checkpoint", 1_000_000)
    mixed_checkpoint_bound = helpers._bound_read(ROOT, config["mixed_checkpoint_path"], "frozen mixed checkpoint", 1_000_000)
    p2_report, _, p2_report_sha = _bound_json(config["p2_training_report_path"], "frozen P2 report", 1_000_000)
    mixed_report, _, mixed_report_sha = _bound_json(config["mixed_training_report_path"], "frozen mixed report", 2_000_000)
    prior_eval_bound = helpers._bound_read(ROOT,
        "artifacts/stroke-seeded-xtick-v1-p2-source-disjoint-probe-eval-v2/report.json",
        "prior P2 source-disjoint probe report", 1_000_000)
    if prior_eval_bound.sha256 != config["p2_evaluation_report_sha256"]:
        raise PairedEvaluationError("prior P2 probe evaluation report hash changed")
    prior_eval = helpers._parse_json(prior_eval_bound, "prior P2 probe evaluation report")
    if (prior_eval.get("probe_manifest_sha256") != config["probe_manifest_sha256"]
            or prior_eval.get("checkpoint_sha256") != config["p2_checkpoint_sha256"]):
        raise PairedEvaluationError("prior P2 report does not refer to the pinned probe/checkpoint")
    p2_config_bound = helpers._bound_read(ROOT, "ml/ocr/stroke_seeded_xtick_v1/training/p2.json", "P2 config", 65_536)
    mixed_config_bound = helpers._bound_read(ROOT,
        "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/train_proposal_curation_v1/curated_mix_v1.json",
        "mixed config", 65_536)
    if (p2_checkpoint_bound.sha256 != config["p2_checkpoint_sha256"]
            or mixed_checkpoint_bound.sha256 != config["mixed_checkpoint_sha256"]
            or p2_report_sha != config["p2_training_report_sha256"]
            or mixed_report_sha != config["mixed_training_report_sha256"]
            or p2_config_bound.sha256 != config["p2_config_sha256"]
            or mixed_config_bound.sha256 != config["mixed_training_config_sha256"]):
        raise PairedEvaluationError("checkpoint or training provenance hashes differ")
    p2_state = torch.load(BytesIO(p2_checkpoint_bound.value), map_location=device, weights_only=True)
    mixed_state = torch.load(BytesIO(mixed_checkpoint_bound.value), map_location=device, weights_only=True)
    p2_expected = {
        "recipe_id": "stroke-seeded-xtick-v1-hard-negative-fit-p2",
        "model_identity": "stroke-seeded-xtick-spatial-locator-rf31-logwh-hard-negative-p2",
        "input_manifest_sha256": config["train_manifest_sha256"], "optimizer_steps": 2760,
        "model_seed": 27103151, "initialization": "fresh random initialization, no checkpoint loaded",
    }
    mixed_expected = {
        "recipe_id": "stroke-seeded-xtick-v1-proposal-curation-source-balanced-mix-v1",
        "model_identity": "stroke-seeded-xtick-spatial-locator-rf31-logwh-curated-source-balanced-mix-v1",
        "base_manifest_sha256": config["train_manifest_sha256"],
        "curation_manifest_sha256": config["curation_manifest_sha256"], "optimizer_steps": 2760,
        "model_seed": 27103151,
    }
    for key, value in p2_expected.items():
        if p2_state.get(key) != value:
            raise PairedEvaluationError(f"P2 checkpoint metadata mismatch: {key}")
    for key, value in mixed_expected.items():
        if mixed_state.get(key) != value:
            raise PairedEvaluationError(f"mixed checkpoint metadata mismatch: {key}")
    for report, expected_config_sha, expected_trainer_sha in (
        (p2_report, config["p2_config_sha256"], config["p2_trainer_sha256"]),
        (mixed_report, config["mixed_training_config_sha256"], config["mixed_training_runner_sha256"]),
    ):
        if (report.get("optimizer_steps") != 2760 or report.get("config_sha256") != expected_config_sha
                or report.get("trainer_sha256") != expected_trainer_sha
                or report.get("checkpoint_sha256") not in (p2_checkpoint_bound.sha256, mixed_checkpoint_bound.sha256)
                or report.get("runtime", {}).get("device_name") != "AMD Radeon RX 6600 XT"
                or report.get("runtime", {}).get("cpu_fallback") is not False):
            raise PairedEvaluationError("training report provenance/GPU binding differs")
    if (p2_report.get("checkpoint_sha256") != p2_checkpoint_bound.sha256
            or p2_report.get("input_manifest_sha256") != config["train_manifest_sha256"]
            or mixed_report.get("checkpoint_sha256") != mixed_checkpoint_bound.sha256
            or mixed_report.get("base_manifest_sha256") != config["train_manifest_sha256"]
            or mixed_report.get("curation_manifest_sha256") != config["curation_manifest_sha256"]):
        raise PairedEvaluationError("training report does not bind its checkpoint or training inputs")
    if mixed_report.get("dev_sealed_private_probe_inputs_read") is not False:
        raise PairedEvaluationError("mixed training report indicates prohibited input access")
    model_a = helpers.build_spatial_locator(torch).to(device)
    model_b = helpers.build_spatial_locator(torch).to(device)
    if p2_state.get("architecture") != mixed_state.get("architecture"):
        raise PairedEvaluationError("checkpoint architectures differ")
    if (p2_state.get("box_parameterization") != mixed_state.get("box_parameterization")
            or p2_state.get("receptive_field_pixels") != 31
            or mixed_state.get("receptive_field_pixels") != 31):
        raise PairedEvaluationError("checkpoint box architecture contract differs")
    model_a.load_state_dict(p2_state["state_dict"], strict=True)
    model_b.load_state_dict(mixed_state["state_dict"], strict=True)
    model_a.eval(); model_b.eval()
    if any(parameter.device != device for model in (model_a, model_b) for parameter in model.parameters()):
        raise PairedEvaluationError("a paired model left RX 6600 XT")
    refs = {
        "p2_checkpoint_sha256": p2_checkpoint_bound.sha256,
        "mixed_checkpoint_sha256": mixed_checkpoint_bound.sha256,
        "p2_training_report_sha256": p2_report_sha,
        "mixed_training_report_sha256": mixed_report_sha,
        "prior_p2_probe_report_sha256": prior_eval_bound.sha256,
        "p2_identity": p2_state["model_identity"], "mixed_identity": mixed_state["model_identity"],
    }
    return model_a, model_b, refs


def _infer_pair(models: Sequence[Any], pixels: Any, config: Mapping[str, Any], device: Any, torch: Any) -> list[list[list[Any]]]:
    predictions: list[list[list[Any]]] = [[], []]
    batch_size = int(config["inference_batch_size"])
    with torch.inference_mode():
        for start in range(0, len(pixels), batch_size):
            batch = torch.from_numpy(pixels[start:start + batch_size]).unsqueeze(1).to(device)
            if batch.device != device:
                raise PairedEvaluationError("probe tensor left RX 6600 XT")
            for i, model in enumerate(models):
                logits, boxes = model(batch)
                predictions[i].extend(p2.decode_predictions(logits, boxes))
                _RUN["inference_batches_per_model"] += 1
    torch.cuda.synchronize(device)
    return predictions


def evaluate(config: Mapping[str, Any], config_bytes: bytes, output: Path) -> dict[str, Any]:
    if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
        raise PairedEvaluationError("inference must run via the project's GPU Python launcher")
    import torch
    _RUN["phase"] = "rx6600xt-binding"
    runtime = helpers.require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    _RUN["phase"] = "checkpoint-admission"
    models = _load_checkpoint_pair(config, device, torch)
    model_p2, model_mixed, refs = models
    _RUN["phase"] = "source-disjoint-probe-admission"
    probe_config, _ = probe_evaluate_gpu.load_config()
    if digest((ROOT / "ml/ocr/stroke_seeded_xtick_v1/probe_p2.json").read_bytes()) != config["probe_config_sha256"]:
        raise PairedEvaluationError("P2 probe config hash differs")
    probe = probe_evaluate_gpu.load_probe_rows(probe_config)
    _RUN["phase"] = "paired-gpu-inference"
    predictions_p2, predictions_mixed = _infer_pair((model_p2, model_mixed), probe["pixels"], config, device, torch)
    targets = probe["boxes"]
    p2_nms = p2.image_detection_metrics(predictions_p2, targets, list(config["score_thresholds"]),
        float(config["nms_iou_threshold"]), float(config["match_iou_threshold"]),
        int(config["maximum_detections_per_image"]))
    mixed_nms = p2.image_detection_metrics(predictions_mixed, targets, list(config["score_thresholds"]),
        float(config["nms_iou_threshold"]), float(config["match_iou_threshold"]),
        int(config["maximum_detections_per_image"]))
    p2_top = top_one_metrics(predictions_p2, targets, list(config["score_thresholds"]), float(config["match_iou_threshold"]))
    mixed_top = top_one_metrics(predictions_mixed, targets, list(config["score_thresholds"]), float(config["match_iou_threshold"]))
    report = {
        "schema": "graphreader.stroke-seeded-xtick-v1-paired-probe-evaluation-report.v1",
        "status": "completed-unregistered-consumed-exploratory-synthetic-diagnostic",
        "recipe_id": config["recipe_id"], "acceptance_status": config["acceptance_status"],
        "config_sha256": digest(config_bytes), "evaluator_sha256": digest(Path(__file__).read_bytes()),
        "probe_manifest_path": config["probe_manifest_path"], "probe_manifest_sha256": probe["manifest_sha256"],
        "probe_generator_sha256": config["probe_generator_sha256"],
        "crop_materializer_sha256": config["crop_materializer_sha256"],
        "inference_helper_hashes": {
            "model_builder": config["inference_model_helper_sha256"],
            "decoder_and_nms_metrics": config["inference_decode_metrics_sha256"],
        },
        "training_dependency_provenance_limitation": {
            "message": "The completed training reports did not bind the execution-time bytes of two imported helpers; current workspace hashes below are recorded for inference but cannot prove the exact helper bytes used during training.",
            "unbound_execution_time_dependencies": [
                {"path": "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py",
                 "current_sha256": config["current_training_model_helper_sha256"]},
                {"path": "ml/ocr/fragmented_xtick_detector_v4/train.py",
                 "current_sha256": config["current_training_hard_negative_helper_sha256"]},
            ],
        },
        "source_disjointness": probe["manifest"]["train_separation"],
        "candidate_source_count": probe["manifest"]["candidate_source_count"],
        "source_group_count": len(probe["manifest"]["sources"]),
        "excluded_source_recipe_count": len(probe["manifest"]["excluded_source_recipes"]),
        "corpus_crop_counts": probe["counts"],
        "annotation_exclusion_reasons": probe["excluded_reasons"],
        "training_image_hash_collision_excluded_count": probe["manifest"]["duplicate_train_image_exclusion_count"],
        "within_probe_duplicate_image_rows_by_kind": probe["duplicate_images_by_kind"],
        "admitted_crop_count_including_exact_duplicates": len(probe["pixels"]),
        "unique_admitted_image_count": len({row["image_sha256"] for row in probe["rows"]}),
        "positive_count": sum(box is not None for box in targets),
        "background_only_count": sum(box is None for box in targets),
        "all_rows_scored_including_exact_duplicates": True,
        "runtime": runtime, "optimizer_steps": 0, "training_performed": False,
        "inference_batch_size": int(config["inference_batch_size"]),
        "inference_batches_per_model": _RUN["inference_batches_per_model"],
        "model_references": refs,
        "metric_contract": {
            "score_thresholds": config["score_thresholds"], "match_iou_threshold": config["match_iou_threshold"],
            "nms_iou_threshold": config["nms_iou_threshold"],
            "maximum_detections_per_image": config["maximum_detections_per_image"],
            "top_one": "highest-confidence valid decoded box before threshold/NMS; one per row; score gate then IoU match",
            "full_nms": "existing image_detection_metrics; all admitted rows including exact duplicates",
        },
        "p2_top_one_metrics": p2_top, "mixed_top_one_metrics": mixed_top,
        "p2_full_nms_metrics": p2_nms, "mixed_full_nms_metrics": mixed_nms,
        "prior_p2_report_sha256": config["p2_evaluation_report_sha256"],
        "comparison_limit": "Probe is already consumed by prior P2 evaluation and now by both models; exploratory synthetic evidence only, not acceptance or real-chart validation.",
        "dev_sealed_private_inputs_read": False, "production_approval": False, "release_eligible": False,
        "finished_utc": datetime.now(timezone.utc).isoformat(),
    }
    (output / "report.json").write_bytes(canonical_json(report))
    return report


def run() -> dict[str, Any]:
    _RUN.update(phase="starting", inference_batches_per_model=0)
    config, raw = load_config()
    output = ROOT / OUTPUT_RELATIVE
    if output.parent != ROOT / "artifacts" or output.exists() or output.is_symlink():
        raise PairedEvaluationError("planned output is unsafe or already exists")
    helpers._checked_chain(ROOT, output.parent, "paired evaluation output parent")
    output.mkdir(parents=False, exist_ok=False)
    (output / "started.json").write_bytes(canonical_json({
        "status": "started", "recipe_id": config["recipe_id"], "config_sha256": digest(raw),
        "evaluator_sha256": digest(Path(__file__).read_bytes()), "started_utc": datetime.now(timezone.utc).isoformat(),
        "optimizer_steps": 0, "inference_started": False,
    }))
    try:
        return evaluate(config, raw, output)
    except Exception as error:
        (output / "failure.json").write_bytes(canonical_json({
            "status": "failed", "recipe_id": config["recipe_id"], "config_sha256": digest(raw),
            "evaluator_sha256": digest(Path(__file__).read_bytes()), "failure_type": type(error).__name__,
            "failure_message": str(error), "phase": _RUN["phase"],
            "optimizer_steps": 0, "dev_sealed_private_inputs_read": False,
            "failed_utc": datetime.now(timezone.utc).isoformat(),
        }))
        raise


if __name__ == "__main__":
    print(json.dumps(run(), sort_keys=True, indent=2))
