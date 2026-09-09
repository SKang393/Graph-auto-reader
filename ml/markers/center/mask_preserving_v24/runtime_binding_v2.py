# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bind multiple ordered train runtime exchanges to one development exchange.

Version 1 remains the historical single-train binding.  This module composes
that parser, the annotation-free runtime loader, and the exact synthetic join
without changing any of them.  It validates diagnostic inputs only and grants
no production approval.
"""

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
from typing import Any

from ml.synthetic.dataset import FAMILY_AXES

from . import runtime_binding, runtime_family_scenes, runtime_inputs
from .runtime_family_scenes import RuntimeFamilySceneJoin, RuntimeFamilySceneError
from .runtime_inputs import (
    RuntimeInputError,
    ValidatedRuntimePanelInput,
)


BINDING_SCHEMA = "graphreader.marker-runtime-training-input-binding.v2"
REPOSITORY_ROOT = Path(__file__).resolve().parents[4]
_SCOPE_FIELDS = {
    "schema",
    "synthetic_only",
    "private_data",
    "sealed_runs",
    "training_input_ready",
    "production_approved",
    "complete_artifact_mask",
    "train",
    "dev",
    "implementation",
    "v1_binding_source_sha256",
    "loader_source_sha256",
    "join_source_sha256",
    "binding_v2_source_sha256",
    "train_tensor_set_sha256",
    "dev_tensor_set_sha256",
    "mapping_audit_sha256",
}


def load_runtime_family_binding_v2(
    path: Path,
    expected_sha256: str,
    *,
    repository_root: Path = REPOSITORY_ROOT,
) -> RuntimeFamilySceneJoin:
    """Load ordered train exchanges and one development exchange fail closed.

    Every train exchange is first passed through the unchanged v1 runtime input
    loader together with the same development binding.  Exact source
    regeneration therefore completes before ``_join_split`` reads labels, and
    returned tensors contain only planes loaded from the runtime reports.
    """

    root = repository_root.resolve()
    _, payload = runtime_inputs._read_bound_artifact(
        path, expected_sha256, root, "multi-seed training input binding"
    )
    try:
        document = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise RuntimeInputError("multi-seed training input binding is not valid JSON") from exception
    runtime_binding._object(document, _SCOPE_FIELDS, "multi-seed training input")
    if (
        document["schema"] != BINDING_SCHEMA
        or document["synthetic_only"] is not True
        or document["private_data"] is not False
        or type(document["sealed_runs"]) is not int
        or document["sealed_runs"] != 0
        or document["training_input_ready"] is not True
        or document["production_approved"] is not False
        or document["complete_artifact_mask"] is not False
    ):
        raise RuntimeInputError("multi-seed binding has a foreign or approved evidence scope")

    _validate_bound_sources(document)
    raw_train = document["train"]
    if not isinstance(raw_train, list) or not raw_train:
        raise RuntimeInputError("multi-seed binding requires a nonempty ordered train array")
    train_bindings = tuple(runtime_binding._split(value, root) for value in raw_train)
    dev_binding = runtime_binding._split(document["dev"], root)
    implementation = runtime_binding._implementation(document["implementation"], root)

    train_scenes = []
    train_audits = []
    dev_scenes = None
    dev_audits = None
    seen_train_sources: set[str] = set()
    seen_train_panels: set[str] = set()
    seen_train_panel_payloads: set[str] = set()
    seen_train_scenes: set[int] = set()
    seen_train_datasets: set[int] = set()
    seen_train_tensors: set[str] = set()
    train_family_axes = {axis: set() for axis in FAMILY_AXES}
    dev_family_axes = None
    dev_panel_identity = None
    dev_panel_payloads = None
    dev_tensor_hashes = None

    for train_binding in train_bindings:
        inputs = runtime_inputs.load_bound_runtime_inputs(
            train_binding,
            dev_binding,
            implementation,
            repository_root=root,
        )
        if inputs.implementation != implementation:
            raise RuntimeInputError("runtime pair used a different common implementation identity")
        current_dev_identity = _panel_identity(inputs.dev)
        if dev_panel_identity is None:
            dev_panel_identity = current_dev_identity
        elif current_dev_identity != dev_panel_identity:
            raise RuntimeInputError("runtime pairs produced different development input identities")

        train_sources = {panel.source_sha256 for panel in inputs.train}
        train_panels = {panel.panel_id for panel in inputs.train}
        train_panel_payloads = [panel.panel_sha256 for panel in inputs.train]
        current_dev_payloads = {panel.panel_sha256 for panel in inputs.dev}
        if dev_panel_payloads is None:
            dev_panel_payloads = current_dev_payloads
        elif current_dev_payloads != dev_panel_payloads:
            raise RuntimeInputError("runtime pairs produced different development panel payloads")
        train_scene_ids = {panel.scene_seed for panel in inputs.train}
        train_dataset_ids = {panel.dataset_seed for panel in inputs.train}
        if len(train_dataset_ids) != 1:
            raise RuntimeInputError("one ordered train binding mixes dataset identities")
        dataset_identity = next(iter(train_dataset_ids))
        if seen_train_sources & train_sources:
            raise RuntimeInputError("ordered train bindings contain duplicate source identities")
        if seen_train_panels & train_panels:
            raise RuntimeInputError("ordered train bindings contain duplicate panel identities")
        if len(set(train_panel_payloads)) != len(train_panel_payloads) or (
            seen_train_panel_payloads & set(train_panel_payloads)
        ):
            raise RuntimeInputError("ordered train bindings contain duplicate panel PNG payloads")
        if set(train_panel_payloads) & current_dev_payloads:
            raise RuntimeInputError("train and validation panel PNG payloads overlap")
        if seen_train_scenes & train_scene_ids:
            raise RuntimeInputError("ordered train bindings contain duplicate scene identities")
        if dataset_identity in seen_train_datasets:
            raise RuntimeInputError("ordered train bindings contain duplicate dataset identities")

        try:
            joined_train, joined_train_audit, dataset_seed = runtime_family_scenes._join_split(
                inputs.train, "train"
            )
            if dev_scenes is None:
                dev_scenes, dev_audits, _ = runtime_family_scenes._join_split(
                    inputs.dev, "validation"
                )
                dev_tensor_hashes = {_scene_tensor_sha256(scene) for scene in dev_scenes}
        except RuntimeFamilySceneError as exception:
            raise RuntimeInputError(str(exception)) from exception
        if dataset_seed != dataset_identity:
            raise RuntimeInputError("joined train dataset identity differs from runtime inputs")
        train_tensor_hashes = [_scene_tensor_sha256(scene) for scene in joined_train]
        if len(set(train_tensor_hashes)) != len(train_tensor_hashes) or (
            seen_train_tensors & set(train_tensor_hashes)
        ):
            raise RuntimeInputError("ordered train bindings contain duplicate runtime tensors")
        if dev_tensor_hashes is None or set(train_tensor_hashes) & dev_tensor_hashes:
            raise RuntimeInputError("train and validation runtime tensors overlap")
        seen_train_sources.update(train_sources)
        seen_train_panels.update(train_panels)
        seen_train_panel_payloads.update(train_panel_payloads)
        seen_train_scenes.update(train_scene_ids)
        seen_train_datasets.add(dataset_identity)
        seen_train_tensors.update(train_tensor_hashes)
        _add_family_axes(train_family_axes, joined_train)
        train_scenes.extend(joined_train)
        train_audits.extend(joined_train_audit)

    if dev_scenes is None or dev_audits is None:
        raise RuntimeInputError("multi-seed binding did not produce a development split")
    dev_family_axes = {axis: set() for axis in FAMILY_AXES}
    _add_family_axes(dev_family_axes, dev_scenes)
    for axis in FAMILY_AXES:
        overlap = train_family_axes[axis] & dev_family_axes[axis]
        if overlap:
            raise RuntimeInputError(
                f"train and validation {axis} family identities overlap: {sorted(overlap)}"
            )

    joined = RuntimeFamilySceneJoin(
        tuple(train_scenes),
        tuple(dev_scenes),
        tuple(train_audits) + tuple(dev_audits),
    )
    for actual, key in (
        (runtime_binding.tensor_set_sha256(joined.train), "train_tensor_set_sha256"),
        (runtime_binding.tensor_set_sha256(joined.dev), "dev_tensor_set_sha256"),
        (runtime_binding.mapping_audit_sha256(joined), "mapping_audit_sha256"),
    ):
        if actual != runtime_inputs._sha(document[key], key):
            raise RuntimeInputError(f"joined multi-seed runtime {key} differs from the frozen binding")
    return joined


def _validate_bound_sources(document: dict[str, Any]) -> None:
    for module, key in (
        (runtime_binding, "v1_binding_source_sha256"),
        (runtime_inputs, "loader_source_sha256"),
        (runtime_family_scenes, "join_source_sha256"),
    ):
        actual = sha256(Path(module.__file__).read_bytes()).hexdigest()
        if actual != runtime_inputs._sha(document[key], key):
            raise RuntimeInputError(f"frozen {key} differs from the executing implementation")
    actual_self = sha256(Path(__file__).read_bytes()).hexdigest()
    if actual_self != runtime_inputs._sha(
        document["binding_v2_source_sha256"], "binding_v2_source_sha256"
    ):
        raise RuntimeInputError(
            "frozen binding_v2_source_sha256 differs from the executing implementation"
        )


def _panel_identity(panels: tuple[ValidatedRuntimePanelInput, ...]) -> tuple[tuple[Any, ...], ...]:
    return tuple(
        (
            panel.split,
            panel.dataset_seed,
            panel.family,
            panel.scene_seed,
            panel.source_sha256,
            panel.panel_id,
            panel.panel_sha256,
            panel.width,
            panel.height,
            panel.crop,
            panel.requested_crop,
        )
        for panel in panels
    )


def _scene_tensor_sha256(scene) -> str:
    return sha256(scene.tensor.detach().cpu().contiguous().numpy().tobytes(order="C")).hexdigest()


def _add_family_axes(target: dict[str, set[str]], scenes) -> None:
    for scene in scenes:
        parsed = _parse_family_identity(scene.family)
        for axis, value in parsed.items():
            target[axis].add(value)


def _parse_family_identity(value: Any) -> dict[str, str]:
    if not isinstance(value, str):
        raise RuntimeInputError("runtime family identity must be a string")
    parts = value.split("|")
    if len(parts) != len(FAMILY_AXES):
        raise RuntimeInputError("runtime family identity has an invalid semantic-axis shape")
    parsed: dict[str, str] = {}
    for expected_axis, part in zip(FAMILY_AXES, parts, strict=True):
        axis, separator, family = part.partition("=")
        if separator != "=" or axis != expected_axis or not family or "=" in family:
            raise RuntimeInputError("runtime family identity has an invalid semantic-axis shape")
        parsed[axis] = family
    return parsed


__all__ = ["BINDING_SCHEMA", "load_runtime_family_binding_v2"]
