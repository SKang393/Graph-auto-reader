# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import re
import subprocess
from pathlib import Path

import pytest

from ml.policy.evidence_policy import (
    POLICY_REPOSITORY_PATH,
    classify_candidate_failure,
    consumes_candidate_budget,
    evidence_policy_reference,
    execution_provider_rule,
    load_evidence_policy,
    split_rule,
)


REPO_ROOT = Path(__file__).resolve().parents[3]


def test_only_sealed_read_consumes_candidate_budget() -> None:
    assert consumes_candidate_budget("train") is False
    assert consumes_candidate_budget("dev") is False
    assert consumes_candidate_budget("sealed") is True


def test_dev_inspection_is_unrestricted_and_sealed_is_aggregate_only() -> None:
    assert split_rule("dev")["case_level_inspection"] == "unrestricted"
    assert split_rule("sealed")["case_level_inspection"] == "aggregate_only_until_retired"


def test_pre_read_failure_is_void_but_post_read_failure_is_consumed() -> None:
    assert classify_candidate_failure(sealed_read_started=False) == "void"
    assert classify_candidate_failure(sealed_read_started=True) == "consumed"


def test_policy_reference_is_checksum_bound() -> None:
    reference = evidence_policy_reference()

    assert reference["path"] == POLICY_REPOSITORY_PATH
    assert reference["schema_version"] == 1
    assert reference["policy_revision"] == "2026-08-19"
    assert re.fullmatch(r"[0-9a-f]{64}", str(reference["sha256"]))


def test_sealed_reuse_and_reserve_match_governing_policy() -> None:
    policy = load_evidence_policy()

    assert policy["sealed_set_reuse"]["maximum_distinct_revisions"] == 5
    assert policy["sealed_set_reuse"]["minimum_unused_sets"] == 2


def test_unknown_split_fails_closed() -> None:
    with pytest.raises(ValueError, match="unsupported evidence split"):
        split_rule("public")


def test_sealed_execution_policy_is_cpu_only_and_optimization_disabled() -> None:
    sealed = execution_provider_rule("sealed")

    assert sealed["allowed_providers"] == ["CPUExecutionProvider"]
    assert sealed["cpu_fallback_required"] is True
    assert sealed["graph_optimization"] == "ORT_DISABLE_ALL"


def test_all_revision_protocols_reference_the_shared_policy() -> None:
    tracked = subprocess.run(
        ["git", "ls-files", "ml/ocr/**/protocol.py", "ml/markers/**/protocol.py"],
        cwd=REPO_ROOT,
        capture_output=True,
        check=True,
        text=True,
    ).stdout.splitlines()
    protocols = [REPO_ROOT / path for path in tracked]

    assert protocols
    missing = [
        path.relative_to(REPO_ROOT).as_posix()
        for path in protocols
        if POLICY_REPOSITORY_PATH not in path.read_text(encoding="utf-8")
    ]
    assert missing == []


def test_revision_protocols_do_not_override_dev_inspection_policy() -> None:
    tracked = subprocess.run(
        ["git", "ls-files", "ml/ocr/**/protocol.py", "ml/markers/**/protocol.py"],
        cwd=REPO_ROOT,
        capture_output=True,
        check=True,
        text=True,
    ).stdout.splitlines()
    protocols = [REPO_ROOT / path for path in tracked]

    stale = [
        path.relative_to(REPO_ROOT).as_posix()
        for path in protocols
        if "public_case_level_failure_analysis_permitted" in path.read_text(encoding="utf-8")
    ]
    assert stale == []
