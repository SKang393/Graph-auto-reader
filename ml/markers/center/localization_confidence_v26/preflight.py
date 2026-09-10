# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Model-free target audit for localization-aware marker confidence.

This module does not define or authorize a candidate revision. It authenticates
the frozen V25 training inventory and measures one proposed target composition:
one confidence-positive proposal per reachable truth, with the remaining V25
positive proposals retained for regression but ignored by classification.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import time
from typing import Any, Sequence

import torch
from torch import Tensor

from ml.markers.center.mask_preserving_v24 import train_p1 as v24
from ml.markers.center.plot_domain_v25 import train_p1 as v25
from ml.markers.center.plot_domain_v25.proposal_domain import extract_proposals_in_domain
from ml.markers.gate_seal import canonical_json_bytes, sha256_file


REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
OUTPUT_SCHEMA = "graphreader.marker-center-localization-confidence-v26-target-preflight.v1"
POSITIVE_DISTANCE_PX = 3.0
PROPOSAL_SUPPORT_DISTANCE_PX = 5.0
V25_RESULT_PATH = Path("ml/markers/center/plot_domain_v25/P1_RESULT.json")


@dataclass(frozen=True)
class SceneInventory:
    """One V25 scene's complete proposals and selected training rows."""

    identity: str
    proposal_coordinates: Tensor
    selected_indices: tuple[int, ...]
    truth_centers: tuple[tuple[float, float], ...]
    selected_patches: Tensor
    selected_labels: Tensor
    selected_offsets: Tensor
    selected_radii: Tensor
    selected_hard: Tensor


@dataclass(frozen=True)
class TargetAssignment:
    """Proposed masks over the unchanged selected V25 rows."""

    row_ids: tuple[dict[str, object], ...]
    confidence_targets: Tensor
    classification_mask: Tensor
    regression_mask: Tensor
    reachable_truth_count: int
    zero_eligible_truth_count: int
    proposal_only_3_to_5_truth_count: int
    outside_5_truth_count: int
    within_3_but_assigned_elsewhere_truth_count: int


def _require_finite_2d(value: Tensor, label: str) -> Tensor:
    tensor = torch.as_tensor(value, dtype=torch.float32)
    if tensor.ndim != 2 or tensor.shape[1] != 2 or not bool(torch.isfinite(tensor).all()):
        raise ValueError(f"{label} must be finite [count,2] coordinates")
    return tensor


def assign_unique_nearest_confidence(scene: SceneInventory) -> TargetAssignment:
    """Compose deterministic confidence/regression masks for one scene.

    Eligibility exactly mirrors V25: a proposal belongs to its nearest truth
    (lowest truth index breaks an exact distance tie) and is positive at <=3 px.
    For each reachable truth, the confidence target is the eligible proposal
    ordered first by squared distance, then x, y, and source proposal index.
    """

    coordinates = _require_finite_2d(scene.proposal_coordinates, "proposal coordinates")
    truths = _require_finite_2d(
        torch.tensor(scene.truth_centers, dtype=torch.float32).reshape(-1, 2),
        "truth centers",
    )
    selected = scene.selected_indices
    if tuple(sorted(set(selected))) != selected:
        raise ValueError("selected proposal indices must be unique and sorted")
    if any(index < 0 or index >= len(coordinates) for index in selected):
        raise ValueError("selected proposal index is outside the proposal inventory")
    count = len(selected)
    for label, value, suffix in (
        ("patches", scene.selected_patches, (3, 33, 33)),
        ("labels", scene.selected_labels, ()),
        ("offsets", scene.selected_offsets, (2,)),
        ("radii", scene.selected_radii, ()),
        ("hard flags", scene.selected_hard, ()),
    ):
        if tuple(value.shape) != (count, *suffix):
            raise ValueError(f"selected {label} do not match selected proposal indices")
    labels = scene.selected_labels.to(torch.float32)
    if not bool(torch.isfinite(labels).all()) or not bool(torch.all((labels == 0) | (labels == 1))):
        raise ValueError("selected labels must be finite binary values")
    if not bool(torch.isfinite(scene.selected_patches).all()):
        raise ValueError("selected patches must be finite")
    if not bool(torch.isfinite(scene.selected_offsets).all()) or not bool(torch.isfinite(scene.selected_radii).all()):
        raise ValueError("selected regression targets must be finite")

    selected_position = {proposal_index: row for row, proposal_index in enumerate(selected)}
    eligible_by_truth: list[list[tuple[float, float, float, int]]] = [
        [] for _ in range(len(truths))
    ]
    nearest_to_truth = [math.inf] * len(truths)
    existing_positive: set[int] = set()
    if len(truths):
        # Use the same float32 cdist and <= comparison as V25. Recomputing in
        # Python double precision can change membership exactly at 3 pixels.
        distance_matrix = torch.cdist(coordinates, truths)
        nearest_distance, nearest_truth_index = distance_matrix.min(dim=1)
        nearest_to_truth = [float(value) for value in distance_matrix.min(dim=0).values]
        for proposal_index, coordinate in enumerate(coordinates.tolist()):
            distance = float(nearest_distance[proposal_index])
            truth_index = int(nearest_truth_index[proposal_index])
            if bool(nearest_distance[proposal_index].le(POSITIVE_DISTANCE_PX)):
                existing_positive.add(proposal_index)
                eligible_by_truth[truth_index].append(
                    (distance * distance, float(coordinate[0]), float(coordinate[1]), proposal_index)
                )

    missing = existing_positive.difference(selected_position)
    if missing:
        raise ValueError("V25 selected inventory omitted an eligible positive proposal")
    selected_positive = {
        proposal_index for row, proposal_index in enumerate(selected) if labels[row].item() > 0.5
    }
    if selected_positive != existing_positive:
        raise ValueError("selected labels differ from the V25 <=3 px positive definition")

    chosen: set[int] = set()
    for eligible in eligible_by_truth:
        if eligible:
            chosen.add(min(eligible)[3])
    if len(chosen) != sum(bool(value) for value in eligible_by_truth):
        raise RuntimeError("one proposal was selected as the confidence target for multiple truths")

    confidence_targets = torch.zeros(count, dtype=torch.float32)
    classification_mask = labels <= 0.5
    regression_mask = labels > 0.5
    for proposal_index in chosen:
        row = selected_position[proposal_index]
        confidence_targets[row] = 1.0
        classification_mask[row] = True

    row_ids = tuple(
        {
            "scene": scene.identity,
            "proposal_index": proposal_index,
            "coordinates": [
                float(coordinates[proposal_index, 0]),
                float(coordinates[proposal_index, 1]),
            ],
        }
        for proposal_index in selected
    )
    reachable = sum(bool(value) for value in eligible_by_truth)
    assigned_elsewhere = sum(
        not eligible_by_truth[index] and distance <= POSITIVE_DISTANCE_PX
        for index, distance in enumerate(nearest_to_truth)
    )
    only_3_to_5 = sum(
        POSITIVE_DISTANCE_PX < distance <= PROPOSAL_SUPPORT_DISTANCE_PX
        for distance in nearest_to_truth
    )
    outside_5 = sum(distance > PROPOSAL_SUPPORT_DISTANCE_PX for distance in nearest_to_truth)
    return TargetAssignment(
        row_ids,
        confidence_targets,
        classification_mask,
        regression_mask,
        reachable,
        len(truths) - reachable,
        only_3_to_5,
        outside_5,
        assigned_elsewhere,
    )


def _tensor_digest(values: Sequence[tuple[str, Tensor]]) -> str:
    digest = hashlib.sha256()
    for name, tensor in values:
        value = tensor.detach().cpu().contiguous()
        metadata = canonical_json_bytes(
            {"name": name, "dtype": str(value.dtype), "shape": list(value.shape)}
        )
        digest.update(len(metadata).to_bytes(8, "little"))
        digest.update(metadata)
        digest.update((value.numel() * value.element_size()).to_bytes(8, "little"))
        flattened = value.view(-1)
        for start in range(0, value.numel(), 1_048_576):
            digest.update(flattened[start:start + 1_048_576].numpy().tobytes(order="C"))
    return digest.hexdigest()


def summarize_assignments(
    scenes: Sequence[SceneInventory],
) -> tuple[dict[str, object], tuple[TargetAssignment, ...]]:
    assignments = tuple(assign_unique_nearest_confidence(scene) for scene in scenes)
    rows = tuple(row for assignment in assignments for row in assignment.row_ids)
    confidence = torch.cat(tuple(item.confidence_targets for item in assignments))
    classification = torch.cat(tuple(item.classification_mask for item in assignments))
    regression = torch.cat(tuple(item.regression_mask for item in assignments))
    labels = torch.cat(tuple(scene.selected_labels.to(torch.float32) for scene in scenes))
    truths = sum(len(scene.truth_centers) for scene in scenes)
    report: dict[str, object] = {
        "scene_count": len(scenes),
        "truth_count": truths,
        "selected_row_count": len(rows),
        "v25_positive_row_count": int((labels > 0.5).sum()),
        "v25_negative_row_count": int((labels <= 0.5).sum()),
        "confidence_positive_row_count": int(confidence.sum()),
        "regression_positive_row_count": int(regression.sum()),
        "classification_ignored_regression_row_count": int((regression & ~classification).sum()),
        "zero_eligible_truth_count": sum(item.zero_eligible_truth_count for item in assignments),
        "proposal_only_3_to_5_truth_count": sum(item.proposal_only_3_to_5_truth_count for item in assignments),
        "outside_5_truth_count": sum(item.outside_5_truth_count for item in assignments),
        "within_3_but_assigned_elsewhere_truth_count": sum(
            item.within_3_but_assigned_elsewhere_truth_count for item in assignments
        ),
        "sampled_row_identity_sha256": hashlib.sha256(canonical_json_bytes(rows)).hexdigest(),
        "target_assignment_sha256": _tensor_digest((
            ("confidence_targets", confidence),
            ("classification_mask", classification),
            ("regression_mask", regression),
        )),
    }
    if report["confidence_positive_row_count"] != truths - report["zero_eligible_truth_count"]:
        raise RuntimeError("confidence target count does not equal reachable truth count")
    return report, assignments


def exact_patch_label_collision_report(
    patches: Tensor,
    labels: Tensor,
    radii: Tensor,
) -> dict[str, object]:
    """Count byte-identical selected patches carrying opposing V25 labels."""

    if tuple(patches.shape[1:]) != (3, 33, 33) or len(patches) != len(labels) or len(labels) != len(radii):
        raise ValueError("collision inputs must be aligned 3x33x33 training rows")
    if not bool(torch.isfinite(labels).all()):
        raise ValueError("collision inputs must be finite")
    groups: dict[str, list[int]] = {}
    for index in range(len(patches)):
        patch = patches[index].detach().cpu().contiguous()
        if not bool(torch.isfinite(patch).all()):
            raise ValueError("collision inputs must be finite")
        raw = patch.numpy().tobytes(order="C")
        groups.setdefault(hashlib.sha256(raw).hexdigest(), []).append(index)
    mixed = [
        indices for indices in groups.values()
        if any(labels[index].item() > 0.5 for index in indices)
        and any(labels[index].item() <= 0.5 for index in indices)
    ]
    positive_indices = [index for indices in mixed for index in indices if labels[index].item() > 0.5]
    negative_indices = [index for indices in mixed for index in indices if labels[index].item() <= 0.5]
    return {
        "unique_patch_sha256_count": len(groups),
        "opposing_label_patch_sha256_count": len(mixed),
        "positive_rows_in_opposing_label_groups": len(positive_indices),
        "negative_rows_in_opposing_label_groups": len(negative_indices),
        "positive_rows_by_truth_radius": {
            "above_8px": sum(float(radii[index]) > 8.0 for index in positive_indices),
            "within_2_5_to_8px": sum(2.5 <= float(radii[index]) <= 8.0 for index in positive_indices),
            "below_2_5px": sum(float(radii[index]) < 2.5 for index in positive_indices),
        },
    }


def _scene_inventories(
    scenes: Sequence[Any],
    values: tuple[Any, ...],
    *,
    family_domain: bool,
) -> tuple[SceneInventory, ...]:
    sampling = values[5]
    recorded_selections = sampling.selections
    if len(recorded_selections) < len(scenes):
        raise RuntimeError("V25 scene and selection inventories differ")
    if family_domain and len(recorded_selections) != len(scenes):
        raise RuntimeError("V25 family scene and selection inventories differ")
    if not family_domain and any(recorded_selections[len(scenes):]):
        raise RuntimeError("V25 real-range sampler has populated trailing selections")
    # The frozen V24 real-range sampler appends surplus empty lists while it
    # enumerates negative proposals. Its trainer consumes only the scene-count
    # prefix. Mirror that historical behavior without treating trailing rows as
    # scenes or silently accepting populated trailing selections.
    recorded_selections = recorded_selections[:len(scenes)]
    inventories: list[SceneInventory] = []
    offset = 0
    for bound, recorded in zip(scenes, recorded_selections, strict=True):
        scene = bound.scene if family_domain else bound
        proposals = (
            extract_proposals_in_domain(scene.tensor, bound.panel_domain.domain).proposals
            if family_domain
            else v24.extract_proposals(scene.tensor)
        )
        if family_domain:
            # V25 family sampling records its complete selected inventory.
            selected = tuple(recorded)
        else:
            # The inherited real-range sampler records sampled negatives only;
            # V24 then unions them with every <=3 px positive before gathering.
            centers = torch.tensor(scene.centers, dtype=torch.float32).reshape(-1, 2)
            if not len(centers):
                raise RuntimeError("V25 component scene unexpectedly has no marker truths")
            positive = torch.nonzero(
                torch.cdist(proposals.coordinates, centers).min(dim=1).values.le(POSITIVE_DISTANCE_PX),
                as_tuple=False,
            ).flatten().tolist()
            selected = tuple(sorted(set(positive).union(int(index) for index in recorded)))
        count = len(selected)
        identity = (
            scene.sampling_identity
            if family_domain
            else f"{scene.split}:{scene.family}:{scene.seed}"
        )
        inventories.append(SceneInventory(
            identity,
            proposals.coordinates,
            tuple(selected),
            tuple((float(x), float(y)) for x, y in scene.centers),
            values[0][offset:offset + count],
            values[1][offset:offset + count],
            values[2][offset:offset + count],
            values[3][offset:offset + count],
            values[4][offset:offset + count],
        ))
        offset += count
    if offset != len(values[1]):
        raise RuntimeError("V25 flattened training rows exceed the scene selections")
    return tuple(inventories)


def build_preflight(*, repository_root: Path = REPOSITORY_ROOT) -> dict[str, object]:
    """Authenticate V25 and return the proposed model-free target audit."""

    started = time.perf_counter()
    root = repository_root.resolve()
    prepared = v25._prepare(root, v25._default_dependencies())
    _, config_sha256, config = v25._validate_candidate_config(v25.CONFIG_PATH, root, prepared.report)
    result_path = root / V25_RESULT_PATH
    result_bytes = result_path.read_bytes()
    result = json.loads(result_bytes)
    if (
        result.get("status") != "failed_dev_unconsumed"
        or result.get("candidate_config_sha256") != config_sha256
        or result.get("sealed_runs") != 0
        or result.get("production_approval") is not False
    ):
        raise RuntimeError("V25 closed outcome identity or scope changed")

    component_scenes = _scene_inventories(
        prepared.component_train, prepared.component_values, family_domain=False
    )
    family_scenes = _scene_inventories(
        prepared.family_train, prepared.family_values, family_domain=True
    )
    component_report, component_assignments = summarize_assignments(component_scenes)
    family_report, family_assignments = summarize_assignments(family_scenes)

    patches = torch.cat((prepared.component_values[0], prepared.family_values[0]))
    labels = torch.cat((prepared.component_values[1], prepared.family_values[1])).to(torch.float32)
    offsets = torch.cat((prepared.component_values[2], prepared.family_values[2]))
    radii = torch.cat((prepared.component_values[3], prepared.family_values[3]))
    hard = torch.cat((prepared.component_values[4], prepared.family_values[4]))
    all_rows = tuple(
        row
        for assignments in (component_assignments, family_assignments)
        for assignment in assignments
        for row in assignment.row_ids
    )
    inventory_sha256 = _tensor_digest((
        ("patches", patches),
        ("labels", labels),
        ("offsets", offsets),
        ("radii", radii),
        ("hard", hard),
    ))
    if len(all_rows) != int(config["training_example_count_expected"]):
        raise RuntimeError("proposed target audit changed V25 selected-row count")
    if int((labels > 0.5).sum()) != int(config["positive_example_count_expected"]):
        raise RuntimeError("proposed target audit changed V25 positive inventory")
    if int(hard.sum()) != int(config["hard_negative_example_count_expected"]):
        raise RuntimeError("proposed target audit changed V25 hard-negative inventory")

    return {
        "schema": OUTPUT_SCHEMA,
        "status": "model_free_target_preflight_only",
        "hypothesis_status": "tentative_requires_fixed_output_suppressor_anchor_eligibility",
        "v25": {
            "config_path": v25.CONFIG_PATH.as_posix(),
            "config_sha256": config_sha256,
            "result_path": V25_RESULT_PATH.as_posix(),
            "result_sha256": hashlib.sha256(result_bytes).hexdigest(),
            "component_selected_index_sha256": prepared.component_values[5].selected_index_sha256,
            "family_selected_index_sha256": prepared.family_values[5].selected_index_sha256,
            "selected_row_identity_sha256": hashlib.sha256(canonical_json_bytes(all_rows)).hexdigest(),
            "selected_tensor_inventory_sha256": inventory_sha256,
            "training_generator_state_sha256": hashlib.sha256(
                prepared.training_generator_state.cpu().numpy().tobytes(order="C")
            ).hexdigest(),
        },
        "target_definition": {
            "positive_distance_px": POSITIVE_DISTANCE_PX,
            "canonical_tie_order": ["distance_squared", "x", "y", "proposal_index"],
            "confidence": "one nearest eligible proposal per reachable truth",
            "regression": "all unchanged V25 <=3px positive proposals",
            "classification_ignored": "non-nearest V25 positive proposals",
            "negative_labels": "all unchanged V25 negative proposals",
        },
        "component_train": component_report,
        "family_train": family_report,
        "combined": {
            "truth_count": int(component_report["truth_count"]) + int(family_report["truth_count"]),
            "selected_row_count": len(all_rows),
            "v25_positive_row_count": int((labels > 0.5).sum()),
            "v25_hard_negative_row_count": int(hard.sum()),
            "v25_other_negative_row_count": int((labels <= 0.5).sum()) - int(hard.sum()),
            "confidence_positive_row_count": int(component_report["confidence_positive_row_count"]) + int(family_report["confidence_positive_row_count"]),
            "regression_positive_row_count": int((labels > 0.5).sum()),
            "classification_ignored_regression_row_count": int(component_report["classification_ignored_regression_row_count"]) + int(family_report["classification_ignored_regression_row_count"]),
            "zero_eligible_truth_count": int(component_report["zero_eligible_truth_count"]) + int(family_report["zero_eligible_truth_count"]),
        },
        "exact_patch_label_collisions": exact_patch_label_collision_report(patches, labels, radii),
        "sampling_used_dev_truth": False,
        "optimizer_steps_run": 0,
        "model_inference_runs": 0,
        "private_reads": 0,
        "sealed_runs": 0,
        "production_approval": False,
        "source_sha256": sha256_file(Path(__file__)),
        "elapsed_ms": round((time.perf_counter() - started) * 1000.0, 3),
    }


def write_preflight(output_path: Path, *, repository_root: Path = REPOSITORY_ROOT) -> dict[str, object]:
    if output_path.exists():
        raise FileExistsError(f"target preflight output already exists: {output_path}")
    report = build_preflight(repository_root=repository_root)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(canonical_json_bytes(report))
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    arguments = parser.parse_args()
    report = write_preflight(arguments.output)
    print(json.dumps({
        "status": report["status"],
        "truth_count": report["combined"]["truth_count"],
        "zero_eligible_truth_count": report["combined"]["zero_eligible_truth_count"],
        "output": str(arguments.output),
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
