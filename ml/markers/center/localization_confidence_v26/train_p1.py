# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train-only V26 runner for the fixed annulus-reservation sampler."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import time
from typing import Any, Callable, Mapping, Sequence

import numpy as np
import torch

from ml.markers.center.localization_confidence_v26 import annulus_reservation_preflight
from ml.markers.center.mask_preserving_v24 import train_p1 as v24
from ml.markers.center.plot_domain_v25 import train_p1 as v25
from ml.markers.center.plot_domain_v25.proposal_domain import extract_proposals_in_domain
from ml.markers.gate_seal import (
    canonical_json_bytes, sha256_file, source_bundle_sha256,
    verify_bound_source_snapshot,
)
from ml.markers.training_budget import (
    TrainingAuthorization,
    acquire_training_candidate,
    complete_training_candidate,
    void_candidate,
)


REPO_ROOT = Path(__file__).resolve().parents[4]
ROOT = Path("ml/markers/center/localization_confidence_v26")
CONFIG_PATH = ROOT / "training/p1.json"
PROTOCOL_PATH = ROOT / "protocol.json"
TASK = "marker-center"
REVISION = "marker-center-annulus-reservation-v26"
CANDIDATE_ID = "P1"
CONFIG_SCHEMA = "graphreader.marker-center-annulus-reservation-v26-candidate-config.v1"
REPORT_SCHEMA = "graphreader.marker-center-annulus-reservation-v26-candidate.v1"
FAILURE_SCHEMA = "graphreader.marker-center-annulus-reservation-v26-failure.v1"

BASE_V25_CONFIG_PATH = v25.CONFIG_PATH
BASE_V25_CONFIG_SHA256 = "e84acb31ca4df3204d3894e893b31ba7fede55aa91a6c885f791eee05b7fe9b2"
BASE_V25_RESULT_PATH = Path("ml/markers/center/plot_domain_v25/P1_RESULT.json")
BASE_V25_RESULT_SHA256 = "fb64443005b6ca4c26d4e5f1d8b37d8882a93a186d9b2b92fffc476a8c823a63"
COVERAGE_REPORT_PATH = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/negative-coverage-v1/coverage.json"
)
COVERAGE_REPORT_SHA256 = "921e6020831055cde224729954a863c23b6804bee232a9431c773bce660ed1d5"
COVERAGE_CACHE_PATH = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/negative-coverage-v1/"
    "frozen-v25-train-proposal-metadata.npz"
)
COVERAGE_CACHE_SHA256 = "5b8afc209e1c2a6f849ef044056b46903c79692d7264a724651a3a158369576b"
ANNULUS_REPORT_PATH = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/annulus-reservation-v1/preflight.json"
)
ANNULUS_REPORT_SHA256 = "1bcece9f371b97c0e38679eb433259894faa2ef5dbad146bef3cd02319bc810b"
ANNULUS_CACHE_PATH = Path(
    "artifacts/goal22-runs/marker-v26-target-preflight/annulus-reservation-v1/"
    "proposed-selection-metadata.npz"
)
ANNULUS_CACHE_SHA256 = "4a03c08d1566a1f587910ea2fd9c49069d850c62a558b15f614af1f01c93fa8e"
CHECKPOINT_NAME = "marker-center-annulus-reservation-v26-p1.pt"
ONNX_NAME = "marker-center-annulus-reservation-v26-p1.onnx"
REPORT_NAME = "candidate-report.json"

RUNNER_SOURCE_PATHS = tuple(dict.fromkeys((
    *v25.RUNNER_SOURCE_PATHS,
    ROOT / "train_p1.py",
    PROTOCOL_PATH,
    ROOT / "measure_negative_coverage.py",
    ROOT / "annulus_reservation_preflight.py",
)))

RECIPE = {
    "base_v25_config_sha256": BASE_V25_CONFIG_SHA256,
    "sampler": "fixed_truth_annulus_reservation_v1",
    "bands_px": [[3.0, 5.0], [5.0, 8.0], [8.0, 12.0]],
    "reservation": "one_closest_negative_per_scene_truth_and_band",
    "replacement": [
        "same_scene_and_original_stratum",
        "same_original_stratum_global",
        "cross_stratum_global_fallback",
    ],
    "seed": 20260903,
    "epochs": 36,
    "batch_size": 128,
    "learning_rate": 0.001,
    "weight_decay": 0.0001,
    "positive_loss_weight": 16.0,
    "hard_negative_loss_weight": 5.0,
    "focal_alpha": 0.25,
    "focal_gamma": 2.0,
    "label_positive_distance_px": 3.0,
    "confidence_threshold": 0.25,
    "selection_thresholds": [0.4, 0.55, 0.7],
    "provider": "CPUExecutionProvider",
    "onnx_dynamic_candidate_counts": [1, 8, 37],
    "onnx_parity_tolerance": 1e-5,
}
EXPECTED_DATA = {
    "component_train": {"scene_count": 167, "truth_count": 2004},
    "family_train": {"source_count": 20, "panel_count": 28, "truth_count": 500},
    "component_dev": {"scene_count": 167, "truth_count": 2004},
    "family_dev": {"source_count": 3, "panel_count": 9, "truth_count": 206},
    "training_example_count": 44891,
    "positive_example_count": 4081,
    "negative_example_count": 40810,
    "hard_negative_example_count": 8617,
    "optimizer_steps": 12636,
}

_CONFIG_KEYS = {
    "schema", "task", "revision", "candidate_id", "stage",
    "expected_runner_source_bundle_sha256", "base_v25_config", "base_v25_result",
    "negative_coverage_report", "negative_coverage_cache", "annulus_preflight",
    "annulus_selection_cache", "recipe", "expected_data", "synthetic_only",
    "private_data", "sealed_data", "production_approval", "release_eligible",
}


class V26TrainingError(RuntimeError):
    """The V26 source, sampler evidence, or training contract changed."""


@dataclass(frozen=True)
class PreparedTraining:
    config: Mapping[str, Any]
    config_path: Path
    config_sha256: str
    base: Any
    component_values: tuple[torch.Tensor, ...]
    family_values: tuple[torch.Tensor, ...]
    training_generator_state: torch.Tensor
    selection_evidence: Mapping[str, Any]


@dataclass(frozen=True)
class Dependencies:
    prepare_base: Callable[..., Any]
    acquire: Callable[..., TrainingAuthorization]
    complete: Callable[..., Path]
    void: Callable[[TrainingAuthorization, BaseException], Path]
    verify_snapshot: Callable[[Path, Path, object], None]


def _default_dependencies() -> Dependencies:
    return Dependencies(v25._prepare, acquire_training_candidate,
                        complete_training_candidate, void_candidate,
                        verify_bound_source_snapshot)


def prepare_training(
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: Dependencies | None = None,
) -> PreparedTraining:
    """Authenticate V25 and rebuild only its selected train proposal rows."""

    root = repository_root.resolve()
    relative_config = _relative(root, config_path, "configuration")
    config, config_sha = _load_config(root / relative_config, root)
    if source_bundle_sha256(root, RUNNER_SOURCE_PATHS) != config[
        "expected_runner_source_bundle_sha256"
    ]:
        raise V26TrainingError("V26 runner source bundle changed")
    deps = dependencies or _default_dependencies()
    base = deps.prepare_base(root, v25._default_dependencies())
    base_config_path = _descriptor_path(root, config["base_v25_config"], "base V25 config")
    _, base_config_sha, _ = v25._validate_candidate_config(
        base_config_path.relative_to(root), root, base.report
    )
    if base_config_sha != BASE_V25_CONFIG_SHA256:
        raise V26TrainingError("Prepared V25 configuration changed")
    _validate_base_result(root, config["base_v25_result"])
    coverage_report, coverage = _load_report_and_cache(
        root, config["negative_coverage_report"], config["negative_coverage_cache"],
        COVERAGE_REPORT_PATH, COVERAGE_REPORT_SHA256, COVERAGE_CACHE_PATH,
        COVERAGE_CACHE_SHA256, "negative coverage",
    )
    annulus_report, annulus = _load_report_and_cache(
        root, config["annulus_preflight"], config["annulus_selection_cache"],
        ANNULUS_REPORT_PATH, ANNULUS_REPORT_SHA256, ANNULUS_CACHE_PATH,
        ANNULUS_CACHE_SHA256, "annulus reservation",
    )
    _validate_evidence_links(root, base, coverage_report, annulus_report)
    component_values, component_evidence = _rebuild_scope(
        "component", base.component_train, base.component_values[5],
        coverage_report, coverage, annulus, domain_limited=False,
    )
    family_values, family_evidence = _rebuild_scope(
        "family", base.family_train, base.family_values[5],
        coverage_report, coverage, annulus, domain_limited=True,
    )
    _validate_selection_evidence(component_evidence, annulus_report.get("component_train"),
                                 "component")
    _validate_selection_evidence(family_evidence, annulus_report.get("family_train"),
                                 "family")
    _validate_rebuilt_counts(component_values, family_values)
    return PreparedTraining(
        config, relative_config, config_sha, base, component_values, family_values,
        base.training_generator_state.clone(),
        {"component": component_evidence, "family": family_evidence},
    )


def _rebuild_scope(
    scope: str,
    scenes: Sequence[Any],
    old_sampling: Any,
    coverage_report: Mapping[str, Any],
    coverage: Mapping[str, np.ndarray],
    annulus: Mapping[str, np.ndarray],
    *,
    domain_limited: bool,
) -> tuple[tuple[torch.Tensor, ...], dict[str, Any]]:
    prefix = f"{scope}_"
    proposed = annulus[prefix + "proposed_selected"]
    frozen = coverage[prefix + "selected"]
    if proposed.dtype != np.bool_ or frozen.dtype != np.bool_ or proposed.shape != frozen.shape:
        raise V26TrainingError(f"{scope} selection masks changed")
    parts: list[list[torch.Tensor]] = [[] for _ in range(5)]
    old_masks: list[np.ndarray] = []
    if len(old_sampling.selections) < len(scenes) or any(old_sampling.selections[len(scenes):]):
        raise V26TrainingError(f"{scope} V25 sampler selection shape changed")
    cursor = 0
    for scene_index, item in enumerate(scenes):
        scene = item.scene if domain_limited else item
        proposals = (
            extract_proposals_in_domain(scene.tensor, item.panel_domain.domain).proposals
            if domain_limited else v24.extract_proposals(scene.tensor)
        )
        values, nearest_index, nearest_distance = _proposal_values(scene, proposals)
        count = len(values[1])
        rows = slice(cursor, cursor + count)
        _validate_regenerated_rows(
            scope, scene_index, proposals.coordinates, nearest_index,
            nearest_distance, coverage, rows,
        )
        indices = old_sampling.selections[scene_index]
        old = np.zeros(count, dtype=np.bool_)
        old[list(indices)] = True
        if not domain_limited:
            old |= values[1].numpy() > 0.5
        old_masks.append(old)
        selection = np.flatnonzero(proposed[rows])
        if np.any((values[1].numpy() > 0.5) & ~proposed[rows]):
            raise V26TrainingError(f"{scope} reservation omitted a positive row")
        tensor_index = torch.from_numpy(selection.astype(np.int64, copy=False))
        for destination, value in zip(parts, values, strict=True):
            destination.append(value.index_select(0, tensor_index))
        cursor += count
    if cursor != len(proposed) or not np.array_equal(np.concatenate(old_masks), frozen):
        raise V26TrainingError(f"{scope} frozen V25 selection no longer maps to regenerated rows")
    selected = np.concatenate(old_masks)
    roles = coverage[prefix + "retention_role_id"]
    strata = coverage[prefix + "stratum_id"]
    cache_metadata = coverage_report.get("cache", {})
    role_ids = cache_metadata.get("retention_role_ids", {})
    stratum_ids = cache_metadata.get("stratum_ids", {})
    if roles.shape != selected.shape or strata.shape != selected.shape:
        raise V26TrainingError(f"{scope} retention roles changed")
    required_roles = (
        {"topology_reserved", "connector_anchor_reserved", "sparse_fragment_reserved"}
        if scope == "component" else {"hard_negative_retained"}
    )
    protected_role_ids = tuple(
        value for name, value in role_ids.items() if name in required_roles
    )
    protected = selected & np.isin(roles, protected_role_ids)
    if scope == "component":
        hard_stratum = stratum_ids.get("hard_existing")
        if type(hard_stratum) is not int:
            raise V26TrainingError("component hard-existing stratum identity changed")
        protected |= selected & (strata == hard_stratum)
    if np.any(protected & ~proposed):
        raise V26TrainingError(f"{scope} reservation displaced a protected V25 row")
    result = tuple(torch.cat(value) for value in parts)
    return result, {
        "frozen_selection_sha256": annulus_reservation_preflight._selection_sha256(
            frozen, coverage[prefix + "scene_index"], coverage[prefix + "proposal_index"]),
        "proposed_selection_sha256": annulus_reservation_preflight._selection_sha256(
            proposed, coverage[prefix + "scene_index"], coverage[prefix + "proposal_index"]),
        "selected_rows": int(proposed.sum()),
        "preserved_protected_rows": int(protected.sum()),
        "added_rows": int(annulus[prefix + "added"].sum()),
        "displaced_rows": int(annulus[prefix + "displaced"].sum()),
        "row_order": "scene_then_ascending_proposal_index",
    }


def _proposal_values(
    scene: Any, proposals: Any
) -> tuple[tuple[torch.Tensor, ...], np.ndarray, np.ndarray]:
    coordinates = proposals.coordinates
    centers = torch.tensor(scene.centers, dtype=torch.float32).reshape(-1, 2)
    radii = torch.tensor(scene.diameters, dtype=torch.float32) / 2.0
    if len(centers):
        distances = torch.cdist(coordinates, centers)
        nearest_distance, nearest_index = distances.min(dim=1)
        labels = nearest_distance.le(3.0).float()
        offsets = (centers[nearest_index] - coordinates) / 4.0
        target_radii = radii[nearest_index]
    else:
        nearest_distance = torch.full((len(coordinates),), float("inf"))
        nearest_index = torch.full((len(coordinates),), -1, dtype=torch.int64)
        labels = torch.zeros(len(coordinates), dtype=torch.float32)
        offsets = torch.zeros_like(coordinates)
        target_radii = torch.zeros_like(labels)
    hard = torch.zeros(len(coordinates), dtype=torch.bool)
    for kind, x, y in scene.hard_negatives:
        radius = v24._hard_negative_radius(scene, kind)
        if radius is not None and len(coordinates):
            point = torch.tensor(((x, y),), dtype=torch.float32)
            hard |= torch.cdist(coordinates, point).squeeze(1).le(radius)
    return (
        proposals.patches, labels, offsets, target_radii, hard,
    ), nearest_index.numpy(), nearest_distance.numpy()


def _validate_regenerated_rows(
    scope: str,
    scene_index: int,
    coordinates: torch.Tensor,
    nearest_index: np.ndarray,
    nearest_distance: np.ndarray,
    coverage: Mapping[str, np.ndarray],
    rows: slice,
) -> None:
    prefix = f"{scope}_"
    count = len(coordinates)
    checks = (
        np.array_equal(coverage[prefix + "scene_index"][rows], np.full(count, scene_index, np.int32)),
        np.array_equal(coverage[prefix + "proposal_index"][rows], np.arange(count, dtype=np.int32)),
        np.array_equal(coverage[prefix + "coordinates"][rows], coordinates.numpy()),
        np.array_equal(coverage[prefix + "nearest_truth_index"][rows], nearest_index.astype(np.int32)),
        np.array_equal(coverage[prefix + "nearest_truth_distance_px"][rows], nearest_distance),
    )
    if not all(checks):
        raise V26TrainingError(f"{scope} regenerated proposal identity differs from frozen cache")


def _validate_rebuilt_counts(
    component: tuple[torch.Tensor, ...], family: tuple[torch.Tensor, ...]
) -> None:
    labels = torch.cat((component[1], family[1]))
    hard = torch.cat((component[4], family[4]))
    positive = int((labels > 0.5).sum())
    if (
        len(labels) != EXPECTED_DATA["training_example_count"]
        or positive != EXPECTED_DATA["positive_example_count"]
        or len(labels) - positive != EXPECTED_DATA["negative_example_count"]
        or int((hard & (labels <= 0.5)).sum()) != EXPECTED_DATA["hard_negative_example_count"]
    ):
        raise V26TrainingError("V26 rebuilt training row counts changed")


def run(
    output_dir: Path,
    checkpoint: Path,
    v21_onnx: Path,
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: Dependencies | None = None,
) -> dict[str, Any]:
    """Train and evaluate the single authorized train/dev V26 candidate."""

    root = repository_root.resolve()
    output = output_dir if output_dir.is_absolute() else root / output_dir
    output = output.resolve()
    _require_inside(root, output, "output")
    if output.exists():
        raise V26TrainingError(f"V26 output already exists: {output}")
    prepared = prepare_training(config_path, repository_root=root, dependencies=dependencies)
    config = prepared.config
    current_config, current_config_sha = _load_config(root / prepared.config_path, root)
    if (current_config_sha != prepared.config_sha256 or current_config != config
            or source_bundle_sha256(root, RUNNER_SOURCE_PATHS)
            != config["expected_runner_source_bundle_sha256"]):
        raise V26TrainingError("V26 inputs changed during model-free preparation")
    base_config, _ = _read_json(root / BASE_V25_CONFIG_PATH, "base V25 config")
    if sha256_file(checkpoint) != base_config["checkpoint_sha256"]:
        raise V26TrainingError("V21 checkpoint identity changed")
    if sha256_file(v21_onnx) != base_config["v21_onnx_sha256"]:
        raise V26TrainingError("V21 ONNX identity changed")
    deps = dependencies or _default_dependencies()
    authorization = deps.acquire(
        root, task=TASK, revision=REVISION, candidate_id=CANDIDATE_ID,
        config_path=prepared.config_path, runner_source_paths=RUNNER_SOURCE_PATHS,
    )
    started = time.perf_counter()
    phase = "authorization"
    steps = 0
    report_path = output / REPORT_NAME
    try:
        if (
            authorization.binding.get("candidate_config_path")
            != prepared.config_path.as_posix()
            or authorization.binding.get("candidate_config_sha256")
            != prepared.config_sha256
            or authorization.snapshot_path is None
        ):
            raise V26TrainingError("Training authorization captured different V26 inputs")
        deps.verify_snapshot(
            root, authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"),
        )
        output.mkdir(parents=True)
        phase = "initialization"
        v24._configure(RECIPE["seed"])
        patches = torch.cat((prepared.component_values[0], prepared.family_values[0]))
        labels = torch.cat((prepared.component_values[1], prepared.family_values[1]))
        offsets = torch.cat((prepared.component_values[2], prepared.family_values[2]))
        radii = torch.cat((prepared.component_values[3], prepared.family_values[3]))
        hard = torch.cat((prepared.component_values[4], prepared.family_values[4]))
        generator = torch.Generator()
        generator.set_state(prepared.training_generator_state)
        payload = torch.load(checkpoint, map_location="cpu", weights_only=False)
        model = v25.ScaleClassifierNet(v25.ModelConfig(seed=20260902))
        model.load_state_dict(payload["state_dict"])
        optimizer = torch.optim.AdamW(
            model.parameters(), lr=RECIPE["learning_rate"], weight_decay=RECIPE["weight_decay"]
        )
        deps.verify_snapshot(
            root, authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"),
        )
        phase = "training"
        model.train()
        for _ in range(RECIPE["epochs"]):
            order = torch.randperm(len(labels), generator=generator)
            for start in range(0, len(labels), RECIPE["batch_size"]):
                index = order[start:start + RECIPE["batch_size"]]
                loss = v25.v21_loss(
                    model.forward_raw(patches[index]), labels[index], offsets[index], radii[index],
                    hard[index], positive_weight=RECIPE["positive_loss_weight"],
                    hard_weight=RECIPE["hard_negative_loss_weight"],
                    alpha=RECIPE["focal_alpha"], gamma=RECIPE["focal_gamma"],
                )
                optimizer.zero_grad(set_to_none=True)
                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
                optimizer.step()
                steps += 1
        if steps != EXPECTED_DATA["optimizer_steps"]:
            raise V26TrainingError("V26 optimizer step count changed")
        phase = "dev"
        model.eval()
        thresholds = [RECIPE["confidence_threshold"], *RECIPE["selection_thresholds"]]
        component_dev = [v24._evaluate(prepared.base.component_dev, model, value) for value in thresholds]
        family_dev = [v25._evaluate_domain(prepared.base.family_dev, model, value) for value in thresholds]
        bar = v24._shared_marker_acceptance_bar()
        dev_passed = v24._passes_required_dev_gates(component_dev[0], family_dev[0], bar)
        phase = "export"
        out_pt = output / CHECKPOINT_NAME
        out_onnx = output / ONNX_NAME
        torch.save({"state_dict": model.state_dict(), "config": model.export_contract()}, out_pt)
        torch.onnx.export(
            model, torch.zeros((1, 3, 33, 33)), out_onnx,
            input_names=["candidate_patches"], output_names=["candidate_predictions"],
            dynamic_axes={"candidate_patches": {0: "candidate_count"},
                          "candidate_predictions": {0: "candidate_count"}},
            opset_version=18, dynamo=False,
        )
        v25.onnx.checker.check_model(v25.onnx.load(out_onnx))
        session = v25.ort.InferenceSession(str(out_onnx), providers=[RECIPE["provider"]])
        parity_source = v24.extract_proposals(prepared.base.component_dev[0].tensor).patches
        parity = []
        for count in RECIPE["onnx_dynamic_candidate_counts"]:
            value = parity_source[:count].contiguous()
            expected = model(value).detach().numpy()
            actual = session.run(["candidate_predictions"], {"candidate_patches": value.numpy()})[0]
            parity.append({"candidate_count": count,
                           "maximum_absolute_error": float(np.max(np.abs(expected - actual)))})
        parity_max = max(value["maximum_absolute_error"] for value in parity)
        report = {
            "schema": REPORT_SCHEMA, "task": TASK, "revision": REVISION,
            "candidate_id": CANDIDATE_ID, "status": "dev_passed" if (
                dev_passed and parity_max <= RECIPE["onnx_parity_tolerance"]
            ) else "failed_dev",
            "candidate_config_path": prepared.config_path.as_posix(),
            "candidate_config_sha256": prepared.config_sha256,
            "base_v25_config": dict(config["base_v25_config"]),
            "base_v25_result": dict(config["base_v25_result"]),
            "annulus_preflight": dict(config["annulus_preflight"]),
            "annulus_selection_cache": dict(config["annulus_selection_cache"]),
            "selection_evidence": prepared.selection_evidence,
            "optimizer_steps": steps, "component_dev_comparisons": component_dev,
            "family_dev_comparisons": family_dev, "acceptance_bar": bar,
            "dev_gate_passed": dev_passed, "checkpoint_sha256": sha256_file(out_pt),
            "onnx_sha256": sha256_file(out_onnx),
            "v21_checkpoint_sha256": sha256_file(checkpoint),
            "v21_onnx_sha256": sha256_file(v21_onnx), "onnx_provider": RECIPE["provider"],
            "onnx_dynamic_candidate_counts": parity,
            "onnx_parity_maximum_absolute_error": parity_max,
            "elapsed_ms": round((time.perf_counter() - started) * 1000, 3),
            "synthetic_only": True, "private_data": False, "sealed_data": False,
            "sealed_runs": 0, "production_approval": False, "release_eligible": False,
            "training_authorization": authorization.binding,
        }
        _write_json(report_path, report)
        deps.complete(authorization, status=report["status"], report_sha256=sha256_file(report_path))
        return report
    except Exception as error:
        failure = {
            "schema": FAILURE_SCHEMA, "task": TASK, "revision": REVISION,
            "candidate_id": CANDIDATE_ID, "status": "failed_runner", "phase": phase,
            "optimizer_steps": steps, "optimizer_steps_known": True,
            "exception_type": type(error).__name__, "exception_message": str(error),
            "synthetic_only": True, "private_data": False, "sealed_data": False,
            "sealed_runs": 0, "production_approval": False,
            "training_authorization": authorization.binding,
        }
        try:
            if output.is_dir() and not report_path.exists():
                _write_json(report_path, failure)
        finally:
            deps.void(authorization, error)
        raise


def _load_report_and_cache(
    root: Path, report_descriptor: Any, cache_descriptor: Any,
    report_path: Path, report_sha: str, cache_path: Path, cache_sha: str, label: str,
) -> tuple[dict[str, Any], dict[str, np.ndarray]]:
    report_resolved = _exact_descriptor(root, report_descriptor, report_path, report_sha, label)
    cache_resolved = _exact_descriptor(root, cache_descriptor, cache_path, cache_sha, f"{label} cache")
    report, _ = _read_json(report_resolved, label)
    with np.load(cache_resolved, allow_pickle=False) as loaded:
        arrays = {name: loaded[name] for name in loaded.files}
    expected_names = (
        {f"{scope}_{name}" for scope in ("component", "family") for name in (
            "scene_index", "proposal_index", "coordinates", "nearest_truth_index",
            "nearest_truth_distance_px", "nearest_truth_radius_px", "selected",
            "stratum_id", "retention_role_id",
        )} if label == "negative coverage" else
        {f"{scope}_{name}" for scope in ("component", "family") for name in (
            "proposed_selected", "reserved", "added", "displaced",
        )}
    )
    if set(arrays) != expected_names:
        raise V26TrainingError(f"{label} cache arrays changed")
    return report, arrays


def _validate_evidence_links(
    root: Path, base: Any, coverage: Mapping[str, Any], annulus: Mapping[str, Any]
) -> None:
    coverage_inputs = coverage.get("inputs", {})
    annulus_inputs = annulus.get("inputs", {})
    if (
        coverage.get("status") != "model_free_complete"
        or coverage.get("model_inference_runs") != 0
        or coverage.get("optimizer_steps_run") != 0
        or coverage_inputs.get("v25_config_sha256") != BASE_V25_CONFIG_SHA256
        or coverage_inputs.get("v25_result_sha256") != BASE_V25_RESULT_SHA256
        or coverage_inputs.get("measurement_source_sha256")
        != sha256_file(root / ROOT / "measure_negative_coverage.py")
        or coverage_inputs.get("component_selected_index_sha256")
        != base.component_values[5].selected_index_sha256
        or coverage_inputs.get("family_selected_index_sha256")
        != base.family_values[5].selected_index_sha256
        or annulus.get("status") != "model_free_preflight_only"
        or annulus.get("dev_truth_used_for_selection") is not False
        or annulus.get("optimizer_steps_run") != 0
        or annulus_inputs.get("coverage_report_sha256") != COVERAGE_REPORT_SHA256
        or annulus_inputs.get("coverage_cache_sha256") != COVERAGE_CACHE_SHA256
        or annulus_inputs.get("source_sha256")
        != sha256_file(root / ROOT / "annulus_reservation_preflight.py")
    ):
        raise V26TrainingError("V26 frozen sampler evidence changed")
    if any(value.get("private_reads") != 0 or value.get("sealed_runs") != 0
           or value.get("production_approval") is not False for value in (coverage, annulus)):
        raise V26TrainingError("V26 sampler evidence escaped train-only scope")


def _validate_selection_evidence(
    observed: Mapping[str, Any], expected: Any, scope: str
) -> None:
    if not isinstance(expected, dict):
        raise V26TrainingError(f"{scope} annulus evidence is missing")
    required = {
        "frozen_selection_sha256": expected.get("frozen_selection_sha256"),
        "proposed_selection_sha256": expected.get("proposed_selection_sha256"),
        "selected_rows": expected.get("selected_positive_row_count", 0)
        + expected.get("selected_negative_count_after", 0),
        "preserved_protected_rows": expected.get("protected_selected_negative_count"),
        "added_rows": expected.get("added_reserved_row_count"),
        "displaced_rows": expected.get("displaced_row_count"),
        "row_order": "scene_then_ascending_proposal_index",
    }
    if observed != required:
        raise V26TrainingError(f"{scope} applied selection differs from annulus preflight")


def _validate_base_result(root: Path, descriptor: Any) -> None:
    path = _exact_descriptor(root, descriptor, BASE_V25_RESULT_PATH, BASE_V25_RESULT_SHA256,
                             "base V25 result")
    result, _ = _read_json(path, "base V25 result")
    if (result.get("task") != TASK or result.get("revision") != "marker-center-plot-domain-v25"
            or result.get("candidate_id") != "P1" or result.get("optimizer_steps") != 12636):
        raise V26TrainingError("Base V25 result identity changed")


def _load_config(path: Path, root: Path) -> tuple[dict[str, Any], str]:
    config, digest = _read_json(path, "V26 config")
    if set(config) != _CONFIG_KEYS:
        raise V26TrainingError("V26 configuration fields changed")
    fixed = {
        "schema": CONFIG_SCHEMA, "task": TASK, "revision": REVISION,
        "candidate_id": CANDIDATE_ID, "stage": "P1", "recipe": RECIPE,
        "expected_data": EXPECTED_DATA, "synthetic_only": True, "private_data": False,
        "sealed_data": False, "production_approval": False, "release_eligible": False,
    }
    if any(config.get(key) != value for key, value in fixed.items()):
        raise V26TrainingError("V26 configuration identity, recipe, or scope changed")
    expected = (
        ("base_v25_config", BASE_V25_CONFIG_PATH, BASE_V25_CONFIG_SHA256),
        ("base_v25_result", BASE_V25_RESULT_PATH, BASE_V25_RESULT_SHA256),
        ("negative_coverage_report", COVERAGE_REPORT_PATH, COVERAGE_REPORT_SHA256),
        ("negative_coverage_cache", COVERAGE_CACHE_PATH, COVERAGE_CACHE_SHA256),
        ("annulus_preflight", ANNULUS_REPORT_PATH, ANNULUS_REPORT_SHA256),
        ("annulus_selection_cache", ANNULUS_CACHE_PATH, ANNULUS_CACHE_SHA256),
    )
    for key, expected_path, expected_sha in expected:
        _exact_descriptor(root, config[key], expected_path, expected_sha, key)
    _sha_text(config.get("expected_runner_source_bundle_sha256"), "runner source bundle")
    return config, digest


def _exact_descriptor(root: Path, value: Any, expected_path: Path,
                      expected_sha: str, label: str) -> Path:
    path = _descriptor_path(root, value, label)
    digest = _sha_text(value.get("sha256") if isinstance(value, dict) else None, label)
    if path != (root / expected_path).resolve() or digest != expected_sha:
        raise V26TrainingError(f"{label} differs from its frozen identity")
    if sha256_file(path) != digest:
        raise V26TrainingError(f"{label} bytes changed")
    return path


def _descriptor_path(root: Path, value: Any, label: str) -> Path:
    if not isinstance(value, dict) or set(value) != {"path", "sha256"}:
        raise V26TrainingError(f"{label} descriptor changed")
    raw = value.get("path")
    if not isinstance(raw, str) or not raw or Path(raw).is_absolute():
        raise V26TrainingError(f"{label} path is unsafe")
    path = (root / raw).resolve()
    _require_inside(root, path, label)
    return path


def _read_json(path: Path, label: str) -> tuple[dict[str, Any], str]:
    try:
        payload = path.read_bytes()
        value = json.loads(payload)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise V26TrainingError(f"Could not read {label}") from error
    if not isinstance(value, dict):
        raise V26TrainingError(f"{label} must be a JSON object")
    return value, hashlib.sha256(payload).hexdigest()


def _relative(root: Path, path: Path, label: str) -> Path:
    resolved = (path if path.is_absolute() else root / path).resolve()
    _require_inside(root, resolved, label)
    return resolved.relative_to(root)


def _require_inside(root: Path, path: Path, label: str) -> None:
    if path == root or root not in path.parents:
        raise V26TrainingError(f"{label} escaped the repository")


def _sha_text(value: Any, label: str) -> str:
    if (not isinstance(value, str) or len(value) != 64 or value != value.lower()
            or any(character not in "0123456789abcdef" for character in value)):
        raise V26TrainingError(f"{label} SHA-256 is invalid")
    return value


def _write_json(path: Path, value: Mapping[str, Any]) -> None:
    with path.open("xb") as stream:
        stream.write(canonical_json_bytes(dict(value)))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--onnx", type=Path, required=True)
    parser.add_argument("--config", type=Path, default=CONFIG_PATH)
    args = parser.parse_args()
    report = run(args.output_dir, args.checkpoint, args.onnx, args.config)
    print(json.dumps({"status": report["status"], "optimizer_steps": report["optimizer_steps"]},
                     sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
