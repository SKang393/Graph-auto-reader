# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Runtime panel identity and marker-free crop sampling regressions."""

from dataclasses import dataclass, replace
from types import SimpleNamespace

import pytest
import torch

from ml.markers.center.mask_preserving_v24.family_scenes import FamilyScene
from ml.markers.center.mask_preserving_v24 import train_p1


@dataclass(frozen=True)
class PanelScene(FamilyScene):
    sampling_identity: str


def _scene(identity="source:crop:panel-1"):
    return PanelScene(
        "train", "owned-family", 39300, torch.zeros((3, 32, 32)),
        ((8.0, 8.0),), (4.0,), (), identity,
    )


def test_runtime_sample_digest_binds_panel_and_proposal_location():
    scene = _scene()
    selected = ((0,),)
    locations = (torch.tensor(((8.0, 8.0),)),)
    baseline = train_p1._selected_index_sha256((scene,), selected, locations)
    assert baseline == train_p1._selected_index_sha256((scene,), selected, locations)
    assert baseline != train_p1._selected_index_sha256(
        (replace(scene, sampling_identity="source:crop:panel-2"),), selected, locations
    )
    assert baseline != train_p1._selected_index_sha256(
        (scene,), selected, (torch.tensor(((9.0, 8.0),)),)
    )
    with pytest.raises(ValueError, match="requires proposal coordinates"):
        train_p1._selected_index_sha256((scene,), selected)


def test_marker_free_panel_retains_zero_budget_without_inventing_targets(monkeypatch):
    proposals = SimpleNamespace(
        coordinates=torch.tensor(((8.0, 8.0), (20.0, 20.0))),
        patches=torch.zeros((2, 3, 33, 33)),
    )
    monkeypatch.setattr(train_p1, "extract_proposals", lambda tensor: proposals)
    positive = _scene()
    background = replace(
        positive, centers=(), diameters=(), sampling_identity="source:crop:background"
    )
    patches, labels, offsets, radii, hard, report = train_p1._examples_with_report(
        (positive, background), 10, torch.Generator().manual_seed(17),
        sampling_mode="family-train",
    )
    assert report.selections == ((0, 1), ())
    assert labels.tolist() == [1.0, 0.0]
    assert len(patches) == 2
    assert offsets[0].tolist() == [0.0, 0.0]
    assert radii.tolist() == [2.0, 2.0]
    assert torch.isfinite(offsets).all()
    assert not hard.any()


def test_runtime_family_sampling_still_rejects_dev(monkeypatch):
    def forbidden_extract(tensor):
        pytest.fail("dev inputs reached proposal sampling")

    monkeypatch.setattr(train_p1, "extract_proposals", forbidden_extract)
    with pytest.raises(ValueError, match="train FamilyScene"):
        train_p1._examples_with_report(
            (replace(_scene(), split="validation"),), 10,
            torch.Generator().manual_seed(17), sampling_mode="family-train",
        )
