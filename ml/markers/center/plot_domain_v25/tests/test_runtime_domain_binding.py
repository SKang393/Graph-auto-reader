# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from copy import deepcopy
from pathlib import Path

import numpy as np
import pytest
import torch

from ml.markers.center.mask_preserving_v24.runtime_family_scenes import RuntimeFamilyScene
from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    RuntimeEvidenceBinding,
    RuntimeModelIdentity,
    ValidatedRuntimePanelInput,
)
from ml.markers.center.plot_domain_v25.runtime_domain_binding import (
    RuntimeDomainBindingError,
    _bind_split,
    _collect_failures,
    _panel_domain_from_report,
    load_runtime_domain_binding,
)


SOURCE_SHA = "1" * 64
PANEL_SHA = "2" * 64


def _input() -> ValidatedRuntimePanelInput:
    shape = (30, 40)
    return ValidatedRuntimePanelInput(
        split="train",
        dataset_seed=393,
        family="renderer=a|font=b|degradation=c|template=d|marker=e",
        scene_seed=4,
        source_sha256=SOURCE_SHA,
        panel_id="panel-1",
        panel_sha256=PANEL_SHA,
        width=40,
        height=30,
        crop=(10, 20, 40, 30),
        requested_crop=(9.5, 19.5, 40.5, 30.5),
        gray8=np.zeros(shape, dtype=np.uint8),
        ocr_mask=np.zeros(shape, dtype=np.float32),
        geometry_mask=np.zeros(shape, dtype=np.float32),
        artifact_mask=np.zeros(shape, dtype=np.float32),
    )


def _axis_model() -> RuntimeModelIdentity:
    return RuntimeModelIdentity("axis", "axis-opencv-v1", "OpenCvSharpExtern", "axis-opencv-v1", "3" * 64, "cpu")


def _evidence() -> tuple[dict, dict]:
    model = {
        "model_id": "OpenCvSharpExtern",
        "version": "axis-opencv-v1",
        "sha256": "3" * 64,
        "provider": "cpu",
    }
    envelope = {
        "contract_version": 1,
        "run_id": "run",
        "project_id": "project",
        "panel_id": "panel-1",
        "stage": "axis",
        "stage_version": "axis-opencv-v1",
        "input_sha256": PANEL_SHA,
        "coordinate_space": "original_pixels",
        "model": model,
        "timing": {},
        "confidence": 0.9,
        "warnings": [],
        "transforms": [],
    }
    corners = {
        "bottom_left": {"x": 4.0, "y": 25.0, "is_finite": True},
        "bottom_right": {"x": 35.0, "y": 25.0, "is_finite": True},
        "top_right": {"x": 35.0, "y": 4.0, "is_finite": True},
        "top_left": {"x": 4.0, "y": 4.0, "is_finite": True},
    }
    polygon = {**corners, "points": [corners[key] for key in ("bottom_left", "bottom_right", "top_right", "top_left")]}
    panel = {
        "panel_id": "panel-1",
        "image_sha256": PANEL_SHA,
        "width": 40,
        "height": 30,
        "source_image_sha256": SOURCE_SHA,
        "source_width": 100,
        "source_height": 80,
        "crop": {"x": 10, "y": 20, "width": 40, "height": 30},
        "requested_crop": {"x": 9.5, "y": 19.5, "width": 40.5, "height": 30.5},
        "source_to_panel_matrix": [1, 0, -10, 0, 1, -20, 0, 0, 1],
        "panel_to_source_matrix": [1, 0, 10, 0, 1, 20, 0, 0, 1],
        "composed_mask_source_envelopes": [envelope],
        "axis": {
            "envelope": envelope,
            "geometry": {"coordinate_space": "original_pixels", "plot_polygon": polygon},
        },
    }
    case = {"image_sha256": SOURCE_SHA, "width": 100, "height": 80}
    return case, panel


def _binding() -> RuntimeEvidenceBinding:
    return RuntimeEvidenceBinding("train", Path("manifest.json"), "4" * 64, Path("report.json"), "5" * 64)


def test_axis_polygon_binds_exact_crop_and_round_trips_to_source() -> None:
    case, panel = _evidence()

    result = _panel_domain_from_report(_input(), case, panel, _binding(), _axis_model())

    assert result.domain.polygon == ((4, 25), (35, 25), (35, 4), (4, 4))
    assert result.source_polygon == ((14, 45), (45, 45), (45, 24), (14, 24))
    assert result.domain.identity == result.evidence_sha256
    assert len(result.evidence_sha256) == 64


@pytest.mark.parametrize("mutation,match", [
    (lambda panel: panel["source_to_panel_matrix"].__setitem__(2, -9), "crop matrices"),
    (lambda panel: panel["axis"]["envelope"].__setitem__("input_sha256", "9" * 64), "identity differs"),
    (lambda panel: panel["axis"]["geometry"]["plot_polygon"]["points"].reverse(), "corner and point order"),
    (lambda panel: panel["axis"]["geometry"]["plot_polygon"]["top_left"].__setitem__("x", 41), "panel bounds"),
])
def test_tampered_axis_or_mapping_fails_closed(mutation, match: str) -> None:
    case, panel = _evidence()
    panel = deepcopy(panel)
    mutation(panel)

    with pytest.raises(RuntimeDomainBindingError, match=match):
        _panel_domain_from_report(_input(), case, panel, _binding(), _axis_model())


def test_missing_axis_never_falls_back_to_truth_or_full_canvas() -> None:
    case, panel = _evidence()
    panel.pop("axis")

    with pytest.raises(RuntimeDomainBindingError, match="missing exact axis"):
        _panel_domain_from_report(_input(), case, panel, _binding(), _axis_model())


def test_failed_source_and_panel_are_retained_in_failure_inventory() -> None:
    report = {
        "cases": [
            {"image_sha256": SOURCE_SHA, "status": "failed", "stage": "axis", "error": "no axes", "panels": []},
            {
                "image_sha256": "6" * 64,
                "status": "panels-completed",
                "panels": [{"panel_id": "panel-2", "status": "failed", "stage": "axis", "error": "invalid polygon"}],
            },
        ]
    }

    failures = _collect_failures("validation", report)

    assert [(item.source_sha256, item.panel_id, item.stage) for item in failures] == [
        (SOURCE_SHA, None, "axis"),
        ("6" * 64, "panel-2", "axis"),
    ]


def test_domain_truth_join_requires_every_source_panel_and_crop_identity() -> None:
    case, panel = _evidence()
    panel_domain = _panel_domain_from_report(_input(), case, panel, _binding(), _axis_model())
    scene = RuntimeFamilyScene(
        split="train",
        family=_input().family,
        seed=4,
        tensor=torch.zeros((3, 30, 40), dtype=torch.float32),
        centers=((20.0, 10.0),),
        diameters=(6.0,),
        hard_negatives=(),
        source_sha256=SOURCE_SHA,
        panel_id="panel-1",
        panel_sha256=PANEL_SHA,
        crop=(10, 20, 40, 30),
        requested_crop=(9.5, 19.5, 40.5, 30.5),
        dataset_seed=393,
        sampling_identity="sampling",
    )

    assert _bind_split((panel_domain,), (scene,), "train")[0].scene is scene
    changed = RuntimeFamilyScene(**{**scene.__dict__, "panel_sha256": "9" * 64})
    with pytest.raises(RuntimeDomainBindingError, match="scene identity"):
        _bind_split((panel_domain,), (changed,), "train")


def test_loader_rejects_non_preregistered_binding_before_reading_files(tmp_path: Path) -> None:
    with pytest.raises(RuntimeDomainBindingError, match="preregistered family binding"):
        load_runtime_domain_binding(tmp_path / "foreign.json", "9" * 64, repository_root=tmp_path)
