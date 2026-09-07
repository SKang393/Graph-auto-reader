# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import math
import json
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest
import torch

from ml.markers.center.mask_preserving_v24.mask_preserving import extract_proposals
from ml.markers.center.mask_preserving_v24 import protocol
from ml.markers.center.real_range_generator_v1.generator import build_split


@pytest.mark.parametrize("distance,radius,expected", [(4.5, 2.5, 1), (5.0, 2.5, 2), (5.5, 2.5, 2), (9.5, 8.0, 1), (10.0, 8.0, 2)])
def test_nms_shared_csharp_distance_and_radius_boundaries(distance, radius, expected):
    from ml.markers.center.mask_preserving_v24.mask_preserving import postprocess

    tensor = torch.zeros((3, 32, 32))
    tensor[0] = 1
    proposals = extract_proposals(tensor)
    assert len(proposals.coordinates) == 64
    assert proposals.coordinates[18:20].tolist() == [[8, 8], [12, 8]]
    output = np.zeros((64, 4), dtype=np.float32)
    output[18] = [.9, 0, 0, radius]
    output[19] = [.8, (distance-4)/4, 0, radius]
    assert len(postprocess(SimpleNamespace(tensor=tensor), proposals, output)) == expected


def test_consensus_shared_csharp_does_not_move_decoded_point():
    from ml.markers.center.mask_preserving_v24.mask_preserving import postprocess

    tensor = torch.zeros((3, 32, 32))
    for x, y in (
        (6, 8), (12, 8), (9, 5), (9, 11),
        (6, 5), (12, 5), (6, 11), (12, 11),
    ):
        tensor[0, y, x] = 1
    proposals = extract_proposals(tensor)
    target = proposals.coordinates.tolist().index([8, 8])
    output = np.zeros((len(proposals.coordinates), 4), dtype=np.float32)
    output[target] = [.9, 0, 0, 3]

    assert postprocess(SimpleNamespace(tensor=tensor), proposals, output) == ()

def test_masks_do_not_remove_ink_supported_proposals():
    scene = build_split("dev")[0]
    proposals = extract_proposals(scene.tensor)
    assert proposals.patches.shape[1:] == (3, 33, 33)
    assert proposals.patches.shape[0] > 0
    assert float(proposals.patches[:, 1].sum()) > 0
    assert float(proposals.patches[:, 2].sum()) > 0

def test_mask_crossing_truths_retain_nearby_proposals():
    scene = next(
        item for item in build_split("dev")
        if any(
            float(item.tensor[1:, int(y)-2:int(y)+3, int(x)-2:int(x)+3].max()) >= .35
            for x, y in item.centers
        )
    )
    proposals = extract_proposals(scene.tensor)
    coordinates = proposals.coordinates.tolist()
    masked = [
        (x, y) for x, y in scene.centers
        if float(scene.tensor[1:, int(y)-2:int(y)+3, int(x)-2:int(x)+3].max()) >= .35
    ]
    assert masked
    assert all(any(math.hypot(px-x, py-y) <= 5 for px, py in coordinates) for x, y in masked)

def test_feasibility_result_is_non_consuming_and_startable():
    report = json.loads(Path("ml/markers/center/mask_preserving_v24/FEASIBILITY.json").read_text(encoding="utf-8"))
    assert report["status"] == "failed_frozen_model_training_candidate_startable"
    assert report["scope"]["candidate_consumed"] is False
    assert report["scope"]["optimizer_steps"] == 0
    assert report["scope"]["real_sealed_reads"] == 0
    assert report["proposal_coverage"]["recall"] == 1.0
    assert report["metrics"]["precision"] < 0.95
    assert report["metrics"]["recall"] < 0.95
    assert report["binding"]["v21_onnx_sha256"] == protocol.V21_ONNX_SHA256
    assert not Path(report["binding"]["v21_onnx_path"]).is_absolute()


def test_active_gate_reads_shared_marker_center_bars():
    from ml.markers.center.mask_preserving_v24.train_p1 import _shared_marker_acceptance_bar

    assert _shared_marker_acceptance_bar() == {
        "proposal_recall_minimum": 0.95,
        "precision_minimum": 0.95,
        "recall_minimum": 0.95,
        "prohibited_structure_hit_rate_maximum": 0.02,
    }
