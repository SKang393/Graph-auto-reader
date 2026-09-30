# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import copy
import pytest

from ml.markers.center.crowded_coverage_v30 import runner


def config():
    return {"task": "marker-center", "revision": "marker-center-crowded-coverage-v30",
            "candidate_id": "P1", "recipe": copy.deepcopy(runner.RECIPE), "sealed_runs_authorized": 0,
            "source_checkpoint_sha256": "774ef9455e9ce7b489e640c5237b81275f24e42c2d2d0d3d87be4e953b3eb365"}


def test_fixed_owned_warm_start_recipe_is_admitted():
    runner.validate_config(config())
    assert runner.RECIPE["checkpoint_selection"] == "fixed_final_epoch_no_dev_selection"


@pytest.mark.parametrize("defect", ["revision", "candidate", "initializer", "threshold", "epochs", "sealed"])
def test_recipe_or_authorization_drift_is_rejected_before_training(defect):
    value = config()
    if defect == "revision":
        value["revision"] = "marker-center-shape-coverage-v28"
    elif defect == "candidate":
        value["candidate_id"] = "P2"
    elif defect == "initializer":
        value["source_checkpoint_sha256"] = "0"*64
    elif defect == "threshold":
        value["recipe"]["confidence_threshold"] = .1
    elif defect == "epochs":
        value["recipe"]["epochs"] = 9
    else:
        value["sealed_runs_authorized"] = 1
    with pytest.raises(ValueError):
        runner.validate_config(value)
