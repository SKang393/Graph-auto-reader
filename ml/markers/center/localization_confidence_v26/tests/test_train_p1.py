# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.center.localization_confidence_v26 import train_p1 as subject


def _scope_fixture():
    scenes = []
    proposals = []
    for scene_index in range(2):
        scenes.append(SimpleNamespace(
            tensor=torch.zeros((3, 3, 3)), centers=((0.0, 0.0),), diameters=(6.0,),
            hard_negatives=(),
        ))
        proposals.append(SimpleNamespace(
            coordinates=torch.tensor(((0.0, 0.0), (4.0, 0.0), (10.0, 0.0))),
            patches=torch.tensor((scene_index * 10, scene_index * 10 + 1,
                                  scene_index * 10 + 2), dtype=torch.float32).reshape(3, 1),
        ))
    scene_index = np.repeat(np.arange(2, dtype=np.int32), 3)
    proposal_index = np.tile(np.arange(3, dtype=np.int32), 2)
    coordinates = np.tile(np.asarray(((0, 0), (4, 0), (10, 0)), np.float32), (2, 1))
    distances = np.tile(np.asarray((0, 4, 10), np.float32), 2)
    coverage = {
        "component_scene_index": scene_index,
        "component_proposal_index": proposal_index,
        "component_coordinates": coordinates,
        "component_nearest_truth_index": np.zeros(6, np.int32),
        "component_nearest_truth_distance_px": distances,
        "component_nearest_truth_radius_px": np.full(6, 3, np.float32),
        "component_selected": np.tile(np.asarray((True, False, True)), 2),
        "component_stratum_id": np.zeros(6, np.uint8),
        "component_retention_role_id": np.full(6, 3, np.uint8),
    }
    annulus = {
        "component_proposed_selected": np.tile(np.asarray((True, True, False)), 2),
        "component_reserved": np.tile(np.asarray((False, True, False)), 2),
        "component_added": np.tile(np.asarray((False, True, False)), 2),
        "component_displaced": np.tile(np.asarray((False, False, True)), 2),
    }
    report = {"cache": {
        "retention_role_ids": {
            "connector_anchor_reserved": 0, "hard_negative_retained": 1,
            "positive": 2, "quota_ranked": 3, "topology_reserved": 6,
        },
        "stratum_ids": {"generic": 0, "hard_existing": 5},
    }}
    sampling = SimpleNamespace(selections=((2,), (2,)))
    return scenes, proposals, report, coverage, annulus, sampling


def test_annulus_selection_changes_only_selected_rows(monkeypatch: pytest.MonkeyPatch) -> None:
    scenes, proposals, report, coverage, annulus, sampling = _scope_fixture()
    iterator = iter(proposals)
    monkeypatch.setattr(subject.v24, "extract_proposals", lambda _: next(iterator))

    values, evidence = subject._rebuild_scope(
        "component", scenes, sampling, report, coverage, annulus, domain_limited=False
    )

    assert values[0].flatten().tolist() == [0.0, 1.0, 10.0, 11.0]
    assert values[1].tolist() == [1.0, 0.0, 1.0, 0.0]
    assert values[2].tolist() == [[0.0, 0.0], [-1.0, 0.0], [0.0, 0.0], [-1.0, 0.0]]
    assert values[3].tolist() == [3.0, 3.0, 3.0, 3.0]
    assert values[4].tolist() == [False, False, False, False]
    assert evidence["selected_rows"] == 4
    assert evidence["added_rows"] == evidence["displaced_rows"] == 2


def test_unchanged_selection_preserves_v25_scene_and_proposal_order(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    scenes, proposals, report, coverage, annulus, sampling = _scope_fixture()
    annulus["component_proposed_selected"] = coverage["component_selected"].copy()
    annulus["component_added"][:] = False
    annulus["component_displaced"][:] = False
    iterator = iter(proposals)
    monkeypatch.setattr(subject.v24, "extract_proposals", lambda _: next(iterator))

    values, _ = subject._rebuild_scope(
        "component", scenes, sampling, report, coverage, annulus, domain_limited=False
    )

    assert values[0].flatten().tolist() == [0.0, 2.0, 10.0, 12.0]


def test_protected_frozen_row_cannot_be_displaced(monkeypatch: pytest.MonkeyPatch) -> None:
    scenes, proposals, report, coverage, annulus, sampling = _scope_fixture()
    coverage["component_retention_role_id"][2] = 6
    iterator = iter(proposals)
    monkeypatch.setattr(subject.v24, "extract_proposals", lambda _: next(iterator))

    with pytest.raises(subject.V26TrainingError, match="protected"):
        subject._rebuild_scope(
            "component", scenes, sampling, report, coverage, annulus,
            domain_limited=False,
        )


def test_regenerated_proposal_identity_must_match_frozen_cache(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    scenes, proposals, report, coverage, annulus, sampling = _scope_fixture()
    coverage["component_coordinates"][1, 0] = 5
    iterator = iter(proposals)
    monkeypatch.setattr(subject.v24, "extract_proposals", lambda _: next(iterator))

    with pytest.raises(subject.V26TrainingError, match="proposal identity"):
        subject._rebuild_scope(
            "component", scenes, sampling, report, coverage, annulus,
            domain_limited=False,
        )


def test_fixed_recipe_and_denominators_preserve_v25_budget() -> None:
    assert subject.RECIPE["sampler"] == "fixed_truth_annulus_reservation_v1"
    assert subject.RECIPE["bands_px"] == [[3.0, 5.0], [5.0, 8.0], [8.0, 12.0]]
    assert subject.RECIPE["epochs"] == 36
    assert subject.RECIPE["batch_size"] == 128
    assert subject.EXPECTED_DATA["positive_example_count"] == 4081
    assert subject.EXPECTED_DATA["negative_example_count"] == 40810
    assert subject.EXPECTED_DATA["hard_negative_example_count"] == 8617
    assert subject.EXPECTED_DATA["optimizer_steps"] == 12636
    assert subject.EXPECTED_DATA["component_dev"]["truth_count"] == 2004
    assert subject.EXPECTED_DATA["family_dev"]["truth_count"] == 206


def test_source_drift_rejects_before_base_regeneration_and_acquisition(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    prepared: list[bool] = []
    acquired: list[bool] = []
    monkeypatch.setattr(subject, "_load_config", lambda *_: ({
        "expected_runner_source_bundle_sha256": "a" * 64,
    }, "b" * 64))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *_: "c" * 64)
    dependencies = subject.Dependencies(
        lambda *args: prepared.append(True), lambda *args, **kwargs: acquired.append(True),
        lambda *args, **kwargs: Path(), lambda *args: Path(), lambda *args: None,
    )

    with pytest.raises(subject.V26TrainingError, match="source bundle"):
        subject.prepare_training(
            Path("config.json"), repository_root=tmp_path, dependencies=dependencies
        )
    assert prepared == []
    assert acquired == []


def test_input_drift_during_preparation_rejects_before_acquisition(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    acquired: list[bool] = []
    prepared = subject.PreparedTraining(
        {"expected_runner_source_bundle_sha256": "a" * 64}, Path("config.json"), "b" * 64,
        SimpleNamespace(), (), (), torch.Generator().get_state(), {},
    )
    monkeypatch.setattr(subject, "prepare_training", lambda *args, **kwargs: prepared)
    monkeypatch.setattr(subject, "_load_config", lambda *args, **kwargs: (
        dict(prepared.config), "c" * 64
    ))
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *args: "a" * 64)
    dependencies = subject.Dependencies(
        lambda *args: None, lambda *args, **kwargs: acquired.append(True),
        lambda *args, **kwargs: Path(), lambda *args: Path(), lambda *args: None,
    )

    with pytest.raises(subject.V26TrainingError, match="changed during"):
        subject.run(
            Path("run"), tmp_path / "checkpoint", tmp_path / "model",
            Path("config.json"), repository_root=tmp_path, dependencies=dependencies,
        )
    assert acquired == []


def test_dev_tensors_are_not_part_of_rebuilt_training_values() -> None:
    component = (
        torch.zeros((35838, 1)), torch.zeros(35838), torch.zeros((35838, 2)),
        torch.zeros(35838), torch.zeros(35838, dtype=torch.bool),
    )
    family = (
        torch.zeros((9053, 1)), torch.zeros(9053), torch.zeros((9053, 2)),
        torch.zeros(9053), torch.zeros(9053, dtype=torch.bool),
    )
    component[1][:3258] = 1
    family[1][:823] = 1
    component[4][3258:3258 + 8617] = True
    subject._validate_rebuilt_counts(component, family)
    assert sum(len(value) for value in (component[1], family[1])) == 44891


def test_failure_after_acquisition_writes_zero_step_record_and_voids(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    checkpoint = tmp_path / "checkpoint.pt"
    onnx = tmp_path / "parent.onnx"
    checkpoint.write_bytes(b"checkpoint")
    onnx.write_bytes(b"onnx")
    base_config_path = tmp_path / subject.BASE_V25_CONFIG_PATH
    base_config_path.parent.mkdir(parents=True)
    base_config_path.write_text(json.dumps({
        "checkpoint_sha256": sha256(checkpoint.read_bytes()).hexdigest(),
        "v21_onnx_sha256": sha256(onnx.read_bytes()).hexdigest(),
    }), encoding="utf-8")
    authorization = SimpleNamespace(
        snapshot_path=tmp_path / "snapshot.json",
        binding={
            "candidate_config_path": "config.json",
            "candidate_config_sha256": "a" * 64,
            "source_snapshot_sha256": "b" * 64,
        },
    )
    voided: list[BaseException] = []
    prepared = subject.PreparedTraining(
        {}, Path("config.json"), "a" * 64,
        SimpleNamespace(report={}, component_dev=(), family_dev=()),
        (torch.zeros((1, 1)), torch.zeros(1), torch.zeros((1, 2)),
         torch.zeros(1), torch.zeros(1, dtype=torch.bool)),
        (torch.zeros((1, 1)), torch.zeros(1), torch.zeros((1, 2)),
         torch.zeros(1), torch.zeros(1, dtype=torch.bool)),
        torch.Generator().get_state(), {},
    )
    monkeypatch.setattr(subject, "source_bundle_sha256", lambda *args: "c" * 64)
    prepared = subject.PreparedTraining(
        {"expected_runner_source_bundle_sha256": "c" * 64}, Path("config.json"), "a" * 64,
        prepared.base, prepared.component_values, prepared.family_values,
        prepared.training_generator_state, prepared.selection_evidence,
    )
    monkeypatch.setattr(subject, "prepare_training", lambda *args, **kwargs: prepared)
    monkeypatch.setattr(
        subject, "_load_config", lambda *args, **kwargs: (dict(prepared.config), "a" * 64)
    )
    monkeypatch.setattr(subject.torch, "load", lambda *args, **kwargs: (_ for _ in ()).throw(
        RuntimeError("fixture initialization failure")
    ))
    dependencies = subject.Dependencies(
        lambda *args: None,
        lambda *args, **kwargs: authorization,
        lambda *args, **kwargs: Path(),
        lambda auth, error: voided.append(error) or Path(),
        lambda *args: None,
    )

    with pytest.raises(RuntimeError, match="fixture initialization failure"):
        subject.run(
            Path("run"), checkpoint, onnx, Path("config.json"),
            repository_root=tmp_path, dependencies=dependencies,
        )
    failure = json.loads((tmp_path / "run" / subject.REPORT_NAME).read_text())
    assert failure["optimizer_steps"] == 0
    assert failure["optimizer_steps_known"] is True
    assert failure["phase"] == "initialization"
    assert len(voided) == 1
