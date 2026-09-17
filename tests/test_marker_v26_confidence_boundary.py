# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

from dataclasses import dataclass
import importlib.util
from pathlib import Path
import sys

import numpy as np
import pytest
import torch


SCRIPT = (
    Path(__file__).resolve().parents[1]
    / "tools/GraphReader.SyntheticRuntimeEvidence/diagnose_marker_v26_confidence_boundary.py"
)
SPEC = importlib.util.spec_from_file_location("diagnose_marker_v26_confidence_boundary", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
subject = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = subject
SPEC.loader.exec_module(subject)


def _patch(value: float) -> torch.Tensor:
    return torch.full((3, 33, 33), value, dtype=torch.float32)


def test_canonical_source_rebinding_changes_only_canonical_field() -> None:
    historical = "1" * 64
    current = "2" * 64
    document = {
        "canonical_json_source_sha256": historical,
        "runtime_binding_source_sha256": "3" * 64,
        "runtime_loader_source_sha256": "4" * 64,
        "runtime_join_source_sha256": "5" * 64,
        "historical_domain_source_sha256": "6" * 64,
        "proposal_domain_source_sha256": "7" * 64,
        "binding_v3_source_sha256": "8" * 64,
    }
    original = dict(document)
    validated: list[dict[str, str]] = []

    def validator(value: dict[str, str]) -> None:
        validated.append(dict(value))

    subject._validate_with_canonical_source_rebinding(
        document,
        expected_historical_sha256=historical,
        authenticated_current_sha256=current,
        helper_validator=validator,
    )

    assert document == original
    assert validated == [{**original, "canonical_json_source_sha256": current}]


def test_canonical_source_rebinding_retains_unrelated_helper_rejection() -> None:
    historical = "1" * 64
    current = "2" * 64
    document = {
        "canonical_json_source_sha256": historical,
        "runtime_binding_source_sha256": "wrong",
    }

    def validator(value: dict[str, str]) -> None:
        assert value["canonical_json_source_sha256"] == current
        if value["runtime_binding_source_sha256"] != "3" * 64:
            raise RuntimeError("runtime binding source differs")

    with pytest.raises(RuntimeError, match="runtime binding source differs"):
        subject._validate_with_canonical_source_rebinding(
            document,
            expected_historical_sha256=historical,
            authenticated_current_sha256=current,
            helper_validator=validator,
        )


@dataclass(frozen=True)
class _SourceIdentity:
    relative_path: Path
    sha256: str


@dataclass(frozen=True)
class _GeneratorProfile:
    sources: tuple[_SourceIdentity, ...]


def test_renderer_source_rebinding_changes_only_renderer_for_original_validator() -> None:
    renderer = Path("ml/synthetic/renderer.py")
    historical = "1" * 64
    current = "2" * 64
    other = _SourceIdentity(Path("ml/synthetic/dataset.py"), "3" * 64)
    profile = _GeneratorProfile((_SourceIdentity(renderer, historical), other))
    validated: list[_GeneratorProfile] = []

    def validator(value: _GeneratorProfile, root: Path) -> None:
        assert root == Path("repository")
        validated.append(value)

    subject._validate_with_renderer_source_rebinding(
        profile,
        Path("repository"),
        renderer_relative_path=renderer,
        expected_historical_sha256=historical,
        authenticated_current_sha256=current,
        profile_validator=validator,
    )

    assert profile.sources == (_SourceIdentity(renderer, historical), other)
    assert validated == [_GeneratorProfile((_SourceIdentity(renderer, current), other))]


def test_renderer_source_rebinding_retains_unrelated_source_rejection() -> None:
    renderer = Path("ml/synthetic/renderer.py")
    historical = "1" * 64
    current = "2" * 64
    profile = _GeneratorProfile(
        (
            _SourceIdentity(renderer, historical),
            _SourceIdentity(Path("ml/synthetic/dataset.py"), "wrong"),
        )
    )

    def validator(value: _GeneratorProfile, _root: Path) -> None:
        assert value.sources[0].sha256 == current
        if value.sources[1].sha256 != "3" * 64:
            raise RuntimeError("dataset source differs")

    with pytest.raises(RuntimeError, match="dataset source differs"):
        subject._validate_with_renderer_source_rebinding(
            profile,
            Path("repository"),
            renderer_relative_path=renderer,
            expected_historical_sha256=historical,
            authenticated_current_sha256=current,
            profile_validator=validator,
        )


def test_nearest_pair_metrics_excludes_identity_and_reports_channel_maxima() -> None:
    patches = torch.stack((_patch(0.0), _patch(0.25), _patch(1.0)))

    result = subject._nearest_pair_metrics(
        patches,
        np.asarray((0, 1)),
        np.asarray((0, 1, 2)),
        exclude_identity=True,
        block_size=1,
    )

    assert result["matched_query_count"] == 2
    assert result["unmatched_query_count"] == 0
    assert result["normalized_rmse"]["minimum"] == pytest.approx(0.25)
    assert result["normalized_rmse"]["maximum"] == pytest.approx(0.25)
    for channel in subject.CHANNEL_NAMES:
        assert result["channel_max_absolute_difference"][channel]["median"] == pytest.approx(0.25)


def test_nearest_pair_metrics_reports_unmatched_singleton_same_label() -> None:
    patches = torch.stack((_patch(0.0), _patch(1.0)))

    result = subject._nearest_pair_metrics(
        patches, (0,), (0,), exclude_identity=True, block_size=4
    )

    assert result["query_count"] == 1
    assert result["matched_query_count"] == 0
    assert result["unmatched_query_count"] == 1
    assert result["normalized_rmse"] == {"count": 0}


def test_score_aggregates_preserve_fixed_threshold_and_annulus_accounting() -> None:
    scores = np.asarray((0.9, 0.1, 0.3, 0.2, 0.8, 0.01), dtype=np.float32)
    labels = np.asarray((True, True, False, False, False, False))
    distances = np.asarray((0.0, 2.0, 4.0, 6.0, 10.0, 20.0), dtype=np.float32)

    result = subject._score_aggregates(scores, labels, distances, 0.25)

    assert result["positive_le3"]["above_or_equal_threshold"] == 1
    assert result["positive_le3"]["below_threshold"] == 1
    assert result["negative_all"]["above_or_equal_threshold"] == 2
    assert result["negative_gt3_le5"]["count"] == 1
    assert result["negative_gt5_le8"]["count"] == 1
    assert result["negative_gt8_le12"]["count"] == 1
    assert result["negative_outside_annuli"]["count"] == 1


def test_boundary_audit_never_pairs_across_assigned_truths() -> None:
    patches = torch.stack((_patch(0.0), _patch(0.1), _patch(0.9), _patch(1.0)))
    metadata = {
        "scene_index": np.asarray((0, 0, 0, 0), dtype=np.int32),
        "nearest_truth_index": np.asarray((0, 0, 1, 1), dtype=np.int32),
        "nearest_truth_distance_px": np.asarray((0.0, 4.0, 0.0, 4.0), dtype=np.float32),
        "labels": np.asarray((True, False, True, False)),
    }

    result = subject._boundary_audit(patches, metadata, block_size=2)

    band = result["negative_gt3_le5"]
    assert band["positive_nearest_opposite"]["matched_query_count"] == 2
    assert band["negative_nearest_opposite"]["matched_query_count"] == 2
    assert band["positive_nearest_opposite"]["normalized_rmse"]["minimum"] == pytest.approx(
        0.1, abs=1e-5
    )
