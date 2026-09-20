# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import pytest

from ml.markers.center.train_negative_v29.runner import RECIPE, REVISION, validate_config


def config():
    return {"task":"marker-center","revision":REVISION,"candidate_id":"P1",
        "recipe":dict(RECIPE),"sealed_runs_authorized":0,
        "source_checkpoint_sha256":"ec343b0d7c1963893f940abbba9b93cf4bf0d35f52026215d9d2050422e25ec2"}


def test_declared_training_only_candidate_is_accepted():
    validate_config(config())


@pytest.mark.parametrize("field,value",[
    ("revision","another-revision"),("candidate_id","P2"),
    ("sealed_runs_authorized",1),("source_checkpoint_sha256","0"*64)])
def test_identity_read_budget_or_initializer_cannot_change(field,value):
    candidate=config();candidate[field]=value
    with pytest.raises(ValueError):
        validate_config(candidate)


def test_training_threshold_cannot_drift_from_runtime():
    candidate=config();candidate["recipe"]["confidence_threshold"]=.5
    with pytest.raises(ValueError,match="recipe"):
        validate_config(candidate)
