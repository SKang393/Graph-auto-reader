# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Stream a fixed in-sample train-only audit of V39 confidence hard negatives."""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import stat
from typing import Any

import numpy as np
import torch

from ml.markers.center.confidence_spatial_v35.model import SpatialCenterNet
from ml.markers.center.scale_classifier_v16.model import ModelConfig, ScaleClassifierNet

PROJECT_ROOT = Path(__file__).resolve().parents[4]
PROTOCOL_PATH = Path(__file__).resolve().with_name("protocol.json")
PROTOCOL_RELATIVE = Path("ml/markers/center/confidence_pairwise_false_positive_diagnostic_v1/protocol.json")
OUTPUT_RELATIVE = Path("artifacts/goal22-runs/marker-center-confidence-pairwise-v39-train-hardneg-diagnostic-v1")
THRESHOLD = 0.1
SOURCE_PATHS = (
    Path("ml/markers/center/confidence_pairwise_false_positive_diagnostic_v1/__init__.py"),
    Path("ml/markers/center/confidence_pairwise_false_positive_diagnostic_v1/diagnostic.py"),
    Path("ml/markers/center/confidence_pairwise_false_positive_diagnostic_v1/protocol.json"),
    Path("ml/markers/center/confidence_pairwise_false_positive_diagnostic_v1/test_diagnostic.py"),
)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_stream(stream) -> str:
    digest = hashlib.sha256()
    stream.seek(0)
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(chunk)
    stream.seek(0)
    return digest.hexdigest()


def source_bundle_sha256(project_root: Path = PROJECT_ROOT) -> str:
    digest = hashlib.sha256()
    for relative in SOURCE_PATHS:
        path = _safe_file(project_root, relative)
        payload = path.read_bytes()
        encoded = relative.as_posix().encode("utf-8")
        digest.update(len(encoded).to_bytes(4, "big"))
        digest.update(encoded)
        digest.update(len(payload).to_bytes(8, "big"))
        digest.update(payload)
    return digest.hexdigest()


def _safe_file(project_root: Path, relative: Path) -> Path:
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("Evidence path must be relative and traversal-free")
    root = project_root.resolve(strict=True)
    path = root / relative
    current = root
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    for part in relative.parts:
        current = current / part
        try:
            metadata = current.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & reparse_flag:
            raise ValueError(f"Reparse-point evidence path is forbidden: {relative.as_posix()}")
    resolved = path.resolve(strict=True)
    try:
        resolved.relative_to(root)
    except ValueError as error:
        raise ValueError("Evidence path resolves outside the project") from error
    if not resolved.is_file():
        raise ValueError("Evidence path is not a regular file")
    return resolved


def _safe_directory(project_root: Path, relative: Path) -> Path:
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("Evidence directory must be relative and traversal-free")
    root = project_root.resolve(strict=True)
    path = root / relative
    current = root
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    for part in relative.parts:
        current = current / part
        metadata = current.lstat()
        if stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & reparse_flag:
            raise ValueError(f"Reparse-point evidence directory is forbidden: {relative.as_posix()}")
    resolved = path.resolve(strict=True)
    try:
        resolved.relative_to(root)
    except ValueError as error:
        raise ValueError("Evidence directory resolves outside the project") from error
    if not resolved.is_dir():
        raise ValueError("Evidence path is not a directory")
    return resolved


def _read_pinned_bytes(project_root: Path, relative: Path, expected_sha256: str) -> bytes:
    path = _safe_file(project_root, relative)
    with path.open("rb") as stream:
        before = sha256_stream(stream)
        if before != expected_sha256:
            raise ValueError(f"Input hash mismatch: {relative.as_posix()}")
        payload = stream.read()
        if sha256_bytes(payload) != before:
            raise ValueError(f"Input changed while reading: {relative.as_posix()}")
    if _safe_file(project_root, relative) != path:
        raise ValueError(f"Input path changed while reading: {relative.as_posix()}")
    return payload


def _read_pinned_json(project_root: Path, relative: Path, expected_sha256: str) -> dict[str, Any]:
    value = json.loads(_read_pinned_bytes(project_root, relative, expected_sha256))
    if not isinstance(value, dict):
        raise ValueError(f"Expected JSON object: {relative.as_posix()}")
    return value


def _load_pinned_npy(project_root: Path, data_root: Path, item: dict[str, Any], *, shape: tuple[int, ...], dtype) -> np.ndarray:
    relative = Path(item["path"])
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError("Feature-report array path is not relative and traversal-free")
    data_relative = data_root.relative_to(project_root.resolve(strict=True)) / relative
    path = _safe_file(project_root, data_relative)
    with path.open("rb") as stream:
        before = sha256_stream(stream)
        if before != item.get("sha256"):
            raise ValueError(f"Array hash mismatch: {data_relative.as_posix()}")
        array = np.load(stream, allow_pickle=False)
        after = sha256_stream(stream)
        if after != before:
            raise ValueError(f"Array changed while loading: {data_relative.as_posix()}")
    if _safe_file(project_root, data_relative) != path:
        raise ValueError("Array path changed while loading")
    if array.shape != shape or array.dtype != dtype or not np.isfinite(array).all():
        raise ValueError(f"Array contract mismatch: {data_relative.as_posix()}")
    return array


def validate_inputs(project_root: Path, protocol: dict[str, Any]) -> tuple[dict, dict, dict, dict]:
    train = protocol["feature_report"]
    candidate = protocol["candidate"]
    feature_report = _read_pinned_json(project_root, Path(train["path"]), train["sha256"])
    if (feature_report.get("status") != train["status"] or
            feature_report.get("feature_width") != train["feature_width"] or
            feature_report.get("training_rows") != train["all_training_rows"] or
            feature_report.get("original_training_rows") != train["identity_rows_only"] or
            any(feature_report.get(key) != 0 for key in ("optimizer_steps", "development_tensor_reads", "private_reads", "sealed_reads"))):
        raise ValueError("Train feature report scope or identity changed")
    if feature_report.get("all_original_rows_and_labels_retained") is not True:
        raise ValueError("Authenticated original train labels are not retained")

    training_report = _read_pinned_json(project_root, Path(candidate["training_report_path"]), candidate["training_report_sha256"])
    if (training_report.get("status") != "training_complete_awaiting_dev_evaluation" or
            training_report.get("revision") != candidate["revision"] or
            training_report.get("candidate_id") != candidate["candidate_id"] or
            training_report.get("checkpoint_sha256") != candidate["checkpoint_sha256"] or
            training_report.get("runner_source_bundle_sha256") != candidate["training_source_bundle_sha256"] or
            training_report.get("training_rows") != candidate["expected_training_rows"] or
            training_report.get("training", {}).get("optimizer_steps") != candidate["expected_training_steps"] or
            training_report.get("device") != candidate["expected_device"] or
            any(training_report.get(key) != 0 for key in ("development_reads", "private_reads", "sealed_reads"))):
        raise ValueError("V39 training report no longer matches the fixed train-only run")

    _read_pinned_bytes(project_root, Path(candidate["config_path"]), candidate["config_sha256"])
    v39_protocol = _read_pinned_json(project_root, Path(candidate["protocol_path"]), candidate["protocol_sha256"])
    separation = v39_protocol.get("train_dev_separation", {})
    split = protocol["train_dev_identity"]
    if (separation.get("per_scene_overlap_verified") is not False or
            separation.get("split_sha256", {}).get("component_train_split") != split["component_train_split_sha256"] or
            separation.get("split_sha256", {}).get("component_dev_split") != split["component_dev_split_sha256"] or
            separation.get("split_sha256", {}).get("family_train_split") != split["family_train_split_sha256"] or
            separation.get("split_sha256", {}).get("family_dev_split") != split["family_dev_split_sha256"]):
        raise ValueError("Train/dev split identity metadata changed or overstates scene separation")

    model_paths = []
    for item in protocol["scoring_sources"]:
        _read_pinned_bytes(project_root, Path(item["path"]), item["sha256"])
        model_paths.append(item["path"])
    if len(set(model_paths)) != len(model_paths):
        raise ValueError("Duplicate scoring source binding")

    groups = feature_report.get("groups")
    if not isinstance(groups, list) or len(groups) != 20:
        raise ValueError("Expected the fixed 20-group authenticated synthetic train report")
    expected_groups = protocol["feature_report"]["selected_groups"]
    group_by_scope = {item.get("scope"): item for item in groups}
    if len(group_by_scope) != len(groups):
        raise ValueError("Duplicate train feature group scope")
    selected = []
    for expected in expected_groups:
        group = group_by_scope.get(expected["scope"])
        if group is None or any(group.get(key) != expected[value] for key, value in (
            ("rows", "rows"), ("positive", "positive"), ("negative", "negative")
        )):
            raise ValueError(f"Selected identity group changed: {expected['scope']}")
        if group["features"].get("sha256") != expected["features_sha256"] or group["labels"].get("sha256") != expected["labels_sha256"]:
            raise ValueError(f"Selected identity feature/label pins changed: {expected['scope']}")
        selected.append(group)
    if sum(item["rows"] for item in selected) != train["identity_rows_only"]:
        raise ValueError("Selected identity train-row denominator changed")
    if sum(item["positive"] for item in selected) != train["identity_positive"] or sum(item["negative"] for item in selected) != train["identity_negative"]:
        raise ValueError("Selected identity train-label denominators changed")
    return feature_report, training_report, v39_protocol, {item["scope"]: item for item in selected}


def _load_model(project_root: Path, protocol: dict[str, Any]) -> SpatialCenterNet:
    binding = protocol["candidate"]
    checkpoint_bytes = _read_pinned_bytes(project_root, Path(binding["checkpoint_path"]), binding["checkpoint_sha256"])
    checkpoint = torch.load(io.BytesIO(checkpoint_bytes), map_location="cpu", weights_only=True)
    identity = checkpoint.get("candidate_binding", {})
    if (identity.get("task") != "marker-center" or identity.get("revision") != binding["revision"] or
            identity.get("candidate_id") != binding["candidate_id"] or
            identity.get("runner_source_bundle_sha256") != binding["training_source_bundle_sha256"]):
        raise ValueError("Candidate checkpoint binding differs from the registered training report")
    base = ScaleClassifierNet(ModelConfig())
    model = SpatialCenterNet(base)
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    model.eval()
    model.requires_grad_(False)
    if any(parameter.requires_grad for parameter in model.parameters()):
        raise RuntimeError("Diagnostic inference model unexpectedly has trainable parameters")
    return model


def _metrics(labels: np.ndarray, scores: np.ndarray) -> dict[str, Any]:
    if labels.ndim != 1 or scores.shape != labels.shape or not np.isfinite(scores).all():
        raise ValueError("Invalid score/label shape or values")
    if not np.isin(labels, (0.0, 1.0)).all():
        raise ValueError("Train labels must be binary 0/1")
    predicted = scores >= THRESHOLD
    positive = labels == 1.0
    negative = ~positive
    tp = int(np.count_nonzero(predicted & positive))
    fp = int(np.count_nonzero(predicted & negative))
    fn = int(np.count_nonzero(~predicted & positive))
    tn = int(np.count_nonzero(~predicted & negative))
    predicted_positive = tp + fp
    actual_positive = tp + fn
    return {
        "threshold": THRESHOLD,
        "true_positives": tp,
        "false_positives": fp,
        "false_negatives": fn,
        "true_negatives": tn,
        "precision": tp / predicted_positive if predicted_positive else 0.0,
        "recall": tp / actual_positive if actual_positive else 0.0,
        "positive_rows": int(np.count_nonzero(positive)),
        "negative_rows": int(np.count_nonzero(negative)),
    }


def _score_batch(model: SpatialCenterNet, features: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    if features.ndim != 2 or features.shape[1] != 768 or features.dtype != np.float32 or not np.isfinite(features).all():
        raise ValueError("Scoring batch must be finite float32 [N,768]")
    x = torch.from_numpy(np.ascontiguousarray(features))
    with torch.inference_mode():
        base_logits = model.base.head[3](model.base.head[2](model.base.head[1](x)))
        candidate_logits = model.confidence(x)
        baseline = torch.sigmoid(base_logits[:, 0]).cpu().numpy().copy()
        candidate = torch.sigmoid(candidate_logits).cpu().numpy().copy()
    if (not np.isfinite(baseline).all() or not np.isfinite(candidate).all() or
            np.any((baseline < 0) | (baseline > 1)) or np.any((candidate < 0) | (candidate > 1))):
        raise ValueError("Confidence inference produced invalid probabilities")
    return baseline, candidate


def _top_negative_rows(scope: str, labels: np.ndarray, baseline: np.ndarray, candidate: np.ndarray, limit: int) -> list[dict[str, Any]]:
    negative_indices = np.flatnonzero(labels == 0.0)
    if len(negative_indices) == 0:
        return []
    count = min(limit, len(negative_indices))
    local = np.argpartition(candidate[negative_indices], len(negative_indices) - count)[-count:]
    rows = []
    for position in negative_indices[local]:
        rows.append({
            "scope": scope,
            "row_index_within_scope": int(position),
            "label": 0,
            "candidate_v39_confidence": float(candidate[position]),
            "frozen_v27_confidence": float(baseline[position]),
            "above_fixed_threshold": bool(candidate[position] >= THRESHOLD),
            "in_sample": True,
        })
    return sorted(rows, key=lambda item: (-item["candidate_v39_confidence"], item["scope"], item["row_index_within_scope"]))[:limit]


def _score_group(model: SpatialCenterNet, project_root: Path, data_root: Path,
                 group: dict[str, Any], chunk_rows: int, top_limit: int) -> dict[str, Any]:
    count = group["rows"]
    features = _load_pinned_npy(project_root, data_root, group["features"], shape=(count, 768), dtype=np.dtype(np.float32))
    labels = _load_pinned_npy(project_root, data_root, group["labels"], shape=(count,), dtype=np.dtype(np.float32))
    if int(np.count_nonzero(labels == 1.0)) != group["positive"] or int(np.count_nonzero(labels == 0.0)) != group["negative"]:
        raise ValueError(f"Train label counts changed for {group['scope']}")
    candidate_parts: list[np.ndarray] = []
    baseline_parts: list[np.ndarray] = []
    for start in range(0, count, chunk_rows):
        stop = min(start + chunk_rows, count)
        baseline, candidate = _score_batch(model, features[start:stop])
        baseline_parts.append(baseline)
        candidate_parts.append(candidate)
    baseline_scores = np.concatenate(baseline_parts)
    candidate_scores = np.concatenate(candidate_parts)
    negative_scores = candidate_scores[labels == 0.0]
    quantiles = np.quantile(negative_scores, (0.5, 0.9, 0.99, 1.0)) if len(negative_scores) else np.zeros(4)
    return {
        "scope": group["scope"],
        "rows": count,
        "positive_rows": group["positive"],
        "negative_rows": group["negative"],
        "frozen_v27": _metrics(labels, baseline_scores),
        "candidate_v39": _metrics(labels, candidate_scores),
        "candidate_negative_confidence_quantiles": {
            "p50": float(quantiles[0]), "p90": float(quantiles[1]),
            "p99": float(quantiles[2]), "max": float(quantiles[3]),
        },
        "top_candidate_negative_rows": _top_negative_rows(group["scope"], labels, baseline_scores, candidate_scores, top_limit),
    }


def _aggregate(per_group: list[dict[str, Any]]) -> dict[str, Any]:
    result = {}
    for model_key in ("frozen_v27", "candidate_v39"):
        counts = {key: sum(item[model_key][key] for item in per_group)
                  for key in ("true_positives", "false_positives", "false_negatives", "true_negatives", "positive_rows", "negative_rows")}
        predicted_positive = counts["true_positives"] + counts["false_positives"]
        actual_positive = counts["true_positives"] + counts["false_negatives"]
        result[model_key] = {
            **counts,
            "threshold": THRESHOLD,
            "precision": counts["true_positives"] / predicted_positive if predicted_positive else 0.0,
            "recall": counts["true_positives"] / actual_positive if actual_positive else 0.0,
        }
    return result


def run(project_root: Path, protocol_sha256: str, expected_source_bundle_sha256: str) -> dict[str, Any]:
    project_root = project_root.resolve(strict=True)
    actual_bundle = source_bundle_sha256(project_root)
    if actual_bundle != expected_source_bundle_sha256:
        raise ValueError("Diagnostic source bundle differs from its external pin")
    protocol_bytes = _read_pinned_bytes(project_root, PROTOCOL_RELATIVE, protocol_sha256)
    protocol = json.loads(protocol_bytes)
    if not isinstance(protocol, dict) or protocol.get("schema") != "graphreader.marker-center-confidence-v39-train-hard-negative-diagnostic.v1":
        raise ValueError("Unsupported train hard-negative diagnostic protocol")
    if protocol.get("fixed_threshold") != THRESHOLD or protocol.get("data_policy", {}).get("threshold_search") is not False:
        raise ValueError("Diagnostic threshold policy changed")
    _, training_report, v39_protocol, selected = validate_inputs(project_root, protocol)
    model = _load_model(project_root, protocol)
    torch.set_num_threads(4)
    data_root = _safe_directory(project_root, Path("artifacts/goal22-worktrees/resume-20260908"))
    _safe_file(project_root, Path("artifacts/goal22-worktrees/resume-20260908/artifacts/goal22-runs/reflected-spatial-features-20261002-v1/report.json"))

    per_group = []
    total_top = []
    for expected in protocol["feature_report"]["selected_groups"]:
        group = selected[expected["scope"]]
        result = _score_group(model, project_root, data_root, group, protocol["chunk_rows"], protocol["top_negative_count"])
        per_group.append(result)
        total_top.extend(result["top_candidate_negative_rows"])
    top = sorted(total_top, key=lambda item: (-item["candidate_v39_confidence"], item["scope"], item["row_index_within_scope"]))[:protocol["top_negative_count"]]
    total_rows = sum(item["rows"] for item in per_group)
    report = {
        "schema": "graphreader.marker-center-confidence-v39-train-hard-negative-report.v1",
        "status": "train_only_in_sample_hard_negative_audit",
        "task": "marker-center",
        "candidate_id": "P1",
        "candidate_revision": protocol["candidate"]["revision"],
        "scope": "identity-transform rows from authenticated synthetic V37 train feature report only",
        "threshold": THRESHOLD,
        "feature_width": 768,
        "scored_rows": total_rows,
        "per_group": per_group,
        "aggregate": _aggregate(per_group),
        "top_candidate_negative_rows": top,
        "inputs": {
            "feature_report_sha256": protocol["feature_report"]["sha256"],
            "training_report_sha256": protocol["candidate"]["training_report_sha256"],
            "checkpoint_sha256": protocol["candidate"]["checkpoint_sha256"],
            "training_source_bundle_sha256": protocol["candidate"]["training_source_bundle_sha256"],
            "v39_protocol_sha256": protocol["candidate"]["protocol_sha256"],
        },
        "train_dev_identity": protocol["train_dev_identity"],
        "accounting": {
            "development_payloads_read": 0,
            "private_reads": 0,
            "sealed_reads": 0,
            "optimizer_steps": 0,
            "inference_provider": "PyTorch CPU",
            "thresholds_evaluated": [THRESHOLD],
        },
        "limitations": [
            "Scores and labels are from training rows used to fit V39, so false-positive counts are in-sample and are not independent product accuracy estimates.",
            "Rows have feature-report group and local row identities only; they have no scene IDs, so top rows cannot be tied back to source scenes by this diagnostic.",
            "The confidence-only audit does not perform geometric matching, NMS, or full production pipeline evaluation.",
            "Distinct train/dev split hashes are recorded, but per-scene train/dev overlap is not verified.",
        ],
    }
    output_relative = Path(protocol["data_policy"]["output_path"])
    if output_relative != OUTPUT_RELATIVE:
        raise ValueError("Diagnostic output path differs from the fixed protocol")
    output = project_root / output_relative
    _reject_reparse_ancestors(project_root, output)
    if output.exists():
        raise FileExistsError(f"Fresh diagnostic output already exists: {output_relative.as_posix()}")
    output.mkdir(parents=True, exist_ok=False)
    encoded = (json.dumps(report, indent=2, sort_keys=True) + "\n").encode("utf-8")
    report_path = output / "diagnostic.json"
    with report_path.open("xb") as stream:
        stream.write(encoded)
    receipt = {
        "schema": "graphreader.marker-center-confidence-v39-train-hard-negative-receipt.v1",
        "status": report["status"],
        "diagnostic_path": (output_relative / "diagnostic.json").as_posix(),
        "diagnostic_sha256": sha256_bytes(encoded),
        "protocol_sha256": protocol_sha256,
        "source_bundle_sha256": actual_bundle,
        "feature_report_sha256": protocol["feature_report"]["sha256"],
        "candidate_checkpoint_sha256": protocol["candidate"]["checkpoint_sha256"],
        "development_payloads_read": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
    }
    with (output / "receipt.json").open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(receipt, stream, indent=2, sort_keys=True)
        stream.write("\n")
    return receipt


def _reject_reparse_ancestors(project_root: Path, path: Path) -> None:
    root = project_root.resolve(strict=True)
    candidate = path if path.is_absolute() else root / path
    candidate = Path(os.path.abspath(candidate))
    try:
        relative = candidate.relative_to(root)
    except ValueError as error:
        raise ValueError("Diagnostic output escaped project") from error
    current = root
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    for part in relative.parts:
        current = current / part
        try:
            metadata = current.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & reparse_flag:
            raise ValueError("Reparse-point output ancestor is forbidden")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--protocol-sha256")
    parser.add_argument("--source-bundle-sha256")
    parser.add_argument("--print-source-bundle", action="store_true")
    args = parser.parse_args()
    if args.print_source_bundle:
        print(source_bundle_sha256())
        return 0
    if not args.protocol_sha256 or not args.source_bundle_sha256:
        parser.error("--protocol-sha256 and --source-bundle-sha256 are required for execution")
    receipt = run(PROJECT_ROOT, args.protocol_sha256, args.source_bundle_sha256)
    print(json.dumps(receipt, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
