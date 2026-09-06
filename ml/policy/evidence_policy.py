# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authoritative evidence-policy decisions shared by model revisions."""

from __future__ import annotations

import hashlib
import json
from copy import deepcopy
from pathlib import Path
from typing import Any


POLICY_PATH = Path(__file__).with_name("evidence-policy.json")
ACCEPTANCE_BARS_PATH = Path(__file__).with_name("acceptance-bars.json")
POLICY_REPOSITORY_PATH = "ml/policy/evidence-policy.json"


def _read_policy() -> dict[str, Any]:
    policy = json.loads(POLICY_PATH.read_text(encoding="utf-8"))
    splits = policy.get("splits", {})
    if set(splits) != {"train", "dev", "sealed"}:
        raise ValueError("evidence policy must define train, dev, and sealed splits")
    if any(splits[name]["consumes_candidate_budget"] for name in ("train", "dev")):
        raise ValueError("train and dev must not consume candidate budget")
    if not splits["sealed"]["consumes_candidate_budget"]:
        raise ValueError("sealed must consume candidate budget")
    if policy["budget_accounting"]["consumption_event"] != "first_sealed_split_read":
        raise ValueError("candidate budget may be consumed only at first sealed split read")
    execution = policy.get("execution_providers", {})
    cpu_provider = execution.get("cpu_provider")
    if cpu_provider != "CPUExecutionProvider":
        raise ValueError("CPUExecutionProvider must remain the mandatory provider")
    sealed = execution.get("sealed", {})
    if sealed.get("allowed_providers") != [cpu_provider]:
        raise ValueError("sealed execution must allow only CPUExecutionProvider")
    if sealed.get("cpu_fallback_required") is not True:
        raise ValueError("sealed execution must require CPU fallback")
    if sealed.get("graph_optimization") != "ORT_DISABLE_ALL":
        raise ValueError("sealed execution must disable ONNX graph optimization")
    train_dev = execution.get("train_dev", {})
    if train_dev.get("cpu_fallback_required") is not True:
        raise ValueError("train/dev execution must require CPU fallback")
    if train_dev.get("allowed_non_cpu_providers") != "any_installed_provenance_reviewed":
        raise ValueError("train/dev must allow any installed provenance-reviewed provider")
    return policy


def load_evidence_policy() -> dict[str, Any]:
    """Return an independent copy of the checked authoritative policy."""

    return deepcopy(_read_policy())


def tier1_acceptance_bars() -> dict[str, Any]:
    """Return the canonical reviewable-error thresholds."""

    document = json.loads(ACCEPTANCE_BARS_PATH.read_text(encoding="utf-8"))
    bars = document.get("tier1_reviewable_error")
    if not isinstance(bars, dict):
        raise ValueError("acceptance bars must define tier1_reviewable_error")
    return deepcopy(bars)


def evidence_policy_reference() -> dict[str, object]:
    """Return the checksum-bound reference stored by revision protocols."""

    policy = _read_policy()
    return {
        "path": POLICY_REPOSITORY_PATH,
        "schema_version": policy["schema_version"],
        "policy_revision": policy["policy_revision"],
        "sha256": hashlib.sha256(POLICY_PATH.read_bytes()).hexdigest(),
    }


def split_rule(split: str) -> dict[str, Any]:
    """Return the authoritative rule for one split."""

    try:
        return deepcopy(_read_policy()["splits"][split])
    except KeyError as error:
        raise ValueError(f"unsupported evidence split: {split}") from error


def execution_provider_rule(split: str) -> dict[str, Any]:
    """Return the provider rule for a train, dev, or sealed split."""

    policy = _read_policy()
    execution = policy["execution_providers"]
    if split == "sealed":
        return deepcopy(execution["sealed"])
    if split in {"train", "dev"}:
        return deepcopy(execution["train_dev"])
    raise ValueError(f"unsupported evidence split: {split}")


def cpu_execution_provider() -> str:
    """Return the mandatory CPU provider name from the authoritative policy."""

    return str(_read_policy()["execution_providers"]["cpu_provider"])


def consumes_candidate_budget(split: str) -> bool:
    """Report whether reading the named split consumes a candidate."""

    return bool(split_rule(split)["consumes_candidate_budget"])


def classify_candidate_failure(*, sealed_read_started: bool) -> str:
    """Classify a failed run under the shared budget rule."""

    return "consumed" if sealed_read_started else "void"


__all__ = [
    "POLICY_PATH",
    "ACCEPTANCE_BARS_PATH",
    "POLICY_REPOSITORY_PATH",
    "classify_candidate_failure",
    "consumes_candidate_budget",
    "cpu_execution_provider",
    "evidence_policy_reference",
    "execution_provider_rule",
    "load_evidence_policy",
    "split_rule",
    "tier1_acceptance_bars",
]
