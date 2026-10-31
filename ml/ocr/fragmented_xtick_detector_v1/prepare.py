# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fail-closed source-only preparation for fresh fragmented X-tick identities.

This module deliberately produces only deterministic procedural specifications.
It does not render a raster, load a candidate, execute inference, or open a
training, development, or sealed corpus.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from hashlib import sha256
import json
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parent
PROTOCOL_PATH = ROOT / "PROTOCOL.json"
SOURCE_PATHS = ("__init__.py", "PROTOCOL.json", "README.md", "prepare.py", "materialize.py", "test_prepare.py")
FAMILY = "fresh-fragmented-numeric-xtick-v1"
REVISION = "graph-text-fragmented-xtick-detector-v1"
_NUMERIC_LABELS = ("10", "12", "20", "25", "50", "100", "2.5", "-1")
_VARIANTS = ("left-gap", "middle-gap", "right-gap", "double-gap")
_FORBIDDEN_LEGACY_TOKENS = ("v8", "v9", "v29", "v30", "archive", "checkpoint", "onnx")


class PreparationError(RuntimeError):
    """The source-only registration is malformed or no longer fresh."""


@dataclass(frozen=True)
class SplitRegistration:
    split: str
    case_count: int
    seed_offset: int
    renderer_family: str
    degradation_family: str


def _canonical_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")).encode("utf-8")


def _sha256(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _read_protocol() -> dict[str, Any]:
    try:
        value = json.loads(PROTOCOL_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise PreparationError("Fragmented X-tick protocol is unreadable") from error
    if not isinstance(value, dict):
        raise PreparationError("Fragmented X-tick protocol must be an object")
    return value


def registrations() -> tuple[SplitRegistration, ...]:
    protocol = _read_protocol()
    rows = protocol.get("splits")
    if not isinstance(rows, list) or len(rows) != 3:
        raise PreparationError("Fragmented X-tick protocol must register exactly three splits")
    result: list[SplitRegistration] = []
    for row in rows:
        if not isinstance(row, dict):
            raise PreparationError("Fragmented X-tick split registration must be an object")
        try:
            registration = SplitRegistration(
                split=str(row["split"]),
                case_count=int(row["case_count"]),
                seed_offset=int(row["seed_offset"]),
                renderer_family=str(row["renderer_family"]),
                degradation_family=str(row["degradation_family"]),
            )
        except (KeyError, TypeError, ValueError) as error:
            raise PreparationError("Fragmented X-tick split registration is malformed") from error
        if registration.case_count <= 0 or registration.seed_offset <= 0:
            raise PreparationError("Fragmented X-tick splits require positive counts and seeds")
        result.append(registration)
    if {item.split for item in result} != {"train", "dev", "sealed_public_future"}:
        raise PreparationError("Fragmented X-tick split names changed")
    if len({item.seed_offset for item in result}) != len(result):
        raise PreparationError("Fragmented X-tick split seed offsets must be distinct")
    return tuple(result)


def _case_identity(registration: SplitRegistration, index: int) -> str:
    payload = {
        "family": FAMILY,
        "split": registration.split,
        "seed": registration.seed_offset + index,
        "variant": _VARIANTS[index % len(_VARIANTS)],
        "numeric_label": _NUMERIC_LABELS[(index * 5 + 1) % len(_NUMERIC_LABELS)],
    }
    return sha256(_canonical_json(payload)).hexdigest()


def case_specifications(registration: SplitRegistration) -> tuple[dict[str, object], ...]:
    cases: list[dict[str, object]] = []
    for index in range(registration.case_count):
        text = _NUMERIC_LABELS[(index * 5 + 1) % len(_NUMERIC_LABELS)]
        fragment_count = min(3, len(text))
        seed = registration.seed_offset + index
        case_id = f"{FAMILY}-{registration.split}-{seed}"
        cases.append({
            "case_id": case_id,
            "case_identity_sha256": _case_identity(registration, index),
            "split": registration.split,
            "seed": seed,
            "renderer_family": registration.renderer_family,
            "degradation_family": registration.degradation_family,
            "positive": {
                "role": "XTick",
                "text": text,
                "numeric": True,
                "fragment_variant": _VARIANTS[index % len(_VARIANTS)],
                "fragment_count": fragment_count,
                "complete_label_required": True,
                "candidate_group_key": f"{case_id}:label",
            },
            "negative_confusers": [
                {"kind": "x_axis_tick_segment", "candidate_group_key": f"{case_id}:tick"},
                {"kind": "x_axis_baseline_fragment", "candidate_group_key": f"{case_id}:baseline"},
                {"kind": "marker_like_dot", "candidate_group_key": f"{case_id}:dot"},
            ],
            "materialized": False,
        })
    return tuple(cases)


def source_bundle_sha256() -> str:
    digest = sha256()
    for relative in SOURCE_PATHS:
        path = ROOT / relative
        if not path.is_file():
            raise PreparationError(f"Fragmented X-tick source is missing: {relative}")
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(path.read_bytes())
        digest.update(b"\0")
    return digest.hexdigest()


def build_preflight() -> dict[str, object]:
    protocol = _read_protocol()
    expected = {
        "schema": "graphreader.ocr-fragmented-xtick-preparation.v1",
        "evidence_policy": "ml/policy/evidence-policy.json",
        "revision": REVISION,
        "task": "ocr-detection",
        "state": "train-only-materializer-preparation",
        "synthetic_family": FAMILY,
        "production_approval": False,
        "release_eligible": False,
    }
    for key, value in expected.items():
        if protocol.get(key) != value:
            raise PreparationError(f"Fragmented X-tick protocol field changed: {key}")
    training = protocol.get("training")
    future = protocol.get("future_evaluation")
    materialization = protocol.get("train_materialization")
    if not isinstance(training, dict) or not isinstance(future, dict) or not isinstance(materialization, dict) or (
        training.get("optimizer_steps_authorized") != 0
        or training.get("candidate_id") is not None
        or future.get("dev_evaluation_authorized") is not False
        or future.get("sealed_evaluation_authorized") is not False
        or future.get("sealed_cases_materialized") is not False
        or materialization.get("authorized") is not True
        or materialization.get("development_or_public_rendering_authorized") is not False
    ):
        raise PreparationError("Fragmented X-tick preparation must remain non-executable")
    split_rows: list[dict[str, object]] = []
    identities: set[str] = set()
    case_ids: set[str] = set()
    for registration in registrations():
        cases = case_specifications(registration)
        for case in cases:
            case_id = str(case["case_id"])
            identity = str(case["case_identity_sha256"])
            if case_id in case_ids or identity in identities:
                raise PreparationError("Fragmented X-tick case identity collision")
            if any(token in case_id.lower() for token in _FORBIDDEN_LEGACY_TOKENS):
                raise PreparationError("Fragmented X-tick case identity includes a legacy token")
            if case["materialized"] is not False:
                raise PreparationError("Fragmented X-tick preparation cannot materialize a case")
            positive = case["positive"]
            confusers = case["negative_confusers"]
            if not isinstance(positive, dict) or not isinstance(confusers, list) or (
                positive.get("role") != "XTick"
                or positive.get("numeric") is not True
                or positive.get("complete_label_required") is not True
                or int(positive.get("fragment_count", 0)) < 2
                or {entry.get("kind") for entry in confusers if isinstance(entry, dict)}
                != {"x_axis_tick_segment", "x_axis_baseline_fragment", "marker_like_dot"}
            ):
                raise PreparationError("Fragmented X-tick label/confuser contract changed")
            if len({str(positive["candidate_group_key"]), *(str(item["candidate_group_key"]) for item in confusers)}) != 4:
                raise PreparationError("Fragmented X-tick positive and confuser groups overlap")
            case_ids.add(case_id)
            identities.add(identity)
        split_rows.append({
            "split": registration.split,
            "case_count": registration.case_count,
            "seed_offset": registration.seed_offset,
            "renderer_family": registration.renderer_family,
            "degradation_family": registration.degradation_family,
            "case_identity_stream_sha256": sha256(_canonical_json(cases)).hexdigest(),
        })
    return {
        "schema": "graphreader.ocr-fragmented-xtick-preflight.v1",
        "revision": REVISION,
        "state": "train-only-materializer-preparation",
        "source_paths": list(SOURCE_PATHS),
        "source_bundle_sha256": source_bundle_sha256(),
        "protocol_sha256": _sha256(PROTOCOL_PATH),
        "split_registrations": split_rows,
        "case_count": len(case_ids),
        "unique_case_identity_count": len(identities),
        "synthetic_only": True,
        "pixels_rendered": False,
        "train_materialization_authorized": True,
        "weights_loaded": False,
        "optimizer_steps": 0,
        "dev_evaluation": False,
        "sealed_evaluation": False,
        "production_approval": False,
        "release_eligible": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preflight", action="store_true", help="print only the deterministic source preparation receipt")
    arguments = parser.parse_args()
    if not arguments.preflight:
        parser.error("only --preflight is supported; this preparation cannot materialize or execute a candidate")
    print(json.dumps(build_preflight(), indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
