# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Aggregate eligibility among exact-unmatched extras under the fixed V37 training inventory."""
from __future__ import annotations

from collections import Counter, defaultdict
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import time

ROOT = Path.cwd()
PROJECT_ROOT = ROOT.parents[2]
OUT = Path("artifacts/goal22-runs/residual-negative-v38-cache-20261029-v1")
ATTR_DIR = Path("artifacts/goal22-runs/reflection-train-extra-attribution-20261002-v3")
AUDIT_DIR = Path("artifacts/goal22-runs/reflection-dense-train-audit-20261002-v1")
FEATURE_REPORT_PATH = Path("artifacts/goal22-runs/reflected-spatial-features-20261002-v1/report.json")
V37_DIR = Path("ml/markers/center/confidence_reflection_v37")
CONFIG_PATH = V37_DIR / "p1.json"
PROTOCOL_PATH = V37_DIR / "protocol.json"
V3_REPORT = Path("artifacts/goal22-runs/reflection-train-patch-exact-match-20261029-v3/report.json")
V2_REPORT = Path("artifacts/goal22-runs/reflection-train-full-v37-patch-match-20261029-v2/report.json")
ELIGIBILITY_REPORT = Path("artifacts/goal22-runs/reflection-train-negative-eligibility-20261029-v1/report.json")
ELIGIBILITY_SCRIPT = Path("artifacts/goal22-tools/Diagnose-ReflectionTrainNegativeEligibility20261029.py")
ELIGIBILITY_LAUNCHER = Path("artifacts/goal22-tools/Run-ReflectionTrainNegativeEligibility20261029.ps1")
PROTOCOL_PATH_V38 = Path("ml/markers/center/residual_negative_v38/protocol.json")
V38_ID = "marker-center-residual-negative-v38-train"
V38_SCHEMA_VERSION = 1
V37_RUN_REPORT = Path("artifacts/goal22-runs/center-confidence-reflection-v37/P1/report.json")
NEGATIVE_MINER_SOURCE = Path("ml/markers/center/train_negative_coverage.py")
BATCH_ROWS = 256
MAX_RUNTIME_SECONDS = 900
EXPECTED_V3_REPORT_SHA256 = "4d822614d8370ffd175591807dcf7787f99cecc0e2a35a899eb4d6ded4e34d2f"
EXPECTED_V2_REPORT_SHA256 = "1fef4eaed51bdf9835cfb9c0884493529a5fa919ec53bcbfd5858229e1f9ac6f"
EXPECTED_ELIGIBILITY_REPORT_SHA256 = "37353ebf64d12e25f183cdf78306ceda1298452a96b5bc8b9354d3db6b252d44"
EXPECTED_V37_RUN_REPORT_SHA256 = "cedef52cbae22a095e81db2797bf7d378ea702b9c6185c1088441e7417a0730d"
EXPECTED_V37_CHECKPOINT_SHA256 = "2daeedb92fdfb6c425cca6035040ca0e857c6d6bb1ba548281729fee71c24cd4"
EXPECTED_NEGATIVE_MINER_SHA256 = "c30e14062bb2eb5bb1c136181504eaf39008f6b8d0f30093621a57c7a5b542e3"
STARTED = time.perf_counter()

sys.path.insert(0, str(ROOT))

import numpy as np
import torch

from ml.markers.gate_seal import canonical_json_bytes, source_bundle_sha256
from ml.markers.center import shape_coverage_cache as scene_cache
from ml.markers.center import shape_coverage_evaluation as evaluation
from ml.markers.center import shape_coverage_cache as parent_cache
from ml.markers.center.component_diversity_v27 import train_p1 as v27
from ml.markers.center.confidence_reflection_v37 import runner as v37
from ml.markers.center.confidence_reflection_v37.augmentation import TRANSFORMS, reflect_patches
from ml.markers.center.mask_preserving_v24 import mask_preserving as v24
from ml.markers.classifier.native_context_v3.runner import WorkBudget

EXPECTED_BASE_GROUPS = {"component", "family"}
EXPECTED_ADDITIONAL_GROUPS = {"coverage", "crowded", "crowded_mined_negative"}
EXPECTED_TRAIN_ROWS = {
    "component": 35838,
    "family": 9053,
    "coverage": 42747,
    "crowded": 182614,
    "crowded_mined_negative": 6366,
}
EXPECTED_COUNTS = {
    "component": (3258, 32580),
    "family": (823, 8230),
    "coverage": (3863, 38884),
    "crowded": (16732, 165882),
    "crowded_mined_negative": (0, 6366),
}


def apply_safety_rules(anchor_distance: np.ndarray, decoded_distance: np.ndarray) -> np.ndarray:
    """Apply strict saved NumPy float32 miner rules, with no score cutoff."""
    anchor = np.asarray(anchor_distance)
    decoded = np.asarray(decoded_distance)
    if anchor.dtype != np.float32 or decoded.dtype != np.float32 or anchor.shape != decoded.shape:
        raise ValueError("Safety distances must be same-shaped NumPy float32 arrays")
    return (anchor > 8.) & (decoded > 5.)


def exact_deduplicate_patches(entries, *, digest_fn=None) -> list[int]:
    """Keep first exact patch identity; digest hits alone never count as equality."""
    if digest_fn is None:
        digest_fn = lambda raw: hashlib.sha256(raw).digest()
    buckets: dict[bytes, list[tuple[int, bytes, torch.Tensor]]] = defaultdict(list)
    unique: list[int] = []
    for key, patch in entries:
        raw = patch_bytes(patch)
        digest = digest_fn(raw)
        bucket = buckets[digest]
        if any(raw == prior_raw and torch.equal(patch, prior_patch)
               for _, prior_raw, prior_patch in bucket):
            continue
        bucket.append((int(key), raw, patch))
        unique.append(int(key))
    return unique


def build_cache_payload(patches: list[torch.Tensor]) -> dict[str, torch.Tensor]:
    if not patches:
        raise ValueError("Residual negative cache cannot be empty")
    if any(patch.shape != (3, 33, 33) or patch.dtype != torch.float32 for patch in patches):
        raise ValueError("Residual patches must be float32 [3,33,33]")
    values = torch.stack([patch.contiguous() for patch in patches]).contiguous()
    count = len(values)
    return {"patches": values, "labels": torch.zeros(count, dtype=torch.float32),
            "offsets": torch.zeros((count, 2), dtype=torch.float32),
            "radii": torch.zeros(count, dtype=torch.float32),
            "hard_negative": torch.ones(count, dtype=torch.float32)}


def validate_protocol(protocol: dict) -> None:
    expected = {
        "schema_version": V38_SCHEMA_VERSION,
        "cache_id": V38_ID,
        "split": "train",
        "scope": "residual_negative",
        "source_report_sha256": EXPECTED_ELIGIBILITY_REPORT_SHA256,
        "full_v37_match_report_sha256": EXPECTED_V2_REPORT_SHA256,
        "expected_rows": 90,
        "source_scope_counts": {"component": 50, "family": 40},
        "operating_point": 0.1,
        "miner_confidence_cutoff_applied": False,
        "eligibility_rules": {
            "proposal_anchor_distance_pixels": "> 8.0",
            "decoded_center_nearest_truth_distance_pixels": "> 5.0",
            "distance_dtype": "NumPy float32",
            "decoded_center": "coordinates + outputs[:, 1:3] * 4.0",
        },
        "patch_contract": {
            "dtype": "float32",
            "shape": [3, 33, 33],
            "zero_padding_pixels": 16,
            "extraction": "V24 full-frame component and V27 plot-domain family proposal extraction",
        },
        "payload": {
            "filename": "train-residual-negative.pt",
            "keys": ["patches", "labels", "offsets", "radii", "hard_negative"],
            "dtypes": {name: "float32" for name in
                       ("patches", "labels", "offsets", "radii", "hard_negative")},
            "values": {"labels": 0, "offsets": 0, "radii": 0, "hard_negative": 1},
        },
        "ordering": ["component before family", "ascending scene index within scope",
                     "ascending proposal index within scene",
                     "exact patch byte duplicates removed globally, first retained"],
        "deduplication": "SHA-256 index with exact contiguous bytes and torch.equal confirmation",
    }
    if protocol != expected:
        raise ValueError("V38 cache protocol drifted from the bound contract")


def validate_eligibility_report(report: dict, actual_sha256: str) -> None:
    counts = report.get("aggregate_counts", {})
    binding = report.get("bindings", {})
    per_scope = counts.get("eligibility_by_scope_and_rule", {})
    if (actual_sha256 != EXPECTED_ELIGIBILITY_REPORT_SHA256
            or report.get("status") != "complete_train_only_negative_eligibility_diagnostic"
            or counts.get("all_v37_exact_match_population", {}).get("no_exact_match_to_any_of_five_groups") != 96
            or counts.get("eligible_original_patch_rows") != 90
            or counts.get("eligible_distinct_original_patches_after_exact_self_dedup") != 90
            or counts.get("eligible_distinct_new_original_patches_after_v37_exclusion") != 90
            or per_scope.get("component", {}).get("both_rules_pass") != 50
            or per_scope.get("family", {}).get("both_rules_pass") != 40
            or report.get("eligibility_contract", {}).get("operating_point") != 0.1
            or report.get("eligibility_contract", {}).get("miner_confidence_cutoff_applied") is not False
            or binding.get("completed_full_v37_match_report_sha256") != EXPECTED_V2_REPORT_SHA256
            or binding.get("completed_v3_report_sha256") != EXPECTED_V3_REPORT_SHA256
            or binding.get("fixed_v37_run_report_sha256") != EXPECTED_V37_RUN_REPORT_SHA256
            or binding.get("fixed_v37_threshold") != 0.1
            or binding.get("fixed_v37_checkpoint_sha256") != EXPECTED_V37_CHECKPOINT_SHA256
            or binding.get("negative_miner_source_sha256") != EXPECTED_NEGATIVE_MINER_SHA256
            or counts.get("eligible_distinct_original_patches_within_scope") != {"component": 50, "family": 40}):
        raise ValueError("Authenticated residual-negative eligibility report drifted")
    execution = report.get("execution", {})
    if any(execution.get(key) != 0 for key in
           ("private_reads", "sealed_reads", "model_inference_runs", "optimizer_steps")):
        raise ValueError("Eligibility report contains prohibited reads or model execution")


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def stream_hash(path: Path) -> tuple[str, int]:
    digest = hashlib.sha256()
    total = 0
    with path.open("rb") as stream:
        while True:
            block = stream.read(8 * 1024 * 1024)
            if not block:
                break
            digest.update(block)
            total += len(block)
    return digest.hexdigest(), total


def protocol_hash_bindings(v37_protocol_sha256: str, v38_protocol_sha256: str) -> dict[str, str]:
    """Keep the legacy V37 key and bind the cache protocol under its exact path."""
    return {
        "protocol.json": v37_protocol_sha256,
        PROTOCOL_PATH_V38.as_posix(): v38_protocol_sha256,
    }


def verify_v38_protocol_unchanged(repo_root: Path, expected_sha256: str) -> None:
    actual_sha256, _ = stream_hash(repo_root / PROTOCOL_PATH_V38)
    if actual_sha256 != expected_sha256:
        raise ValueError("V38 cache protocol changed before evidence write")


def resolve_recorded_path(raw: str) -> Path:
    recorded = Path(raw)
    if not recorded.is_absolute():
        return ROOT / recorded
    historical_root = Path(r"C:\Users\Sungwoo\Desktop\Graph-auto-reader")
    try:
        return PROJECT_ROOT / recorded.relative_to(historical_root)
    except ValueError:
        pass
    try:
        return ROOT / recorded.relative_to(ROOT)
    except ValueError:
        pass
    try:
        return PROJECT_ROOT / recorded.relative_to(PROJECT_ROOT)
    except ValueError as exc:
        raise ValueError(f"Unrecognized absolute manifest path: {raw}") from exc


def verify_refs(refs, budget: WorkBudget, *, description: str) -> dict:
    """Hash unique file references with bounded memory; reject conflicting refs."""
    expected_by_path: dict[str, str] = getattr(verify_refs, "_expected", {})
    checked, bytes_read = 0, 0
    for item in refs:
        raw, expected = item["path"], item["sha256"]
        path = resolve_recorded_path(raw)
        key = str(path).casefold()
        previous = expected_by_path.setdefault(key, expected)
        if previous != expected:
            raise ValueError(f"Conflicting recorded hashes for {raw}")
        if key not in expected_by_path or key in getattr(verify_refs, "_done", set()):
            continue
        if not path.is_file():
            raise FileNotFoundError(f"Required {description} input is missing: {raw}")
        before = path.stat()
        with budget.work_block():
            actual, size = stream_hash(path)
        after = path.stat()
        if actual != expected:
            raise ValueError(f"{description} hash mismatch: {raw}")
        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise RuntimeError(f"{description} input changed while being read: {raw}")
        checked += 1
        bytes_read += size
        getattr(verify_refs, "_done", set()).add(key)
        getattr(verify_refs, "_stats", {})[key] = (str(path), after.st_size, after.st_mtime_ns)
        if time.perf_counter() - STARTED > MAX_RUNTIME_SECONDS:
            raise TimeoutError(f"Diagnostic exceeded {MAX_RUNTIME_SECONDS}s while verifying {description} inputs")
    verify_refs._expected = expected_by_path
    return {"unique_files_checked": checked, "bytes_hashed": bytes_read}


def clear_verified_paths() -> None:
    verify_refs._done = set()
    verify_refs._expected = {}
    verify_refs._stats = {}


def verify_input_stats_stable(stats: dict | None = None) -> int:
    if stats is None:
        stats = getattr(verify_refs, "_stats", {})
    for path_text, size, mtime_ns in stats.values():
        current = Path(path_text).stat()
        if (current.st_size, current.st_mtime_ns) != (size, mtime_ns):
            raise RuntimeError(f"Input changed during diagnostic: {path_text}")
    return len(stats)


def patch_bytes(patch: torch.Tensor) -> bytes:
    if patch.shape != (3, 33, 33) or patch.dtype != torch.float32:
        raise ValueError("Training/extra patch must be float32 [3,33,33]")
    array = patch.contiguous().numpy().view(np.uint8)
    return array.tobytes()


def patch_digest(patch: torch.Tensor) -> bytes:
    return hashlib.sha256(memoryview(patch.contiguous().numpy().view(np.uint8))).digest()


def apply_transform(patches: torch.Tensor, transform: str) -> torch.Tensor:
    dims = {"identity": (), "horizontal": (-1,), "vertical": (-2,), "both": (-2, -1)}[transform]
    return torch.flip(patches, dims) if dims else patches


def extract_bound_proposals(bound):
    scene = bound.scene if hasattr(bound, "scene") else bound
    domain = bound.panel_domain.domain if hasattr(bound, "panel_domain") else None
    if domain is not None:
        return v27.v26.extract_proposals_in_domain(scene.tensor, domain).proposals
    return v24.extract_proposals(scene.tensor)


def validate_cached_group(scope: str, group: dict, expected_rows: int,
                          expected_counts: tuple[int, int], budget: WorkBudget) -> dict:
    tensor_path = ROOT / group["tensors"]["path"]
    label_path = ROOT / group["labels"]["path"]
    hard_path = ROOT / group["hard_negative"]["path"]
    # mmap=True keeps the large patch storage file-backed; labels remain chunked.
    payload = torch.load(tensor_path, map_location="cpu", weights_only=True, mmap=True)
    if not isinstance(payload, dict) or set(payload) != {"patches", "labels", "offsets", "radii", "hard_negative"}:
        raise ValueError(f"Unexpected cached tensor fields for {scope}")
    patches, labels, hard = payload["patches"], payload["labels"], payload["hard_negative"]
    offsets, radii = payload["offsets"], payload["radii"]
    if (not isinstance(patches, torch.Tensor) or patches.shape != (expected_rows, 3, 33, 33)
            or patches.dtype != torch.float32 or labels.shape != (expected_rows,)
            or hard.shape != (expected_rows,) or offsets.shape != (expected_rows, 2)
            or radii.shape != (expected_rows,)
            or any(t.dtype != torch.float32 for t in (labels, offsets, radii))
            or hard.dtype not in (torch.float32, torch.bool)):
        raise ValueError(f"Patch/label tensor shape mismatch for {scope}")
    label_rows = np.load(label_path, mmap_mode="r", allow_pickle=False)
    hard_rows = np.load(hard_path, mmap_mode="r", allow_pickle=False)
    if (label_rows.shape != (expected_rows,) or hard_rows.shape != (expected_rows,)
            or label_rows.dtype != np.float32 or hard_rows.dtype not in (np.float32, np.bool_)):
        raise ValueError(f"Recorded label/hard-negative array shape or dtype mismatch for {scope}")
    positives = negatives = hard_negatives = 0
    for start in range(0, expected_rows, BATCH_ROWS):
        end = min(start + BATCH_ROWS, expected_rows)
        with budget.work_block():
            patch_chunk = patches[start:end]
            label_chunk = labels[start:end]
            hard_chunk = hard[start:end]
            offset_chunk = offsets[start:end]
            radius_chunk = radii[start:end]
            label_np = np.asarray(label_rows[start:end])
            hard_np = np.asarray(hard_rows[start:end])
            if (not torch.isfinite(patch_chunk).all() or patch_chunk.min() < 0 or patch_chunk.max() > 1
                    or not torch.isfinite(label_chunk).all() or not torch.isfinite(hard_chunk).all()
                    or not torch.isfinite(offset_chunk).all() or not torch.isfinite(radius_chunk).all()
                    or not np.isfinite(label_np).all() or not np.isfinite(hard_np).all()
                    or not np.isin(label_np, (0, 1)).all() or not np.isin(hard_np, (0, 1)).all()):
                raise ValueError(f"Non-finite or out-of-range training tensor data for {scope}")
            if (not torch.equal(label_chunk, torch.from_numpy(label_np.copy()))
                    or not torch.equal(hard_chunk.bool(), torch.from_numpy(hard_np.astype(np.bool_, copy=True)))):
                raise ValueError(f"Embedded and separately bound labels differ for {scope}")
            if (not torch.all((label_chunk == 0) | (label_chunk == 1))
                    or not torch.all((hard_chunk == 0) | (hard_chunk == 1))):
                raise ValueError(f"Labels or hard-negative flags are not binary for {scope}")
            positives += int((label_chunk == 1).sum())
            negatives += int((label_chunk == 0).sum())
            hard_negatives += int(((label_chunk == 0) & (hard_chunk == 1)).sum())
        if time.perf_counter() - STARTED > MAX_RUNTIME_SECONDS:
            raise TimeoutError(f"Diagnostic exceeded {MAX_RUNTIME_SECONDS}s while validating {scope}")
    if (positives, negatives) != expected_counts or group["positive"] != positives or group["negative"] != negatives:
        raise ValueError(f"Recorded positive/negative denominator mismatch for {scope}")
    return {
        "payload": payload,
        "labels_mmap": label_rows,
        "hard_mmap": hard_rows,
        "rows": expected_rows,
        "positive": positives,
        "negative": negatives,
        "hard_negative": hard_negatives,
    }


def prepare_cache(repo: Path, output: Path) -> dict:
    global ROOT, PROJECT_ROOT, OUT, STARTED
    ROOT = repo.resolve()
    PROJECT_ROOT = ROOT.parents[2]
    OUT = output if output.is_absolute() else ROOT / output
    OUT = OUT.resolve()
    run_root = (ROOT / "artifacts/goal22-runs").resolve()
    if not OUT.is_relative_to(run_root):
        raise ValueError("Cache output must be under artifacts/goal22-runs")
    if OUT.exists():
        raise FileExistsError(f"Refusing to overwrite cache output: {OUT}")
    if (os.environ.get("GOAL22_CPU_CEILING_PERCENT") != "80"
            or os.environ.get("GOAL22_TRAINING_DUTY_PERCENT") != "80"
            or os.environ.get("OMP_WAIT_POLICY") != "PASSIVE"):
        raise RuntimeError("The verified project CPU guard is required")
    torch.set_num_threads(int(os.environ["OMP_NUM_THREADS"]))
    torch.set_num_interop_threads(1)
    OUT.mkdir(parents=True, exist_ok=False)
    STARTED = time.perf_counter()
    budget = WorkBudget(OUT / "CANCEL")

    def guarded_hash(path: Path) -> tuple[str, int]:
        with budget.work_block():
            return stream_hash(path)

    with budget.work_block():
        protocol_bytes = (ROOT / PROTOCOL_PATH_V38).read_bytes()
    protocol_v38_hash = hashlib.sha256(protocol_bytes).hexdigest()
    protocol = json.loads(protocol_bytes.decode("utf-8-sig"))
    validate_protocol(protocol)
    eligibility_hash, eligibility_size = guarded_hash(ROOT / ELIGIBILITY_REPORT)
    eligibility = load_json(ROOT / ELIGIBILITY_REPORT)
    validate_eligibility_report(eligibility, eligibility_hash)
    eligibility_script_hash, _ = guarded_hash(ROOT / ELIGIBILITY_SCRIPT)
    eligibility_launcher_hash, _ = guarded_hash(ROOT / ELIGIBILITY_LAUNCHER)
    if (eligibility.get("output_sha256", {}).get("diagnostic_script") != eligibility_script_hash
            or eligibility.get("output_sha256", {}).get("launcher_script") != eligibility_launcher_hash):
        raise ValueError("Eligibility report source/launcher binding changed")

    prior_full_hash, _ = guarded_hash(ROOT / V2_REPORT)
    if prior_full_hash != EXPECTED_V2_REPORT_SHA256:
        raise ValueError("Completed all-five-group report hash changed")
    prior_full = load_json(ROOT / V2_REPORT)
    if (prior_full.get("status") != "complete_train_only_full_v37_exact_byte_patch_match"
            or prior_full.get("aggregate_counts", {}).get("unmatched_extras_without_any_additional_v37_group_match") != 96
            or prior_full.get("aggregate_counts", {}).get("unmatched_extras_matching_any_additional_v37_group") != 97):
        raise ValueError("Completed all-five-group exact-match denominator changed")
    prior_script_hash, _ = guarded_hash(ROOT / "artifacts/goal22-tools/Diagnose-ReflectionTrainFullV37ExtraPatchMatches20261029.py")
    if prior_full.get("output_sha256", {}).get("diagnostic_script") != prior_script_hash:
        raise ValueError("Completed all-five-group helper source hash changed")

    # Authenticate the prior 193-patch denominator and every source/cache used
    # to produce it. The previous report itself is pinned by its recorded hash.
    v3_hash, v3_size = guarded_hash(ROOT / V3_REPORT)
    if v3_hash != EXPECTED_V3_REPORT_SHA256:
        raise ValueError("Completed v3 report hash changed")
    v3 = load_json(ROOT / V3_REPORT)
    if v3.get("status") != "complete_train_only_exact_byte_patch_match":
        raise ValueError("Completed v3 report status changed")
    attribution = load_json(ROOT / ATTR_DIR / "report.json")
    audit = load_json(ROOT / AUDIT_DIR / "report.json")
    direct_prior_hashes = {
        "attribution_report": guarded_hash(ROOT / ATTR_DIR / "report.json")[0],
        "dense_audit_report": guarded_hash(ROOT / AUDIT_DIR / "report.json")[0],
        "decoded_prediction_cache": guarded_hash(ROOT / ATTR_DIR / "predictions.json")[0],
        "saved_train_scene_cache": guarded_hash(ROOT / ATTR_DIR / "train-scenes.pt")[0],
    }
    expected_prior_hashes = {
        "attribution_report": v3["input_bindings"]["attribution_report_sha256"],
        "dense_audit_report": v3["input_bindings"]["dense_audit_report_sha256"],
        "decoded_prediction_cache": v3["input_bindings"]["decoded_prediction_cache_sha256"],
        "saved_train_scene_cache": v3["input_bindings"]["saved_train_scene_cache_sha256"],
    }
    if direct_prior_hashes != expected_prior_hashes:
        raise ValueError("Prior reports, predictions, or scene-cache binding differs from v3")
    v37_run_hash, _ = guarded_hash(ROOT / V37_RUN_REPORT)
    v37_run = load_json(ROOT / V37_RUN_REPORT)
    model_path = ROOT / audit["model_checkpoint"]["path"]
    model_hash, model_size = guarded_hash(model_path)
    miner_hash, miner_size = guarded_hash(ROOT / NEGATIVE_MINER_SOURCE)
    miner_source = (ROOT / NEGATIVE_MINER_SOURCE).read_text(encoding="utf-8")
    if (v37_run_hash != EXPECTED_V37_RUN_REPORT_SHA256
            or v37_run.get("revision") != "marker-center-confidence-reflection-v37"
            or v37_run.get("candidate_id") != "P1"
            or v37_run.get("status") != "failed_dev_unconsumed"
            or v37_run.get("training", {}).get("training_source_binding", {}).get("recipe", {}).get("confidence_threshold") != 0.1
            or v37_run.get("checkpoint_sha256") != EXPECTED_V37_CHECKPOINT_SHA256
            or audit.get("model_checkpoint", {}).get("sha256") != EXPECTED_V37_CHECKPOINT_SHA256
            or model_hash != EXPECTED_V37_CHECKPOINT_SHA256
            or v37_run.get("private_reads") != 0 or v37_run.get("sealed_reads") != 0
            or miner_hash != EXPECTED_NEGATIVE_MINER_SHA256):
        raise ValueError("Fixed V37 model, .10 threshold, or miner source binding changed")
    if ("np.linalg.norm(coordinates[:, None, :]-centers[None, :, :], axis=2).min(axis=1)" not in miner_source
            or "decoded = coordinates + outputs[:, 1:3]*4." not in miner_source
            or "np.linalg.norm(decoded[:, None, :]-centers[None, :, :], axis=2).min(axis=1)" not in miner_source
            or "(anchor_distance > 8.) & (decoded_distance > 5.)" not in miner_source):
        raise ValueError("Miner NumPy distance rule expressions changed")
    if (attribution.get("private_reads") != 0 or attribution.get("sealed_reads") != 0
            or attribution.get("model_inference_runs") != 0 or attribution.get("optimizer_steps") != 0
            or audit.get("private_reads") != 0 or audit.get("sealed_reads") != 0
            or audit.get("optimizer_steps") != 0):
        raise ValueError("Prior train-only read/training scope changed")
    if (attribution.get("status") != "complete_saved_train_extra_attribution"
            or audit.get("status") != "complete_closed_head_dense_training_audit"
            or v3.get("aggregate_counts", {}).get("decoded_extras_matching_any_selected_original_or_reflected_patch") != 59
            or v3.get("aggregate_counts", {}).get("decoded_extras_without_exact_selected_patch_match") != 193):
        raise ValueError("Prior exact-match population changed")

    clear_verified_paths()
    prior_evidence = verify_refs(attribution["evidence"], budget, description="prior attribution")
    prior_audit_evidence = verify_refs(audit["evidence"], budget, description="dense train audit")
    prior_attribution_sources = verify_refs(load_json(ROOT / ATTR_DIR / "sources.json"), budget,
                                             description="attribution source")
    prior_audit_sources = verify_refs(load_json(ROOT / AUDIT_DIR / "sources.json"), budget,
                                      description="dense audit source")
    prior_stable_inputs = verify_input_stats_stable()
    prior_stat_snapshot = dict(verify_refs._stats)

    # Bind exact V37 source/protocol/config and the feature report declaring all
    # identity and reflected rows. Do not infer training tensor paths.
    config = load_json(ROOT / CONFIG_PATH)
    v37.validate_config(config)
    config_hash, _ = guarded_hash(ROOT / CONFIG_PATH)
    protocol_v37_hash, _ = guarded_hash(ROOT / PROTOCOL_PATH)
    feature_hash, feature_size = guarded_hash(ROOT / FEATURE_REPORT_PATH)
    if protocol_v37_hash != config["protocol_sha256"] or feature_hash != config["feature_report_sha256"]:
        raise ValueError("V37 protocol or feature-report hash differs from p1.json")
    with budget.work_block():
        source_bundle = source_bundle_sha256(ROOT, v37.RUNNER_SOURCES)
    if source_bundle != config["expected_runner_source_bundle_sha256"]:
        raise ValueError("V37 runner source bundle differs from the bound p1.json")
    feature_report = load_json(ROOT / FEATURE_REPORT_PATH)
    if (feature_report.get("status") != "fixed_reflection_training_features_measured"
            or feature_report.get("transforms") != list(TRANSFORMS)
            or feature_report.get("original_training_rows") != sum(EXPECTED_TRAIN_ROWS.values())
            or feature_report.get("training_rows") != 4 * sum(EXPECTED_TRAIN_ROWS.values())
            or feature_report.get("all_original_rows_and_labels_retained") is not True
            or feature_report.get("model_parameters_and_buffers_unchanged") is not True
            or any(feature_report.get(k) != 0 for k in ("optimizer_steps", "private_reads", "sealed_reads", "development_tensor_reads"))):
        raise ValueError("V37 feature report population or read-scope contract changed")
    if feature_report.get("model", {}).get("sha256") != v37.MODEL_SHA256:
        raise ValueError("V37 source model binding changed")

    configured_rows = config.get("training_group_rows")
    report_rows = {group["scope"]: group["rows"] for group in feature_report["groups"]}
    if configured_rows != v37.TRAINING_ROWS or report_rows != v37.TRAINING_ROWS:
        raise ValueError("V37 configured and reported 20-group inventories differ")
    if set(report_rows) != {f"{scope}:{transform}" for scope in EXPECTED_TRAIN_ROWS for transform in TRANSFORMS}:
        raise ValueError("V37 training group/view identities are incomplete or unexpected")
    groups = {group["scope"]: group for group in feature_report["groups"]}
    if len(feature_report["groups"]) != 20:
        raise ValueError("V37 report must contain exactly twenty scope/view groups")

    # Hash every V37 group artifact used or referenced by the training report,
    # plus source/model/parent bindings. Hashing is streaming and bounded.
    clear_verified_paths()
    refs = [*feature_report["evidence"], feature_report["model"], feature_report["parent_feature_report"]]
    for group in feature_report["groups"]:
        refs.extend(group[name] for name in ("features", "raw", "tensors", "labels", "hard_negative"))
    v37_input_hashes = verify_refs(refs, budget, description="V37 feature/training input")

    # Require each transform group to retain the same source tensor, labels and
    # hard-negative flags. Derive the five exact tensor files from identity rows.
    identity_groups = {}
    tensor_inventory = []
    for scope in EXPECTED_TRAIN_ROWS:
        identity = groups[f"{scope}:identity"]
        if (identity.get("rows") != EXPECTED_TRAIN_ROWS[scope]
                or (identity.get("positive"), identity.get("negative")) != EXPECTED_COUNTS[scope]
                or identity.get("transform") != "identity"):
            raise ValueError(f"Identity training group row/label counts changed: {scope}")
        identity_groups[scope] = identity
        for transform in TRANSFORMS:
            group = groups[f"{scope}:{transform}"]
            if (group.get("rows") != identity["rows"]
                    or group.get("transform") != transform
                    or group.get("base_scope", scope) != scope
                    or group.get("positive") != identity["positive"]
                    or group.get("negative") != identity["negative"]
                    or group.get("tensors") != identity["tensors"]
                    or group.get("labels") != identity["labels"]
                    or group.get("hard_negative") != identity["hard_negative"]):
                raise ValueError(f"Reflected group changed tensor/label binding: {scope}:{transform}")
        tensor_inventory.append({
            "scope": scope,
            "rows": identity["rows"],
            "positive": identity["positive"],
            "negative": identity["negative"],
            "tensor": {**identity["tensors"], "size_bytes": (ROOT / identity["tensors"]["path"]).stat().st_size},
            "labels": {**identity["labels"], "size_bytes": (ROOT / identity["labels"]["path"]).stat().st_size},
            "hard_negative": {**identity["hard_negative"], "size_bytes": (ROOT / identity["hard_negative"]["path"]).stat().st_size},
        })
    expected_total = sum(EXPECTED_TRAIN_ROWS.values())
    if expected_total != 276618 or sum(configured_rows.values()) != 4 * expected_total:
        raise ValueError("V37 total row denominator changed")

    # mmap-load every source cache and validate all label/hard-negative arrays
    # before comparing any patches. This is metadata + chunked validation only.
    cached = {}
    label_integrity = {}
    for scope, identity in identity_groups.items():
        cached[scope] = validate_cached_group(scope, identity, EXPECTED_TRAIN_ROWS[scope],
                                               EXPECTED_COUNTS[scope], budget)
        label_integrity[scope] = {key: cached[scope][key] for key in ("rows", "positive", "negative", "hard_negative")}
        label_integrity[scope]["tensor_hard_negative_dtype"] = str(cached[scope]["payload"]["hard_negative"].dtype)
        label_integrity[scope]["file_hard_negative_dtype"] = str(cached[scope]["hard_mmap"].dtype)
    if set(cached) != EXPECTED_BASE_GROUPS | EXPECTED_ADDITIONAL_GROUPS:
        raise ValueError("Loaded V37 raw patch tensor inventory is incomplete")

    # Reconstruct query originals and verify their proposal indexes against the
    # authenticated 195-scene report. Also reproduce selected component/family
    # training tensor order exactly, as established by the earlier run.
    packed_scenes = torch.load(ROOT / ATTR_DIR / "train-scenes.pt", map_location="cpu",
                               weights_only=True, mmap=True)
    if set(packed_scenes) != {"component", "family"} or {k: len(v) for k, v in packed_scenes.items()} != {
            "component": 167, "family": 28}:
        raise ValueError("Saved train scene membership changed")
    dense_records = {(r["scope"], r["scene_index"]): r for r in audit["records"]}
    if len(dense_records) != 195:
        raise ValueError("Dense train audit scene indexes are incomplete")
    all_prediction_rows = load_json(ROOT / ATTR_DIR / "predictions.json")
    extra_rows = [row for row in all_prediction_rows if row.get("outcome") == "extra"]
    if len(extra_rows) != 252:
        raise ValueError("Saved decoded-extra population changed")
    extra_by_scene = defaultdict(list)
    for internal_id, row in enumerate(extra_rows):
        extra_by_scene[(row["scope"], row["scene_index"])].append((internal_id, row))
    query_patches: list[torch.Tensor | None] = [None] * len(extra_rows)
    eligibility_metrics: list[dict | None] = [None] * len(extra_rows)
    query_selection: list[str | None] = [None] * len(extra_rows)
    selection_counts = Counter()
    training_cursor = Counter()
    base_training_match = [defaultdict(set) for _ in extra_rows]

    for scope in ("component", "family"):
        for scene_index, packed in enumerate(packed_scenes[scope]):
            with budget.work_block():
                bound = scene_cache.unpack_scene(packed, expected_split="train")
                scene = bound.scene if hasattr(bound, "scene") else bound
                dense = dense_records.get((scope, scene_index))
                if dense is None or evaluation.source._scene_identity(scene) != dense["identity"]:
                    raise ValueError(f"Authenticated train scene identity changed: {scope}")
                prefix = AUDIT_DIR / f"{scope}-train-{scene_index:03d}"
                coordinates = np.load(str(prefix) + "-coordinates.npy", allow_pickle=False)
                selected = np.load(str(prefix) + "-selected.npy", allow_pickle=False)
                labels = np.load(str(prefix) + "-labels.npy", allow_pickle=False)
                proposals = extract_bound_proposals(bound)
                actual_coordinates = proposals.coordinates.numpy()
                if (selected.dtype != np.bool_ or selected.shape != (len(proposals.patches),)
                        or labels.shape != (len(proposals.patches),)
                        or coordinates.shape != actual_coordinates.shape
                        or not np.array_equal(coordinates, actual_coordinates)):
                    raise ValueError(f"Proposal coordinates/selected flags changed for {scope}")
                indices = np.flatnonzero(selected)
                end = training_cursor[scope] + len(indices)
                source_patches = proposals.patches.index_select(0, torch.from_numpy(indices))
                source_labels = torch.from_numpy(labels[indices].astype(np.float32, copy=True))
                destination = cached[scope]["payload"]["patches"][training_cursor[scope]:end]
                destination_labels = cached[scope]["payload"]["labels"][training_cursor[scope]:end]
                if not torch.equal(source_patches, destination) or not torch.equal(source_labels, destination_labels):
                    raise ValueError(f"Selected scene proposals no longer equal V37 base rows: {scope}")
                training_cursor[scope] = end
                scene_extra_rows = extra_by_scene.get((scope, scene_index), ())
                if scene_extra_rows:
                    outputs_all = np.load(str(prefix) + "-predictions.npy", allow_pickle=False)
                    if (coordinates.dtype != np.float32 or outputs_all.dtype != np.float32
                            or outputs_all.shape != (len(proposals.patches), 4)
                            or not np.isfinite(outputs_all).all()):
                        raise ValueError(f"Saved proposal output shape/dtype changed: {scope}")
                    centers = np.asarray(scene.centers, dtype=np.float32).reshape(-1, 2)
                    pids = np.asarray([int(r["equivalent_proposal_indices"][0]) for _, r in scene_extra_rows
                                       if len(r.get("equivalent_proposal_indices", ())) == 1], dtype=np.int64)
                    if len(pids) != len(scene_extra_rows) or (pids < 0).any() or (pids >= len(coordinates)).any():
                        raise ValueError("Decoded extra proposal mapping changed")
                    outputs = outputs_all[pids]
                    anchors = coordinates[pids]
                    if (not np.isfinite(anchors).all() or not np.equal(anchors, np.floor(anchors)).all()
                            or (outputs[:, 0] < 0).any() or (outputs[:, 0] > 1).any()
                            or (outputs[:, 0] < 0.10).any()):
                        raise ValueError("Saved V37 outputs fail the fixed .10 input operating point")
                    for j, (_, row) in enumerate(scene_extra_rows):
                        if np.float32(row["confidence"]) != outputs[j, 0]:
                            raise ValueError("Extra confidence differs from saved V37 output")
                    label_distance = torch.cdist(torch.from_numpy(np.ascontiguousarray(anchors)),
                                                 torch.from_numpy(np.ascontiguousarray(centers))).min(dim=1).values
                    if not np.array_equal(labels[pids] > 0.5, label_distance.le(3.0).cpu().numpy()):
                        raise ValueError("Saved candidate labels differ from torch.cdist float32 semantics")
                    # Exact train_negative_coverage.py NumPy float32 expressions.
                    anchor_distance = np.linalg.norm(anchors[:, None, :]-centers[None, :, :], axis=2).min(axis=1)
                    decoded = anchors + outputs[:, 1:3]*4.
                    decoded_distance = np.linalg.norm(decoded[:, None, :]-centers[None, :, :], axis=2).min(axis=1)
                    for j, (internal_id, row) in enumerate(scene_extra_rows):
                        selection = "selected" if selected[pids[j]] else "unselected"
                        if (row.get("selection") != selection or row.get("label") != "negative"
                                or labels[pids[j]] > 0.5):
                            raise ValueError("Extra selection/negative label binding changed")
                        eligibility_metrics[internal_id] = {"scope": scope,
                            "anchor_distance": np.float32(anchor_distance[j]),
                            "decoded_distance": np.float32(decoded_distance[j])}

                for internal_id, row in scene_extra_rows:
                    proposal_indices = row["equivalent_proposal_indices"]
                    if len(proposal_indices) != 1:
                        raise ValueError("Decoded extra no longer maps to one proposal row")
                    proposal_index = int(proposal_indices[0])
                    if not (0 <= proposal_index < len(selected)):
                        raise ValueError("Decoded extra proposal index is out of bounds")
                    selection = "selected" if selected[proposal_index] else "unselected"
                    if (row.get("selection") != selection or row.get("label") != "negative"
                            or labels[proposal_index] > 0.5):
                        raise ValueError("Decoded extra selection/negative-label binding changed")
                    query_patches[internal_id] = proposals.patches[proposal_index].clone()
                    query_selection[internal_id] = selection
                    selection_counts[selection] += 1
            if time.perf_counter() - STARTED > MAX_RUNTIME_SECONDS:
                raise TimeoutError(f"Diagnostic exceeded {MAX_RUNTIME_SECONDS}s runtime budget")
        if training_cursor[scope] != EXPECTED_TRAIN_ROWS[scope]:
            raise ValueError(f"Selected original training row cursor mismatch: {scope}")
    if (any(query is None for query in query_patches) or any(metric is None for metric in eligibility_metrics)
            or selection_counts != Counter(selected=57, unselected=195)):
        raise ValueError("Decoded-extra query patch/selection inventory is incomplete")

    # Build a compact digest-to-query index. A digest hit is never sufficient:
    # bytes and tensor values are compared exactly before recording a match.
    query_bytes = [patch_bytes(patch) for patch in query_patches]

    def scan_group(scope: str, target_ids: list[int], match_map: list[dict[str, set[str]]]) -> None:
        patches = cached[scope]["payload"]["patches"]
        query_index = defaultdict(list)
        for internal_id in target_ids:
            query_index[hashlib.sha256(query_bytes[internal_id]).digest()].append(internal_id)
        for start in range(0, EXPECTED_TRAIN_ROWS[scope], BATCH_ROWS):
            end = min(start + BATCH_ROWS, EXPECTED_TRAIN_ROWS[scope])
            with budget.work_block():
                source_chunk = patches[start:end]
                if not torch.isfinite(source_chunk).all() or source_chunk.min() < 0 or source_chunk.max() > 1:
                    raise ValueError(f"Training patch tensor range changed during scan: {scope}")
                for transform in TRANSFORMS:
                    view_chunk = apply_transform(source_chunk, transform)
                    for patch in view_chunk:
                        raw = patch_bytes(patch)
                        digest = hashlib.sha256(raw).digest()
                        candidates = query_index.get(digest)
                        if not candidates:
                            continue
                        for internal_id in candidates:
                            # Explicit tensor and byte comparison makes this an
                            # exact-byte match even if a digest collision occurs.
                            if (raw == query_bytes[internal_id]
                                    and torch.equal(patch, query_patches[internal_id])):
                                match_map[internal_id][scope].add(transform)
            if time.perf_counter() - STARTED > MAX_RUNTIME_SECONDS:
                raise TimeoutError(f"Diagnostic exceeded {MAX_RUNTIME_SECONDS}s runtime budget")

    all_ids = list(range(len(extra_rows)))
    for scope in ("component", "family"):
        scan_group(scope, all_ids, base_training_match)
    base_matched = [bool(result) for result in base_training_match]
    base_matched_count = sum(base_matched)
    if base_matched_count != 59:
        raise ValueError(f"Base component/family reproduction changed: matched={base_matched_count}, expected=59")
    unmatched_ids = [internal_id for internal_id, matched in enumerate(base_matched) if not matched]
    if len(unmatched_ids) != 193:
        raise ValueError(f"Prior unmatched-extra denominator changed: {len(unmatched_ids)}")

    additional_match: list[dict[str, set[str]]] = [defaultdict(set) for _ in extra_rows]
    for scope in sorted(EXPECTED_ADDITIONAL_GROUPS):
        scan_group(scope, unmatched_ids, additional_match)

    by_scope = {}
    by_scope_transform = {}
    for scope in sorted(EXPECTED_ADDITIONAL_GROUPS):
        by_scope[scope] = sum(bool(additional_match[i].get(scope)) for i in unmatched_ids)
        by_scope_transform[scope] = {
            transform: sum(transform in additional_match[i].get(scope, set()) for i in unmatched_ids)
            for transform in TRANSFORMS
        }
    pattern_counts = Counter()
    matched_any = 0
    for internal_id in unmatched_ids:
        matched_groups = tuple(scope for scope in sorted(EXPECTED_ADDITIONAL_GROUPS)
                               if additional_match[internal_id].get(scope))
        pattern_counts["+".join(matched_groups) if matched_groups else "no_additional_group_match"] += 1
        matched_any += bool(matched_groups)
    if sum(pattern_counts.values()) != 193:
        raise AssertionError("Aggregate pattern accounting did not cover all prior unmatched extras")

    unmatched_all_v37 = [i for i in unmatched_ids
                         if not any(additional_match[i].get(scope) for scope in EXPECTED_ADDITIONAL_GROUPS)]
    if len(unmatched_all_v37) != 96:
        raise ValueError(f"Full V37 unmatched denominator changed: {len(unmatched_all_v37)}")
    rule_counts = {scope: Counter() for scope in ("component", "family")}
    eligible_ids = []
    for i in unmatched_all_v37:
        m = eligibility_metrics[i]
        gates = apply_safety_rules(
            np.asarray([m["anchor_distance"]], dtype=np.float32),
            np.asarray([m["decoded_distance"]], dtype=np.float32),
        )
        a = bool(m["anchor_distance"] > 8.)
        d = bool(m["decoded_distance"] > 5.)
        if bool(gates[0]) != (a and d):
            raise AssertionError("NumPy float32 rule helper differs from the saved miner expression")
        c = rule_counts[m["scope"]]
        c["unmatched_extras"] += 1
        c["anchor_pass"] += int(a); c["anchor_fail"] += int(not a)
        c["decoded_center_pass"] += int(d); c["decoded_center_fail"] += int(not d)
        if not a and not d: category = "both_rules_fail"
        elif not a: category = "anchor_only_fail"
        elif not d: category = "decoded_center_only_fail"
        else:
            category = "both_rules_pass"
            eligible_ids.append(i)
        c[category] += 1
    scope_order = {"component": 0, "family": 1}
    ordered_eligible_ids = sorted(eligible_ids, key=lambda i: (
        scope_order[extra_rows[i]["scope"]], int(extra_rows[i]["scene_index"]),
        int(extra_rows[i]["equivalent_proposal_indices"][0]), int(extra_rows[i]["prediction_index"])))
    eligible_unique = exact_deduplicate_patches(
        [(i, query_patches[i]) for i in ordered_eligible_ids])
    if len(eligible_unique) != 90:
        raise ValueError(f"Exact unique eligible row count changed: {len(eligible_unique)}")
    for i in eligible_unique:
        if base_training_match[i] or additional_match[i]:
            raise ValueError("Eligible patch unexpectedly matches existing V37 training rows")
    unique_by_scope = {scope: sum(eligibility_metrics[i]["scope"] == scope for i in eligible_unique)
                       for scope in ("component", "family")}
    if unique_by_scope != {"component": 50, "family": 40}:
        raise ValueError(f"Exact unique source-scope population changed: {unique_by_scope}")
    ordered_patches = [query_patches[i] for i in eligible_unique]
    payload = build_cache_payload(ordered_patches)
    values = tuple(payload[name] for name in parent_cache.NAMES)
    counts = parent_cache.validate_rows(values, expected_count=90)
    if (counts["positive"] != 0 or counts["negative"] != 90
            or counts["hard_negative"] != 90
            or any(torch.count_nonzero(payload[name]) for name in ("labels", "offsets", "radii"))
            or not torch.all(payload["hard_negative"] == 1)):
        raise ValueError("Residual training tensor labels/flags violate the cache contract")
    cache_path = OUT / "train-residual-negative.pt"
    torch.save(payload, cache_path)
    cache_hash, cache_size = guarded_hash(cache_path)
    reloaded = torch.load(cache_path, map_location="cpu", weights_only=True)
    if set(reloaded) != set(parent_cache.NAMES):
        raise ValueError("Serialized residual cache payload keys changed")
    reloaded_values = tuple(reloaded[name] for name in parent_cache.NAMES)
    if parent_cache.validate_rows(reloaded_values, expected_count=90) != counts:
        raise ValueError("Serialized residual cache row validation changed")
    if any(not torch.equal(a, b) for a, b in zip(values, reloaded_values, strict=True)):
        raise ValueError("Serialized residual cache tensor values changed")

    # Ensure all evidence used stayed stable without rehashing multi-gigabyte
    # arrays; compare stat identities captured by a second metadata pass.
    # The source/generator and report file hashes were fully checked pre-scan.
    if time.perf_counter() - STARTED > MAX_RUNTIME_SECONDS:
        raise TimeoutError(f"Diagnostic exceeded {MAX_RUNTIME_SECONDS}s runtime budget")

    stable_v37_inputs = verify_input_stats_stable()
    stable_prior_inputs = verify_input_stats_stable(prior_stat_snapshot)

    verify_v38_protocol_unchanged(ROOT, protocol_v38_hash)
    protocol_bindings = protocol_hash_bindings(protocol_v37_hash, protocol_v38_hash)
    source_hashes = {
        "builder.py": stream_hash(Path(__file__))[0],
        **protocol_bindings,
        "negative_miner_source": miner_hash,
        "eligibility_diagnostic": eligibility_script_hash,
        "eligibility_launcher": eligibility_launcher_hash,
        "full_v37_match_diagnostic": prior_script_hash,
    }
    manifest = {
        "schema_version": V38_SCHEMA_VERSION,
        "cache_id": V38_ID,
        "split": "train",
        "scope": "residual_negative",
        "rows": 90,
        "counts": counts,
        "tensor_file": cache_path.name,
        "tensor_sha256": cache_hash,
        "tensor_size_bytes": cache_size,
        "payload_keys": list(parent_cache.NAMES),
        "tensor_shapes_dtypes": {
            "patches": {"shape": [90, 3, 33, 33], "dtype": "float32"},
            "labels": {"shape": [90], "dtype": "float32"},
            "offsets": {"shape": [90, 2], "dtype": "float32"},
            "radii": {"shape": [90], "dtype": "float32"},
            "hard_negative": {"shape": [90], "dtype": "float32"},
        },
        "source_report_sha256": eligibility_hash,
        "source_scope_counts": unique_by_scope,
        "source_sha256": source_hashes,
        "input_hashes": {
            "eligibility_report": {"path": ELIGIBILITY_REPORT.as_posix(), "sha256": eligibility_hash},
            "full_v37_match_report": {"path": V2_REPORT.as_posix(), "sha256": prior_full_hash},
            "v3_exact_patch_report": {"path": V3_REPORT.as_posix(), "sha256": v3_hash},
            "attribution_report": {"path": (ATTR_DIR / "report.json").as_posix(), "sha256": direct_prior_hashes["attribution_report"]},
            "decoded_predictions": {"path": (ATTR_DIR / "predictions.json").as_posix(), "sha256": direct_prior_hashes["decoded_prediction_cache"]},
            "saved_train_scenes": {"path": (ATTR_DIR / "train-scenes.pt").as_posix(), "sha256": direct_prior_hashes["saved_train_scene_cache"]},
            "dense_audit_report": {"path": (AUDIT_DIR / "report.json").as_posix(), "sha256": direct_prior_hashes["dense_audit_report"]},
            "fixed_v37_run_report": {"path": V37_RUN_REPORT.as_posix(), "sha256": v37_run_hash},
            "fixed_v37_checkpoint": {"path": audit["model_checkpoint"]["path"], "sha256": model_hash},
            "v37_config": {"path": CONFIG_PATH.as_posix(), "sha256": config_hash},
            "v37_protocol": {"path": PROTOCOL_PATH.as_posix(), "sha256": protocol_v37_hash},
            "v38_cache_protocol": {"path": PROTOCOL_PATH_V38.as_posix(), "sha256": protocol_v38_hash},
            "v37_feature_report": {"path": FEATURE_REPORT_PATH.as_posix(), "sha256": feature_hash},
            "negative_miner_source": {"path": NEGATIVE_MINER_SOURCE.as_posix(), "sha256": miner_hash},
        },
        "v37_training_group_inventory": tensor_inventory,
        "v37_source_bundle_sha256": source_bundle,
        "eligibility_rules": {
            "operating_point": 0.1,
            "confidence_cutoff_applied": False,
            "proposal_anchor_distance_pixels": "> 8.0",
            "decoded_center_nearest_truth_distance_pixels": "> 5.0",
            "distance_dtype": "NumPy float32",
            "torch_cdist_label_validation": "float32 centers/coordinates, min distance <= 3.0",
        },
        "deduplication": {
            "new_patches": "SHA-256 bucket plus exact bytes and torch.equal; first exact instance retained",
            "existing_training": "all five V37 scopes and four fixed views scanned; zero eligible matches",
        },
        "ordering": ["component before family", "scene index ascending", "proposal index ascending", "exact duplicate first instance retained"],
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "runtime_seconds": time.perf_counter() - STARTED,
        "cpu_guard": budget.report(),
    }
    manifest_path = OUT / "manifest.json"
    verify_v38_protocol_unchanged(ROOT, protocol_v38_hash)
    manifest_path.write_bytes(canonical_json_bytes(manifest))
    manifest_hash = stream_hash(manifest_path)[0]
    report = {
        "status": "complete_train_only_residual_negative_cache",
        "question": "Build the authenticated 90-row V37 residual-negative train cache.",
        "aggregate_counts": {
            "all_v37_exact_match_population": {"decoded_extras": 252,
                "component_family_matches_reproduced": base_matched_count,
                "component_family_unmatched_extras": len(unmatched_ids),
                "additional_group_matches": matched_any,
                "no_exact_match_to_any_of_five_groups": len(unmatched_all_v37)},
            "eligibility_by_scope_and_rule": {scope: {key: int(c.get(key, 0)) for key in (
                "unmatched_extras", "anchor_pass", "anchor_fail", "decoded_center_pass",
                "decoded_center_fail", "both_rules_fail", "anchor_only_fail",
                "decoded_center_only_fail", "both_rules_pass")}
                for scope, c in rule_counts.items()},
            "eligible_original_patch_rows": len(eligible_ids),
            "eligible_distinct_original_patches_after_exact_self_dedup": len(eligible_unique),
            "eligible_distinct_original_patches_already_in_v37": 0,
            "eligible_distinct_new_original_patches_after_v37_exclusion": len(eligible_unique),
            "eligible_distinct_original_patches_within_scope": unique_by_scope,
            "by_additional_group_nonexclusive": by_scope,
            "by_additional_group_and_transform_nonexclusive": by_scope_transform,
            "exclusive_match_group_patterns": dict(sorted(pattern_counts.items())),
            "complete_v37_training_rows_identity": expected_total,
            "complete_v37_training_rows_all_four_views": 4 * expected_total,
        },
        "v37_training_group_inventory": tensor_inventory,
        "label_validation": label_integrity,
        "bindings": {
            "completed_v3_report_sha256": v3_hash,
            "completed_v3_report_size_bytes": v3_size,
            "completed_full_v37_match_report_sha256": prior_full_hash,
            "completed_full_v37_match_script_sha256": prior_script_hash,
            "fixed_v37_run_report_sha256": v37_run_hash,
            "fixed_v37_checkpoint_sha256": model_hash,
            "fixed_v37_checkpoint_size_bytes": model_size,
            "fixed_v37_threshold": 0.1,
            "negative_miner_source_sha256": miner_hash,
            "negative_miner_source_size_bytes": miner_size,
            "prior_v3_input_hashes": direct_prior_hashes,
            "prior_inputs_stable_after_hashing": prior_stable_inputs,
            "v37_config_sha256": config_hash,
            "v37_protocol_sha256": protocol_v37_hash,
            "v38_cache_protocol_sha256": protocol_v38_hash,
            "v37_feature_report_sha256": feature_hash,
            "v37_feature_report_size_bytes": feature_size,
            "v37_runner_source_bundle_sha256": source_bundle,
            "prior_attribution_evidence": prior_evidence,
            "prior_dense_audit_evidence": prior_audit_evidence,
            "prior_attribution_sources": prior_attribution_sources,
            "prior_dense_audit_sources": prior_audit_sources,
            "v37_feature_source_and_input_hashes": v37_input_hashes,
            "verified_input_paths_stable_after_scan": stable_v37_inputs + stable_prior_inputs,
            "all_195_component_family_train_scene_tensor_hashes_and_indexes_verified": True,
            "all_component_family_proposal_coordinates_and_selection_flags_verified": True,
            "component_family_selected_patches_equal_bound_training_tensors": True,
            "all_five_v37_tensor_caches_and_label_arrays_verified_before_comparison": True,
        },
        "patch_contract": {
            "dtype": "float32",
            "shape": [3, 33, 33],
            "tensor_inputs": "V37 feature report identity-group tensor refs, each SHA-256 verified; cache rows streamed via torch.load(mmap=True)",
            "reflections": list(TRANSFORMS),
            "match_semantics": "SHA-256 narrows candidates; exact contiguous patch bytes and torch.equal values must both match",
            "chunk_rows": BATCH_ROWS,
        },
        "execution": {
            "split": "train only",
            "private_reads": 0,
            "sealed_reads": 0,
            "model_inference_runs": 0,
            "saved_prediction_arrays_reused": True,
            "optimizer_steps": 0,
            "new_candidate_selected": False,
            "case_level_output": False,
            "runtime_seconds": time.perf_counter() - STARTED,
            "cpu_guard": budget.report(),
        },
        "eligibility_contract": {
            "distance_dtype": "NumPy float32, matching train_negative_coverage.py",
            "anchor_distance": "np.linalg.norm(coordinates[:, None, :] - centers[None, :, :], axis=2).min(axis=1)",
            "decoded_center_distance": "decoded = coordinates + outputs[:, 1:3] * 4.; NumPy norm then min(axis=1)",
            "strict_rules": {"proposal_anchor_distance_pixels": "> 8.0", "decoded_center_nearest_truth_distance_pixels": "> 5.0"},
            "separate_label_check": "torch.cdist(float32 coordinates, float32 centers).min(dim=1).values.le(3.0)",
            "operating_point": 0.1,
            "miner_confidence_cutoff_applied": False,
            "deduplication": "SHA-256 index plus exact bytes and torch.equal; original eligible patches only"},
        "cache": {
            "cache_id": V38_ID,
            "schema_version": V38_SCHEMA_VERSION,
            "scope": "residual_negative",
            "rows": 90,
            "tensor_file": cache_path.name,
            "tensor_sha256": cache_hash,
            "manifest_sha256": manifest_hash,
            "counts": counts,
        },
        "limitations": [
            "Cache includes only the 90 distinct synthetic train patches admitted by the bound eligibility report.",
            "No other shape-coverage training source is included in this cache.",
            "No model inference, optimizer step, candidate selection, or causal claim was made.",
        ],
    }
    report["source_report_sha256"] = eligibility_hash
    report["manifest_sha256"] = manifest_hash
    report["output_sha256"] = {
        "builder.py": source_hashes["builder.py"],
        **protocol_bindings,
        "manifest.json": manifest_hash,
        "train-residual-negative.pt": cache_hash,
    }
    report["execution"]["commands"] = {
        "guarded_python": r".\tools\Run-TrainingCpuBudget.ps1 -Program Python313/python.exe -ProgramArguments @('-X','utf8','-m','ml.markers.center.residual_negative_v38.builder','--output','artifacts/goal22-runs/residual-negative-v38-cache-20261029-v1')",
    }
    report["execution"]["runtime_seconds"] = time.perf_counter() - STARTED
    verify_v38_protocol_unchanged(ROOT, protocol_v38_hash)
    (OUT / "report.json").write_bytes(canonical_json_bytes(report))
    return {"status": report["status"], "counts": counts, "cache_sha256": cache_hash,
            "manifest_sha256": manifest_hash, "runtime_seconds": report["execution"]["runtime_seconds"]}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True,
                        help="Fresh directory under artifacts/goal22-runs")
    args = parser.parse_args()
    result = prepare_cache(Path.cwd(), args.output)
    print(json.dumps({"status": result["status"], "counts": result["counts"],
                      "cache_sha256": result["cache_sha256"],
                      "seconds": result["runtime_seconds"]}, sort_keys=True))


if __name__ == "__main__":
    main()
