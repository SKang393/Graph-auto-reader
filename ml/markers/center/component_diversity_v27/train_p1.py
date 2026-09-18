# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Train-only V27 runner for the fixed component-diversity selection."""

from __future__ import annotations

import argparse
from dataclasses import dataclass, replace
import hashlib
import json
import math
import os
from pathlib import Path
import random
import shutil
import time
from typing import Any, Callable, Mapping, Sequence
from uuid import uuid4

import numpy as np
import torch

from ml.markers.center.localization_confidence_v26 import train_p1 as v26
from ml.markers.center.mask_preserving_v24 import train_p1 as v24
from ml.markers.center.plot_domain_v25 import train_p1 as v25
from ml.markers.gate_seal import (
    canonical_json_bytes,
    sha256_file,
    source_bundle_sha256,
    verify_bound_source_snapshot,
)
from ml.markers.training_budget import (
    TrainingAuthorization,
    acquire_training_candidate,
    complete_training_candidate,
    void_candidate,
)


REPO_ROOT = Path(__file__).resolve().parents[4]
ROOT = Path("ml/markers/center/component_diversity_v27")
CONFIG_PATH = ROOT / "training/p1.json"
PROTOCOL_PATH = ROOT / "protocol.json"
TASK = "marker-center"
REVISION = "marker-center-component-diversity-v27"
CANDIDATE_ID = "P1"
CONFIG_SCHEMA = "graphreader.marker-center-component-diversity-v27-candidate-config.v1"
REPORT_SCHEMA = "graphreader.marker-center-component-diversity-v27-candidate.v1"
FAILURE_SCHEMA = "graphreader.marker-center-component-diversity-v27-failure.v1"
RECOVERY_SCHEMA = "graphreader.marker-center-component-diversity-v27-recovery.v1"

V26_PROTOCOL_PATH = v26.PROTOCOL_PATH
V26_PROTOCOL_SHA256 = "410eec2b6936a08bab8bcba341aaf04c6e9ac661215169d74efb57c33358fb7a"
V26_CONFIG_PATH = v26.CONFIG_PATH
V26_CONFIG_SHA256 = "31f8fee2ecb4fc01b2c1234761423dcbb668e98c90157abd4e1179c6749236db"
V26_RESULT_PATH = Path("ml/markers/center/localization_confidence_v26/P1_RESULT.json")
V26_RESULT_SHA256 = "82f7906c71f6fa09f9850c92c5d373adc9f2611fe015d3562c3fdbcd6516f53e"
PREFLIGHT_PATH = Path(
    "artifacts/goal22-runs/marker-component-selection-coverage-preflight/v3/preflight.json"
)
PREFLIGHT_SHA256 = "8830c983cade4b3caaff1ced2b60274c932e8af8a7a27525ca82d96e676dff43"
SELECTION_CACHE_PATH = Path(
    "artifacts/goal22-runs/marker-component-selection-coverage-preflight/v3/"
    "proposed-selection-metadata.npz"
)
SELECTION_CACHE_SHA256 = "d31a25f48170a375fae22f196344d1c8fc85d2297ddc3394c8303d08d34a246b"
COMPONENT_SELECTION_SHA256 = "e2c52a38bbb9e53146ee48c8bc92e8b56bf599393297d5ba57b9c66699d29a7a"
FAMILY_SELECTION_SHA256 = "239b3f2aa14eb9636964c22083e722aba76bd70623a2bbd75362e1db96c527e3"
V25_COMPONENT_SELECTION_SHA256 = "e5a324090f1c021b7992a9d3e04ec583156fa2a2f593c22250917506972e80dd"
V26_COMPONENT_SELECTION_SHA256 = "c184a97ae7ad0292361e62a4aaed5a54d8c1ae1bdb0ab3daab9ddeb8d46b1af0"

GATE_SEAL_PATH = Path("ml/markers/gate_seal.py")
GATE_SEAL_HISTORICAL_SHA256 = "b310789162c06ca11cb348cbf2fcece877a25ce6173ec0dee70bdc29d33ef6d7"
GATE_SEAL_CURRENT_SHA256 = "0015612194e6c082cedd0b9d830dc427e94567302b39e5f3fe43dd090c0eb725"
RENDERER_PATH = Path("ml/synthetic/renderer.py")
RENDERER_HISTORICAL_SHA256 = "6ddba4f5392a078d6c4d0108ca3a48c31f8d3692cdd26fbdbd466849ae5bef1b"
RENDERER_CURRENT_SHA256 = "c49c070afc505f92bfff7ff55b22e669c3ec2b76b56ff807b72a026ca0535283"

CHECKPOINT_NAME = "marker-center-component-diversity-v27-p1.pt"
ONNX_NAME = "marker-center-component-diversity-v27-p1.onnx"
REPORT_NAME = "candidate-report.json"
RECOVERY_NAME = "recovery.pt"
PROGRESS_NAME = "progress.json"

RUNNER_SOURCE_PATHS = tuple(dict.fromkeys((
    *v26.RUNNER_SOURCE_PATHS,
    ROOT / "train_p1.py",
    PROTOCOL_PATH,
)))

RECIPE = {
    "sampler": "fixed_component_cell_stratified_v1",
    "component_selection_sha256": COMPONENT_SELECTION_SHA256,
    "family_selection_sha256": FAMILY_SELECTION_SHA256,
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
    "checkpoint_selection": "final_epoch",
    "initializer_checkpoint_sha256": "ba9722ebd3091c91749c175607a480500d3b651e6719d19b69c7e48cee4ef6c9",
    "initializer_onnx_sha256": "0b413db48f8e6707ee5ec99afff4cd8ec3d25c6b8a8d9f165bd416deb4578a38",
}
EXPECTED_DATA = {
    "component_train": {"scene_count": 167, "truth_count": 2004},
    "family_train": {"source_count": 20, "panel_count": 28, "truth_count": 500},
    "component_dev": {"scene_count": 167, "truth_count": 2004},
    "family_dev": {"source_count": 3, "panel_count": 9, "truth_count": 206},
    "component_selected": 35838,
    "family_selected": 9053,
    "training_example_count": 44891,
    "positive_example_count": 4081,
    "negative_example_count": 40810,
    "hard_negative_example_count": 8617,
    "component_replacements": 8050,
    "optimizer_steps": 12636,
}
EXECUTION = {
    "device": "cpu",
    "torch_intraop_threads": 12,
    "onnx_intraop_threads": 12,
    "onnx_interop_threads": 1,
    "atomic_recovery_interval": "each_completed_epoch",
}
_CONFIG_KEYS = {
    "schema", "task", "revision", "candidate_id", "stage",
    "expected_runner_source_bundle_sha256", "base_v26_protocol", "base_v26_config",
    "base_v26_result", "selection_preflight", "selection_cache", "recipe",
    "expected_data", "execution", "synthetic_only", "private_data", "sealed_data",
    "production_approval", "release_eligible",
}


class V27TrainingError(RuntimeError):
    """The V27 source, selection, recovery, or training contract changed."""


@dataclass(frozen=True)
class PreparedTraining:
    config: Mapping[str, Any]
    config_path: Path
    config_sha256: str
    source_bundle_sha256: str
    base: Any
    component_values: tuple[torch.Tensor, ...]
    family_values: tuple[torch.Tensor, ...]
    training_generator_state: torch.Tensor
    selection_evidence: Mapping[str, Any]
    training_tensor_inventory_sha256: str


@dataclass(frozen=True)
class Dependencies:
    acquire: Callable[..., TrainingAuthorization]
    complete: Callable[..., Path]
    void: Callable[[TrainingAuthorization, BaseException], Path]
    verify_snapshot: Callable[[Path, Path, object], None]


@dataclass(frozen=True)
class TrainingOutcome:
    completed_epochs: int
    optimizer_steps: int
    loss_history: tuple[dict[str, float | int], ...]
    resumed_from_epoch: int


def _default_dependencies() -> Dependencies:
    return Dependencies(
        acquire_training_candidate,
        complete_training_candidate,
        void_candidate,
        verify_bound_source_snapshot,
    )


def _prepare_base_with_authenticated_current_sources(root: Path) -> Any:
    """Run the frozen V25 preparation with two narrow reviewed source rebindings."""

    if sha256_file(root / GATE_SEAL_PATH) != GATE_SEAL_CURRENT_SHA256:
        raise V27TrainingError("Current canonical JSON source identity changed")
    if sha256_file(root / RENDERER_PATH) != RENDERER_CURRENT_SHA256:
        raise V27TrainingError("Current renderer source identity changed")
    binding_module = v25.runtime_domain_binding_v3
    helper_validator = getattr(binding_module, "_validate_helper_sources", None)
    profile_validator = getattr(binding_module, "_validate_profile_sources", None)
    if not callable(helper_validator) or not callable(profile_validator):
        raise V27TrainingError("Frozen V3 source validators are unavailable")

    def authenticated_helpers(document: Mapping[str, Any]) -> None:
        field = "canonical_json_source_sha256"
        if document.get(field) != GATE_SEAL_HISTORICAL_SHA256:
            raise V27TrainingError("Frozen canonical JSON source binding changed")
        rebound = dict(document)
        rebound[field] = GATE_SEAL_CURRENT_SHA256
        helper_validator(rebound)

    def authenticated_profile(profile: Any, repository_root: Path) -> None:
        matches = [
            (index, source)
            for index, source in enumerate(profile.sources)
            if source.relative_path == RENDERER_PATH
        ]
        if len(matches) != 1 or matches[0][1].sha256 != RENDERER_HISTORICAL_SHA256:
            raise V27TrainingError("Frozen renderer source binding changed")
        index, source = matches[0]
        sources = list(profile.sources)
        sources[index] = replace(source, sha256=RENDERER_CURRENT_SHA256)
        profile_validator(replace(profile, sources=tuple(sources)), repository_root)

    binding_module._validate_helper_sources = authenticated_helpers
    binding_module._validate_profile_sources = authenticated_profile
    try:
        base = v25._prepare(root, v25._default_dependencies())
    finally:
        binding_module._validate_helper_sources = helper_validator
        binding_module._validate_profile_sources = profile_validator
    _, base_config_sha, _ = v25._validate_candidate_config(
        v25.CONFIG_PATH, root, base.report
    )
    if base_config_sha != v26.BASE_V25_CONFIG_SHA256:
        raise V27TrainingError("Reconstructed V25 configuration changed")
    return base


def prepare_training(
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
) -> PreparedTraining:
    """Authenticate and reconstruct the exact V27 selected synthetic train rows."""

    root = repository_root.resolve()
    relative_config = _relative(root, config_path, "configuration")
    config, config_sha = _load_config(root / relative_config, root)
    bundle_sha = source_bundle_sha256(root, RUNNER_SOURCE_PATHS)
    if bundle_sha != config["expected_runner_source_bundle_sha256"]:
        raise V27TrainingError("V27 runner source bundle changed")
    _validate_recipe_compatibility()

    v26_config_path = _exact_descriptor(
        root, config["base_v26_config"], V26_CONFIG_PATH, V26_CONFIG_SHA256,
        "base V26 config",
    )
    v26_config, _ = _read_json(v26_config_path, "base V26 config")
    _exact_descriptor(
        root, config["base_v26_protocol"], V26_PROTOCOL_PATH, V26_PROTOCOL_SHA256,
        "base V26 protocol",
    )
    v26_result_path = _exact_descriptor(
        root, config["base_v26_result"], V26_RESULT_PATH, V26_RESULT_SHA256,
        "base V26 result",
    )
    _validate_v26_result(v26_result_path)
    v26._validate_base_result(root, v26_config["base_v25_result"])
    coverage_report, coverage = v26._load_report_and_cache(
        root,
        v26_config["negative_coverage_report"],
        v26_config["negative_coverage_cache"],
        v26.COVERAGE_REPORT_PATH,
        v26.COVERAGE_REPORT_SHA256,
        v26.COVERAGE_CACHE_PATH,
        v26.COVERAGE_CACHE_SHA256,
        "negative coverage",
    )
    annulus_report, annulus = v26._load_report_and_cache(
        root,
        v26_config["annulus_preflight"],
        v26_config["annulus_selection_cache"],
        v26.ANNULUS_REPORT_PATH,
        v26.ANNULUS_REPORT_SHA256,
        v26.ANNULUS_CACHE_PATH,
        v26.ANNULUS_CACHE_SHA256,
        "annulus reservation",
    )

    preflight_path = _exact_descriptor(
        root, config["selection_preflight"], PREFLIGHT_PATH, PREFLIGHT_SHA256,
        "selection preflight",
    )
    cache_path = _exact_descriptor(
        root, config["selection_cache"], SELECTION_CACHE_PATH, SELECTION_CACHE_SHA256,
        "selection cache",
    )
    preflight, _ = _read_json(preflight_path, "selection preflight")
    proposed = _load_selection_cache(cache_path)
    _validate_preflight(preflight, proposed, coverage, annulus)
    _validate_component_selection_lineage(
        preflight, annulus_report, coverage, annulus
    )

    base = _prepare_base_with_authenticated_current_sources(root)
    v26._validate_evidence_links(root, base, coverage_report, annulus_report)
    applied = dict(annulus)
    applied["component_proposed_selected"] = proposed["component_proposed_selected"]
    applied["component_added"] = proposed["component_added"]
    applied["component_displaced"] = proposed["component_displaced"]
    applied["family_proposed_selected"] = proposed["family_proposed_selected"]
    component_values, component_evidence = v26._rebuild_scope(
        "component", base.component_train, base.component_values[5],
        coverage_report, coverage, applied, domain_limited=False,
    )
    family_values, family_evidence = v26._rebuild_scope(
        "family", base.family_train, base.family_values[5],
        coverage_report, coverage, applied, domain_limited=True,
    )
    _validate_component_evidence(component_evidence, preflight, annulus_report)
    v26._validate_selection_evidence(
        family_evidence, annulus_report.get("family_train"), "family"
    )
    v26._validate_rebuilt_counts(component_values, family_values)
    inventory = _tensor_inventory_sha256(component_values, family_values)
    return PreparedTraining(
        config=config,
        config_path=relative_config,
        config_sha256=config_sha,
        source_bundle_sha256=bundle_sha,
        base=base,
        component_values=component_values,
        family_values=family_values,
        training_generator_state=base.training_generator_state.clone(),
        selection_evidence={"component": component_evidence, "family": family_evidence},
        training_tensor_inventory_sha256=inventory,
    )


def _validate_v26_result(path: Path) -> None:
    result, _ = _read_json(path, "base V26 result")
    component = result.get("component_selected", {})
    family = result.get("family_selected", {})
    if (
        result.get("acceptance_status") != "FAIL"
        or result.get("candidate_id") != "P1"
        or result.get("candidate_consumed") is not False
        or result.get("sealed_runs") != 0
        or component.get("precision") != 0.891314895681708
        or component.get("recall") != 0.9166666666666666
        or family.get("precision") != 0.8049792531120332
        or family.get("recall") != 0.941747572815534
    ):
        raise V27TrainingError("Base V26 outcome changed")


def _validate_recipe_compatibility() -> None:
    unchanged = {
        "seed", "epochs", "batch_size", "learning_rate", "weight_decay",
        "positive_loss_weight", "hard_negative_loss_weight", "focal_alpha",
        "focal_gamma", "label_positive_distance_px", "confidence_threshold",
        "selection_thresholds", "provider", "onnx_dynamic_candidate_counts",
        "onnx_parity_tolerance",
    }
    if any(RECIPE[key] != v26.RECIPE[key] for key in unchanged):
        raise V27TrainingError("V27 changed the V26 learning or evaluation recipe")
    if EXPECTED_DATA["training_example_count"] != v26.EXPECTED_DATA["training_example_count"]:
        raise V27TrainingError("V27 changed the V26 training population")


def _load_selection_cache(path: Path) -> dict[str, np.ndarray]:
    expected = {
        "component_proposed_selected", "component_added", "component_displaced",
        "component_bin_id", "family_proposed_selected",
    }
    with np.load(path, allow_pickle=False) as loaded:
        if set(loaded.files) != expected:
            raise V27TrainingError("Selection cache arrays changed")
        return {name: loaded[name] for name in loaded.files}


def _validate_preflight(
    report: Mapping[str, Any],
    proposed: Mapping[str, np.ndarray],
    coverage: Mapping[str, np.ndarray],
    annulus: Mapping[str, np.ndarray],
) -> None:
    component = proposed["component_proposed_selected"]
    family = proposed["family_proposed_selected"]
    source_component = annulus["component_proposed_selected"]
    source_family = annulus["family_proposed_selected"]
    nearest_distance = coverage.get("component_nearest_truth_distance_px")
    identities = report.get("selection_identity", {})
    populations = report.get("populations", {})
    if (
        report.get("status") != "model_free_preflight_only"
        or report.get("scope") != "frozen-v26-synthetic-train-component-selection-coverage"
        or report.get("optimizer_steps") != 0
        or report.get("checkpoint_forward_passes") != 0
        or report.get("thresholds_selected") != 0
        or report.get("private_reads") != 0
        or report.get("sealed_reads") != 0
        or report.get("production_approved") is not False
        or report.get("policy", {}).get("dev_rows_or_scores_used") is not False
        or report.get("cache", {}).get("sha256") != SELECTION_CACHE_SHA256
        or identities.get("component_proposed_sha256") != COMPONENT_SELECTION_SHA256
        or identities.get("family_proposed_sha256") != FAMILY_SELECTION_SHA256
        or populations.get("component", {}).get("selected_before") != 35838
        or populations.get("component", {}).get("selected_after") != 35838
        or populations.get("component", {}).get("positive_selected") != 3258
        or populations.get("component", {}).get("negative_selected") != 32580
        or populations.get("component", {}).get("added") != 8050
        or populations.get("component", {}).get("displaced") != 8050
        or populations.get("family", {}).get("selected_after") != 9053
        or populations.get("family", {}).get("byte_identical") is not True
    ):
        raise V27TrainingError("Selection preflight identity or scope changed")
    if (
        component.dtype != np.bool_
        or family.dtype != np.bool_
        or proposed["component_added"].dtype != np.bool_
        or proposed["component_displaced"].dtype != np.bool_
        or proposed["component_bin_id"].dtype != np.uint8
        or not isinstance(nearest_distance, np.ndarray)
        or len(component) != len(source_component)
        or len(family) != len(source_family)
        or len(proposed["component_added"]) != len(component)
        or len(proposed["component_displaced"]) != len(component)
        or len(proposed["component_bin_id"]) != len(component)
        or nearest_distance.shape != component.shape
        or np.any(proposed["component_bin_id"] > 63)
        or int(component.sum()) != EXPECTED_DATA["component_selected"]
        or int(source_component.sum()) != EXPECTED_DATA["component_selected"]
        or int(family.sum()) != EXPECTED_DATA["family_selected"]
        or int(proposed["component_added"].sum()) != EXPECTED_DATA["component_replacements"]
        or int(proposed["component_displaced"].sum()) != EXPECTED_DATA["component_replacements"]
        or not np.array_equal(family, source_family)
        or not np.array_equal(proposed["component_added"], component & ~source_component)
        or not np.array_equal(proposed["component_displaced"], source_component & ~component)
        or np.any((nearest_distance <= np.float32(3.0)) & ~component)
    ):
        raise V27TrainingError("Selection cache mask contract changed")
    component_sha = v26.annulus_reservation_preflight._selection_sha256(
        component, coverage["component_scene_index"], coverage["component_proposal_index"]
    )
    family_sha = v26.annulus_reservation_preflight._selection_sha256(
        family, coverage["family_scene_index"], coverage["family_proposal_index"]
    )
    if component_sha != COMPONENT_SELECTION_SHA256 or family_sha != FAMILY_SELECTION_SHA256:
        raise V27TrainingError("Selection cache row identity changed")


def _validate_component_evidence(
    observed: Mapping[str, Any],
    report: Mapping[str, Any],
    annulus_report: Mapping[str, Any],
) -> None:
    population = report["populations"]["component"]
    required = {
        "frozen_selection_sha256": annulus_report["component_train"][
            "frozen_selection_sha256"
        ],
        "proposed_selection_sha256": COMPONENT_SELECTION_SHA256,
        "selected_rows": population["selected_after"],
        "preserved_protected_rows": population["protected_selected_negative"],
        "added_rows": population["added"],
        "displaced_rows": population["displaced"],
        "row_order": "scene_then_ascending_proposal_index",
    }
    if observed != required:
        raise V27TrainingError("Applied component selection differs from the preflight")


def _validate_component_selection_lineage(
    report: Mapping[str, Any],
    annulus_report: Mapping[str, Any],
    coverage: Mapping[str, np.ndarray],
    annulus: Mapping[str, np.ndarray],
) -> None:
    identities = report.get("selection_identity", {})
    population = report.get("populations", {}).get("component", {})
    annulus_component = annulus_report.get("component_train", {})
    frozen = coverage.get("component_selected")
    source = annulus.get("component_proposed_selected")
    scene = coverage.get("component_scene_index")
    proposal = coverage.get("component_proposal_index")
    if (
        not isinstance(identities, Mapping)
        or not isinstance(population, Mapping)
        or not isinstance(annulus_component, Mapping)
        or not isinstance(frozen, np.ndarray)
        or not isinstance(source, np.ndarray)
        or not isinstance(scene, np.ndarray)
        or not isinstance(proposal, np.ndarray)
        or frozen.dtype != np.bool_
        or source.dtype != np.bool_
        or frozen.shape != source.shape
        or scene.shape != source.shape
        or proposal.shape != source.shape
    ):
        raise V27TrainingError("Component selection lineage arrays changed")
    frozen_sha = v26.annulus_reservation_preflight._selection_sha256(
        frozen, scene, proposal
    )
    source_sha = v26.annulus_reservation_preflight._selection_sha256(
        source, scene, proposal
    )
    if (
        frozen_sha == source_sha
        or frozen_sha != V25_COMPONENT_SELECTION_SHA256
        or source_sha != V26_COMPONENT_SELECTION_SHA256
        or frozen_sha != annulus_component.get("frozen_selection_sha256")
        or source_sha != annulus_component.get("proposed_selection_sha256")
        or source_sha != identities.get("component_source_sha256")
        or population.get("protected_selected_negative")
        != annulus_component.get("protected_selected_negative_count")
    ):
        raise V27TrainingError("Component V25-to-V26 selection lineage changed")


def _tensor_inventory_sha256(
    component: Sequence[torch.Tensor], family: Sequence[torch.Tensor]
) -> str:
    rows: list[dict[str, Any]] = []
    names = ("patches", "labels", "offsets", "radii", "hard_negative")
    for scope, values in (("component", component), ("family", family)):
        if len(values) != len(names):
            raise V27TrainingError("Training tensor tuple changed")
        for name, value in zip(names, values, strict=True):
            array = value.detach().cpu().contiguous().numpy()
            digest = hashlib.sha256(memoryview(array).cast("B")).hexdigest()
            rows.append({
                "scope": scope,
                "name": name,
                "dtype": str(array.dtype),
                "shape": list(array.shape),
                "sha256": digest,
            })
    return hashlib.sha256(canonical_json_bytes(rows)).hexdigest()


def _recovery_binding(
    prepared: PreparedTraining,
    checkpoint_sha256: str,
    onnx_sha256: str,
    torch_threads: int,
) -> dict[str, Any]:
    if torch.get_num_threads() != torch_threads:
        raise V27TrainingError("Recovery binding requires the active Torch thread limit")
    return {
        "candidate_config_sha256": prepared.config_sha256,
        "runner_source_bundle_sha256": prepared.source_bundle_sha256,
        "training_tensor_inventory_sha256": prepared.training_tensor_inventory_sha256,
        "component_selection_sha256": COMPONENT_SELECTION_SHA256,
        "family_selection_sha256": FAMILY_SELECTION_SHA256,
        "initializer_checkpoint_sha256": checkpoint_sha256,
        "initializer_onnx_sha256": onnx_sha256,
        "torch_intraop_threads": torch_threads,
    }


def _atomic_torch_save(path: Path, payload: Mapping[str, Any]) -> None:
    temporary = path.with_name(f".{path.name}.{uuid4().hex}.partial")
    try:
        with temporary.open("xb") as stream:
            torch.save(dict(payload), stream)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _atomic_json(path: Path, payload: Mapping[str, Any]) -> None:
    temporary = path.with_name(f".{path.name}.{uuid4().hex}.partial")
    try:
        with temporary.open("xb") as stream:
            stream.write(canonical_json_bytes(dict(payload)))
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _load_recovery(
    path: Path,
    *,
    model: torch.nn.Module,
    optimizer: torch.optim.Optimizer,
    generator: torch.Generator,
    binding: Mapping[str, Any],
    recipe: Mapping[str, Any],
    training_rows: int,
) -> TrainingOutcome:
    try:
        payload = torch.load(path, map_location="cpu", weights_only=False)
    except Exception as error:
        raise V27TrainingError("Recovery checkpoint could not be loaded") from error
    required = {
        "schema", "binding", "recipe", "training_rows", "completed_epochs",
        "optimizer_steps", "loss_history", "model_state_dict", "optimizer_state_dict",
        "shuffle_generator_state", "torch_rng_state", "numpy_rng_state", "python_rng_state",
    }
    if not isinstance(payload, dict) or set(payload) != required:
        raise V27TrainingError("Recovery checkpoint structure changed")
    if (
        payload["schema"] != RECOVERY_SCHEMA
        or payload["binding"] != dict(binding)
        or payload["recipe"] != dict(recipe)
        or payload["training_rows"] != training_rows
    ):
        raise V27TrainingError("Recovery checkpoint input binding does not match")
    epochs = payload["completed_epochs"]
    steps = payload["optimizer_steps"]
    steps_per_epoch = math.ceil(training_rows / int(recipe["batch_size"]))
    history = payload["loss_history"]
    if (
        type(epochs) is not int
        or not 1 <= epochs <= int(recipe["epochs"])
        or steps != epochs * steps_per_epoch
        or not isinstance(history, list)
        or len(history) != epochs
        or any(
            not isinstance(row, dict)
            or row.get("epoch") != index
            or row.get("optimizer_steps") != index * steps_per_epoch
            or not isinstance(row.get("mean_loss"), float)
            or not math.isfinite(row["mean_loss"])
            for index, row in enumerate(history, 1)
        )
    ):
        raise V27TrainingError("Recovery checkpoint progress is invalid")
    try:
        model.load_state_dict(payload["model_state_dict"])
        optimizer.load_state_dict(payload["optimizer_state_dict"])
        generator.set_state(payload["shuffle_generator_state"])
        torch.set_rng_state(payload["torch_rng_state"])
        np.random.set_state(payload["numpy_rng_state"])
        random.setstate(payload["python_rng_state"])
    except Exception as error:
        raise V27TrainingError("Recovery checkpoint state is invalid") from error
    return TrainingOutcome(epochs, steps, tuple(history), epochs)


def _train_epochs(
    model: torch.nn.Module,
    optimizer: torch.optim.Optimizer,
    patches: torch.Tensor,
    labels: torch.Tensor,
    offsets: torch.Tensor,
    radii: torch.Tensor,
    hard: torch.Tensor,
    generator: torch.Generator,
    *,
    recipe: Mapping[str, Any],
    binding: Mapping[str, Any],
    recovery_path: Path,
    progress_path: Path,
    loss_function: Callable[..., torch.Tensor] = v25.v21_loss,
    after_epoch: Callable[[TrainingOutcome], None] | None = None,
) -> TrainingOutcome:
    """Train the fixed recipe and atomically persist every completed epoch."""

    row_count = len(labels)
    if not all(len(value) == row_count for value in (patches, offsets, radii, hard)):
        raise V27TrainingError("Training tensors differ in length")
    outcome = TrainingOutcome(0, 0, (), 0)
    if recovery_path.exists():
        outcome = _load_recovery(
            recovery_path,
            model=model,
            optimizer=optimizer,
            generator=generator,
            binding=binding,
            recipe=recipe,
            training_rows=row_count,
        )
    resumed_from = outcome.completed_epochs
    history = list(outcome.loss_history)
    steps = outcome.optimizer_steps
    model.train()
    for epoch in range(outcome.completed_epochs + 1, int(recipe["epochs"]) + 1):
        order = torch.randperm(row_count, generator=generator)
        batch_losses: list[float] = []
        for start in range(0, row_count, int(recipe["batch_size"])):
            index = order[start:start + int(recipe["batch_size"])]
            loss = loss_function(
                model.forward_raw(patches[index]),
                labels[index],
                offsets[index],
                radii[index],
                hard[index],
                positive_weight=recipe["positive_loss_weight"],
                hard_weight=recipe["hard_negative_loss_weight"],
                alpha=recipe["focal_alpha"],
                gamma=recipe["focal_gamma"],
            )
            if not bool(torch.isfinite(loss)):
                raise V27TrainingError("Training loss became non-finite")
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            optimizer.step()
            steps += 1
            batch_losses.append(float(loss.detach()))
        history.append({
            "epoch": epoch,
            "optimizer_steps": steps,
            "mean_loss": float(sum(batch_losses) / len(batch_losses)),
        })
        payload = {
            "schema": RECOVERY_SCHEMA,
            "binding": dict(binding),
            "recipe": dict(recipe),
            "training_rows": row_count,
            "completed_epochs": epoch,
            "optimizer_steps": steps,
            "loss_history": history,
            "model_state_dict": model.state_dict(),
            "optimizer_state_dict": optimizer.state_dict(),
            "shuffle_generator_state": generator.get_state(),
            "torch_rng_state": torch.get_rng_state(),
            "numpy_rng_state": np.random.get_state(),
            "python_rng_state": random.getstate(),
        }
        _atomic_torch_save(recovery_path, payload)
        current = TrainingOutcome(epoch, steps, tuple(history), resumed_from)
        _atomic_json(progress_path, {
            "status": "training" if epoch < int(recipe["epochs"]) else "training_complete",
            "completed_epochs": epoch,
            "optimizer_steps": steps,
            "recovery_sha256": sha256_file(recovery_path),
            "production_approval": False,
        })
        if after_epoch is not None:
            after_epoch(current)
    return TrainingOutcome(int(recipe["epochs"]), steps, tuple(history), resumed_from)


def _resume_source(root: Path, path: Path | None, expected_sha256: str | None) -> Path | None:
    if path is None and expected_sha256 is None:
        return None
    if path is None or not _is_sha256(expected_sha256):
        raise V27TrainingError("Recovery requires a checkpoint and its SHA-256")
    resolved = (path if path.is_absolute() else root / path).resolve()
    artifacts = (root / "artifacts").resolve()
    if artifacts not in resolved.parents or not resolved.is_file():
        raise V27TrainingError("Recovery checkpoint must be an existing artifacts file")
    if sha256_file(resolved) != expected_sha256:
        raise V27TrainingError("Recovery checkpoint bytes changed")
    return resolved


def _validate_initializer(checkpoint: Path, onnx: Path) -> tuple[str, str]:
    checkpoint_sha = sha256_file(checkpoint)
    onnx_sha = sha256_file(onnx)
    if (
        checkpoint_sha != RECIPE["initializer_checkpoint_sha256"]
        or onnx_sha != RECIPE["initializer_onnx_sha256"]
    ):
        raise V27TrainingError("V21 initializer identity changed")
    return checkpoint_sha, onnx_sha


def _configure_training_runtime(torch_threads: int) -> None:
    torch.set_num_threads(torch_threads)
    if torch.get_num_threads() != torch_threads:
        raise V27TrainingError("Torch intraop thread limit was not applied")


def run(
    output_dir: Path,
    checkpoint: Path,
    v21_onnx: Path,
    config_path: Path = CONFIG_PATH,
    *,
    repository_root: Path = REPO_ROOT,
    dependencies: Dependencies | None = None,
    resume_checkpoint: Path | None = None,
    resume_checkpoint_sha256: str | None = None,
    torch_threads: int = 12,
) -> dict[str, Any]:
    """Train and evaluate the one authorized synthetic train/dev V27 candidate."""

    root = repository_root.resolve()
    output = (output_dir if output_dir.is_absolute() else root / output_dir).resolve()
    if (root / "artifacts").resolve() not in output.parents or output.exists():
        raise V27TrainingError("Use a new output directory under repository artifacts")
    if torch_threads != EXECUTION["torch_intraop_threads"]:
        raise V27TrainingError("V27 execution requires the recorded 12 intraop threads")
    torch.set_num_threads(torch_threads)
    resume_source = _resume_source(root, resume_checkpoint, resume_checkpoint_sha256)
    prepared = prepare_training(config_path, repository_root=root)
    current_config, current_sha = _load_config(root / prepared.config_path, root)
    if (
        current_sha != prepared.config_sha256
        or current_config != prepared.config
        or source_bundle_sha256(root, RUNNER_SOURCE_PATHS) != prepared.source_bundle_sha256
    ):
        raise V27TrainingError("V27 inputs changed during model-free preparation")
    checkpoint = checkpoint.resolve()
    v21_onnx = v21_onnx.resolve()
    checkpoint_sha, onnx_sha = _validate_initializer(checkpoint, v21_onnx)
    deps = dependencies or _default_dependencies()
    authorization = deps.acquire(
        root,
        task=TASK,
        revision=REVISION,
        candidate_id=CANDIDATE_ID,
        config_path=prepared.config_path,
        runner_source_paths=RUNNER_SOURCE_PATHS,
    )
    started = time.perf_counter()
    phase = "authorization"
    report_path = output / REPORT_NAME
    training: TrainingOutcome | None = None
    try:
        if (
            authorization.binding.get("candidate_config_path")
            != prepared.config_path.as_posix()
            or authorization.binding.get("candidate_config_sha256")
            != prepared.config_sha256
            or authorization.binding.get("runner_source_bundle_sha256")
            != prepared.source_bundle_sha256
            or authorization.snapshot_path is None
        ):
            raise V27TrainingError("Training authorization captured different V27 inputs")
        deps.verify_snapshot(
            root, authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"),
        )
        output.mkdir(parents=True, exist_ok=False)
        recovery_path = output / RECOVERY_NAME
        if resume_source is not None:
            shutil.copyfile(resume_source, recovery_path)
            if sha256_file(recovery_path) != resume_checkpoint_sha256:
                raise V27TrainingError("Recovery checkpoint changed while copying")
        phase = "initialization"
        v24._configure(RECIPE["seed"])
        _configure_training_runtime(torch_threads)
        patches = torch.cat((prepared.component_values[0], prepared.family_values[0]))
        labels = torch.cat((prepared.component_values[1], prepared.family_values[1]))
        offsets = torch.cat((prepared.component_values[2], prepared.family_values[2]))
        radii = torch.cat((prepared.component_values[3], prepared.family_values[3]))
        hard = torch.cat((prepared.component_values[4], prepared.family_values[4]))
        generator = torch.Generator(device="cpu")
        generator.set_state(prepared.training_generator_state)
        payload = torch.load(checkpoint, map_location="cpu", weights_only=False)
        model = v25.ScaleClassifierNet(v25.ModelConfig(seed=20260902))
        model.load_state_dict(payload["state_dict"])
        optimizer = torch.optim.AdamW(
            model.parameters(),
            lr=RECIPE["learning_rate"],
            weight_decay=RECIPE["weight_decay"],
        )
        recovery_binding = _recovery_binding(
            prepared, checkpoint_sha, onnx_sha, torch_threads
        )
        deps.verify_snapshot(
            root, authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"),
        )
        phase = "training"
        training = _train_epochs(
            model,
            optimizer,
            patches,
            labels,
            offsets,
            radii,
            hard,
            generator,
            recipe=RECIPE,
            binding=recovery_binding,
            recovery_path=recovery_path,
            progress_path=output / PROGRESS_NAME,
        )
        if training.optimizer_steps != EXPECTED_DATA["optimizer_steps"]:
            raise V27TrainingError("V27 optimizer step count changed")
        phase = "dev"
        model.eval()
        thresholds = [RECIPE["confidence_threshold"], *RECIPE["selection_thresholds"]]
        component_dev = [
            v24._evaluate(prepared.base.component_dev, model, value) for value in thresholds
        ]
        family_dev = [
            v25._evaluate_domain(prepared.base.family_dev, model, value) for value in thresholds
        ]
        bar = v24._shared_marker_acceptance_bar()
        dev_passed = v24._passes_required_dev_gates(component_dev[0], family_dev[0], bar)
        phase = "export"
        out_pt = output / CHECKPOINT_NAME
        out_onnx = output / ONNX_NAME
        torch.save({"state_dict": model.state_dict(), "config": model.export_contract()}, out_pt)
        torch.onnx.export(
            model,
            torch.zeros((1, 3, 33, 33)),
            out_onnx,
            input_names=["candidate_patches"],
            output_names=["candidate_predictions"],
            dynamic_axes={
                "candidate_patches": {0: "candidate_count"},
                "candidate_predictions": {0: "candidate_count"},
            },
            opset_version=18,
            dynamo=False,
        )
        v25.onnx.checker.check_model(v25.onnx.load(out_onnx))
        session_options = v25.ort.SessionOptions()
        session_options.intra_op_num_threads = EXECUTION["onnx_intraop_threads"]
        session_options.inter_op_num_threads = EXECUTION["onnx_interop_threads"]
        session = v25.ort.InferenceSession(
            str(out_onnx), sess_options=session_options, providers=[RECIPE["provider"]]
        )
        parity_source = v24.extract_proposals(prepared.base.component_dev[0].tensor).patches
        parity = []
        for count in RECIPE["onnx_dynamic_candidate_counts"]:
            value = parity_source[:count].contiguous()
            expected = model(value).detach().numpy()
            actual = session.run(
                ["candidate_predictions"], {"candidate_patches": value.numpy()}
            )[0]
            parity.append({
                "candidate_count": count,
                "maximum_absolute_error": float(np.max(np.abs(expected - actual))),
            })
        parity_max = max(row["maximum_absolute_error"] for row in parity)
        deps.verify_snapshot(
            root, authorization.snapshot_path,
            authorization.binding.get("source_snapshot_sha256"),
        )
        report = {
            "schema": REPORT_SCHEMA,
            "task": TASK,
            "revision": REVISION,
            "candidate_id": CANDIDATE_ID,
            "status": "dev_passed" if (
                dev_passed and parity_max <= RECIPE["onnx_parity_tolerance"]
            ) else "failed_dev",
            "candidate_config_path": prepared.config_path.as_posix(),
            "candidate_config_sha256": prepared.config_sha256,
            "base_v26_result": dict(prepared.config["base_v26_result"]),
            "selection_preflight": dict(prepared.config["selection_preflight"]),
            "selection_cache": dict(prepared.config["selection_cache"]),
            "selection_evidence": prepared.selection_evidence,
            "training_tensor_inventory_sha256": prepared.training_tensor_inventory_sha256,
            "optimizer_steps": training.optimizer_steps,
            "completed_epochs": training.completed_epochs,
            "loss_history": list(training.loss_history),
            "checkpoint_selection": RECIPE["checkpoint_selection"],
            "component_dev_comparisons": component_dev,
            "family_dev_comparisons": family_dev,
            "acceptance_bar": bar,
            "dev_gate_passed": dev_passed,
            "checkpoint_sha256": sha256_file(out_pt),
            "onnx_sha256": sha256_file(out_onnx),
            "v21_checkpoint_sha256": checkpoint_sha,
            "v21_onnx_sha256": onnx_sha,
            "onnx_provider": RECIPE["provider"],
            "onnx_dynamic_candidate_counts": parity,
            "onnx_parity_maximum_absolute_error": parity_max,
            "recovery": {
                "path": recovery_path.relative_to(root).as_posix(),
                "sha256": sha256_file(recovery_path),
                "completed_epochs": training.completed_epochs,
                "optimizer_steps": training.optimizer_steps,
                "resumed_from_epoch": training.resumed_from_epoch,
                "input_binding": recovery_binding,
            },
            "execution": dict(EXECUTION),
            "elapsed_ms": round((time.perf_counter() - started) * 1000, 3),
            "synthetic_only": True,
            "private_data": False,
            "sealed_data": False,
            "sealed_runs": 0,
            "production_approval": False,
            "release_eligible": False,
            "training_authorization": authorization.binding,
        }
        _write_json(report_path, report)
        deps.complete(
            authorization,
            status=report["status"],
            report_sha256=sha256_file(report_path),
        )
        return report
    except BaseException as error:
        known_steps = training.optimizer_steps if training is not None else 0
        failure = {
            "schema": FAILURE_SCHEMA,
            "task": TASK,
            "revision": REVISION,
            "candidate_id": CANDIDATE_ID,
            "status": "failed_runner",
            "phase": phase,
            "completed_optimizer_steps": known_steps,
            "optimizer_steps_known": phase not in {"training"} or training is not None,
            "recovery_checkpoint_present": (output / RECOVERY_NAME).is_file(),
            "exception_type": type(error).__name__,
            "exception_message": str(error),
            "synthetic_only": True,
            "private_data": False,
            "sealed_data": False,
            "sealed_runs": 0,
            "production_approval": False,
            "training_authorization": authorization.binding,
        }
        try:
            if output.is_dir() and not report_path.exists():
                _write_json(report_path, failure)
        finally:
            deps.void(authorization, error)
        raise


def _load_config(path: Path, root: Path) -> tuple[dict[str, Any], str]:
    config, digest = _read_json(path, "V27 config")
    if set(config) != _CONFIG_KEYS:
        raise V27TrainingError("V27 configuration fields changed")
    fixed = {
        "schema": CONFIG_SCHEMA,
        "task": TASK,
        "revision": REVISION,
        "candidate_id": CANDIDATE_ID,
        "stage": "P1",
        "recipe": RECIPE,
        "expected_data": EXPECTED_DATA,
        "execution": EXECUTION,
        "synthetic_only": True,
        "private_data": False,
        "sealed_data": False,
        "production_approval": False,
        "release_eligible": False,
    }
    if any(config.get(key) != value for key, value in fixed.items()):
        raise V27TrainingError("V27 configuration identity, recipe, or scope changed")
    for key, expected_path, expected_sha in (
        ("base_v26_protocol", V26_PROTOCOL_PATH, V26_PROTOCOL_SHA256),
        ("base_v26_config", V26_CONFIG_PATH, V26_CONFIG_SHA256),
        ("base_v26_result", V26_RESULT_PATH, V26_RESULT_SHA256),
        ("selection_preflight", PREFLIGHT_PATH, PREFLIGHT_SHA256),
        ("selection_cache", SELECTION_CACHE_PATH, SELECTION_CACHE_SHA256),
    ):
        _exact_descriptor(root, config[key], expected_path, expected_sha, key)
    if not _is_sha256(config.get("expected_runner_source_bundle_sha256")):
        raise V27TrainingError("Runner source bundle SHA-256 is invalid")
    return config, digest


def _exact_descriptor(
    root: Path,
    value: Any,
    expected_path: Path,
    expected_sha: str,
    label: str,
) -> Path:
    if not isinstance(value, dict) or set(value) != {"path", "sha256"}:
        raise V27TrainingError(f"{label} descriptor changed")
    raw = value.get("path")
    if not isinstance(raw, str) or not raw or Path(raw).is_absolute():
        raise V27TrainingError(f"{label} path is unsafe")
    path = (root / raw).resolve()
    _require_inside(root, path, label)
    if path != (root / expected_path).resolve() or value.get("sha256") != expected_sha:
        raise V27TrainingError(f"{label} differs from its frozen identity")
    if sha256_file(path) != expected_sha:
        raise V27TrainingError(f"{label} bytes changed")
    return path


def _read_json(path: Path, label: str) -> tuple[dict[str, Any], str]:
    try:
        payload = path.read_bytes()
        value = json.loads(payload)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise V27TrainingError(f"Could not read {label}") from error
    if not isinstance(value, dict):
        raise V27TrainingError(f"{label} must be a JSON object")
    return value, hashlib.sha256(payload).hexdigest()


def _relative(root: Path, path: Path, label: str) -> Path:
    resolved = (path if path.is_absolute() else root / path).resolve()
    _require_inside(root, resolved, label)
    return resolved.relative_to(root)


def _require_inside(root: Path, path: Path, label: str) -> None:
    if path == root or root not in path.parents:
        raise V27TrainingError(f"{label} escaped the repository")


def _is_sha256(value: Any) -> bool:
    return (
        isinstance(value, str)
        and len(value) == 64
        and value == value.lower()
        and all(character in "0123456789abcdef" for character in value)
    )


def _write_json(path: Path, value: Mapping[str, Any]) -> None:
    with path.open("xb") as stream:
        stream.write(canonical_json_bytes(dict(value)))


def _prepare_only(
    checkpoint: Path,
    onnx: Path,
    config: Path,
    *,
    torch_threads: int,
) -> dict[str, Any]:
    if torch_threads != EXECUTION["torch_intraop_threads"]:
        raise V27TrainingError("V27 execution requires the recorded 12 intraop threads")
    torch.set_num_threads(torch_threads)
    prepared = prepare_training(config)
    _configure_training_runtime(torch_threads)
    checkpoint_sha, onnx_sha = _validate_initializer(checkpoint.resolve(), onnx.resolve())
    return {
        "status": "model_free_preparation_complete",
        "candidate_config_sha256": prepared.config_sha256,
        "runner_source_bundle_sha256": prepared.source_bundle_sha256,
        "training_tensor_inventory_sha256": prepared.training_tensor_inventory_sha256,
        "component_selection_sha256": COMPONENT_SELECTION_SHA256,
        "family_selection_sha256": FAMILY_SELECTION_SHA256,
        "training_rows": sum(len(values[1]) for values in (
            prepared.component_values, prepared.family_values
        )),
        "initializer_checkpoint_sha256": checkpoint_sha,
        "initializer_onnx_sha256": onnx_sha,
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approval": False,
        "execution": dict(EXECUTION),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--onnx", type=Path, required=True)
    parser.add_argument("--config", type=Path, default=CONFIG_PATH)
    parser.add_argument("--resume-checkpoint", type=Path)
    parser.add_argument("--resume-checkpoint-sha256")
    parser.add_argument("--torch-threads", type=int, default=12)
    args = parser.parse_args()
    if args.prepare_only:
        if args.output_dir is not None or args.resume_checkpoint is not None:
            parser.error("Prepare-only does not accept output or recovery arguments")
        result = _prepare_only(
            args.checkpoint,
            args.onnx,
            args.config,
            torch_threads=args.torch_threads,
        )
    elif args.output_dir is not None:
        result = run(
            args.output_dir,
            args.checkpoint,
            args.onnx,
            args.config,
            resume_checkpoint=args.resume_checkpoint,
            resume_checkpoint_sha256=args.resume_checkpoint_sha256,
            torch_threads=args.torch_threads,
        )
    else:
        parser.error("Specify --prepare-only or --output-dir")
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
