# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""P2: source-uniform sampler, frozen P1 model recipe, train split only."""

from __future__ import annotations

from collections import Counter, defaultdict
from datetime import datetime, timezone
from hashlib import sha256
import importlib.util
import json
from pathlib import Path
import random
import stat
import sys
import time
from typing import Any, Mapping

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
HERE = Path(__file__).resolve().parent
CONFIG_PATH = HERE / "p2.json"
CONFIG_RELATIVE = CONFIG_PATH.relative_to(ROOT).as_posix()
HELPER_RELATIVE = "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py"
HELPER_PATH = ROOT / HELPER_RELATIVE
PINNED_DEPENDENCIES = {
    "model_helper_sha256": "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5",
    "p1_trainer_sha256": "3e8617809584dadb9c6b35cb52b95136fe766212a2f0bb65f4863b0d17ebc594",
    "train_admission_sha256": "72947560a68324ca2a6b470898755334e2f6ef98dd7491552e3618a615b30ede",
    "p1_config_sha256": "e4059c06a38e934b722c84c5d8e25a16051e2ba915d8c4910e05e029508f4465",
    "curator_sha256": "7e8a32dda6b5cf74f013db0992bd66ed894301dca21cf94ea32f64f1275c32df",
    "manifest_sha256": "aa265cdd89bef3a77e01d0809f48077bdacb1df1ca4e38ea0b81e637109ce672",
    "p1_train_diagnostic_report_sha256": "50aeafeeea48405cf5f219f076ebb9a513cb569f76a8ccac74f3b53a83c39214",
}
PINNED_DEPENDENCY_PATHS = {
    "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py": PINNED_DEPENDENCIES["model_helper_sha256"],
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/train_classifier.py": PINNED_DEPENDENCIES["p1_trainer_sha256"],
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/diagnose_train_split.py": PINNED_DEPENDENCIES["train_admission_sha256"],
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/p1.json": PINNED_DEPENDENCIES["p1_config_sha256"],
    "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/train_proposal_curation_v1/curate_train_proposals.py": PINNED_DEPENDENCIES["curator_sha256"],
    "artifacts/stroke-seeded-xtick-v1-inkscan-train-curation-v2/manifest.json": PINNED_DEPENDENCIES["manifest_sha256"],
    "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p1-train-diagnostic-rx6600xt-r4/report.json": PINNED_DEPENDENCIES["p1_train_diagnostic_report_sha256"],
}


def _read_project_file(relative: str, limit: int) -> bytes:
    """Bound source/config reads and reject symlink/reparse path components."""
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or limit <= 0:
        raise RuntimeError("invalid project-relative read")
    root = ROOT.resolve(strict=True)
    current = root
    for index, part in enumerate(rel.parts):
        current = current / part
        try:
            info = current.lstat()
        except OSError as error:
            raise RuntimeError(f"project input unavailable: {relative}") from error
        attributes = getattr(info, "st_file_attributes", 0)
        if stat.S_ISLNK(info.st_mode) or attributes & 0x400:
            raise RuntimeError(f"reparse component rejected: {relative}")
        is_last = index == len(rel.parts) - 1
        if not is_last and not stat.S_ISDIR(info.st_mode):
            raise RuntimeError(f"non-directory path component: {relative}")
        if is_last and not stat.S_ISREG(info.st_mode):
            raise RuntimeError(f"non-regular project input: {relative}")
        if is_last and info.st_size > limit:
            raise RuntimeError(f"project input exceeds bounded read: {relative}")
    with current.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise RuntimeError(f"project input grew beyond bounded read: {relative}")
    return data


CONFIG_BYTES = _read_project_file(CONFIG_RELATIVE, 65_536)
CONFIG = json.loads(CONFIG_BYTES)


def _sha(data: bytes) -> str:
    return sha256(data).hexdigest()


# Verify and execute the exact helper source bytes directly, bypassing any .pyc
# cache. The classifier/admission modules imported below receive this module.
_helper_source_before = _read_project_file(HELPER_RELATIVE, 2_000_000)
_helper_sha_before = _sha(_helper_source_before)
if _helper_sha_before != PINNED_DEPENDENCIES["model_helper_sha256"]:
    raise RuntimeError("actual helper source bytes do not match P2 config")
import ml.ocr.fragmented_xtick_detector_v3 as _helper_package
_helper_name = "ml.ocr.fragmented_xtick_detector_v3.gpu_train"
_helper_spec = importlib.util.spec_from_file_location(_helper_name, HELPER_PATH)
if _helper_spec is None:
    raise RuntimeError("cannot create pinned helper import spec")
helpers = importlib.util.module_from_spec(_helper_spec)
sys.modules[_helper_name] = helpers
exec(compile(_helper_source_before, str(HELPER_PATH), "exec"), helpers.__dict__)
_helper_package.gpu_train = helpers
_helper_sha_after = _sha(_read_project_file(HELPER_RELATIVE, 2_000_000))
if (Path(helpers.__file__).resolve() != HELPER_PATH.resolve()
        or _helper_sha_after != _helper_sha_before):
    raise RuntimeError("imported helper path/bytes changed during module execution")

import numpy as np
import platform
import importlib.metadata
import torch

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1 import train_classifier as p1
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1 import diagnose_train_split as admission

OUTPUT_RELATIVE = CONFIG["output_relative"]
_RUN: dict[str, Any] = {"phase": "startup", "optimizer_steps": 0}


class P2Error(RuntimeError):
    pass


def digest(value: bytes) -> str:
    return _sha(value)


def _read_bound_json(root: Path, relative: str, label: str, limit: int) -> tuple[dict[str, Any], str]:
    bound = helpers._bound_read(root, relative, label, limit)
    return helpers._parse_json(bound, label), bound.sha256


def validate_config() -> tuple[dict[str, Any], str]:
    config, config_sha = _read_bound_json(ROOT, CONFIG_PATH.relative_to(ROOT).as_posix(), "P2 config", 65_536)
    expected = {
        "schema": "graphreader.stroke-seeded-ink-stroke-classifier-config.v2",
        "recipe_id": "ink-stroke-validity-source-uniform-p2",
        "split": "same-frozen-P1-training-components; holdout metadata only",
        "train_source_groups": sorted(admission.TRAIN_GROUPS),
        "holdout_component_ids": ["stroke-seeded-xtick-v1-train-27103103",
            "stroke-seeded-xtick-v1-train-27103114", "stroke-seeded-xtick-v1-train-27103115",
            "stroke-seeded-xtick-v1-train-27103121"],
        "training_data": {"positive": 81, "negative": 365, "unique_total": 446},
        "source_group_counts": {"positive": 10, "negative": 19},
        "architecture": "P1 V3 feature stack + global average pooling + 24-to-1 binary head",
        "model_seed": 27104152, "sampler_seed": 27104153,
        "epochs": 20, "batch_size": 32, "learning_rate": 0.001,
        "optimizer": "AdamW", "loss": "binary_cross_entropy_with_logits",
        "steps_per_epoch": 23, "per_class_draws_per_epoch": 368,
        "class_draws_per_batch": 16,
        "sampler": "equal round-robin draws per owning source group per class, with remainder rotation; persistent deterministic per-group example cursor with replacement",
        "cpu_training_allowed": False, "production_approval": False,
        "probe_dev_sealed_private_inputs_allowed": False,
        "dependencies": PINNED_DEPENDENCIES,
        "dependency_paths": PINNED_DEPENDENCY_PATHS,
        "expected_runner_sha256": digest(Path(__file__).read_bytes()),
        "output_relative": "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p2-sourceuniform-rx6600xt",
        "p1_train_diagnostic_report_relative": "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p1-train-diagnostic-rx6600xt-r4/report.json",
    }
    for key, value in expected.items():
        if config.get(key) != value:
            raise P2Error(f"P2 config mismatch: {key}")
    if config["dependencies"]["model_helper_sha256"] != _helper_sha_after:
        raise P2Error("config does not pin the helper bytes executed at import")
    for relative, expected_sha in PINNED_DEPENDENCY_PATHS.items():
        path = ROOT / relative
        if digest(_read_project_file(relative, 4_000_000)) != expected_sha:
            raise P2Error(f"pinned dependency SHA mismatch: {relative}")
    if digest(_read_project_file(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000)) != config["expected_runner_sha256"]:
        raise P2Error("P2 runner source identity mismatch")
    return config, config_sha


def _owner_group_indices(rows: list[dict[str, Any]], train_groups: set[str]) -> tuple[dict[int, list[int]], dict[int, str]]:
    group_to_indices: dict[str, list[int]] = defaultdict(list)
    owners: dict[int, str] = {}
    for index, row in enumerate(rows):
        members = sorted(set(row["source_group_ids"]) & train_groups)
        if not members:
            raise P2Error("train image has no admitted owning source group")
        owner = members[0]
        owners[index] = owner
        group_to_indices[owner].append(index)
    return {group: values for group, values in sorted(group_to_indices.items())}, owners


def make_group_state(grouped_indices: Mapping[str, list[int]], seed: int) -> dict[str, dict[str, Any]]:
    if not grouped_indices or any(not indices for indices in grouped_indices.values()):
        raise ValueError("source sampler needs nonempty source groups")
    state: dict[str, dict[str, Any]] = {}
    for group, indices in sorted(grouped_indices.items()):
        group_seed = int.from_bytes(sha256(f"{seed}|{group}".encode("utf-8")).digest()[:8], "big")
        order = list(indices)
        random.Random(group_seed).shuffle(order)
        state[group] = {"order": order, "cursor": 0}
    return state


def source_uniform_order(grouped_indices: Mapping[str, list[int]], draw_count: int, epoch: int,
                        state: dict[str, dict[str, Any]]) -> tuple[list[int], Counter[str]]:
    """Round-robin source groups; draw each group's examples by a persistent rotating cursor."""
    groups = sorted(grouped_indices)
    if not groups or draw_count <= 0 or set(groups) != set(state):
        raise ValueError("invalid sampler groups or draw count")
    result: list[int] = []
    draws_by_group: Counter[str] = Counter()
    start = (epoch * draw_count) % len(groups)
    for slot in range(draw_count):
        group = groups[(start + slot) % len(groups)]
        source = state[group]
        order = source["order"]
        cursor = int(source["cursor"])
        result.append(order[cursor % len(order)])
        source["cursor"] = cursor + 1
        draws_by_group[group] += 1
    return result, draws_by_group


def classification_metrics(labels: list[int], scores: list[float], threshold: float = 0.5) -> dict[str, Any]:
    return p1._classification_metrics(labels, scores, threshold)


def score_summary(values: list[float]) -> dict[str, float | int]:
    return admission._distribution(values)


def _versions(runtime: Mapping[str, Any]) -> dict[str, Any]:
    return {"python": platform.python_version(), "implementation": platform.python_implementation(),
        "torch": str(torch.__version__), "numpy": str(np.__version__),
        "Pillow": importlib.metadata.version("Pillow"), "hip": runtime.get("hip"),
        "device": runtime.get("device"), "device_name": runtime.get("device_name"),
        "cpu_fallback": runtime.get("cpu_fallback")}


def _evaluate_train(model: Any, rows: list[dict[str, Any]], device: Any,
                    batch_size: int) -> tuple[dict[str, Any], dict[str, Any], list[float]]:
    model.eval()
    pixels = np.stack([row["pixels"] for row in rows]).astype(np.float32, copy=False)
    tensor = torch.from_numpy(pixels).unsqueeze(1)
    scores: list[float] = []
    with torch.inference_mode():
        for start in range(0, len(rows), batch_size):
            batch = tensor[start:start + batch_size].to(device)
            if batch.device != device:
                raise P2Error("train-only evaluation tensor left RX 6600 XT")
            scores.extend(torch.sigmoid(model(batch)).detach().cpu().tolist())
    labels = [int(row["label"]) for row in rows]
    overall = classification_metrics(labels, [float(s) for s in scores])
    groups: dict[str, dict[str, list[float]]] = defaultdict(lambda: {"positive": [], "negative": []})
    for row, score in zip(rows, scores):
        klass = "positive" if row["label"] else "negative"
        for group in row["source_group_ids"]:
            groups[group][klass].append(float(score))
    per_group = {}
    for group, classes in sorted(groups.items()):
        per_group[group] = {"positive": score_summary(classes["positive"]) if classes["positive"] else None,
            "negative": score_summary(classes["negative"]) if classes["negative"] else None,
            "positive_metrics": classification_metrics([1] * len(classes["positive"]), classes["positive"]) if classes["positive"] else None,
            "negative_metrics": classification_metrics([0] * len(classes["negative"]), classes["negative"]) if classes["negative"] else None}
    return overall, per_group, [float(s) for s in scores]


def _compare_p1(p1_report: dict[str, Any], p2_overall: dict[str, Any],
                p2_groups: dict[str, Any]) -> dict[str, Any]:
    p1_metrics = p1_report.get("full_crop_train_metrics")
    if not isinstance(p1_metrics, dict) or p1_metrics.get("count") != 446:
        raise P2Error("pinned P1 report lacks the exact train-only baseline metric")
    p1_groups = p1_report.get("score_summary", {}).get("source_groups", {})
    differences = {}
    for group in sorted(set(p1_groups) & set(p2_groups)):
        differences[group] = {}
        for klass in ("positive", "negative"):
            old = p1_groups[group].get("full_crop", {}).get(klass)
            new = p2_groups[group].get(klass)
            if old and new:
                differences[group][klass] = {
                    "p1_count": old["count"], "p2_count": new["count"],
                    "p1_mean_score": old["mean"], "p2_mean_score": new["mean"],
                    "mean_score_delta": new["mean"] - old["mean"],
                    "p1_fraction_at_or_above_0_5": old["fraction_at_or_above_0_5"],
                    "p2_fraction_at_or_above_0_5": new["fraction_at_or_above_0_5"]}
    return {"p1_report_sha256": CONFIG["dependencies"]["p1_train_diagnostic_report_sha256"],
        "p1_train_metrics": p1_metrics, "p2_train_metrics": p2_overall,
        "metric_deltas_p2_minus_p1": {key: p2_overall[key] - p1_metrics[key]
            for key in ("precision", "recall", "accuracy", "true_positive", "false_positive", "false_negative", "true_negative")},
        "per_source_class_score_comparison": differences,
        "p1_holdout_fields_read": False}


def _train(config: dict[str, Any], config_sha: str, output: Path) -> dict[str, Any]:
    _RUN["phase"] = "pinned-train-only-admission"
    data = admission.admit_train_images()
    rows = data["images"]
    expected_class_counts = {"positive": config["training_data"]["positive"],
        "negative": config["training_data"]["negative"]}
    if (data["class_counts"] != expected_class_counts
            or len(rows) != 446 or data["nontrain_image_hash_overlap"] != 0):
        raise P2Error("train-only admitted rows differ from predeclared P2 counts")
    train_groups = set(data["source_groups"])
    positive_rows = [row for row in rows if row["label"] == 1]
    negative_rows = [row for row in rows if row["label"] == 0]
    pos_local = [{"source_group_ids": row["source_group_ids"]} for row in positive_rows]
    neg_local = [{"source_group_ids": row["source_group_ids"]} for row in negative_rows]
    pos_map, pos_owner = _owner_group_indices(pos_local, train_groups)
    neg_map, neg_owner = _owner_group_indices(neg_local, train_groups)
    if (len(pos_map), len(neg_map)) != (10, 19):
        raise P2Error("owner-group class counts differ from predeclared P2 counts")

    runtime = helpers.require_rx_6600_xt(torch)
    device = torch.device(str(runtime["device"]))
    if Path(sys.executable).resolve() != helpers.GPU_PYTHON.resolve():
        raise P2Error("project GPU launcher is required; CPU training is forbidden")
    torch.manual_seed(int(config["model_seed"]))
    model = p1._build_classifier(torch).to(device)
    fmap = tuple(model.features(torch.zeros((1, 1, 96, 256), device=device)).shape[-2:])
    if fmap != (12, 32):
        raise P2Error("frozen P1 architecture shape changed")
    p1._assert_gpu_binding(runtime, model, [torch.zeros((1, 1, 96, 256), device=device)], device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=float(config["learning_rate"]))
    train_pixels = torch.from_numpy(np.stack([row["pixels"] for row in rows]).astype(np.float32)).unsqueeze(1)
    labels = [int(row["label"]) for row in rows]
    pos_state = make_group_state(pos_map, int(config["sampler_seed"]))
    neg_state = make_group_state(neg_map, int(config["sampler_seed"]) + 1)
    pos_global_indices: dict[int, int] = {}
    neg_global_indices: dict[int, int] = {}
    pindex = nindex = 0
    for global_index, row in enumerate(rows):
        if row["label"]:
            pos_global_indices[pindex] = global_index
            pindex += 1
        else:
            neg_global_indices[nindex] = global_index
            nindex += 1

    pos_seen: set[int] = set()
    neg_seen: set[int] = set()
    pos_seen_by_group: dict[str, set[int]] = defaultdict(set)
    neg_seen_by_group: dict[str, set[int]] = defaultdict(set)
    losses: list[float] = []
    epoch_reports = []
    training_started = time.perf_counter()
    steps = int(config["steps_per_epoch"])
    draws = int(config["per_class_draws_per_epoch"])
    half = int(config["class_draws_per_batch"])
    _RUN["phase"] = "source-uniform-optimizer-training"
    for epoch in range(int(config["epochs"])):
        pos_order, pos_group_draws = source_uniform_order(pos_map, draws, epoch, pos_state)
        neg_order, neg_group_draws = source_uniform_order(neg_map, draws, epoch, neg_state)
        if max(pos_group_draws.values()) - min(pos_group_draws.values()) > 1:
            raise P2Error("positive per-source draw counts are not uniform")
        if max(neg_group_draws.values()) - min(neg_group_draws.values()) > 1:
            raise P2Error("negative per-source draw counts are not uniform")
        pos_epoch_unique = set(pos_order)
        neg_epoch_unique = set(neg_order)
        for local in pos_epoch_unique:
            pos_seen.add(pos_global_indices[local])
        for local in neg_epoch_unique:
            neg_seen.add(neg_global_indices[local])
        for local in pos_epoch_unique:
            pos_seen_by_group[pos_owner[local]].add(local)
        for local in neg_epoch_unique:
            neg_seen_by_group[neg_owner[local]].add(local)

        for batch_index in range(steps):
            start = batch_index * half
            selected_local = pos_order[start:start + half] + neg_order[start:start + half]
            selected = [pos_global_indices[i] for i in selected_local[:half]] + [neg_global_indices[i] for i in selected_local[half:]]
            random.Random(int(config["sampler_seed"]) + epoch * 10007 + batch_index).shuffle(selected)
            x = train_pixels[selected].to(device)
            y = torch.tensor([labels[i] for i in selected], dtype=torch.float32, device=device)
            p1._assert_gpu_binding(runtime, model, [x, y], device)
            if int(y.sum()) != half:
                raise P2Error("P2 batch class balance changed")
            optimizer.zero_grad(set_to_none=True)
            loss = torch.nn.functional.binary_cross_entropy_with_logits(model(x), y)
            if not torch.isfinite(loss):
                raise P2Error("non-finite P2 loss")
            loss.backward()
            optimizer.step()
            _RUN["optimizer_steps"] += 1
            losses.append(float(loss.detach().cpu()))

        epoch_reports.append({"epoch": epoch + 1,
            "positive_draws_by_owner_group": dict(sorted(pos_group_draws.items())),
            "negative_draws_by_owner_group": dict(sorted(neg_group_draws.items())),
            "positive_repeated_draws": draws - len(pos_epoch_unique),
            "negative_repeated_draws": draws - len(neg_epoch_unique),
            "positive_unique_covered_this_epoch": len(pos_epoch_unique),
            "negative_unique_covered_this_epoch": len(neg_epoch_unique),
            "positive_cumulative_unique_covered": len(pos_seen),
            "negative_cumulative_unique_covered": len(neg_seen),
            "positive_cumulative_unique_by_owner_group": {g: len(pos_seen_by_group[g]) for g in sorted(pos_map)},
            "negative_cumulative_unique_by_owner_group": {g: len(neg_seen_by_group[g]) for g in sorted(neg_map)}})

    torch.cuda.synchronize(device)
    elapsed_training = time.perf_counter() - training_started
    _RUN["phase"] = "train-only-fit-evaluation"
    start_eval = time.perf_counter()
    p2_metrics, p2_groups, p2_scores = _evaluate_train(model, rows, device, int(config["batch_size"]))
    torch.cuda.synchronize(device)
    eval_elapsed = time.perf_counter() - start_eval
    p1_report_bound = helpers._bound_read(ROOT, config["p1_train_diagnostic_report_relative"],
        "existing P1 train-only metric report", 200_000)
    if p1_report_bound.sha256 != config["dependencies"]["p1_train_diagnostic_report_sha256"]:
        raise P2Error("existing P1 train-only comparison report changed")
    p1_report = json.loads(p1_report_bound.value.decode("utf-8"))

    checkpoint_path = output / "checkpoint.pt"
    torch.save({"state_dict": model.state_dict(), "model_identity": "stroke-seeded-ink-validity-rf31-p2-sourceuniform",
        "architecture": "P1 V3 feature stack + global average pooling + 24-to-1 binary head",
        "recipe_id": config["recipe_id"], "sampler": config["sampler"],
        "train_source_groups": sorted(train_groups), "model_seed": config["model_seed"],
        "sampler_seed": config["sampler_seed"], "runtime": runtime,
        "config_sha256": config_sha, "manifest_sha256": data["manifest_sha256"]}, checkpoint_path)
    checkpoint_sha = digest(checkpoint_path.read_bytes())
    return {
        "schema": "graphreader.stroke-seeded-ink-stroke-classifier-report.v2",
        "status": "completed-train-only-source-uniform-diagnostic",
        "recipe_id": config["recipe_id"], "acceptance_status": "train-only diagnostic; not acceptance",
        "production_approval": False, "release_eligible": False,
        "dependencies": {"p2_config_sha256": config_sha,
            "p2_runner_sha256": config["expected_runner_sha256"],
            **config["dependencies"], "actual_helper_bytes_hashed_before_import": _helper_sha_before,
            "actual_helper_module_file": str(Path(helpers.__file__).resolve()),
            "actual_helper_bytes_hashed_after_import": _helper_sha_after,
            "source_uniform_sampler_implementation_sha256": digest(Path(__file__).read_bytes())},
        "runtime_versions": _versions(runtime),
        "data": {"manifest_sha256": data["manifest_sha256"], "row_occurrences_admitted": data["row_count"],
            "unique_images": 446, "class_counts": {"positive": 81, "negative": 365},
            "train_source_groups": sorted(train_groups), "positive_owner_groups": sorted(pos_map),
            "negative_owner_groups": sorted(neg_map), "nontrain_image_hash_overlap": 0,
            "nontrain_annotation_or_image_bytes_read": False,
            "holdout_used_for_training_or_metrics": False},
        "recipe": {"architecture": config["architecture"], "model_seed": config["model_seed"],
            "sampler_seed": config["sampler_seed"], "epochs": config["epochs"],
            "steps_per_epoch": steps, "optimizer_steps": _RUN["optimizer_steps"],
            "batch_size": config["batch_size"], "learning_rate": config["learning_rate"],
            "optimizer": config["optimizer"], "loss": config["loss"],
            "class_draws_per_batch": half, "draws_per_class_per_epoch": draws,
            "sampler": config["sampler"], "p1_sampler_difference": "P1 replayed a row-proportional first-pass sequence; P2 assigns equal per-epoch draw counts to owning source groups, rotating remainder and examples."},
        "training": {"loss_first": losses[0], "loss_last": losses[-1],
            "elapsed_training_seconds": elapsed_training, "training_performed": True,
            "cpu_training_allowed": False, "probe_dev_sealed_private_inputs_read": False,
            "cumulative_unique_training_coverage": {"positive": len(pos_seen), "negative": len(neg_seen),
                "total": len(pos_seen) + len(neg_seen), "expected_total": 446},
            "coverage_by_epoch": epoch_reports},
        "train_only_metrics": {"threshold": 0.5, "p2_overall": p2_metrics,
            "p2_per_source_group_membership": p2_groups,
            "p1_train_only_comparison": _compare_p1(p1_report, p2_metrics, p2_groups),
            "evaluation_elapsed_seconds": eval_elapsed},
        "checkpoint": {"path": CHECKPOINT_OUTPUT_RELATIVE, "sha256": checkpoint_sha,
            "model_identity": "stroke-seeded-ink-validity-rf31-p2-sourceuniform"},
        "limitations": ["Only the training split was scored; do not infer generalization or use this result as acceptance.",
            "Source-uniform sampling deliberately oversamples small source groups and repeats their images; it changes sample weighting, not only class balance.",
            "Group-level source summaries may count identical hashes in multiple source memberships; overall metrics count each unique image once.",
            "No text/geometry causal isolation was performed; the classifier sees the complete crop."],
    }


CHECKPOINT_OUTPUT_RELATIVE = OUTPUT_RELATIVE + "/checkpoint.pt"


def main() -> None:
    config, config_sha = validate_config()
    output = ROOT / OUTPUT_RELATIVE
    if output.exists() or output.is_symlink() or output.parent != ROOT / "artifacts":
        raise P2Error("P2 output must be a fresh project artifacts child")
    helpers._checked_chain(ROOT, output.parent, "P2 output parent")
    output.mkdir(parents=False, exist_ok=False)
    started = {"status": "started", "recipe_id": config["recipe_id"],
        "config_sha256": config_sha, "runner_sha256": config["expected_runner_sha256"],
        "dependency_sha256": config["dependencies"], "started_utc": datetime.now(timezone.utc).isoformat()}
    (output / "started.json").write_text(json.dumps(started, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    started_at = time.perf_counter()
    try:
        report = _train(config, config_sha, output)
        report["training"]["elapsed_total_seconds"] = time.perf_counter() - started_at
        (output / "report.json").write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    except BaseException as error:
        failure = {"status": "failed", "phase": _RUN["phase"], "optimizer_steps": _RUN["optimizer_steps"],
            "error_type": type(error).__name__, "error": str(error),
            "failed_utc": datetime.now(timezone.utc).isoformat()}
        (output / "failure.json").write_text(json.dumps(failure, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
