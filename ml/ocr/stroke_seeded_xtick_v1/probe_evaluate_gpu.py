# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""GPU-only inference of the frozen P2 model on fresh generated source groups."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import sys
from typing import Any

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT))
from ml.ocr.fragmented_xtick_detector_v3 import gpu_train as helpers
from ml.ocr.stroke_seeded_xtick_v1 import probe_materialize, train_gpu


CONFIG = ROOT / "ml" / "ocr" / "stroke_seeded_xtick_v1" / "probe_p2.json"
_RUN = {"phase": "starting", "inference_batches": 0}


class ProbeEvaluationError(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def register_image_target(seen: dict[str, tuple[str, tuple[float, float, float, float] | None, str | None]],
                          image_sha256: str, kind: str,
                          box: tuple[float, float, float, float] | None,
                          label: str | None) -> bool:
    """Return whether this image hash repeats, rejecting conflicting targets."""
    value = (kind, box, label)
    prior = seen.get(image_sha256)
    if prior is not None and prior != value:
        raise ProbeEvaluationError("byte-identical probe images carry conflicting class/box labels")
    seen[image_sha256] = value
    return prior is not None


def load_config() -> tuple[dict[str, Any], bytes]:
    bound = helpers._bound_read(ROOT, "ml/ocr/stroke_seeded_xtick_v1/probe_p2.json",
                                "P2 probe evaluation config", 65_536)
    config = helpers._parse_json(bound, "P2 probe evaluation config")
    exact = {
        "schema": "graphreader.stroke-seeded-xtick-v1-p2-probe-evaluation-config.v1",
        "recipe_id": "stroke-seeded-xtick-v1-p2-fresh-source-disjoint-diagnostic-v2",
        "probe_manifest_path": "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3/manifest.json",
        "probe_manifest_sha256": "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba",
        "probe_generator_sha256": "c0a6d0559bdd384a84785926fa4dbf0230814f0663b55eff978f5ee32ff6a7a6",
        "crop_materializer_sha256": "98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d",
        "train_manifest_sha256": "12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a",
        "checkpoint_path": "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/checkpoint.pt",
        "checkpoint_sha256": "9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351",
        "training_report_path": "artifacts/stroke-seeded-xtick-v1-p2-hard-negative-fit-v1/report.json",
        "training_report_sha256": "cc013b5eda70af9681dbd87cc3e496a668cc29d0dcfba8d0841250782bdcb615",
        "training_config_sha256": "1ae28371439c7106631337c12db8f1661f7152778532628db8b3163c6b4cd5e5",
        "training_runner_sha256": "dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6",
        "model_identity": "stroke-seeded-xtick-spatial-locator-rf31-logwh-hard-negative-p2",
        "score_thresholds": [0.25, 0.5], "nms_iou_threshold": 0.5, "match_iou_threshold": 0.5,
        "maximum_detections_per_image": 100, "inference_batch_size": 32,
        "required_launcher": "tools/gpu-training/Run-GpuPython.ps1",
        "required_device_name_substring": "RX 6600 XT", "cpu_fallback_allowed": False,
        "optimizer_execution_authorized": False, "dev_input_allowed": False,
        "sealed_input_allowed": False, "private_input_allowed": False,
        "planned_output_root": "artifacts/stroke-seeded-xtick-v1-p2-source-disjoint-probe-eval-v2",
        "acceptance_status": "exploratory synthetic diagnostic only",
    }
    for key, value in exact.items():
        if config.get(key) != value:
            raise ProbeEvaluationError(f"evaluation config identity/scope mismatch: {key}")
    if config.get("expected_evaluator_sha256") != digest(Path(__file__).read_bytes()):
        raise ProbeEvaluationError("probe evaluator source hash changed")
    if digest(Path(probe_materialize.__file__).read_bytes()) != config["probe_generator_sha256"]:
        raise ProbeEvaluationError("probe generator source differs from the materialized corpus identity")
    if digest(Path(probe_materialize.crop_materializer.__file__).read_bytes()) != config["crop_materializer_sha256"]:
        raise ProbeEvaluationError("crop materializer source differs from the probe recipe")
    return config, bound.value


def load_probe_rows(config: dict[str, Any]) -> dict[str, Any]:
    manifest_bound = helpers._bound_read(ROOT, config["probe_manifest_path"], "pinned P2 probe manifest", 5_000_000)
    if manifest_bound.sha256 != config["probe_manifest_sha256"]:
        raise ProbeEvaluationError("source-disjoint diagnostic manifest hash changed")
    corpus_path = ROOT / Path(config["probe_manifest_path"]).parent
    verified = probe_materialize.verify_probe(corpus_path, manifest_bound.sha256)
    manifest = helpers._parse_json(manifest_bound, "pinned P2 probe manifest")
    if (manifest.get("generator_sha256") != config["probe_generator_sha256"]
            or manifest.get("crop_materializer_sha256") != config["crop_materializer_sha256"]
            or manifest.get("train_separation", {}).get("compared_to_manifest_sha256") != config["train_manifest_sha256"]
            or any(manifest.get("train_separation", {}).get("overlap_counts", {}).values())):
        raise ProbeEvaluationError("probe generation or split-disjointness identity differs")
    rows = manifest.get("records")
    if not isinstance(rows, list) or len(rows) != 359:
        raise ProbeEvaluationError("probe record inventory changed")
    corpus_root = manifest_bound.path.parent
    inputs: list[np.ndarray] = []
    targets: list[tuple[float, float, float, float] | None] = []
    selected_rows: list[dict[str, Any]] = []
    counts = {"positive": 0, "background_only": 0, "excluded": 0}
    excluded_reasons: dict[str, int] = {}
    duplicate_images_by_kind: dict[str, int] = {}
    seen_image_targets: dict[str, tuple[str, tuple[float, float, float, float] | None, str | None]] = {}
    group_counts: dict[str, dict[str, int]] = {}
    for row in rows:
        kind = row.get("kind")
        if kind not in counts:
            raise ProbeEvaluationError("unknown probe row class")
        counts[kind] += 1
        image_bound = helpers._bound_read(corpus_root, row["image_path"], f"probe image {row['case_id']}", 1_000_000)
        annotation_bound = helpers._bound_read(corpus_root, row["annotation_path"], f"probe annotation {row['case_id']}", 65_536)
        if image_bound.sha256 != row.get("image_sha256") or annotation_bound.sha256 != row.get("annotation_sha256"):
            raise ProbeEvaluationError("probe image or annotation bytes differ from the manifest")
        annotation = helpers._parse_json(annotation_bound, "probe annotation")
        group_counts.setdefault(str(row["source_id"]), {"positive": 0, "background_only": 0, "excluded": 0})[kind] += 1
        if kind == "excluded":
            for reason in annotation.get("exclusion_reasons", []):
                excluded_reasons[reason] = excluded_reasons.get(reason, 0) + 1
            continue
        if row.get("trainable_single_box") is not True:
            raise ProbeEvaluationError("non-excluded probe row is not single-box trainable")
        if kind == "positive":
            target = annotation.get("targets")
            if not isinstance(target, list) or len(target) != 1 or target[0].get("text") != annotation.get("expected_tick_label"):
                raise ProbeEvaluationError("positive probe row does not contain one verified tick label")
            x0, y0, x1, y1 = (float(value) for value in target[0]["target_crop_xyxy"])
            box = (x0 / 256.0, y0 / 96.0, x1 / 256.0, y1 / 96.0)
            label = str(annotation["expected_tick_label"])
        else:
            if annotation.get("targets") or annotation.get("expected_tick_label") is not None:
                raise ProbeEvaluationError("background-only probe row contains a target")
            box = None
            label = None
        is_duplicate = register_image_target(seen_image_targets, image_bound.sha256, kind, box, label)
        if is_duplicate:
            duplicate_images_by_kind[kind] = duplicate_images_by_kind.get(kind, 0) + 1
        try:
            with Image.open(BytesIO(image_bound.value)) as image_file:
                if image_file.mode != "L" or image_file.size != (256, 96):
                    raise ProbeEvaluationError("probe image shape/mode mismatch")
                pixels = np.asarray(image_file, dtype=np.float32) / 255.0
        except OSError as error:
            raise ProbeEvaluationError(f"probe crop decode failed: {row['case_id']}") from error
        inputs.append(pixels)
        targets.append(box)
        selected_rows.append({"case_id": row["case_id"], "source_id": row["source_id"], "kind": kind,
                              "image_sha256": image_bound.sha256, "expected_label": label})
    if counts != {"positive": 165, "background_only": 158, "excluded": 36}:
        raise ProbeEvaluationError(f"probe class inventory changed: {counts}")
    if len(inputs) != 323 or sum(box is not None for box in targets) != 165 or sum(box is None for box in targets) != 158:
        raise ProbeEvaluationError("admitted probe inventory changed")
    if sum(duplicate_images_by_kind.values()) != 88:
        raise ProbeEvaluationError("within-probe duplicate image count changed")
    return {"manifest": manifest, "manifest_sha256": manifest_bound.sha256,
            "pixels": np.stack(inputs).astype(np.float32), "boxes": targets,
            "rows": selected_rows, "counts": counts, "excluded_reasons": excluded_reasons,
            "duplicate_images_by_kind": duplicate_images_by_kind, "source_group_counts": group_counts,
            "verification": verified}


def evaluate(config: dict[str, Any], config_bytes: bytes, output: Path) -> dict[str, Any]:
    if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
        raise ProbeEvaluationError("frozen checkpoint inference must use the project GPU Python launcher")
    import torch

    _RUN["phase"] = "rx6600xt-binding"
    runtime = helpers.require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    _RUN["phase"] = "frozen-checkpoint-admission"
    checkpoint_bound = helpers._bound_read(ROOT, config["checkpoint_path"], "frozen P2 checkpoint", 1_000_000)
    if checkpoint_bound.sha256 != config["checkpoint_sha256"]:
        raise ProbeEvaluationError("frozen P2 checkpoint bytes changed")
    training_report_bound = helpers._bound_read(ROOT, config["training_report_path"], "frozen P2 report", 1_000_000)
    if training_report_bound.sha256 != config["training_report_sha256"]:
        raise ProbeEvaluationError("P2 training report bytes changed")
    training_report = helpers._parse_json(training_report_bound, "frozen P2 training report")
    if (training_report.get("checkpoint_sha256") != checkpoint_bound.sha256
            or training_report.get("config_sha256") != config["training_config_sha256"]
            or training_report.get("trainer_sha256") != config["training_runner_sha256"]
            or training_report.get("input_manifest_sha256") != config["train_manifest_sha256"]
            or training_report.get("runtime", {}).get("device_name") != "AMD Radeon RX 6600 XT"):
        raise ProbeEvaluationError("frozen P2 model/training provenance does not match the pinned report")
    p2_config_bound = helpers._bound_read(ROOT, "ml/ocr/stroke_seeded_xtick_v1/training/p2.json",
                                          "frozen P2 training config", 65_536)
    if p2_config_bound.sha256 != config["training_config_sha256"]:
        raise ProbeEvaluationError("frozen P2 training config changed")
    _RUN["phase"] = "source-disjoint-probe-admission"
    probe = load_probe_rows(config)
    checkpoint = torch.load(BytesIO(checkpoint_bound.value), map_location=device, weights_only=True)
    required = {
        "recipe_id": "stroke-seeded-xtick-v1-hard-negative-fit-p2",
        "model_identity": config["model_identity"],
        "input_manifest_sha256": config["train_manifest_sha256"],
        "optimizer_steps": 2760, "model_seed": 27103151,
        "initialization": "fresh random initialization, no checkpoint loaded",
    }
    for key, value in required.items():
        if checkpoint.get(key) != value:
            raise ProbeEvaluationError(f"checkpoint metadata mismatch: {key}")
    model = helpers.build_spatial_locator(torch).to(device)
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    if any(parameter.device != device for parameter in model.parameters()):
        raise ProbeEvaluationError("frozen P2 model did not remain on RX 6600 XT")
    model.eval()
    all_predictions = []
    batch_size = int(config["inference_batch_size"])
    _RUN["phase"] = "gpu-inference"
    with torch.inference_mode():
        for start in range(0, len(probe["pixels"]), batch_size):
            batch = torch.from_numpy(probe["pixels"][start:start + batch_size]).unsqueeze(1).to(device)
            if batch.device != device:
                raise ProbeEvaluationError("probe tensor left the RX 6600 XT")
            logits, boxes = model(batch)
            all_predictions.extend(train_gpu.decode_predictions(logits, boxes))
            _RUN["inference_batches"] += 1
    torch.cuda.synchronize(device)
    metrics = train_gpu.image_detection_metrics(
        all_predictions, probe["boxes"], list(config["score_thresholds"]),
        float(config["nms_iou_threshold"]), float(config["match_iou_threshold"]),
        int(config["maximum_detections_per_image"]))
    p2_train = training_report["train_fit_after_image_metrics"]
    source_groups = probe["source_group_counts"]
    report = {
        "schema": "graphreader.stroke-seeded-xtick-v1-p2-probe-evaluation-report.v1",
        "status": "completed-unregistered-exploratory-synthetic-diagnostic",
        "recipe_id": config["recipe_id"], "acceptance_status": config["acceptance_status"],
        "config_sha256": digest(config_bytes), "evaluator_sha256": digest(Path(__file__).read_bytes()),
        "probe_generator_sha256": config["probe_generator_sha256"],
        "crop_materializer_sha256": config["crop_materializer_sha256"],
        "probe_manifest_path": config["probe_manifest_path"], "probe_manifest_sha256": probe["manifest_sha256"],
        "train_manifest_sha256": config["train_manifest_sha256"],
        "source_disjointness": probe["manifest"]["train_separation"],
        "source_group_count": len(probe["manifest"]["sources"]),
        "source_group_sample_counts": {key: value for key, value in source_groups.items()},
        "probe_source_distribution": probe["manifest"]["source_distribution"],
        "candidate_source_count": probe["manifest"]["candidate_source_count"],
        "excluded_source_recipe_count": len(probe["manifest"]["excluded_source_recipes"]),
        "corpus_crop_counts": probe["counts"],
        "training_image_hash_collision_excluded_count": probe["manifest"]["duplicate_train_image_exclusion_count"],
        "within_probe_duplicate_image_rows_by_kind": probe["duplicate_images_by_kind"],
        "annotation_exclusion_reasons": probe["excluded_reasons"],
        "admitted_crop_count_including_exact_duplicates": len(probe["pixels"]),
        "unique_admitted_image_count": len(set(row["image_sha256"] for row in probe["rows"])),
        "positive_count": sum(box is not None for box in probe["boxes"]),
        "background_only_count": sum(box is None for box in probe["boxes"]),
        "frozen_model_identity": config["model_identity"],
        "checkpoint_path": config["checkpoint_path"], "checkpoint_sha256": checkpoint_bound.sha256,
        "training_report_sha256": training_report_bound.sha256,
        "runtime": runtime, "optimizer_steps": 0, "training_performed": False,
        "inference_batches": _RUN["inference_batches"],
        "image_metric_contract": {"score_thresholds": config["score_thresholds"],
            "nms_iou_threshold": config["nms_iou_threshold"], "match_iou_threshold": config["match_iou_threshold"],
            "maximum_detections_per_image": config["maximum_detections_per_image"],
            "top_box": "highest-confidence decoded box before score-threshold/NMS"},
        "probe_image_metrics": metrics,
        "p2_train_fit_image_metrics_for_context_only": p2_train,
        "comparison_limit": "All admitted crop rows are scored, including byte-identical crops; this weights repeated images. No P1 on-probe inference was run. This is a consumed exploratory synthetic diagnostic, not formal acceptance.",
        "dev_sealed_private_inputs_read": False, "production_approval": False, "release_eligible": False,
        "finished_utc": datetime.now(timezone.utc).isoformat(),
    }
    (output / "report.json").write_bytes(canonical_json(report))
    return report


def run() -> dict[str, Any]:
    _RUN.update(phase="starting", inference_batches=0)
    config, config_bytes = load_config()
    output = ROOT / str(config["planned_output_root"])
    if output.parent != ROOT / "artifacts" or output.exists() or output.is_symlink():
        raise ProbeEvaluationError("probe evaluation output must be a fresh project artifacts directory")
    helpers._checked_chain(ROOT, output.parent, "probe evaluation output parent")
    output.mkdir(parents=True, exist_ok=False)
    (output / "started.json").write_bytes(canonical_json({
        "status": "started", "recipe_id": config["recipe_id"], "config_sha256": digest(config_bytes),
        "evaluator_sha256": digest(Path(__file__).read_bytes()),
        "started_utc": datetime.now(timezone.utc).isoformat(), "training_performed": False,
    }))
    try:
        return evaluate(config, config_bytes, output)
    except Exception as error:
        (output / "failure.json").write_bytes(canonical_json({
            "schema": "graphreader.stroke-seeded-xtick-v1-p2-probe-failure.v1", "status": "failed",
            "recipe_id": config["recipe_id"], "config_sha256": digest(config_bytes),
            "evaluator_sha256": digest(Path(__file__).read_bytes()), "failure_type": type(error).__name__,
            "failure_message": str(error), "phase": _RUN["phase"], "inference_batches": _RUN["inference_batches"],
            "optimizer_steps": 0, "dev_sealed_private_inputs_read": False,
        }))
        raise


if __name__ == "__main__":
    print(json.dumps(run(), sort_keys=True, indent=2))
