# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Describe V39 hidden-unit contributions on authenticated train negatives."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any

import numpy as np
import torch

from ml.markers.center.confidence_pairwise_false_positive_diagnostic_v1 import diagnostic as base

PROJECT_ROOT = Path(__file__).resolve().parents[4]
PROTOCOL_RELATIVE = Path("ml/markers/center/confidence_pairwise_fp_attribution_v1/protocol.json")
SOURCE_PATHS = (
    Path("ml/markers/center/confidence_pairwise_fp_attribution_v1/__init__.py"),
    Path("ml/markers/center/confidence_pairwise_fp_attribution_v1/attribution.py"),
    Path("ml/markers/center/confidence_pairwise_fp_attribution_v1/protocol.json"),
    Path("ml/markers/center/confidence_pairwise_fp_attribution_v1/test_attribution.py"),
)


def _sha256(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def source_bundle_sha256(project_root: Path = PROJECT_ROOT) -> str:
    digest = hashlib.sha256()
    for relative in SOURCE_PATHS:
        path = base._safe_file(project_root, relative)
        payload = path.read_bytes()
        if base._safe_file(project_root, relative) != path:
            raise ValueError(f"Attribution source path changed while hashing: {relative.as_posix()}")
        encoded = relative.as_posix().encode("utf-8")
        digest.update(len(encoded).to_bytes(4, "big"))
        digest.update(encoded)
        digest.update(len(payload).to_bytes(8, "big"))
        digest.update(payload)
    return digest.hexdigest()


def _contributions(hidden: torch.Tensor, output_weight: torch.Tensor) -> torch.Tensor:
    if hidden.ndim != 2 or output_weight.ndim != 2 or output_weight.shape != (1, hidden.shape[1]):
        raise ValueError("Expected [N,H] activations and [1,H] output weights")
    contributions = hidden * output_weight[0].unsqueeze(0)
    if not bool(torch.isfinite(contributions).all()):
        raise ValueError("Non-finite hidden-unit contribution")
    return contributions


def _top_deltas(fp_sum: np.ndarray, fp_count: int, tn_sum: np.ndarray, tn_count: int, limit: int) -> list[dict[str, Any]]:
    if fp_count == 0 or tn_count == 0:
        return []
    delta = fp_sum / fp_count - tn_sum / tn_count
    indexes = np.argsort(-delta, kind="stable")[:limit]
    return [
        {
            "hidden_unit": int(index),
            "mean_logit_contribution_fp": float(fp_sum[index] / fp_count),
            "mean_logit_contribution_tn": float(tn_sum[index] / tn_count),
            "mean_contribution_delta_fp_minus_tn": float(delta[index]),
        }
        for index in indexes
    ]


def _accumulate_group(model, project_root: Path, data_root: Path, group: dict[str, Any], *, chunk_rows: int,
                      top_channels: int, threshold: float, decomposition_atol: float,
                      decomposition_rtol: float) -> dict[str, Any]:
    count = group["rows"]
    features = base._load_pinned_npy(project_root, data_root, group["features"], shape=(count, 768), dtype=np.dtype(np.float32))
    labels = base._load_pinned_npy(project_root, data_root, group["labels"], shape=(count,), dtype=np.dtype(np.float32))
    if int(np.count_nonzero(labels == 1.0)) != group["positive"] or int(np.count_nonzero(labels == 0.0)) != group["negative"]:
        raise ValueError(f"Train label counts changed for {group['scope']}")

    categories = ("candidate_false_positive", "candidate_only_false_positive", "candidate_true_negative", "candidate_false_negative_of_baseline")
    counts = {key: 0 for key in categories}
    contribution_sums = {key: np.zeros(64, dtype=np.float64) for key in categories}
    logit_sums = {key: 0.0 for key in categories}
    reconstruction_error = 0.0
    output = model.confidence.output
    base_projection = model.base.head[1]
    base_activation = model.base.head[2]
    base_output = model.base.head[3]
    candidate_projection = model.confidence.projection
    candidate_activation = model.confidence.activation
    for start in range(0, count, chunk_rows):
        stop = min(start + chunk_rows, count)
        x = torch.from_numpy(np.ascontiguousarray(features[start:stop]))
        with torch.inference_mode():
            base_hidden = base_activation(base_projection(x))
            base_logit = base_output(base_hidden)[:, 0]
            candidate_hidden = candidate_activation(candidate_projection(x))
            contributions = _contributions(candidate_hidden, output.weight)
            candidate_logit = contributions.sum(dim=1) + output.bias[0]
            direct_candidate_logit = model.confidence(x)
            reconstruction_error = max(
                reconstruction_error,
                float(torch.max(torch.abs(candidate_logit - direct_candidate_logit)).item()),
            )
            if not torch.allclose(candidate_logit, direct_candidate_logit, atol=decomposition_atol, rtol=decomposition_rtol):
                raise ValueError("Candidate contribution decomposition does not reconstruct its logit")
            baseline_probability = torch.sigmoid(base_logit).cpu().numpy()
            candidate_probability = torch.sigmoid(direct_candidate_logit).cpu().numpy()
            contribution_array = contributions.cpu().numpy().astype(np.float64, copy=False)
            candidate_logits = direct_candidate_logit.cpu().numpy()
        batch_labels = labels[start:stop]
        negative = batch_labels == 0.0
        candidate_fp = negative & (candidate_probability >= threshold)
        baseline_fp = negative & (baseline_probability >= threshold)
        masks = {
            "candidate_false_positive": candidate_fp,
            "candidate_only_false_positive": candidate_fp & ~baseline_fp,
            "candidate_true_negative": negative & ~candidate_fp,
            "candidate_false_negative_of_baseline": baseline_fp & ~candidate_fp,
        }
        for key, mask in masks.items():
            selected_count = int(np.count_nonzero(mask))
            counts[key] += selected_count
            if selected_count:
                contribution_sums[key] += contribution_array[mask].sum(axis=0, dtype=np.float64)
                logit_sums[key] += float(candidate_logits[mask].sum(dtype=np.float64))

    return {
        "scope": group["scope"],
        "rows": count,
        "negative_rows": group["negative"],
        "threshold": threshold,
        "candidate_false_positive_rows": counts["candidate_false_positive"],
        "candidate_only_false_positive_rows": counts["candidate_only_false_positive"],
        "candidate_true_negative_rows": counts["candidate_true_negative"],
        "baseline_false_positives_prevented_by_candidate": counts["candidate_false_negative_of_baseline"],
        "candidate_false_positive_fraction_of_negatives": counts["candidate_false_positive"] / group["negative"] if group["negative"] else 0.0,
        "candidate_only_false_positive_top_hidden_units": _top_deltas(
            contribution_sums["candidate_only_false_positive"], counts["candidate_only_false_positive"],
            contribution_sums["candidate_true_negative"], counts["candidate_true_negative"], top_channels,
        ),
        "all_candidate_false_positive_top_hidden_units": _top_deltas(
            contribution_sums["candidate_false_positive"], counts["candidate_false_positive"],
            contribution_sums["candidate_true_negative"], counts["candidate_true_negative"], top_channels,
        ),
        "mean_candidate_logit": {
            key: logit_sums[key] / counts[key] if counts[key] else None
            for key in categories
        },
        "max_logit_reconstruction_error": reconstruction_error,
    }


def run(project_root: Path, protocol_sha256: str, bundle_sha256: str) -> dict[str, Any]:
    project_root = project_root.resolve(strict=True)
    if source_bundle_sha256(project_root) != bundle_sha256:
        raise ValueError("Attribution source bundle differs from its external pin")
    protocol = json.loads(base._read_pinned_bytes(project_root, PROTOCOL_RELATIVE, protocol_sha256))
    if not isinstance(protocol, dict) or protocol.get("schema") != "graphreader.marker-center-v39-train-fp-attribution.v1":
        raise ValueError("Unsupported attribution protocol")
    prior = protocol["base_diagnostic"]
    if base.source_bundle_sha256(project_root) != prior["source_bundle_sha256"]:
        raise ValueError("Base diagnostic source bundle changed")
    base_protocol_bytes = base._read_pinned_bytes(project_root, Path(prior["protocol_path"]), prior["protocol_sha256"])
    base_protocol = json.loads(base_protocol_bytes)
    if base_protocol.get("schema") != "graphreader.marker-center-confidence-v39-train-hard-negative-diagnostic.v1":
        raise ValueError("Pinned base protocol schema changed")
    prior_report = base._read_pinned_json(project_root, Path(prior["report_path"]), prior["report_sha256"])
    receipt = base._read_pinned_json(project_root, Path(prior["receipt_path"]), prior["receipt_sha256"])
    if (prior_report.get("status") != "train_only_in_sample_hard_negative_audit" or
            prior_report.get("threshold") != protocol["analysis"]["fixed_threshold"] or
            receipt.get("diagnostic_sha256") != prior["report_sha256"] or
            receipt.get("optimizer_steps") != 0 or receipt.get("development_payloads_read") != 0 or
            receipt.get("private_reads") != 0 or receipt.get("sealed_reads") != 0):
        raise ValueError("Pinned base diagnostic receipt/report identity or scope changed")

    feature_report, training_report, _, selected = base.validate_inputs(project_root, base_protocol)
    model = base._load_model(project_root, base_protocol)
    torch.set_num_threads(4)
    data_root = base._safe_directory(project_root, Path("artifacts/goal22-worktrees/resume-20260908"))
    groups = []
    for scope in protocol["analysis"]["group_scopes"]:
        if scope not in selected:
            raise ValueError(f"Requested train identity scope is not authenticated: {scope}")
        groups.append(_accumulate_group(
            model, project_root, data_root, selected[scope],
            chunk_rows=protocol["analysis"]["chunk_rows"],
            top_channels=protocol["analysis"]["top_channels"],
            threshold=protocol["analysis"]["fixed_threshold"],
            decomposition_atol=protocol["analysis"]["logit_decomposition_tolerance"]["absolute"],
            decomposition_rtol=protocol["analysis"]["logit_decomposition_tolerance"]["relative"],
        ))
    if sum(item["rows"] for item in groups) != feature_report["original_training_rows"]:
        raise ValueError("Attribution row denominator differs from authenticated identity training rows")
    report = {
        "schema": "graphreader.marker-center-v39-train-fp-attribution-report.v1",
        "status": "train_only_in_sample_hidden_unit_attribution",
        "threshold": protocol["analysis"]["fixed_threshold"],
        "groups": groups,
        "accounting": {
            "scored_rows": sum(item["rows"] for item in groups),
            "development_payloads_read": 0,
            "private_reads": 0,
            "sealed_reads": 0,
            "optimizer_steps": 0,
            "inference_provider": "PyTorch CPU",
            "thresholds_evaluated": [protocol["analysis"]["fixed_threshold"]],
        },
        "inputs": {
            "base_diagnostic_report_sha256": prior["report_sha256"],
            "base_diagnostic_receipt_sha256": prior["receipt_sha256"],
            "feature_report_sha256": base_protocol["feature_report"]["sha256"],
            "candidate_checkpoint_sha256": base_protocol["candidate"]["checkpoint_sha256"],
            "candidate_training_report_sha256": base_protocol["candidate"]["training_report_sha256"],
        },
        "limitations": protocol["interpretation_limits"],
        "production_approval": False,
        "release_eligible": False,
        "training_steps": training_report["training"]["optimizer_steps"],
    }

    relative_output = Path(protocol["analysis"]["output_path"])
    if relative_output.is_absolute() or ".." in relative_output.parts:
        raise ValueError("Attribution output path must be project-relative")
    output = project_root / relative_output
    base._reject_reparse_ancestors(project_root, output)
    if output.exists():
        raise FileExistsError(f"Fresh attribution output already exists: {relative_output.as_posix()}")
    output.mkdir(parents=True, exist_ok=False)
    report_bytes = (json.dumps(report, indent=2, sort_keys=True) + "\n").encode("utf-8")
    report_relative = relative_output / "attribution.json"
    with (output / "attribution.json").open("xb") as stream:
        stream.write(report_bytes)
    receipt_value = {
        "schema": "graphreader.marker-center-v39-train-fp-attribution-receipt.v1",
        "status": report["status"],
        "report_path": report_relative.as_posix(),
        "report_sha256": _sha256(report_bytes),
        "protocol_sha256": protocol_sha256,
        "source_bundle_sha256": bundle_sha256,
        "base_diagnostic_report_sha256": prior["report_sha256"],
        "development_payloads_read": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "optimizer_steps": 0,
        "production_approval": False,
        "release_eligible": False,
    }
    receipt_bytes = (json.dumps(receipt_value, indent=2, sort_keys=True) + "\n").encode("utf-8")
    with (output / "receipt.json").open("xb") as stream:
        stream.write(receipt_bytes)
    return receipt_value


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
        parser.error("--protocol-sha256 and --source-bundle-sha256 are required")
    print(json.dumps(run(PROJECT_ROOT, args.protocol_sha256, args.source_bundle_sha256), sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
