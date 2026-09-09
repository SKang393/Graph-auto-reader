# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Model-free V25 runner preparation and contract tests."""

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    RuntimeModelIdentity,
    ValidatedRuntimePanelInput,
)
from ml.markers.center.plot_domain_v25 import protocol
from ml.markers.center.plot_domain_v25.proposal_domain import PlotDomain
from ml.markers.center.plot_domain_v25 import runtime_domain_binding_v3
from ml.markers.center.plot_domain_v25 import train_p1 as subject


REPO_ROOT = Path(__file__).resolve().parents[5]


def _scenes(count: int, truths: int, sources: int) -> tuple[SimpleNamespace, ...]:
    base, remainder = divmod(truths, count)
    return tuple(
        SimpleNamespace(
            centers=tuple((1.0, 1.0) for _ in range(base + (index < remainder))),
            source_sha256=f"{index % sources:064x}",
        )
        for index in range(count)
    )


def _sampling(recipe: dict) -> SimpleNamespace:
    config = recipe["negative_sampler"]
    return SimpleNamespace(
        capacities=config["expected_capacities"],
        counts=config["quotas"],
        selected_index_sha256=config["selected_index_sha256"],
    )


def _component_values(recipe: dict) -> tuple:
    count = recipe["real_range_training_example_count_expected"]
    labels = torch.zeros(count)
    labels[:recipe["real_range_positive_example_count_expected"]] = 1
    hard = torch.zeros(count, dtype=torch.bool)
    hard[:recipe["real_range_hard_negative_example_count_expected"]] = True
    return (
        torch.zeros((count, 3, 1, 1)), labels, torch.zeros((count, 2)),
        torch.zeros(count), hard, _sampling(recipe),
    )


def _family_values() -> tuple:
    count = 40
    labels = torch.zeros(count)
    labels[:10] = 1
    hard = torch.zeros(count, dtype=torch.bool)
    hard[10:15] = True
    bins = {str(index): index for index in range(64)}
    quotas = {str(index): index // 2 for index in range(64)}
    sampling = SimpleNamespace(
        selected_index_sha256="7" * 64,
        stratified_bin_capacities=bins,
        stratified_bin_quotas=quotas,
    )
    domain = {
        "ink_supported_proposals": 600,
        "omitted_by_domain": 40,
        "truths_supported_within_5px": 478,
        "truth_count": 500,
    }
    return (
        torch.zeros((count, 3, 1, 1)), labels, torch.zeros((count, 2)),
        torch.zeros(count), hard, sampling, domain,
    )


def _dependencies(events: list[str]) -> subject.PreparationDependencies:
    recipe = json.loads((REPO_ROOT / protocol.COMPONENT_CONFIG_PATH).read_text())
    train = tuple(SimpleNamespace(scene=scene) for scene in _scenes(28, 500, 20))
    dev = tuple(SimpleNamespace(scene=scene) for scene in _scenes(9, 206, 3))
    binding = SimpleNamespace(
        train=("train-panels",), dev=("dev-panels",),
        panel_inventory_sha256=protocol.FAMILY_PANEL_INVENTORY_SHA256,
        expected_mapping_audit_sha256=protocol.FAMILY_MAPPING_AUDIT_SHA256,
        expected_truth_mapping_sha256=protocol.FAMILY_TRUTH_MAPPING_SHA256,
    )

    def load(*_):
        events.append("load_binding")
        return binding

    def join(*_):
        events.append("join_truth")
        return SimpleNamespace(train=train, dev=dev)

    def build(split: str, independent: bool):
        events.append(f"build_{split}_{independent}")
        return _scenes(167, 2004, 167)

    def component_examples(*_):
        events.append("component_sampling")
        return _component_values(recipe)

    def family_examples(scenes, *_):
        events.append(f"family_sampling_{len(scenes)}")
        assert tuple(item.scene for item in scenes) == tuple(item.scene for item in train)
        return _family_values()

    def support(scenes):
        events.append(f"dev_support_{len(scenes)}")
        assert tuple(item.scene for item in scenes) == tuple(item.scene for item in dev)
        return {
            "ink_supported_proposals": 300,
            "omitted_by_domain": 20,
            "truths_supported_within_5px": 206,
            "truth_count": 206,
        }

    return subject.PreparationDependencies(
        load_binding=load,
        join_truth=join,
        build_component_split=build,
        component_audit=lambda: {
            "layout_family_audit": {
                "independent_layout_required": True,
                "train_dev_family_disjoint": True,
                "train_dev_layout_disjoint": True,
            }
        },
        component_examples=component_examples,
        family_examples=family_examples,
        domain_support=support,
        panel_tensor_hash=lambda panels: (
            protocol.FAMILY_TRAIN_TENSOR_SHA256
            if panels == binding.train else protocol.FAMILY_DEV_TENSOR_SHA256
        ),
    )


def test_protocol_supersedes_historical_binding_with_v3() -> None:
    document = json.loads((REPO_ROOT / protocol.DEV_PROTOCOL_PATH).read_text())
    split = document["split_identities"]
    assert split["family_binding_sha256"] == protocol.FAMILY_BINDING_SHA256
    assert split["superseded_family_binding_sha256"] == (
        "ddaccff59347f06045166c02ffa5f7c1488cd485db0a46115ce0411451c7c85f"
    )
    assert split["family_train_panels"] == 28
    assert split["family_dev_panels"] == 9
    assert sha256((REPO_ROOT / protocol.DEV_PROTOCOL_PATH).read_bytes()).hexdigest() == protocol.DEV_PROTOCOL_SHA256


def test_model_free_preparation_uses_train_only_for_sampling() -> None:
    events: list[str] = []
    report = subject.prepare_model_free(
        repository_root=REPO_ROOT,
        dependencies=_dependencies(events),
    )

    assert report["status"] == "prepared_unapproved"
    assert report["split_counts"] == {
        "component_train_scenes": 167, "component_train_truths": 2004,
        "component_dev_scenes": 167, "component_dev_truths": 2004,
        "family_train_panels": 28, "family_train_sources": 20,
        "family_train_truths": 500, "family_dev_panels": 9,
        "family_dev_sources": 3, "family_dev_truths": 206,
    }
    assert events.index("family_sampling_28") < events.index("dev_support_9")
    assert report["sampling_used_dev_truth"] is False
    assert report["optimizer_steps_run"] == 0
    assert report["private_reads"] == report["sealed_runs"] == 0
    assert report["production_approval"] is False
    assert set(report["requirements"]["family_stratified_bin_capacities"]) == {
        str(index) for index in range(64)
    }


def test_domain_family_sampling_uses_bound_domain_and_fixed_stratifier() -> None:
    tensor = torch.zeros((3, 32, 32))
    tensor[0] = 1
    scene = SimpleNamespace(
        tensor=tensor, centers=((16.0, 16.0),), diameters=(8.0,), hard_negatives=(),
        sampling_identity="fixture-panel", split="train", family="fixture", seed=1,
    )
    bound = SimpleNamespace(
        scene=scene,
        panel_domain=SimpleNamespace(
            domain=PlotDomain.component_full_canvas(32, 32, identity="fixture-domain")
        ),
    )

    values = subject._family_examples(
        (bound,), 10, torch.Generator().manual_seed(20260904)
    )

    assert values[1].sum().item() >= 1
    assert values[6]["truth_count"] == 1
    assert values[6]["truths_supported_within_5px"] == 1
    assert set(values[5].stratified_bin_capacities) == {str(index) for index in range(64)}


def test_default_tensor_hash_unwraps_runtime_panel_domain() -> None:
    runtime_input = ValidatedRuntimePanelInput(
        split="train",
        dataset_seed=1,
        family="fixture",
        scene_seed=2,
        source_sha256="a" * 64,
        panel_id="00000000-0000-0000-0000-000000000001",
        panel_sha256="b" * 64,
        width=2,
        height=2,
        crop=(0, 0, 2, 2),
        requested_crop=(0.0, 0.0, 2.0, 2.0),
        gray8=np.array([[0, 255], [127, 64]], dtype=np.uint8),
        ocr_mask=np.array([[0.0, 1.0], [0.5, 0.25]], dtype=np.float32),
        geometry_mask=np.zeros((2, 2), dtype=np.float32),
        artifact_mask=np.array([[1.0, 0.0], [0.25, 0.5]], dtype=np.float32),
    )
    panel = runtime_domain_binding_v3.RuntimePanelDomain(
        runtime_input=runtime_input,
        domain=PlotDomain.component_full_canvas(2, 2, identity="fixture-domain"),
        source_width=2,
        source_height=2,
        source_polygon=((0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)),
        source_to_panel_matrix=(1.0, 0.0, 0.0, 1.0, 0.0, 0.0),
        panel_to_source_matrix=(1.0, 0.0, 0.0, 1.0, 0.0, 0.0),
        manifest_sha256="c" * 64,
        report_sha256="d" * 64,
        axis_model=RuntimeModelIdentity(
            stage="axis", stage_version="fixture", model_id="fixture-model",
            version="1", sha256="e" * 64, provider="fixture-provider",
        ),
        evidence_sha256="f" * 64,
    )

    actual = subject._default_dependencies().panel_tensor_hash((panel,))
    expected = runtime_domain_binding_v3.panel_tensor_multiset_sha256((runtime_input,))

    assert actual == expected


def _candidate_config(root: Path, preparation: dict) -> Path:
    component_path = root / protocol.COMPONENT_CONFIG_PATH
    component_path.parent.mkdir(parents=True, exist_ok=True)
    component_bytes = (REPO_ROOT / protocol.COMPONENT_CONFIG_PATH).read_bytes()
    component_path.write_bytes(component_bytes)
    requirements = preparation["requirements"]
    config = json.loads(component_bytes)
    config.update({
        "schema": subject.CONFIG_SCHEMA,
        "revision": protocol.TRAINING_REVISION,
        "component_recipe_path": protocol.COMPONENT_CONFIG_PATH.as_posix(),
        "component_recipe_sha256": protocol.COMPONENT_CONFIG_SHA256,
        "runtime_domain_binding_path": protocol.FAMILY_BINDING_PATH.as_posix(),
        "runtime_domain_binding_sha256": protocol.FAMILY_BINDING_SHA256,
        "dev_protocol_path": protocol.DEV_PROTOCOL_PATH.as_posix(),
        "dev_protocol_sha256": protocol.DEV_PROTOCOL_SHA256,
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
        "model_free_preflight_path": "artifacts/preflight.json",
        "production_approval": False,
        "release_eligible": False,
    })
    preflight_path = root / config["model_free_preflight_path"]
    preflight_path.parent.mkdir(parents=True)
    preflight_path.write_bytes(subject.canonical_json_bytes(preparation))
    config["model_free_preflight_sha256"] = sha256(preflight_path.read_bytes()).hexdigest()
    path = root / "training/p1.json"
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps(config), encoding="utf-8")
    return path.relative_to(root)


def test_candidate_config_freezes_model_free_requirements(tmp_path: Path) -> None:
    preparation = subject.prepare_model_free(
        repository_root=REPO_ROOT, dependencies=_dependencies([])
    )
    relative = _candidate_config(tmp_path, preparation)

    actual_path, _, actual = subject._validate_candidate_config(
        relative, tmp_path, preparation
    )

    assert actual_path == relative
    assert actual["family_selected_index_sha256"] == preparation["requirements"]["family_selected_index_sha256"]


@pytest.mark.parametrize(
    "field,value,error",
    [
        ("maximum_negative_per_positive", 11, "preserved V24 recipe"),
        ("family_stratified_bin_quotas_expected", {}, "model-free preparation"),
        ("confidence_threshold", 0.3, "scope or operating point"),
    ],
)
def test_candidate_config_rejects_recipe_or_preflight_drift(
    tmp_path: Path, field: str, value: object, error: str
) -> None:
    preparation = subject.prepare_model_free(
        repository_root=REPO_ROOT, dependencies=_dependencies([])
    )
    relative = _candidate_config(tmp_path, preparation)
    path = tmp_path / relative
    config = json.loads(path.read_text())
    config[field] = value
    path.write_text(json.dumps(config), encoding="utf-8")

    with pytest.raises((ValueError, RuntimeError), match=error):
        subject._validate_candidate_config(relative, tmp_path, preparation)
