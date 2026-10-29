# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import copy

import pytest

from ml.markers.center.confidence_residual_v38 import runner


def valid_config():
    return {
        "task": runner.TASK, "revision": runner.REVISION, "candidate_id": "P1",
        "recipe": copy.deepcopy(runner.RECIPE), "training_group_rows": dict(runner.TRAINING_ROWS),
        "source_checkpoint_sha256": runner.MODEL_SHA256, "private_reads": 0,
        "sealed_runs_authorized": 0, "production_approval": False, "release_eligible": False,
        "eligibility_report_sha256": runner.ELIGIBILITY_SHA256,
        "eligibility_report_path": "artifacts/goal22-runs/reflection-train-negative-eligibility-20261029-v1/report.json",
        "residual_manifest_path": "artifacts/goal22-runs/residual-negative-v38-cache-20261029-v1/manifest.json",
        "residual_manifest_sha256": "a" * 64, "optimizer_steps_expected": 69184,
    }


def test_fixed_experiment_configuration_rejects_scope_and_identity_drift():
    config = valid_config()
    runner.validate_config(config)
    for field, value in (("sealed_runs_authorized", 1), ("production_approval", True),
                         ("release_eligible", True), ("eligibility_report_sha256", "b" * 64),
                         ("optimizer_steps_expected", 69160), ("residual_manifest_path", "other.json")):
        changed = copy.deepcopy(config)
        changed[field] = value
        with pytest.raises(ValueError):
            runner.validate_config(changed)
    changed = copy.deepcopy(config)
    changed["training_group_rows"]["residual_negative:identity"] = 89
    with pytest.raises(ValueError):
        runner.validate_config(changed)
