# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import pytest

from ml.markers.center.crowded_negative_v31.runner import RECIPE, REVISION, validate_config


def config():
    return {"task":"marker-center","revision":REVISION,"candidate_id":"P1",
        "recipe":dict(RECIPE),"sealed_runs_authorized":0,"private_reads":0,"production_approval":False,
        "source_checkpoint_sha256":"5e9cf6f901e1b473e7d46c9b6a556a38f9d530f32be8f577732649a1b19c398e"}


def test_declared_training_only_candidate_is_accepted():
    validate_config(config())


@pytest.mark.parametrize("field,value",[
    ("revision","another-revision"),("candidate_id","P2"),
    ("sealed_runs_authorized",1),("private_reads",1),("production_approval",True),("source_checkpoint_sha256","0"*64)])
def test_identity_read_budget_or_initializer_cannot_change(field,value):
    candidate=config();candidate[field]=value
    with pytest.raises(ValueError):
        validate_config(candidate)


def test_training_threshold_cannot_drift_from_runtime():
    candidate=config();candidate["recipe"]["confidence_threshold"]=.5
    with pytest.raises(ValueError,match="recipe"):
        validate_config(candidate)
