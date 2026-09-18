# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import copy
import hashlib
from pathlib import Path

import numpy as np
import pytest
import torch

from ml.markers.center.component_diversity_v27 import train_p1 as subject


class TinyModel(torch.nn.Module):
    def __init__(self) -> None:
        super().__init__()
        self.layer = torch.nn.Linear(2, 1)

    def forward_raw(self, value: torch.Tensor) -> torch.Tensor:
        return self.layer(value).squeeze(1)


class PlannedStop(RuntimeError):
    pass


def _fixture():
    patches = torch.tensor([
        [0.0, 0.0], [1.0, 0.0], [0.0, 1.0],
        [1.0, 1.0], [0.5, 0.25], [0.25, 0.5],
    ])
    labels = torch.tensor([0.0, 1.0, 1.0, 0.0, 1.0, 0.0])
    offsets = torch.zeros((6, 2))
    radii = torch.ones(6)
    hard = torch.zeros(6, dtype=torch.bool)
    recipe = {
        "epochs": 4,
        "batch_size": 2,
        "positive_loss_weight": 1.0,
        "hard_negative_loss_weight": 1.0,
        "focal_alpha": 0.25,
        "focal_gamma": 2.0,
    }
    return patches, labels, offsets, radii, hard, recipe


def _loss(raw, labels, _offsets, _radii, _hard, **_options):
    return torch.square(raw - labels).mean()


def _model(initial):
    model = TinyModel()
    model.load_state_dict(copy.deepcopy(initial))
    return model


def test_fixed_revision_changes_only_component_selection() -> None:
    assert subject.RECIPE["sampler"] == "fixed_component_cell_stratified_v1"
    assert subject.RECIPE["component_selection_sha256"] == subject.COMPONENT_SELECTION_SHA256
    assert subject.RECIPE["family_selection_sha256"] == subject.FAMILY_SELECTION_SHA256
    assert subject.RECIPE["epochs"] == 36
    assert subject.RECIPE["batch_size"] == 128
    assert subject.RECIPE["learning_rate"] == 0.001
    assert subject.RECIPE["confidence_threshold"] == 0.25
    assert subject.RECIPE["checkpoint_selection"] == "final_epoch"
    assert subject.EXPECTED_DATA["training_example_count"] == 44891
    assert subject.EXPECTED_DATA["positive_example_count"] == 4081
    assert subject.EXPECTED_DATA["negative_example_count"] == 40810
    assert subject.EXPECTED_DATA["optimizer_steps"] == 12636
    assert subject.EXPECTED_DATA["component_dev"]["truth_count"] == 2004
    assert subject.EXPECTED_DATA["family_dev"]["truth_count"] == 206
    assert subject.EXECUTION["torch_intraop_threads"] == 12
    assert subject.EXECUTION["onnx_intraop_threads"] == 12
    assert subject.EXECUTION["onnx_interop_threads"] == 1


def test_component_lineage_distinguishes_v25_from_v26_source(monkeypatch) -> None:
    scene = np.array([0, 0, 1, 1], dtype=np.int64)
    proposal = np.array([0, 1, 0, 1], dtype=np.int64)
    frozen_v25 = np.array([True, False, True, False], dtype=np.bool_)
    source_v26 = np.array([True, True, False, False], dtype=np.bool_)
    frozen_sha = subject.v26.annulus_reservation_preflight._selection_sha256(
        frozen_v25, scene, proposal
    )
    source_sha = subject.v26.annulus_reservation_preflight._selection_sha256(
        source_v26, scene, proposal
    )
    monkeypatch.setattr(subject, "V25_COMPONENT_SELECTION_SHA256", frozen_sha)
    monkeypatch.setattr(subject, "V26_COMPONENT_SELECTION_SHA256", source_sha)
    preflight = {
        "selection_identity": {"component_source_sha256": source_sha},
        "populations": {
            "component": {
                "selected_after": 2,
                "protected_selected_negative": 1,
                "added": 1,
                "displaced": 1,
            }
        },
    }
    annulus_report = {
        "component_train": {
            "frozen_selection_sha256": frozen_sha,
            "proposed_selection_sha256": source_sha,
            "protected_selected_negative_count": 1,
        }
    }
    coverage = {
        "component_selected": frozen_v25,
        "component_scene_index": scene,
        "component_proposal_index": proposal,
    }
    annulus = {"component_proposed_selected": source_v26}
    subject._validate_component_selection_lineage(
        preflight, annulus_report, coverage, annulus
    )
    evidence = {
        "frozen_selection_sha256": frozen_sha,
        "proposed_selection_sha256": subject.COMPONENT_SELECTION_SHA256,
        "selected_rows": 2,
        "preserved_protected_rows": 1,
        "added_rows": 1,
        "displaced_rows": 1,
        "row_order": "scene_then_ascending_proposal_index",
    }
    subject._validate_component_evidence(evidence, preflight, annulus_report)

    mislabeled = dict(evidence, frozen_selection_sha256=source_sha)
    with pytest.raises(subject.V27TrainingError, match="Applied component selection"):
        subject._validate_component_evidence(mislabeled, preflight, annulus_report)

    tampered = dict(annulus, component_proposed_selected=frozen_v25)
    with pytest.raises(subject.V27TrainingError, match="selection lineage"):
        subject._validate_component_selection_lineage(
            preflight, annulus_report, coverage, tampered
        )


def test_completed_epoch_stop_and_resume_matches_uninterrupted(tmp_path: Path) -> None:
    values = _fixture()
    patches, labels, offsets, radii, hard, recipe = values
    torch.manual_seed(917)
    initial = copy.deepcopy(TinyModel().state_dict())
    binding = {"input": "a" * 64}

    uninterrupted = _model(initial)
    uninterrupted_optimizer = torch.optim.AdamW(uninterrupted.parameters(), lr=0.01)
    uninterrupted_generator = torch.Generator().manual_seed(41)
    direct = subject._train_epochs(
        uninterrupted, uninterrupted_optimizer,
        patches, labels, offsets, radii, hard, uninterrupted_generator,
        recipe=recipe, binding=binding,
        recovery_path=tmp_path / "direct.pt",
        progress_path=tmp_path / "direct.json",
        loss_function=_loss,
    )

    interrupted = _model(initial)
    interrupted_optimizer = torch.optim.AdamW(interrupted.parameters(), lr=0.01)
    interrupted_generator = torch.Generator().manual_seed(41)

    def stop(outcome):
        if outcome.completed_epochs == 2:
            raise PlannedStop("planned completed-epoch stop")

    with pytest.raises(PlannedStop):
        subject._train_epochs(
            interrupted, interrupted_optimizer,
            patches, labels, offsets, radii, hard, interrupted_generator,
            recipe=recipe, binding=binding,
            recovery_path=tmp_path / "resumed.pt",
            progress_path=tmp_path / "resumed.json",
            loss_function=_loss, after_epoch=stop,
        )

    resumed = _model(initial)
    resumed_optimizer = torch.optim.AdamW(resumed.parameters(), lr=0.01)
    resumed_generator = torch.Generator().manual_seed(41)
    recovered = subject._train_epochs(
        resumed, resumed_optimizer,
        patches, labels, offsets, radii, hard, resumed_generator,
        recipe=recipe, binding=binding,
        recovery_path=tmp_path / "resumed.pt",
        progress_path=tmp_path / "resumed.json",
        loss_function=_loss,
    )

    assert recovered.resumed_from_epoch == 2
    assert recovered.optimizer_steps == direct.optimizer_steps == 12
    assert recovered.loss_history == direct.loss_history
    assert all(
        torch.equal(uninterrupted.state_dict()[name], resumed.state_dict()[name])
        for name in uninterrupted.state_dict()
    )
    assert torch.equal(uninterrupted_generator.get_state(), resumed_generator.get_state())


def test_recovery_rejects_different_input_binding(tmp_path: Path) -> None:
    patches, labels, offsets, radii, hard, recipe = _fixture()
    recipe = dict(recipe, epochs=1)
    torch.manual_seed(5)
    initial = copy.deepcopy(TinyModel().state_dict())
    model = _model(initial)
    optimizer = torch.optim.AdamW(model.parameters(), lr=0.01)
    subject._train_epochs(
        model, optimizer, patches, labels, offsets, radii, hard,
        torch.Generator().manual_seed(7), recipe=recipe,
        binding={"input": "a" * 64},
        recovery_path=tmp_path / "recovery.pt",
        progress_path=tmp_path / "progress.json",
        loss_function=_loss,
    )
    target = _model(initial)
    target_optimizer = torch.optim.AdamW(target.parameters(), lr=0.01)
    with pytest.raises(subject.V27TrainingError, match="input binding"):
        subject._load_recovery(
            tmp_path / "recovery.pt",
            model=target,
            optimizer=target_optimizer,
            generator=torch.Generator().manual_seed(7),
            binding={"input": "b" * 64},
            recipe=recipe,
            training_rows=len(labels),
        )


def test_resume_source_rejects_byte_tamper(tmp_path: Path) -> None:
    artifacts = tmp_path / "artifacts"
    artifacts.mkdir()
    checkpoint = artifacts / "recovery.pt"
    checkpoint.write_bytes(b"original")
    digest = hashlib.sha256(checkpoint.read_bytes()).hexdigest()
    assert subject._resume_source(tmp_path, checkpoint, digest) == checkpoint
    checkpoint.write_bytes(b"tampered")
    with pytest.raises(subject.V27TrainingError, match="bytes changed"):
        subject._resume_source(tmp_path, checkpoint, digest)
