# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Ledger-authorized V24 P1 fine-tune runner with frozen runtime family inputs."""
from __future__ import annotations
import argparse, hashlib, json, random, time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
import numpy as np
import onnx, onnxruntime as ort, torch
from ml.markers.center.focal_confidence_v21.focal_loss import v21_loss
from ml.markers.center.scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from ml.markers.center.real_range_generator_v1.generator import ANTI_ALIAS_BLUR_RADII, audit as generator_audit, build_split
from ml.markers.center.real_range_generator_v1.negative_sampler import CONNECTOR_ANCHOR_MAX_DISTANCE_PX, CONNECTOR_ENDPOINT_OFFSET_PX, GENERIC_CONNECTOR_BAND_RADIUS_PX, TOPOLOGY_HARD_RADIUS_PX, TOPOLOGY_KINDS, TOPOLOGY_RADIUS_PX, TOPOLOGY_SAMPLER_RADIUS_PX, SampledNegatives, sample_negatives
from ml.markers.center.metrics import center_metrics
from ml.markers.gate_seal import canonical_json_bytes, sha256_file
from ml.markers.training_budget import acquire_training_candidate, complete_training_candidate, void_candidate
from ml.synthetic.dataset import family_holdout_audit
from .family_scenes import FamilyScene
from . import runtime_binding, runtime_binding_v2, runtime_inputs
from .runtime_binding import tensor_set_sha256
from .mask_preserving import extract_proposals, postprocess, prohibited_hits
from .stratified_background import select_stratified_background
from . import protocol

REPO_ROOT = Path(__file__).resolve().parents[4]
CONFIG_PATH = Path("ml/markers/center/mask_preserving_v24/training/p1.json")
FAMILY_HARD_NEGATIVE_RADIUS_PX = 8.0
FAMILY_GENERIC_NEGATIVE_PREFIX = "prefix"
FAMILY_GENERIC_NEGATIVE_UNIFORM = "uniform_without_replacement"
FAMILY_GENERIC_NEGATIVE_STRATIFIED = "global_mask_density_stratified"
FAMILY_GENERIC_NEGATIVE_UNIFORM_SEED = 20260909
FAMILY_GENERIC_NEGATIVE_STRATIFIED_SEED = 20260909
FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY = "family_stratified_bin_capacities_expected"
FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY = "family_stratified_bin_quotas_expected"
UNIFORM_BACKGROUND_PROTOCOL_PATH = Path(
    "ml/markers/center/mask_preserving_v24/uniform_background_dev_protocol.json"
)
UNIFORM_BACKGROUND_PROTOCOL_SHA256 = (
    "c831d010dab52d12f1856c99a848c21e7dbe9f99547a8f1c6644ffe009ddee8b"
)
STRATIFIED_BACKGROUND_PROTOCOL_PATH = Path(
    "ml/markers/center/mask_preserving_v24/stratified_background_dev_protocol.json"
)
STRATIFIED_BACKGROUND_PROTOCOL_SHA256 = (
    "917980a9014638fc91f696f9ef38c942c15685f8917b09387b5f2693c9d23e41"
)
RUNNER_SOURCE_PATHS = (
    Path("ml/markers/center/mask_preserving_v24/protocol.py"),
    Path("ml/markers/center/mask_preserving_v24/mask_preserving.py"),
    Path("ml/markers/center/mask_preserving_v24/FEASIBILITY.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/diagnose_retry.py"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY2_MORPHOLOGY_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/train_p1.py"),
    Path("ml/markers/center/mask_preserving_v24/stratified_background.py"),
    Path("ml/markers/center/focal_confidence_v21/focal_loss.py"),
    Path("ml/markers/center/scale_classifier_v16/model.py"),
    Path("ml/markers/center/dataset.py"),
    Path("ml/markers/center/line_aware_v1/pipeline.py"),
    Path("ml/markers/center/line_aware_v1/dataset.py"),
    Path("ml/markers/center/postprocess.py"),
    Path("ml/markers/center/real_range_generator_v1/generator.py"),
    Path("ml/markers/center/real_range_generator_v1/negative_sampler.py"),
    Path("ml/markers/center/real_range_generator_v1/AUDIT.json"),
    Path("ml/markers/center/real_range_generator_v1/NEGATIVE_PROPOSAL_AUDIT.json"),
    Path("docs/GOAL-22-PHASE-4R-V24-NEGATIVE-PATCH-GAP.json"),
    Path("docs/GOAL-22-PHASE-4R-V24-MORPHOLOGY-GAP.json"),
    Path("docs/GOAL-22-PHASE-4R-V24-RETRY3-MORPHOLOGY-GAP.json"),
    Path("docs/GOAL-22-PHASE-4R-V24-RETRY7-MORPHOLOGY-GAP.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY4_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY4_GENERIC_FP_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY5_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY5_GENERIC_FP_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY6_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY7_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY7_MORPHOLOGY_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY8_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/P1_RETRY8_RESULT.json"),
    Path("ml/markers/center/mask_preserving_v24/family_scenes.py"),
    Path("ml/markers/center/mask_preserving_v24/export_family_rasters.py"),
    Path("ml/markers/center/mask_preserving_v24/runtime_inputs.py"),
    Path("ml/markers/center/mask_preserving_v24/runtime_family_scenes.py"),
    Path("ml/markers/center/mask_preserving_v24/runtime_binding.py"),
    Path("ml/markers/center/mask_preserving_v24/runtime_binding_v2.py"),
    Path("ml/markers/center/mask_preserving_v24/coverage_dev_protocol.json"),
    UNIFORM_BACKGROUND_PROTOCOL_PATH,
    STRATIFIED_BACKGROUND_PROTOCOL_PATH,
    Path("tools/GraphReader.SyntheticRuntimeEvidence/score_family_ocr.py"),
    Path("ml/synthetic/dataset.py"),
    Path("ml/synthetic/fonts.py"),
    Path("ml/synthetic/schema.py"),
    Path("ml/synthetic/scene.schema.json"),
    Path("ml/synthetic/io.py"),
    Path("ml/synthetic/contact_sheet.py"),
    Path("ml/synthetic/templates.py"),
    Path("ml/synthetic/renderer.py"),
    Path("docs/GOAL-22-PHASE-4R-V24-FAMILY-DEV-BASELINE.json"),
    Path("docs/GOAL-22-V24-RUNTIME-INPUT-PREFLIGHT.json"),
    Path("ml/markers/center/metrics.py"),
    Path("ml/policy/evidence-policy.json"),
    Path("ml/policy/acceptance-bars.json"),
    Path("ml/policy/evidence_policy.py"),
    Path("ml/markers/gate_seal.py"),
    Path("ml/markers/training_budget.py"),
)


@dataclass(frozen=True)
class StratifiedFamilySamplingReport:
    selections: tuple[tuple[int, ...], ...]
    capacities: dict[str, int]
    counts: dict[str, int]
    selected_index_sha256: str
    stratified_bin_capacities: dict[str, int]
    stratified_bin_quotas: dict[str, int]

def _sha(path: Path) -> str: return hashlib.sha256(path.read_bytes()).hexdigest()
def _configure(seed: int) -> None:
    random.seed(seed); np.random.seed(seed); torch.manual_seed(seed); torch.set_num_threads(1); torch.use_deterministic_algorithms(True)


def _repository_config_path(config_path: Path) -> tuple[Path, Path]:
    relative = Path(config_path)
    if (
        relative == Path(".")
        or relative.is_absolute()
        or relative.drive
        or ".." in relative.parts
    ):
        raise ValueError("training config must be a repository-relative path")
    root = REPO_ROOT.resolve()
    resolved = (root / relative).resolve()
    if root not in resolved.parents or not resolved.is_file():
        raise ValueError("training config must be an existing file inside the repository")
    return Path(relative.as_posix()), resolved


def _load_config(config_path: Path) -> tuple[Path, str, dict]:
    relative, resolved = _repository_config_path(config_path)
    payload = resolved.read_bytes()
    try:
        config = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError("training config is not valid JSON") from error
    if not isinstance(config, dict):
        raise ValueError("training config must be a JSON object")
    return relative, hashlib.sha256(payload).hexdigest(), config


def _load_bound_family_inputs(config: dict):
    binding_value = config.get("runtime_training_input_binding_path")
    binding_sha256 = config.get("runtime_training_input_binding_sha256")
    if not binding_value or not binding_sha256:
        raise RuntimeError("training requires a frozen actual-runtime family input binding")
    binding_path = runtime_binding._relative_path(
        binding_value, REPO_ROOT.resolve(), "runtime training input binding"
    )
    _, payload = runtime_inputs._read_bound_artifact(
        binding_path,
        binding_sha256,
        REPO_ROOT.resolve(),
        "runtime training input binding",
    )
    document = runtime_inputs._json_object(payload, "runtime training input binding")
    schema = document.get("schema")
    if schema == runtime_binding.BINDING_SCHEMA:
        loader = runtime_binding.load_runtime_family_binding
    elif schema == runtime_binding_v2.BINDING_SCHEMA:
        loader = runtime_binding_v2.load_runtime_family_binding_v2
    else:
        raise RuntimeError(f"unsupported runtime training input binding schema: {schema!r}")
    return loader(binding_path, binding_sha256), schema


def _acquire_candidate(config_path: Path, config_sha256: str):
    authorization = acquire_training_candidate(
        REPO_ROOT,
        task=protocol.TASK,
        revision=protocol.TRAINING_REVISION,
        candidate_id=protocol.TRAINING_CANDIDATE_ID,
        config_path=config_path,
        runner_source_paths=RUNNER_SOURCE_PATHS,
    )
    if (
        authorization.binding.get("candidate_config_path") != config_path.as_posix()
        or authorization.binding.get("candidate_config_sha256") != config_sha256
    ):
        error = RuntimeError("training authorization captured a different configuration identity")
        void_candidate(authorization, error)
        raise error
    return authorization


def _prepare_bound_family_inputs(
    config: dict, config_path: Path, config_sha256: str
):
    binding_started = time.perf_counter()
    _configure(config["seed"])
    family_join, binding_schema = _load_bound_family_inputs(config)
    binding_elapsed_ms = round((time.perf_counter() - binding_started) * 1000, 3)
    authorization = _acquire_candidate(config_path, config_sha256)
    return family_join, binding_schema, binding_elapsed_ms, authorization


def _hard_negative_radius(scene, kind: str) -> float | None:
    if kind in {"text", "line_intersection", "axis"}:
        return 8.0
    if kind in TOPOLOGY_KINDS:
        return TOPOLOGY_HARD_RADIUS_PX
    if isinstance(scene, FamilyScene):
        return FAMILY_HARD_NEGATIVE_RADIUS_PX
    return None


def _tensor_set_sha256(scenes) -> str:
    return tensor_set_sha256(scenes)


def _passes_marker_bar(metrics: dict[str, float], bar: dict[str, float]) -> bool:
    return (
        metrics["proposal_recall"] >= bar["proposal_recall_minimum"]
        and metrics["precision"] >= bar["precision_minimum"]
        and metrics["recall"] >= bar["recall_minimum"]
        and metrics["prohibited_structure_hit_rate"]
        <= bar["prohibited_structure_hit_rate_maximum"]
    )


def _passes_required_dev_gates(
    real_range_metrics: dict[str, float],
    family_metrics: dict[str, float],
    bar: dict[str, float],
) -> bool:
    return _passes_marker_bar(real_range_metrics, bar) and _passes_marker_bar(
        family_metrics, bar
    )


def _shared_marker_acceptance_bar() -> dict[str, float]:
    """Read the canonical marker-center bars instead of revision-local gates."""
    policy = json.loads((REPO_ROOT / protocol.ACCEPTANCE_BARS_PATH).read_text(encoding="utf-8"))
    tier1 = policy["tier1_reviewable_error"]
    return {
        "proposal_recall_minimum": float(tier1["marker_center_recall_minimum"]),
        "precision_minimum": float(tier1["marker_center_precision_minimum"]),
        "recall_minimum": float(tier1["marker_center_recall_minimum"]),
        "prohibited_structure_hit_rate_maximum": float(
            tier1["prohibited_structure_hit_rate_maximum"]
        ),
    }


def _selected_index_sha256(scenes, selections, proposal_coordinates=None) -> str:
    identities = []
    for scene_number, (scene, indices) in enumerate(zip(scenes, selections, strict=True)):
        panel_identity = getattr(scene, "sampling_identity", None)
        if panel_identity is None:
            identities.extend(
                f"{scene.split}:{scene.family}:{scene.seed}:{index}" for index in indices
            )
            continue
        if proposal_coordinates is None:
            raise ValueError("runtime panel sampling requires proposal coordinates")
        coordinates = proposal_coordinates[scene_number]
        identities.extend(
            {
                "panel": panel_identity,
                "proposal_index": index,
                "coordinates": [float(value) for value in coordinates[index]],
            }
            for index in indices
        )
    return hashlib.sha256(canonical_json_bytes(identities)).hexdigest()


def _validate_family_generic_negative_options(
    sampling_mode: str,
    family_generic_negative_mode: str,
    family_generic_negative_seed: int | None,
) -> None:
    if type(family_generic_negative_mode) is not str or family_generic_negative_mode not in {
        FAMILY_GENERIC_NEGATIVE_PREFIX,
        FAMILY_GENERIC_NEGATIVE_UNIFORM,
        FAMILY_GENERIC_NEGATIVE_STRATIFIED,
    }:
        raise ValueError("unsupported family generic-negative mode")
    if sampling_mode != "family-train" and (
        family_generic_negative_mode != FAMILY_GENERIC_NEGATIVE_PREFIX
        or family_generic_negative_seed is not None
    ):
        raise ValueError("family generic-negative options require family-train sampling")
    if family_generic_negative_mode == FAMILY_GENERIC_NEGATIVE_PREFIX:
        if family_generic_negative_seed is not None:
            raise ValueError("prefix family generic-negative sampling does not accept a seed")
        return
    expected_seed = (
        FAMILY_GENERIC_NEGATIVE_UNIFORM_SEED
        if family_generic_negative_mode == FAMILY_GENERIC_NEGATIVE_UNIFORM
        else FAMILY_GENERIC_NEGATIVE_STRATIFIED_SEED
    )
    if (
        type(family_generic_negative_seed) is not int
        or family_generic_negative_seed != expected_seed
    ):
        raise ValueError(
            f"{family_generic_negative_mode} family generic-negative sampling requires seed 20260909"
        )
    if family_generic_negative_mode == FAMILY_GENERIC_NEGATIVE_UNIFORM:
        if _sha(REPO_ROOT / UNIFORM_BACKGROUND_PROTOCOL_PATH) != UNIFORM_BACKGROUND_PROTOCOL_SHA256:
            raise RuntimeError("uniform family background protocol identity changed")
        return
    if _sha(REPO_ROOT / STRATIFIED_BACKGROUND_PROTOCOL_PATH) != STRATIFIED_BACKGROUND_PROTOCOL_SHA256:
        raise RuntimeError("stratified family background protocol identity changed")


def _family_generic_negative_config(config: dict) -> tuple[str, int | None]:
    mode = config.get("family_generic_negative_mode", FAMILY_GENERIC_NEGATIVE_PREFIX)
    seed = config.get("family_generic_negative_seed")
    _validate_family_generic_negative_options("family-train", mode, seed)
    if mode == FAMILY_GENERIC_NEGATIVE_STRATIFIED:
        _validated_stratified_bin_map(config.get(FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY), FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY)
        _validated_stratified_bin_map(config.get(FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY), FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY)
    return mode, seed


def _validated_stratified_bin_map(value, label: str) -> dict[str, int]:
    expected_keys = {str(bin_id) for bin_id in range(64)}
    if type(value) is not dict or set(value) != expected_keys:
        raise ValueError(f"{label} must be an object with exactly numeric keys 0 through 63")
    if any(type(value[key]) is not int or value[key] < 0 for key in expected_keys):
        raise ValueError(f"{label} values must be non-negative integers")
    return value


def _validate_stratified_family_sampling_config(
    config: dict,
    sampling: StratifiedFamilySamplingReport,
) -> None:
    expected_capacities = _validated_stratified_bin_map(
        config.get(FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY),
        FAMILY_STRATIFIED_BIN_CAPACITIES_CONFIG_KEY,
    )
    expected_quotas = _validated_stratified_bin_map(
        config.get(FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY),
        FAMILY_STRATIFIED_BIN_QUOTAS_CONFIG_KEY,
    )
    actual_capacities = _validated_stratified_bin_map(
        sampling.stratified_bin_capacities,
        "computed stratified bin capacities",
    )
    actual_quotas = _validated_stratified_bin_map(
        sampling.stratified_bin_quotas,
        "computed stratified bin quotas",
    )
    if actual_capacities != expected_capacities:
        raise RuntimeError("stratified family bin capacities changed")
    if actual_quotas != expected_quotas:
        raise RuntimeError("stratified family bin quotas changed")


def _stratified_family_examples(
    scenes,
    maximum_negative_per_positive: int,
    generator: torch.Generator,
    background_seed: int,
):
    prepared = []
    eligible_by_scene = []
    generic_budget = 0
    for scene in scenes:
        proposals = extract_proposals(scene.tensor)
        centers = torch.tensor(scene.centers, dtype=torch.float32).reshape(-1, 2)
        radii = torch.tensor(scene.diameters, dtype=torch.float32) / 2.0
        if len(centers):
            distance = torch.cdist(proposals.coordinates, centers)
            nearest, nearest_index = distance.min(dim=1)
            labels = nearest.le(3.0).float()
            offsets = (centers[nearest_index] - proposals.coordinates) / 4.0
            target_radii = radii[nearest_index]
        else:
            labels = torch.zeros(len(proposals.coordinates), dtype=torch.float32)
            offsets = torch.zeros_like(proposals.coordinates)
            target_radii = torch.zeros_like(labels)
        hard = torch.zeros(len(proposals.coordinates), dtype=torch.bool)
        for kind, x, y in scene.hard_negatives:
            radius = _hard_negative_radius(scene, kind)
            if radius is not None:
                point = torch.tensor(((x, y),), dtype=torch.float32)
                hard |= torch.cdist(proposals.coordinates, point).squeeze(1).le(radius)
        positive = torch.nonzero(labels > .5).flatten()
        hard_negative = torch.nonzero(hard & (labels <= .5)).flatten()
        negative_budget = len(positive) * maximum_negative_per_positive
        if len(hard_negative) > negative_budget:
            hard_negative = hard_negative[
                torch.randperm(len(hard_negative), generator=generator)[:negative_budget]
            ]
        generic_budget += max(0, negative_budget - len(hard_negative))
        eligible = torch.nonzero((labels <= .5) & ~hard).flatten()
        prepared.append(
            (proposals, labels, offsets, target_radii, hard, positive, hard_negative)
        )
        eligible_by_scene.append(eligible)

    background = select_stratified_background(
        tuple(item[0].patches for item in prepared),
        tuple(eligible_by_scene),
        generic_budget,
        background_seed,
    )
    values = [[] for _ in range(5)]
    selections = []
    proposal_coordinates = []
    for prepared_scene, generic_indices in zip(prepared, background.selections, strict=True):
        proposals, labels, offsets, target_radii, hard, positive, hard_negative = prepared_scene
        generic = torch.tensor(generic_indices, dtype=torch.int64)
        selected = torch.cat((positive, hard_negative, generic)).unique(sorted=True)
        selections.append(tuple(int(index) for index in selected.tolist()))
        proposal_coordinates.append(proposals.coordinates)
        values[0].append(proposals.patches[selected])
        values[1].append(labels[selected])
        values[2].append(offsets[selected])
        values[3].append(target_radii[selected])
        values[4].append(hard[selected])

    selected_count = sum(len(indices) for indices in selections)
    positive_count = sum(int((part > .5).sum()) for part in values[1])
    hard_count = sum(
        int((hard_part & (label_part <= .5)).sum())
        for label_part, hard_part in zip(values[1], values[4], strict=True)
    )
    report = StratifiedFamilySamplingReport(
        tuple(selections),
        {"selected": selected_count},
        {
            "positive": positive_count,
            "hard_negative": hard_count,
            "other_negative": selected_count - positive_count - hard_count,
        },
        _selected_index_sha256(scenes, selections, proposal_coordinates),
        {str(bin_id): capacity for bin_id, capacity in enumerate(background.capacities)},
        {str(bin_id): quota for bin_id, quota in enumerate(background.quotas)},
    )
    return tuple(torch.cat(part) for part in values) + (report,)


def _examples_with_report(
    scenes,
    maximum_negative_per_positive: int,
    generator: torch.Generator,
    *,
    sampling_mode: str,
    family_generic_negative_mode: str = FAMILY_GENERIC_NEGATIVE_PREFIX,
    family_generic_negative_seed: int | None = None,
):
    if maximum_negative_per_positive != 10:
        raise ValueError("V24 retry requires maximum_negative_per_positive=10")
    if sampling_mode not in {"real-range-train", "family-train", "subset-test"}:
        raise ValueError("sampling_mode must be 'real-range-train', 'family-train', or 'subset-test'")
    _validate_family_generic_negative_options(
        sampling_mode,
        family_generic_negative_mode,
        family_generic_negative_seed,
    )
    if not scenes:
        raise ValueError("scenes must not be empty")
    if sampling_mode == "real-range-train" and (
        len(scenes) != 167 or not all(scene.split == "train" for scene in scenes)
    ):
        raise ValueError("real-range production sampling requires the complete 167-scene train split")
    if sampling_mode == "family-train" and (
        not all(isinstance(scene, FamilyScene) for scene in scenes)
        or not all(scene.split == "train" for scene in scenes)
    ):
        raise ValueError("family production sampling requires train FamilyScene inputs")
    if family_generic_negative_mode == FAMILY_GENERIC_NEGATIVE_STRATIFIED:
        return _stratified_family_examples(
            scenes,
            maximum_negative_per_positive,
            generator,
            family_generic_negative_seed,
        )
    values = [[] for _ in range(5)]
    if sampling_mode != "real-range-train":
        background_generator = None
        if family_generic_negative_mode == FAMILY_GENERIC_NEGATIVE_UNIFORM:
            background_generator = torch.Generator(device="cpu").manual_seed(
                family_generic_negative_seed
            )
        selections = []
        proposal_coordinates = []
        for scene in scenes:
            proposals = extract_proposals(scene.tensor)
            proposal_coordinates.append(proposals.coordinates)
            centers = torch.tensor(scene.centers, dtype=torch.float32).reshape(-1, 2)
            radii = torch.tensor(scene.diameters, dtype=torch.float32) / 2.0
            if len(centers):
                distance = torch.cdist(proposals.coordinates, centers)
                nearest, nearest_index = distance.min(dim=1)
                labels = nearest.le(3.0).float()
                offsets = (centers[nearest_index] - proposals.coordinates) / 4.0
                target_radii = radii[nearest_index]
            else:
                # An actual crop may contain only background/artifacts. Retain
                # it in the split; the existing per-positive budget selects no
                # training examples from this panel, without inventing a target.
                labels = torch.zeros(len(proposals.coordinates), dtype=torch.float32)
                offsets = torch.zeros_like(proposals.coordinates)
                target_radii = torch.zeros_like(labels)
            hard = torch.zeros(len(proposals.coordinates), dtype=torch.bool)
            for kind, x, y in scene.hard_negatives:
                radius = _hard_negative_radius(scene, kind)
                if radius is not None:
                    hard |= torch.cdist(proposals.coordinates, torch.tensor(((x, y),), dtype=torch.float32)).squeeze(1).le(radius)
            positive = torch.nonzero(labels > .5).flatten()
            hard_negative = torch.nonzero(hard & (labels <= .5)).flatten()
            negative_budget = len(positive) * maximum_negative_per_positive
            if len(hard_negative) > negative_budget:
                hard_negative = hard_negative[
                    torch.randperm(len(hard_negative), generator=generator)[:negative_budget]
                ]
            negative = torch.nonzero((labels <= .5) & ~hard).flatten()
            generic_budget = max(0, negative_budget - len(hard_negative))
            if (
                background_generator is not None
                and len(negative) > generic_budget
            ):
                order = torch.randperm(len(negative), generator=background_generator)
                negative = negative.index_select(0, order[:generic_budget])
            else:
                negative = negative[:generic_budget]
            selected = torch.cat((positive, hard_negative, negative)).unique(sorted=True)
            selections.append(tuple(int(index) for index in selected.tolist()))
            values[0].append(proposals.patches[selected]); values[1].append(labels[selected]); values[2].append(offsets[selected]); values[3].append(target_radii[selected]); values[4].append(hard[selected])
        selected_count = sum(len(indices) for indices in selections)
        positive_count = sum(int((part > .5).sum()) for part in values[1])
        hard_count = sum(
            int((hard_part & (label_part <= .5)).sum())
            for label_part, hard_part in zip(values[1], values[4], strict=True)
        )
        report = SampledNegatives(
            tuple(selections),
            {"selected": selected_count},
            {
                "positive": positive_count,
                "hard_negative": hard_count,
                "other_negative": selected_count - positive_count - hard_count,
            },
            _selected_index_sha256(scenes, selections, proposal_coordinates)
            if sampling_mode == "family-train"
            else "subset-test",
        )
        return tuple(torch.cat(part) for part in values) + (report,)
    records = []
    prepared = []
    for scene in scenes:
        proposals = extract_proposals(scene.tensor)
        coords = proposals.coordinates; centers = torch.tensor(scene.centers, dtype=torch.float32); radii = torch.tensor(scene.diameters, dtype=torch.float32) / 2.0
        distance = torch.cdist(coords, centers); nearest, nearest_index = distance.min(dim=1); labels = nearest.le(3.0).float()
        hard = torch.zeros(len(coords), dtype=torch.bool)
        for kind, x, y in scene.hard_negatives:
            radius = _hard_negative_radius(scene, kind)
            if radius is not None:
                hard |= torch.cdist(coords, torch.tensor(((x, y),), dtype=torch.float32)).squeeze(1).le(radius)
        positive = torch.nonzero(labels > .5).flatten()
        records.append((scene, proposals, labels, hard))
        prepared.append((scene, proposals, labels, hard, centers, nearest_index, radii))
    sampled = sample_negatives(records, split="train", seed=20260904)
    for scene_number, (scene, proposals, labels, hard, centers, nearest_index, radii) in enumerate(prepared):
        positive = torch.nonzero(labels > .5).flatten()
        negative = torch.tensor(sampled.selections[scene_number], dtype=torch.long)
        selected = torch.cat((positive, negative)).unique(sorted=True)
        values[0].append(proposals.patches[selected]); values[1].append(labels[selected]); values[2].append((centers[nearest_index]-proposals.coordinates).index_select(0,selected)/4.0); values[3].append(radii[nearest_index].index_select(0,selected)); values[4].append(hard[selected])
    return tuple(torch.cat(part) for part in values) + (sampled,)

def _examples(scenes, maximum_negative_per_positive: int, generator: torch.Generator):
    return _examples_with_report(
        scenes,
        maximum_negative_per_positive,
        generator,
        sampling_mode="subset-test",
    )[:5]

def _evaluate(scenes, model, threshold):
    tp=fp=fn=dup=hits=truth=proposal_tp=0
    for scene in scenes:
        proposals=extract_proposals(scene.tensor); output=model(proposals.patches).detach().numpy(); predictions=postprocess(scene, proposals, output) if threshold == .25 else postprocess(scene, proposals, output)
        # Sensitivity thresholds are represented by filtering model probabilities here.
        predictions=tuple(p for p in predictions if p.confidence >= threshold)
        truth += len(scene.centers)
        edges=sorted((float(np.hypot(p.x-x,p.y-y)),i,j) for i,p in enumerate(predictions) for j,(x,y) in enumerate(scene.centers) if np.hypot(p.x-x,p.y-y)<=5)
        usedp=set(); usedt=set()
        for _,i,j in edges:
            if i not in usedp and j not in usedt: usedp.add(i); usedt.add(j)
        proposal_truths = {
            truth_index for x, y in proposals.coordinates.tolist()
            for truth_index, (truth_x, truth_y) in enumerate(scene.centers)
            if np.hypot(x-truth_x, y-truth_y) <= 5
        }
        metrics = center_metrics(predictions, scene.centers, 5.0)
        tp += len(usedp); fp += len(predictions)-len(usedp); fn += len(scene.centers)-len(usedt); proposal_tp += len(proposal_truths); dup += metrics.duplicate_count; hits += sum(prohibited_hits(predictions,scene).values())
    precision=tp/max(1,tp+fp); recall=tp/max(1,truth); f1=2*precision*recall/max(1e-12,precision+recall)
    return {"threshold":threshold,"scene_count":len(scenes),"proposal_true_positives":proposal_tp,"proposal_recall":proposal_tp/max(1,truth),"true_positives":tp,"false_positives":fp,"false_negatives":fn,"precision":precision,"recall":recall,"f1":f1,"duplicate_count":dup,"prohibited_structure_hits":hits,"prohibited_structure_hit_rate":hits/max(1,tp+fp)}

def run(
    output_dir: Path,
    checkpoint: Path,
    v21_onnx: Path,
    config_path: Path = CONFIG_PATH,
) -> dict:
    if output_dir.exists(): raise RuntimeError(f"candidate output already exists: {output_dir}")
    config_path, config_sha256, config = _load_config(config_path)
    family_generic_negative_mode, family_generic_negative_seed = (
        _family_generic_negative_config(config)
    )
    if _sha(checkpoint) != config["checkpoint_sha256"]: raise ValueError("V21 checkpoint hash changed")
    if _sha(v21_onnx) != config["v21_onnx_sha256"]: raise ValueError("V21 ONNX hash changed")
    for path_key, hash_key in (("feasibility_path","feasibility_sha256"),("retry_diagnosis_path","retry_diagnosis_sha256"),("morphology_diagnosis_path","morphology_diagnosis_sha256"),("morphology_gap_path","morphology_gap_sha256"),("retry3_morphology_gap_path","retry3_morphology_gap_sha256"),("retry4_diagnosis_path","retry4_diagnosis_sha256"),("retry4_generic_fp_diagnosis_path","retry4_generic_fp_diagnosis_sha256"),("retry5_diagnosis_path","retry5_diagnosis_sha256"),("retry5_generic_fp_diagnosis_path","retry5_generic_fp_diagnosis_sha256"),("retry6_diagnosis_path","retry6_diagnosis_sha256"),("retry7_diagnosis_path","retry7_diagnosis_sha256"),("retry7_morphology_diagnosis_path","retry7_morphology_diagnosis_sha256"),("retry7_morphology_gap_path","retry7_morphology_gap_sha256"),("retry8_result_path","retry8_result_sha256"),("retry8_diagnosis_path","retry8_diagnosis_sha256"),("generator_audit_path","generator_audit_sha256"),("negative_audit_path","negative_audit_sha256"),("negative_gap_path","negative_gap_sha256"),("family_dev_baseline_path","family_dev_baseline_sha256"),("evidence_policy_path","evidence_policy_sha256"),("acceptance_bars_path","acceptance_bars_sha256")):
        if _sha(REPO_ROOT/str(config[path_key])) != config[hash_key]: raise ValueError(f"bound input changed: {config[path_key]}")
    if _sha(REPO_ROOT/config["negative_sampler"]["source_path"]) != config["negative_sampler"]["source_sha256"]: raise ValueError("negative sampler source changed")
    anti_aliasing = config.get("anti_aliasing")
    if anti_aliasing is None or tuple(float(value) for value in anti_aliasing.get("blur_radii_px", ())) != ANTI_ALIAS_BLUR_RADII or anti_aliasing.get("scene_index_schedule") != "ANTI_ALIAS_BLUR_RADII[index % len(ANTI_ALIAS_BLUR_RADII)]":
        raise ValueError("anti-aliasing schedule does not match generator constant")
    if float(config["family_hard_negative_radius_px"]) != FAMILY_HARD_NEGATIVE_RADIUS_PX:
        raise ValueError("five-axis family hard-negative radius changed")
    if _sha(REPO_ROOT / config["runtime_input_preflight_path"]) != config["runtime_input_preflight_sha256"]:
        raise RuntimeError("runtime input preflight identity changed")
    family_join, binding_schema, binding_elapsed_ms, authorization = (
        _prepare_bound_family_inputs(config, config_path, config_sha256)
    )
    output_dir.mkdir(parents=True); report_path=output_dir/"candidate-report.json"; started=time.perf_counter(); phase="initialization"
    try:
        _configure(config["seed"]); split_audit=generator_audit(independent_layout=True)
        layout_audit = split_audit["layout_family_audit"]
        if not layout_audit["independent_layout_required"] or not layout_audit["train_dev_family_disjoint"] or not layout_audit["train_dev_layout_disjoint"]:
            raise RuntimeError("independent family-disjoint dev layout contract failed")
        family_audit = family_holdout_audit()
        if not family_audit["train_dev_family_disjoint"]:
            raise RuntimeError("five-axis synthetic family holdout is not disjoint")
        if family_audit["splits"]["train"]["aggregate_sha256"] != config["family_train_split_sha256"] or family_audit["splits"]["dev"]["aggregate_sha256"] != config["family_dev_split_sha256"]:
            raise RuntimeError("five-axis synthetic family split changed")
        train=build_split("train"); dev=build_split("dev", independent_layout=True)
        family_train=family_join.train; family_dev=family_join.dev
        if _tensor_set_sha256(family_train) != config["family_train_tensor_set_sha256"] or _tensor_set_sha256(family_dev) != config["family_dev_tensor_set_sha256"]:
            raise RuntimeError("five-axis synthetic family tensors changed")
        gen=torch.Generator().manual_seed(config["seed"]+1)
        real_values=_examples_with_report(train,config["maximum_negative_per_positive"],gen,sampling_mode="real-range-train")
        family_values=_examples_with_report(
            family_train,
            config["maximum_negative_per_positive"],
            gen,
            sampling_mode="family-train",
            family_generic_negative_mode=family_generic_negative_mode,
            family_generic_negative_seed=family_generic_negative_seed,
        )
        real_patches,real_labels,real_offsets,real_radii,real_hard,sampling=real_values
        family_patches,family_labels,family_offsets,family_radii,family_hard,family_sampling=family_values
        if len(real_labels) != config["real_range_training_example_count_expected"] or int((real_labels>.5).sum()) != config["real_range_positive_example_count_expected"] or int(real_hard.sum()) != config["real_range_hard_negative_example_count_expected"]:
            raise RuntimeError("real-range training example contract changed")
        if len(family_labels) != config["family_training_example_count_expected"] or int((family_labels>.5).sum()) != config["family_positive_example_count_expected"] or int(family_hard.sum()) != config["family_hard_negative_example_count_expected"]:
            raise RuntimeError("five-axis family training example contract changed")
        if family_sampling.selected_index_sha256 != config["family_selected_index_sha256"]:
            raise RuntimeError("five-axis family selected indices changed")
        if family_generic_negative_mode == FAMILY_GENERIC_NEGATIVE_STRATIFIED:
            _validate_stratified_family_sampling_config(config, family_sampling)
        family_sampling_outcome = {
            "mode": "family-train",
            "capacities": family_sampling.capacities,
            "counts": family_sampling.counts,
            "selected_index_sha256": family_sampling.selected_index_sha256,
        }
        if family_generic_negative_mode in {
            FAMILY_GENERIC_NEGATIVE_UNIFORM,
            FAMILY_GENERIC_NEGATIVE_STRATIFIED,
        }:
            family_sampling_outcome.update({
                "generic_negative_mode": family_generic_negative_mode,
                "generic_negative_seed": family_generic_negative_seed,
            })
        if family_generic_negative_mode == FAMILY_GENERIC_NEGATIVE_STRATIFIED:
            family_sampling_outcome.update({
                "stratified_bin_capacities": family_sampling.stratified_bin_capacities,
                "stratified_bin_quotas": family_sampling.stratified_bin_quotas,
            })
        patches=torch.cat((real_patches,family_patches)); labels=torch.cat((real_labels,family_labels)); offsets=torch.cat((real_offsets,family_offsets)); radii=torch.cat((real_radii,family_radii)); hard=torch.cat((real_hard,family_hard))
        sampler_config = config["negative_sampler"]
        if sampling.capacities != sampler_config["expected_capacities"] or sampling.selected_index_sha256 != sampler_config["selected_index_sha256"] or sampling.counts != sampler_config["quotas"]:
            raise RuntimeError("negative sampler contract changed")
        topology_config = sampler_config["topology"]
        if float(topology_config["radius_px"]) != TOPOLOGY_SAMPLER_RADIUS_PX:
            raise RuntimeError("topology radius contract changed")
        if float(topology_config["input_audit_radius_px"]) != TOPOLOGY_RADIUS_PX:
            raise RuntimeError("topology input-audit radius contract changed")
        if sampling.topology_capacity != topology_config["expected_capacity"] or sampling.topology_selected != topology_config["expected_selected"] or sampling.topology_selected_index_sha256 != topology_config["selected_index_sha256"]:
            raise RuntimeError("topology sampling contract changed")
        topology_hard_config = topology_config["hard"]
        if float(topology_hard_config["radius_px"]) != TOPOLOGY_HARD_RADIUS_PX:
            raise RuntimeError("topology hard radius contract changed")
        if sampling.topology_hard_capacity != topology_hard_config["expected_capacity"] or sampling.topology_hard_selected != topology_hard_config["expected_selected"] or sampling.hard_training_total != topology_hard_config["hard_training_total"]:
            raise RuntimeError("topology hard sampling contract changed")
        connector_config = sampler_config["connector"]
        if float(connector_config["max_distance_px"]) != CONNECTOR_ANCHOR_MAX_DISTANCE_PX:
            raise RuntimeError("connector anchor distance contract changed")
        if float(connector_config["endpoint_offset_px"]) != CONNECTOR_ENDPOINT_OFFSET_PX:
            raise RuntimeError("connector endpoint offset contract changed")
        if sampling.connector_anchor_target_count != connector_config["target_count"] or sampling.connector_anchor_capacity != connector_config["expected_capacity"] or sampling.connector_anchor_selected != connector_config["expected_selected"] or sampling.connector_anchor_selected_index_sha256 != connector_config["selected_index_sha256"] or sampling.generic_remainder_selected != connector_config["generic_remainder_selected"]:
            raise RuntimeError("connector anchor sampling contract changed")
        band_config = sampler_config["generic_connector_band"]
        if float(band_config["radius_px"]) != GENERIC_CONNECTOR_BAND_RADIUS_PX or sampling.capacities["generic_connector_band"] != band_config["expected_capacity"] or sampling.counts["generic_connector_band"] != band_config["expected_selected"] or sampling.generic_connector_band_selected_index_sha256 != band_config["selected_index_sha256"]:
            raise RuntimeError("generic connector-band sampling contract changed")
        if len(labels) != config["training_example_count_expected"] or int((labels>.5).sum()) != config["positive_example_count_expected"] or int(hard.sum()) != config["hard_negative_example_count_expected"]: raise RuntimeError("training example contract changed")
        payload=torch.load(checkpoint,map_location="cpu",weights_only=False); model=ScaleClassifierNet(ModelConfig(seed=20260902)); model.load_state_dict(payload["state_dict"]); optimizer=torch.optim.AdamW(model.parameters(),lr=config["learning_rate"],weight_decay=config["weight_decay"]); steps=0; phase="training"; model.train()
        for epoch in range(config["epochs"]):
            order=torch.randperm(len(labels),generator=gen)
            for start in range(0,len(labels),config["batch_size"]):
                index=order[start:start+config["batch_size"]]; loss=v21_loss(model.forward_raw(patches[index]),labels[index],offsets[index],radii[index],hard[index],positive_weight=config["positive_loss_weight"],hard_weight=config["hard_negative_loss_weight"],alpha=config["focal_alpha"],gamma=config["focal_gamma"]); optimizer.zero_grad(set_to_none=True); loss.backward(); torch.nn.utils.clip_grad_norm_(model.parameters(),5.0); optimizer.step(); steps+=1
            if (epoch + 1) % 6 == 0 or epoch + 1 == config["epochs"]:
                print(json.dumps({"phase": "training", "epoch": epoch + 1,
                                  "epochs": config["epochs"], "optimizer_steps": steps,
                                  "elapsed_seconds": round(time.perf_counter() - started, 3)}), flush=True)
        if steps != config["optimizer_steps_expected"] or steps > config["optimizer_steps_maximum"]: raise RuntimeError(f"optimizer step contract changed: {steps}")
        phase="dev"; model.eval(); comparisons=[_evaluate(dev,model,t) for t in [0.25,*config["selection_thresholds"]]]; family_comparisons=[_evaluate(family_dev,model,t) for t in [0.25,*config["selection_thresholds"]]]; selected=comparisons[0]; family_selected=family_comparisons[0]; bar=_shared_marker_acceptance_bar(); real_range_dev_passed=_passes_marker_bar(selected,bar); family_dev_passed=_passes_marker_bar(family_selected,bar); dev_passed=_passes_required_dev_gates(selected,family_selected,bar)
        phase="export"; out_pt=output_dir/"marker-center-mask-preserving-v24-p1.pt"; torch.save({"state_dict":model.state_dict(),"config":model.export_contract()},out_pt); out_onnx=output_dir/"marker-center-mask-preserving-v24-p1.onnx"; torch.onnx.export(model,torch.zeros((1,3,33,33)),out_onnx,input_names=["candidate_patches"],output_names=["candidate_predictions"],dynamic_axes={"candidate_patches":{0:"candidate_count"},"candidate_predictions":{0:"candidate_count"}},opset_version=18,dynamo=False); onnx.checker.check_model(onnx.load(out_onnx)); session=ort.InferenceSession(str(out_onnx),providers=[protocol.PROVIDER]);
        if session.get_providers()[0] != protocol.PROVIDER: raise RuntimeError("CPUExecutionProvider was not selected")
        parity=[]; parity_source=extract_proposals(dev[0].tensor).patches
        for count in [1,8,37]:
            x=parity_source[:count].contiguous(); expected=model(x).detach().numpy(); actual=session.run(["candidate_predictions"],{"candidate_patches":x.numpy()})[0]; parity.append({"candidate_count":count,"maximum_absolute_error":float(np.max(np.abs(expected-actual)))})
        report={"schema":"graphreader.marker-center-mask-preserving-v24-candidate.v1","task":protocol.TASK,"revision":protocol.TRAINING_REVISION,"candidate_id":protocol.TRAINING_CANDIDATE_ID,"candidate_config_path":config_path.as_posix(),"candidate_config_sha256":config_sha256,"status":"dev_passed" if dev_passed and max(r["maximum_absolute_error"] for r in parity)<=config["onnx_parity_tolerance"] else "failed_dev","synthetic_only":True,"private_data":False,"real_dev_reads":0,"real_sealed_reads":0,"sealed_runs":0,"optimizer_steps":steps,"training_example_count":len(labels),"positive_example_count":int((labels>.5).sum()),"hard_negative_example_count":int(hard.sum()),"real_range_training_example_count":len(real_labels),"family_training_example_count":len(family_labels),"family_positive_example_count":int((family_labels>.5).sum()),"family_hard_negative_example_count":int(family_hard.sum()),"negative_sampling":{"seed":20260904,"split":"train","capacities":sampling.capacities,"counts":sampling.counts,"selected_index_sha256":sampling.selected_index_sha256,"topology_radius_px":topology_config["radius_px"],"topology_capacity":sampling.topology_capacity,"topology_selected":sampling.topology_selected,"topology_selected_index_sha256":sampling.topology_selected_index_sha256,"topology_hard_radius_px":topology_hard_config["radius_px"],"topology_hard_capacity":sampling.topology_hard_capacity,"topology_hard_selected":sampling.topology_hard_selected,"hard_training_total":sampling.hard_training_total,"connector_endpoint_offset_px":connector_config["endpoint_offset_px"],"connector_anchor_max_distance_px":connector_config["max_distance_px"],"connector_anchor_target_count":sampling.connector_anchor_target_count,"connector_anchor_capacity":sampling.connector_anchor_capacity,"connector_anchor_selected":sampling.connector_anchor_selected,"connector_anchor_selected_index_sha256":sampling.connector_anchor_selected_index_sha256,"generic_connector_band_radius_px":band_config["radius_px"],"generic_connector_band_capacity":sampling.capacities["generic_connector_band"],"generic_connector_band_selected":sampling.counts["generic_connector_band"],"generic_connector_band_selected_index_sha256":sampling.generic_connector_band_selected_index_sha256,"generic_remainder_selected":sampling.generic_remainder_selected},"family_sampling":family_sampling_outcome,"generator_layout_family_audit":layout_audit,"five_axis_family_audit":family_audit,"family_train_tensor_set_sha256":config["family_train_tensor_set_sha256"],"family_dev_tensor_set_sha256":config["family_dev_tensor_set_sha256"],"acceptance_bar":bar,"dev_comparisons":comparisons,"family_dev_comparisons":family_comparisons,"selected":selected,"family_selected":family_selected,"real_range_dev_gate_passed":real_range_dev_passed,"family_dev_gate_passed":family_dev_passed,"dev_gate_passed":dev_passed,"checkpoint_sha256":_sha(out_pt),"onnx_sha256":_sha(out_onnx),"v21_checkpoint_sha256":config["checkpoint_sha256"],"v21_onnx_sha256":config["v21_onnx_sha256"],"onnx_provider":protocol.PROVIDER,"onnx_dynamic_candidate_counts":parity,"onnx_parity_maximum_absolute_error":max(r["maximum_absolute_error"] for r in parity),"elapsed_ms":round((time.perf_counter()-started)*1000,3),"production_approval":False,"release_eligible":False}
        report["runtime_training_inputs"] = {
            "binding_path": config["runtime_training_input_binding_path"],
            "binding_sha256": config["runtime_training_input_binding_sha256"],
            "binding_schema": binding_schema,
            "preflight_elapsed_ms": binding_elapsed_ms,
            "family_train_panel_count": len(family_join.train),
            "family_dev_panel_count": len(family_join.dev),
            "family_train_source_count": len({scene.source_sha256 for scene in family_join.train}),
            "family_dev_source_count": len({scene.source_sha256 for scene in family_join.dev}),
            "annotation_masks_used": False,
            "complete_artifact_mask": False,
            "production_approved": False,
        }
    except Exception as error:
        report={"schema":"graphreader.marker-center-mask-preserving-v24-failure.v1","task":protocol.TASK,"revision":protocol.TRAINING_REVISION,"candidate_id":protocol.TRAINING_CANDIDATE_ID,"candidate_config_path":config_path.as_posix(),"candidate_config_sha256":config_sha256,"status":"failed_runner","phase":phase,"exception_type":type(error).__name__,"exception_message":str(error),"synthetic_only":True,"private_data":False,"real_dev_reads":0,"real_sealed_reads":0,"sealed_runs":0}
        report_path.write_bytes(canonical_json_bytes(report)); void_candidate(authorization,error); raise
    report_path.write_bytes(canonical_json_bytes(report)); complete_training_candidate(authorization,status=report["status"],report_sha256=sha256_file(report_path)); return report

def main():
    p=argparse.ArgumentParser(); p.add_argument("--output-dir",type=Path,required=True); p.add_argument("--checkpoint",type=Path,required=True); p.add_argument("--onnx",type=Path,required=True); p.add_argument("--config",type=Path,default=CONFIG_PATH); a=p.parse_args(); print(json.dumps(run(a.output_dir.resolve(),a.checkpoint.resolve(),a.onnx.resolve(),a.config),indent=2,sort_keys=True))
if __name__ == "__main__": main()
