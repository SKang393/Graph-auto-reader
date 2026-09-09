# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""V25 plot-domain P1 runner with a model-free preparation boundary."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import time
from typing import Any, Callable, Sequence

import numpy as np
import onnx
import onnxruntime as ort
import torch

from ml.markers.center.focal_confidence_v21.focal_loss import v21_loss
from ml.markers.center.metrics import center_metrics
from ml.markers.center.real_range_generator_v1.generator import build_split
from ml.markers.center.scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from ml.markers.center.mask_preserving_v24 import train_p1 as v24
from ml.markers.center.mask_preserving_v24.mask_preserving import prohibited_hits
from ml.markers.center.mask_preserving_v24.stratified_background import (
    select_stratified_background,
)
from ml.markers.gate_seal import canonical_json_bytes, sha256_file
from ml.markers.training_budget import (
    acquire_training_candidate,
    complete_training_candidate,
    void_candidate,
)
from . import protocol, runtime_domain_binding_v3
from .proposal_domain import extract_proposals_in_domain, postprocess_in_domain


REPO_ROOT = Path(__file__).resolve().parents[4]
CONFIG_PATH = Path("ml/markers/center/plot_domain_v25/training/p1.json")
PREPARATION_SCHEMA = "graphreader.marker-center-plot-domain-v25-preparation.v1"
CONFIG_SCHEMA = "graphreader.marker-center-plot-domain-v25-candidate-config.v1"
FAMILY_GENERIC_NEGATIVE_SEED = 20260909

_V25_SOURCE_PATHS = (
    Path("ml/markers/center/plot_domain_v25/train_p1.py"),
    Path("ml/markers/center/plot_domain_v25/protocol.py"),
    protocol.DEV_PROTOCOL_PATH,
    Path("ml/markers/center/plot_domain_v25/proposal_domain.py"),
    Path("ml/markers/center/plot_domain_v25/runtime_domain_binding.py"),
    Path("ml/markers/center/plot_domain_v25/runtime_domain_binding_v3.py"),
)
RUNNER_SOURCE_PATHS = tuple(dict.fromkeys(
    (*_V25_SOURCE_PATHS, *v24.RUNNER_SOURCE_PATHS,
     *runtime_domain_binding_v3.GENERATOR_SOURCE_PATHS)
))

_LOCKED_RECIPE_KEYS = (
    "seed", "epochs", "batch_size", "learning_rate", "weight_decay",
    "positive_loss_weight", "hard_negative_loss_weight",
    "maximum_negative_per_positive", "label_positive_distance_px",
    "confidence_threshold", "selection_thresholds", "provider",
    "onnx_dynamic_candidate_counts", "onnx_parity_tolerance", "architecture",
    "classification_loss", "focal_alpha", "focal_gamma", "model_license",
    "checkpoint_sha256", "v21_onnx_sha256", "negative_sampler", "anti_aliasing",
    "real_range_training_example_count_expected",
    "real_range_positive_example_count_expected",
    "real_range_hard_negative_example_count_expected",
    "train_split", "train_split_sha256", "dev_split", "dev_split_sha256",
)


@dataclass(frozen=True)
class PreparationDependencies:
    load_binding: Callable[[Path, str, Path], Any]
    join_truth: Callable[[Any, Path], Any]
    build_component_split: Callable[[str, bool], Sequence[Any]]
    component_audit: Callable[[], dict[str, Any]]
    component_examples: Callable[[Sequence[Any], int, torch.Generator], tuple[Any, ...]]
    family_examples: Callable[[Sequence[Any], int, torch.Generator], tuple[Any, ...]]
    domain_support: Callable[[Sequence[Any]], dict[str, int]]
    panel_tensor_hash: Callable[[Sequence[Any]], str]


@dataclass(frozen=True)
class _Prepared:
    report: dict[str, Any]
    component_train: tuple[Any, ...]
    component_dev: tuple[Any, ...]
    family_train: tuple[Any, ...]
    family_dev: tuple[Any, ...]
    component_values: tuple[Any, ...]
    family_values: tuple[Any, ...]
    training_generator_state: torch.Tensor


def _default_dependencies() -> PreparationDependencies:
    return PreparationDependencies(
        load_binding=lambda path, digest, root: runtime_domain_binding_v3.load_runtime_domain_binding_v3(
            path, digest, repository_root=root
        ),
        join_truth=lambda binding, root: runtime_domain_binding_v3.join_runtime_domain_truth_v3(
            binding, repository_root=root
        ),
        build_component_split=lambda split, independent: build_split(
            split, independent_layout=independent
        ) if split == "dev" else build_split(split),
        component_audit=lambda: v24.generator_audit(independent_layout=True),
        component_examples=lambda scenes, maximum, generator: v24._examples_with_report(
            scenes, maximum, generator, sampling_mode="real-range-train"
        ),
        family_examples=_family_examples,
        domain_support=_domain_support,
        panel_tensor_hash=lambda panels: runtime_domain_binding_v3.panel_tensor_multiset_sha256(
            tuple(panel.runtime_input for panel in panels)
        ),
    )


def _sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _read_json(path: Path, label: str) -> tuple[dict[str, Any], str]:
    payload = path.read_bytes()
    try:
        value = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{label} is not valid JSON") from error
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be a JSON object")
    return value, hashlib.sha256(payload).hexdigest()


def _component_recipe(root: Path) -> dict[str, Any]:
    path = root / protocol.COMPONENT_CONFIG_PATH
    recipe, digest = _read_json(path, "V24 component recipe")
    if digest != protocol.COMPONENT_CONFIG_SHA256:
        raise RuntimeError("V24 retry13 component recipe identity changed")
    return recipe


def _validate_protocol_inputs(root: Path) -> None:
    if _sha(root / protocol.DEV_PROTOCOL_PATH) != protocol.DEV_PROTOCOL_SHA256:
        raise RuntimeError("V25 development protocol identity changed")
    if _sha(root / protocol.FAMILY_BINDING_PATH) != protocol.FAMILY_BINDING_SHA256:
        raise RuntimeError("V25 V3 family binding identity changed")


def _truth_count(scenes: Sequence[Any]) -> int:
    return sum(len(scene.centers) for scene in scenes)


def _family_examples(
    bound_scenes: Sequence[Any],
    maximum_negative_per_positive: int,
    generator: torch.Generator,
) -> tuple[Any, ...]:
    if maximum_negative_per_positive != 10 or not bound_scenes:
        raise ValueError("V25 family sampling requires nonempty scenes and ratio 10")
    prepared = []
    eligible_by_scene = []
    generic_budget = 0
    support_truth_count = 0
    ink_supported = 0
    omitted_by_domain = 0
    for bound in bound_scenes:
        scene = bound.scene
        batch = extract_proposals_in_domain(scene.tensor, bound.panel_domain.domain)
        proposals = batch.proposals
        centers = torch.tensor(scene.centers, dtype=torch.float32).reshape(-1, 2)
        radii = torch.tensor(scene.diameters, dtype=torch.float32) / 2.0
        if len(centers) and len(proposals.coordinates):
            distance = torch.cdist(proposals.coordinates, centers)
            nearest, nearest_index = distance.min(dim=1)
            labels = nearest.le(3.0).float()
            offsets = (centers[nearest_index] - proposals.coordinates) / 4.0
            target_radii = radii[nearest_index]
            support_truth_count += int(distance.min(dim=0).values.le(5.0).sum())
        else:
            labels = torch.zeros(len(proposals.coordinates), dtype=torch.float32)
            offsets = torch.zeros_like(proposals.coordinates)
            target_radii = torch.zeros_like(labels)
        hard = torch.zeros(len(proposals.coordinates), dtype=torch.bool)
        for kind, x, y in scene.hard_negatives:
            radius = v24._hard_negative_radius(scene, kind)
            if radius is not None and len(proposals.coordinates):
                point = torch.tensor(((x, y),), dtype=torch.float32)
                hard |= torch.cdist(proposals.coordinates, point).squeeze(1).le(radius)
        positive = torch.nonzero(labels > 0.5).flatten()
        hard_negative = torch.nonzero(hard & (labels <= 0.5)).flatten()
        negative_budget = len(positive) * maximum_negative_per_positive
        if len(hard_negative) > negative_budget:
            hard_negative = hard_negative[
                torch.randperm(len(hard_negative), generator=generator)[:negative_budget]
            ]
        generic_budget += max(0, negative_budget - len(hard_negative))
        eligible_by_scene.append(torch.nonzero((labels <= 0.5) & ~hard).flatten())
        prepared.append((proposals, labels, offsets, target_radii, hard, positive, hard_negative))
        ink_supported += batch.ink_supported_count
        omitted_by_domain += batch.omitted_by_domain_count

    background = select_stratified_background(
        tuple(item[0].patches for item in prepared),
        tuple(eligible_by_scene),
        generic_budget,
        FAMILY_GENERIC_NEGATIVE_SEED,
    )
    values: list[list[torch.Tensor]] = [[] for _ in range(5)]
    selections = []
    coordinates = []
    for item, generic_indices in zip(prepared, background.selections, strict=True):
        proposals, labels, offsets, target_radii, hard, positive, hard_negative = item
        generic = torch.tensor(generic_indices, dtype=torch.int64)
        selected = torch.cat((positive, hard_negative, generic)).unique(sorted=True)
        selections.append(tuple(int(index) for index in selected.tolist()))
        coordinates.append(proposals.coordinates)
        for destination, source in zip(
            values, (proposals.patches, labels, offsets, target_radii, hard), strict=True
        ):
            destination.append(source[selected])
    tensors = tuple(torch.cat(parts) for parts in values)
    labels, hard = tensors[1], tensors[4]
    selected_count = len(labels)
    positive_count = int((labels > 0.5).sum())
    hard_count = int((hard & (labels <= 0.5)).sum())
    sampling = v24.StratifiedFamilySamplingReport(
        tuple(selections),
        {"selected": selected_count},
        {
            "positive": positive_count,
            "hard_negative": hard_count,
            "other_negative": selected_count - positive_count - hard_count,
        },
        v24._selected_index_sha256(
            tuple(bound.scene for bound in bound_scenes), selections, coordinates
        ),
        {str(index): value for index, value in enumerate(background.capacities)},
        {str(index): value for index, value in enumerate(background.quotas)},
    )
    domain = {
        "ink_supported_proposals": ink_supported,
        "omitted_by_domain": omitted_by_domain,
        "truths_supported_within_5px": support_truth_count,
        "truth_count": _truth_count(tuple(bound.scene for bound in bound_scenes)),
    }
    return (*tensors, sampling, domain)


def _prepare(
    repository_root: Path,
    dependencies: PreparationDependencies,
) -> _Prepared:
    root = repository_root.resolve()
    _validate_protocol_inputs(root)
    recipe = _component_recipe(root)
    v24._configure(int(recipe["seed"]))
    binding = dependencies.load_binding(
        root / protocol.FAMILY_BINDING_PATH,
        protocol.FAMILY_BINDING_SHA256,
        root,
    )
    joined = dependencies.join_truth(binding, root)
    v24._configure(int(recipe["seed"]))
    audit = dependencies.component_audit()
    layout_audit = audit.get("layout_family_audit", {})
    if (
        layout_audit.get("independent_layout_required") is not True
        or layout_audit.get("train_dev_family_disjoint") is not True
        or layout_audit.get("train_dev_layout_disjoint") is not True
    ):
        raise RuntimeError("V24 component split audit changed")
    component_train = tuple(dependencies.build_component_split("train", False))
    component_dev = tuple(dependencies.build_component_split("dev", True))
    family_train = tuple(joined.train)
    family_dev = tuple(joined.dev)
    checks = {
        "component_train_scenes": len(component_train),
        "component_train_truths": _truth_count(component_train),
        "component_dev_scenes": len(component_dev),
        "component_dev_truths": _truth_count(component_dev),
        "family_train_panels": len(family_train),
        "family_train_sources": len({item.scene.source_sha256 for item in family_train}),
        "family_train_truths": _truth_count(tuple(item.scene for item in family_train)),
        "family_dev_panels": len(family_dev),
        "family_dev_sources": len({item.scene.source_sha256 for item in family_dev}),
        "family_dev_truths": _truth_count(tuple(item.scene for item in family_dev)),
    }
    expected = {
        "component_train_scenes": protocol.COMPONENT_TRAIN_SCENES,
        "component_train_truths": protocol.COMPONENT_TRAIN_TRUTHS,
        "component_dev_scenes": protocol.COMPONENT_DEV_SCENES,
        "component_dev_truths": protocol.COMPONENT_DEV_TRUTHS,
        "family_train_panels": protocol.FAMILY_TRAIN_PANELS,
        "family_train_sources": protocol.FAMILY_TRAIN_SOURCES,
        "family_train_truths": protocol.FAMILY_TRAIN_TRUTHS,
        "family_dev_panels": protocol.FAMILY_DEV_PANELS,
        "family_dev_sources": protocol.FAMILY_DEV_SOURCES,
        "family_dev_truths": protocol.FAMILY_DEV_TRUTHS,
    }
    if checks != expected:
        raise RuntimeError("V25 complete split counts changed")
    if (
        binding.panel_inventory_sha256 != protocol.FAMILY_PANEL_INVENTORY_SHA256
        or binding.expected_mapping_audit_sha256 != protocol.FAMILY_MAPPING_AUDIT_SHA256
        or binding.expected_truth_mapping_sha256 != protocol.FAMILY_TRUTH_MAPPING_SHA256
    ):
        raise RuntimeError("V25 binding aggregate identities changed")
    if (
        dependencies.panel_tensor_hash(binding.train)
        != protocol.FAMILY_TRAIN_TENSOR_SHA256
        or dependencies.panel_tensor_hash(binding.dev)
        != protocol.FAMILY_DEV_TENSOR_SHA256
    ):
        raise RuntimeError("V25 family tensor identities changed")

    generator = torch.Generator().manual_seed(int(recipe["seed"]) + 1)
    component_values = dependencies.component_examples(
        component_train, int(recipe["maximum_negative_per_positive"]), generator
    )
    family_values = dependencies.family_examples(
        family_train, int(recipe["maximum_negative_per_positive"]), generator
    )
    training_generator_state = generator.get_state().clone()
    component_labels, component_hard, component_sampling = (
        component_values[1], component_values[4], component_values[5]
    )
    family_labels, family_hard, family_sampling, train_domain = (
        family_values[1], family_values[4], family_values[5], family_values[6]
    )
    dev_support = dependencies.domain_support(family_dev)
    sampler_config = recipe["negative_sampler"]
    if (
        len(component_labels) != recipe["real_range_training_example_count_expected"]
        or int((component_labels > 0.5).sum())
        != recipe["real_range_positive_example_count_expected"]
        or int(component_hard.sum())
        != recipe["real_range_hard_negative_example_count_expected"]
        or component_sampling.capacities != sampler_config["expected_capacities"]
        or component_sampling.counts != sampler_config["quotas"]
        or component_sampling.selected_index_sha256
        != sampler_config["selected_index_sha256"]
    ):
        raise RuntimeError("V24 component sampling identity changed")
    requirements = {
        "component_training_examples": len(component_labels),
        "component_positive_examples": int((component_labels > 0.5).sum()),
        "component_hard_negative_examples": int(component_hard.sum()),
        "component_selected_index_sha256": component_sampling.selected_index_sha256,
        "family_training_examples": len(family_labels),
        "family_positive_examples": int((family_labels > 0.5).sum()),
        "family_hard_negative_examples": int(family_hard.sum()),
        "family_selected_index_sha256": family_sampling.selected_index_sha256,
        "family_stratified_bin_capacities": family_sampling.stratified_bin_capacities,
        "family_stratified_bin_quotas": family_sampling.stratified_bin_quotas,
        "training_examples": len(component_labels) + len(family_labels),
        "positive_examples": int((component_labels > 0.5).sum()) + int((family_labels > 0.5).sum()),
        "hard_negative_examples": int(component_hard.sum()) + int(family_hard.sum()),
        "optimizer_steps": int(recipe["epochs"]) * math.ceil(
            (len(component_labels) + len(family_labels)) / int(recipe["batch_size"])
        ),
    }
    report = {
        "schema": PREPARATION_SCHEMA,
        "status": "prepared_unapproved",
        "task": protocol.TASK,
        "revision": protocol.TRAINING_REVISION,
        "candidate_id": protocol.TRAINING_CANDIDATE_ID,
        "component_recipe": {
            "path": protocol.COMPONENT_CONFIG_PATH.as_posix(),
            "sha256": protocol.COMPONENT_CONFIG_SHA256,
            "train_split_sha256": recipe["train_split_sha256"],
            "dev_split_sha256": recipe["dev_split_sha256"],
        },
        "family_binding": {
            "path": protocol.FAMILY_BINDING_PATH.as_posix(),
            "sha256": protocol.FAMILY_BINDING_SHA256,
            "panel_inventory_sha256": protocol.FAMILY_PANEL_INVENTORY_SHA256,
            "train_tensor_set_sha256": protocol.FAMILY_TRAIN_TENSOR_SHA256,
            "dev_tensor_set_sha256": protocol.FAMILY_DEV_TENSOR_SHA256,
            "mapping_audit_sha256": protocol.FAMILY_MAPPING_AUDIT_SHA256,
            "truth_mapping_sha256": protocol.FAMILY_TRUTH_MAPPING_SHA256,
        },
        "split_counts": checks,
        "family_train_domain": train_domain,
        "family_dev_domain": dev_support,
        "requirements": requirements,
        "sampling_used_dev_truth": False,
        "optimizer_steps_run": 0,
        "private_reads": 0,
        "sealed_runs": 0,
        "production_approval": False,
    }
    return _Prepared(
        report, component_train, component_dev, family_train, family_dev,
        component_values, family_values, training_generator_state,
    )


def _domain_support(bound_scenes: Sequence[Any]) -> dict[str, int]:
    truths = supported = proposals = omitted = 0
    for bound in bound_scenes:
        batch = extract_proposals_in_domain(bound.scene.tensor, bound.panel_domain.domain)
        centers = torch.tensor(bound.scene.centers, dtype=torch.float32).reshape(-1, 2)
        truths += len(centers)
        proposals += batch.ink_supported_count
        omitted += batch.omitted_by_domain_count
        if len(centers) and len(batch.proposals.coordinates):
            supported += int(
                torch.cdist(batch.proposals.coordinates, centers).min(dim=0).values.le(5).sum()
            )
    return {
        "ink_supported_proposals": proposals,
        "omitted_by_domain": omitted,
        "truths_supported_within_5px": supported,
        "truth_count": truths,
    }


def prepare_model_free(
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: PreparationDependencies | None = None,
) -> dict[str, Any]:
    """Authenticate and measure V25 inputs without authorization or model execution."""

    return _prepare(repository_root, dependencies or _default_dependencies()).report


def _validate_candidate_config(
    config_path: Path,
    root: Path,
    preparation: dict[str, Any],
) -> tuple[Path, str, dict[str, Any]]:
    relative = Path(config_path)
    if relative.is_absolute() or relative.drive or ".." in relative.parts:
        raise ValueError("V25 training config must be repository-relative")
    resolved = (root / relative).resolve()
    if root.resolve() not in resolved.parents:
        raise ValueError("V25 training config escapes the repository")
    config, digest = _read_json(resolved, "V25 training config")
    if (
        config.get("schema") != CONFIG_SCHEMA
        or config.get("task") != protocol.TASK
        or config.get("revision") != protocol.TRAINING_REVISION
        or config.get("candidate_id") != protocol.TRAINING_CANDIDATE_ID
        or config.get("private_data") is not False
        or config.get("real_dev_reads") != 0
        or config.get("real_sealed_reads") != 0
        or config.get("sealed_runs") != 0
        or config.get("public_gate_evaluations") != 0
        or config.get("candidate_budget") != 1
        or config.get("production_approval") is not False
        or config.get("release_eligible") is not False
        or config.get("provider") != protocol.PROVIDER
        or config.get("confidence_threshold") != protocol.CONFIDENCE_THRESHOLD
    ):
        raise ValueError("V25 candidate config violates fixed scope or operating point")
    recipe = _component_recipe(root)
    for key in _LOCKED_RECIPE_KEYS:
        if config.get(key) != recipe.get(key):
            raise ValueError(f"V25 candidate changed preserved V24 recipe field: {key}")
    exact = {
        "component_recipe_path": protocol.COMPONENT_CONFIG_PATH.as_posix(),
        "component_recipe_sha256": protocol.COMPONENT_CONFIG_SHA256,
        "runtime_domain_binding_path": protocol.FAMILY_BINDING_PATH.as_posix(),
        "runtime_domain_binding_sha256": protocol.FAMILY_BINDING_SHA256,
        "dev_protocol_path": protocol.DEV_PROTOCOL_PATH.as_posix(),
        "dev_protocol_sha256": protocol.DEV_PROTOCOL_SHA256,
    }
    if any(config.get(key) != value for key, value in exact.items()):
        raise ValueError("V25 candidate changed a fixed input identity")
    requirements = preparation["requirements"]
    expected_fields = {
        "family_training_example_count_expected": requirements["family_training_examples"],
        "family_positive_example_count_expected": requirements["family_positive_examples"],
        "family_hard_negative_example_count_expected": requirements["family_hard_negative_examples"],
        "family_selected_index_sha256": requirements["family_selected_index_sha256"],
        "family_stratified_bin_capacities_expected": requirements["family_stratified_bin_capacities"],
        "family_stratified_bin_quotas_expected": requirements["family_stratified_bin_quotas"],
        "training_example_count_expected": requirements["training_examples"],
        "positive_example_count_expected": requirements["positive_examples"],
        "hard_negative_example_count_expected": requirements["hard_negative_examples"],
        "optimizer_steps_expected": requirements["optimizer_steps"],
        "optimizer_steps_maximum": requirements["optimizer_steps"],
    }
    if any(config.get(key) != value for key, value in expected_fields.items()):
        raise RuntimeError("V25 candidate config differs from model-free preparation")
    preflight_relative = Path(str(config.get("model_free_preflight_path", "")))
    if (
        preflight_relative == Path(".")
        or preflight_relative.is_absolute()
        or preflight_relative.drive
        or ".." in preflight_relative.parts
    ):
        raise ValueError("V25 model-free preflight path is unsafe")
    preflight_path = (root / preflight_relative).resolve()
    if root.resolve() not in preflight_path.parents or not preflight_path.is_file():
        raise ValueError("V25 model-free preflight must be a repository file")
    preflight, preflight_sha256 = _read_json(preflight_path, "V25 model-free preflight")
    if (
        preflight_sha256 != config.get("model_free_preflight_sha256")
        or preflight != preparation
    ):
        raise RuntimeError("V25 model-free preflight differs from current preparation")
    return Path(relative.as_posix()), digest, config


def _evaluate_domain(bound_scenes: Sequence[Any], model: Any, threshold: float) -> dict[str, Any]:
    tp = fp = fn = duplicate = hits = truth = proposal_tp = 0
    omitted = decoded_outside = 0
    for bound in bound_scenes:
        scene, domain = bound.scene, bound.panel_domain.domain
        batch = extract_proposals_in_domain(scene.tensor, domain)
        output = model(batch.proposals.patches).detach().numpy()
        processed = postprocess_in_domain(scene, batch.proposals, output, domain)
        predictions = tuple(item for item in processed.predictions if item.confidence >= threshold)
        truth += len(scene.centers)
        edges = sorted(
            (float(np.hypot(item.x - x, item.y - y)), i, j)
            for i, item in enumerate(predictions)
            for j, (x, y) in enumerate(scene.centers)
            if np.hypot(item.x - x, item.y - y) <= 5
        )
        used_predictions: set[int] = set()
        used_truth: set[int] = set()
        for _, i, j in edges:
            if i not in used_predictions and j not in used_truth:
                used_predictions.add(i)
                used_truth.add(j)
        supported = {
            j for x, y in batch.proposals.coordinates.tolist()
            for j, (truth_x, truth_y) in enumerate(scene.centers)
            if np.hypot(x - truth_x, y - truth_y) <= 5
        }
        metrics = center_metrics(predictions, scene.centers, 5.0)
        tp += len(used_predictions)
        fp += len(predictions) - len(used_predictions)
        fn += len(scene.centers) - len(used_truth)
        proposal_tp += len(supported)
        duplicate += metrics.duplicate_count
        hits += sum(prohibited_hits(predictions, scene).values())
        omitted += batch.omitted_by_domain_count
        decoded_outside += processed.decoded_outside_plot
    precision = tp / max(1, tp + fp)
    recall = tp / max(1, truth)
    return {
        "threshold": threshold, "scene_count": len(bound_scenes),
        "proposal_true_positives": proposal_tp,
        "proposal_recall": proposal_tp / max(1, truth),
        "true_positives": tp, "false_positives": fp, "false_negatives": fn,
        "precision": precision, "recall": recall,
        "f1": 2 * precision * recall / max(1e-12, precision + recall),
        "duplicate_count": duplicate, "prohibited_structure_hits": hits,
        "prohibited_structure_hit_rate": hits / max(1, tp + fp),
        "domain_omitted_ink_proposals": omitted,
        "decoded_outside_plot": decoded_outside,
    }


def run(
    output_dir: Path,
    checkpoint: Path,
    v21_onnx: Path,
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: PreparationDependencies | None = None,
) -> dict[str, Any]:
    """Run the authorized candidate after all model-free requirements match."""

    if output_dir.exists():
        raise RuntimeError(f"candidate output already exists: {output_dir}")
    root = repository_root.resolve()
    prepared = _prepare(root, dependencies or _default_dependencies())
    config_path, config_sha256, config = _validate_candidate_config(
        config_path, root, prepared.report
    )
    if _sha(checkpoint) != config["checkpoint_sha256"] or _sha(v21_onnx) != config["v21_onnx_sha256"]:
        raise ValueError("V21 initializer identity changed")
    authorization = acquire_training_candidate(
        root,
        task=protocol.TASK,
        revision=protocol.TRAINING_REVISION,
        candidate_id=protocol.TRAINING_CANDIDATE_ID,
        config_path=config_path,
        runner_source_paths=RUNNER_SOURCE_PATHS,
    )
    report_path = output_dir / "candidate-report.json"
    started = time.perf_counter()
    phase = "initialization"
    try:
        output_dir.mkdir(parents=True)
        v24._configure(int(config["seed"]))
        component = prepared.component_values
        family = prepared.family_values
        patches = torch.cat((component[0], family[0]))
        labels = torch.cat((component[1], family[1]))
        offsets = torch.cat((component[2], family[2]))
        radii = torch.cat((component[3], family[3]))
        hard = torch.cat((component[4], family[4]))
        generator = torch.Generator()
        generator.set_state(prepared.training_generator_state)
        payload = torch.load(checkpoint, map_location="cpu", weights_only=False)
        model = ScaleClassifierNet(ModelConfig(seed=20260902))
        model.load_state_dict(payload["state_dict"])
        optimizer = torch.optim.AdamW(
            model.parameters(), lr=config["learning_rate"], weight_decay=config["weight_decay"]
        )
        steps = 0
        phase = "training"
        model.train()
        for _ in range(int(config["epochs"])):
            order = torch.randperm(len(labels), generator=generator)
            for start in range(0, len(labels), int(config["batch_size"])):
                index = order[start:start + int(config["batch_size"])]
                loss = v21_loss(
                    model.forward_raw(patches[index]), labels[index], offsets[index],
                    radii[index], hard[index],
                    positive_weight=config["positive_loss_weight"],
                    hard_weight=config["hard_negative_loss_weight"],
                    alpha=config["focal_alpha"], gamma=config["focal_gamma"],
                )
                optimizer.zero_grad(set_to_none=True)
                loss.backward()
                torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
                optimizer.step()
                steps += 1
        if steps != config["optimizer_steps_expected"]:
            raise RuntimeError("V25 optimizer step contract changed")
        phase = "dev"
        model.eval()
        thresholds = [protocol.CONFIDENCE_THRESHOLD, *config["selection_thresholds"]]
        component_comparisons = [v24._evaluate(prepared.component_dev, model, value) for value in thresholds]
        family_comparisons = [_evaluate_domain(prepared.family_dev, model, value) for value in thresholds]
        bar = v24._shared_marker_acceptance_bar()
        dev_passed = v24._passes_required_dev_gates(
            component_comparisons[0], family_comparisons[0], bar
        )
        phase = "export"
        out_pt = output_dir / "marker-center-plot-domain-v25-p1.pt"
        out_onnx = output_dir / "marker-center-plot-domain-v25-p1.onnx"
        torch.save({"state_dict": model.state_dict(), "config": model.export_contract()}, out_pt)
        torch.onnx.export(
            model, torch.zeros((1, 3, 33, 33)), out_onnx,
            input_names=["candidate_patches"], output_names=["candidate_predictions"],
            dynamic_axes={"candidate_patches": {0: "candidate_count"}, "candidate_predictions": {0: "candidate_count"}},
            opset_version=18, dynamo=False,
        )
        onnx.checker.check_model(onnx.load(out_onnx))
        session = ort.InferenceSession(str(out_onnx), providers=[protocol.PROVIDER])
        if session.get_providers()[0] != protocol.PROVIDER:
            raise RuntimeError("CPUExecutionProvider was not selected")
        parity = []
        parity_source = v24.extract_proposals(prepared.component_dev[0].tensor).patches
        for count in config["onnx_dynamic_candidate_counts"]:
            value = parity_source[:count].contiguous()
            expected = model(value).detach().numpy()
            actual = session.run(["candidate_predictions"], {"candidate_patches": value.numpy()})[0]
            parity.append({
                "candidate_count": count,
                "maximum_absolute_error": float(np.max(np.abs(expected - actual))),
            })
        parity_maximum = max(item["maximum_absolute_error"] for item in parity)
        report = {
            "schema": "graphreader.marker-center-plot-domain-v25-candidate.v1",
            "task": protocol.TASK, "revision": protocol.TRAINING_REVISION,
            "candidate_id": protocol.TRAINING_CANDIDATE_ID,
            "candidate_config_path": config_path.as_posix(),
            "candidate_config_sha256": config_sha256,
            "status": "dev_passed" if dev_passed and parity_maximum <= config["onnx_parity_tolerance"] else "failed_dev",
            "preparation": prepared.report,
            "optimizer_steps": steps,
            "component_dev_comparisons": component_comparisons,
            "family_dev_comparisons": family_comparisons,
            "acceptance_bar": bar,
            "dev_gate_passed": dev_passed,
            "checkpoint_sha256": _sha(out_pt), "onnx_sha256": _sha(out_onnx),
            "v21_checkpoint_sha256": config["checkpoint_sha256"],
            "v21_onnx_sha256": config["v21_onnx_sha256"],
            "onnx_provider": protocol.PROVIDER,
            "onnx_dynamic_candidate_counts": parity,
            "onnx_parity_maximum_absolute_error": parity_maximum,
            "elapsed_ms": round((time.perf_counter() - started) * 1000, 3),
            "synthetic_only": True, "private_data": False, "real_dev_reads": 0,
            "real_sealed_reads": 0, "sealed_runs": 0,
            "production_approval": False, "release_eligible": False,
        }
    except Exception as error:
        report = {
            "schema": "graphreader.marker-center-plot-domain-v25-failure.v1",
            "task": protocol.TASK, "revision": protocol.TRAINING_REVISION,
            "candidate_id": protocol.TRAINING_CANDIDATE_ID,
            "candidate_config_path": config_path.as_posix(),
            "candidate_config_sha256": config_sha256,
            "status": "failed_runner", "phase": phase,
            "exception_type": type(error).__name__, "exception_message": str(error),
            "synthetic_only": True, "private_data": False,
            "real_dev_reads": 0, "real_sealed_reads": 0, "sealed_runs": 0,
        }
        try:
            if output_dir.is_dir():
                report_path.write_bytes(canonical_json_bytes(report))
        finally:
            void_candidate(authorization, error)
        raise
    report_path.write_bytes(canonical_json_bytes(report))
    complete_training_candidate(
        authorization, status=report["status"], report_sha256=sha256_file(report_path)
    )
    return report


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--checkpoint", type=Path)
    parser.add_argument("--onnx", type=Path)
    parser.add_argument("--config", type=Path, default=CONFIG_PATH)
    arguments = parser.parse_args()
    if arguments.prepare_only:
        result = prepare_model_free()
    else:
        if arguments.output_dir is None or arguments.checkpoint is None or arguments.onnx is None:
            parser.error("training requires --output-dir, --checkpoint, and --onnx")
        result = run(
            arguments.output_dir.resolve(), arguments.checkpoint.resolve(),
            arguments.onnx.resolve(), arguments.config,
        )
    print(json.dumps(result, indent=2, sort_keys=True))


if __name__ == "__main__":
    main()
