# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from __future__ import annotations

import hashlib
import importlib.util
from pathlib import Path
import sys

import numpy as np
import pytest
import torch


SCRIPT = (
    Path(__file__).resolve().parents[1]
    / "tools/GraphReader.SyntheticRuntimeEvidence/diagnose_marker_v26_cross_split_support.py"
)
SPEC = importlib.util.spec_from_file_location("diagnose_marker_v26_cross_split_support", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
subject = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = subject
SPEC.loader.exec_module(subject)


def _patch(value: float) -> torch.Tensor:
    return torch.full((3, 2, 2), value, dtype=torch.float32)


def test_authenticated_npz_rejects_byte_tamper_before_loading(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    cache = tmp_path / "cache.npz"
    cache.write_bytes(b"authenticated-cache")
    expected = hashlib.sha256(cache.read_bytes()).hexdigest()
    calls: list[tuple[Path, set[str]]] = []

    def fake_load(path: Path, names: set[str]) -> dict[str, np.ndarray]:
        calls.append((path, names))
        return {"rows": np.asarray([1], dtype=np.int64)}

    monkeypatch.setattr(subject.boundary, "_load_npz", fake_load)
    loaded = subject._load_authenticated_npz(cache, expected, {"rows"})
    assert loaded["rows"].tolist() == [1]
    assert calls == [(cache, {"rows"})]

    cache.write_bytes(b"tampered-cache")
    with pytest.raises(subject.DiagnosticError, match="authenticated NPZ changed"):
        subject._load_authenticated_npz(cache, expected, {"rows"})
    assert calls == [(cache, {"rows"})]


def test_ranked_sampling_is_order_independent_and_keeps_smallest_ranks() -> None:
    forward: list[tuple[int, str, int, torch.Tensor, float]] = []
    reverse: list[tuple[int, str, int, torch.Tensor, float]] = []
    rows = [(rank, f"scene-{rank}", rank) for rank in (9, 2, 7, 1, 5)]
    for destination, values in ((forward, rows), (reverse, reversed(rows))):
        for rank, identity, proposal in values:
            subject._offer_ranked(
                destination,
                rank=rank,
                scene_identity=identity,
                proposal_index=proposal,
                patch=_patch(rank / 10),
                score=rank / 10,
                limit=3,
            )

    assert [item[2] for item in subject._ordered_sample(forward)] == [1, 2, 5]
    assert [item[2] for item in subject._ordered_sample(reverse)] == [1, 2, 5]


def test_norm_dot_search_matches_direct_brute_force_across_blocks() -> None:
    generator = torch.Generator().manual_seed(20260917)
    queries = torch.rand((4, 3, 2, 2), generator=generator)
    candidates = torch.rand((11, 3, 2, 2), generator=generator)

    result = subject._nearest_support(queries, candidates, block_size=3)
    direct = (
        queries[:, None].double() - candidates[None, :].double()
    ).square().reshape(len(queries), len(candidates), -1).sum(dim=2)

    assert np.array_equal(result["nearest_indices"], direct.argmin(dim=1).numpy())
    assert np.asarray(result["direct_squared"]) == pytest.approx(
        direct.min(dim=1).values.numpy(), rel=0, abs=1e-15
    )


def test_norm_dot_search_directly_resolves_near_equal_and_exact_ties() -> None:
    query = torch.zeros((2, 3, 2, 2), dtype=torch.float32)
    candidates = torch.zeros((4, 3, 2, 2), dtype=torch.float32)
    candidates[0, 0, 0, 0] = 2e-7
    candidates[1, 0, 0, 0] = 1e-7
    candidates[2, 0, 0, 0] = 1e-7
    candidates[3] = 1.0
    query[1, 0, 0, 0] = 1e-7

    result = subject._nearest_support(query, candidates, block_size=1)
    direct = (
        query[:, None].double() - candidates[None, :].double()
    ).square().reshape(len(query), len(candidates), -1).sum(dim=2)

    assert np.array_equal(result["nearest_indices"], direct.argmin(dim=1).numpy())
    assert result["nearest_indices"].tolist() == [1, 1]
    assert np.all(np.asarray(result["roundoff_candidate_counts"]) >= 2)
