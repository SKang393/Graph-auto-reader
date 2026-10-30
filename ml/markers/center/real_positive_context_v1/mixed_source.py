# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bound family-row preparation and deterministic mixed-source batch staging.

This is not an admission, provenance approval or training runner. The caller
must separately authenticate the candidate, the archive receipt/key, source
split and positive-center labels. The existing runner remains fail-closed.
"""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
from pathlib import Path
import struct

import torch
from torch import Tensor

from .objective import SyntheticLabels, _require_training_gpu
from .positive_archive import PositiveArchive
from .schedule import FixedSchedule, StepPlan

_DOMAIN = b"graphreader.mixed-family-row.v1\0"
_VIEW_DIMS = ((), (-1,), (-2,), (-2, -1))
_PROJECT_ROOT = Path(__file__).resolve().parents[4]
_TEST_ONLY = object()


def _row_id(patch: Tensor, label: int, index: int, view: int) -> bytes:
    if patch.device.type != "cpu" or patch.dtype != torch.float32 or tuple(patch.shape) != (3, 33, 33):
        raise ValueError("Synthetic row must be a CPU float32 33x33 runtime patch")
    if label not in (0, 1) or not 0 <= view < 4 or not 0 <= index < (1 << 64):
        raise ValueError("Synthetic row identity inputs are invalid")
    payload = patch.contiguous().numpy().astype("<f4", copy=False).tobytes(order="C")
    return sha256(_DOMAIN + struct.pack("<QBB", index, view, label) + payload).digest()


class FamilyRows:
    """Owned local 33x33 center patches with a separately checked label array."""

    def __init__(self, contexts: Tensor, labels: Tensor, bound_labels: SyntheticLabels) -> None:
        if (not isinstance(contexts, Tensor) or contexts.device.type != "cpu" or contexts.dtype != torch.float32 or
                contexts.ndim != 4 or tuple(contexts.shape[1:]) != (3, 65, 65) or contexts.shape[0] == 0 or
                not isinstance(labels, Tensor) or labels.device.type != "cpu" or labels.dtype != torch.float32 or
                tuple(labels.shape) != (len(contexts),) or not isinstance(bound_labels, SyntheticLabels)):
            raise ValueError("Family contexts and independently bound labels have invalid shape or dtype")
        if (not torch.isfinite(contexts).all() or (contexts < 0).any() or (contexts > 1).any() or
                not torch.all((labels == 0) | (labels == 1))):
            raise ValueError("Family contexts or labels contain invalid values")
        label_bytes = labels.to(dtype=torch.uint8).contiguous().numpy().tobytes()
        if label_bytes != bound_labels.values:
            raise ValueError("Family loader labels differ byte-for-byte from the bound V37 labels")
        self._patches = contexts[:, :, 16:49, 16:49].contiguous().clone()
        self._labels = label_bytes
        self._count = len(contexts)
        identities = []
        for view, dims in enumerate(_VIEW_DIMS):
            for index in range(self._count):
                patch = self._patches[index]
                if dims:
                    patch = patch.flip(dims)
                identities.append(_row_id(patch, label_bytes[index], index, view))
        self._identities = b"".join(identities)
        if len(set(identities)) != len(identities):
            raise ValueError("Synthetic row identities are not unique")

    @property
    def row_identities(self) -> bytes:
        return self._identities

    @property
    def count(self) -> int:
        return self._count * 4

    def _row(self, position: int) -> tuple[Tensor, int, bytes]:
        if type(position) is not int or not 0 <= position < self.count:
            raise IndexError("Synthetic row is outside the population")
        view, index = divmod(position, self._count)
        patch = self._patches[index]
        if _VIEW_DIMS[view]:
            patch = patch.flip(_VIEW_DIMS[view])
        label = self._labels[index]
        return patch, label, _row_id(patch, label, index, view)


def load_bound_family() -> tuple[FamilyRows, dict]:
    """Load the existing family loader and compare its labels to the V37 report.

    This performs the loader's documented source/identity reads. It does not
    approve the report's runtime-mask readiness or source provenance for a run.
    """
    from ml.markers.center.dual_scale_context_v1.training_data import load
    from .runner_foundation import load_bound_synthetic_labels
    contexts, labels, provenance = load()
    expected = load_bound_synthetic_labels(_PROJECT_ROOT, _PROJECT_ROOT / "artifacts/goal22-worktrees/resume-20260908", "family")
    return FamilyRows(contexts, labels, expected), provenance


@dataclass(frozen=True)
class PreparedBatch:
    plan: StepPlan
    synthetic: Tensor
    labels: SyntheticLabels
    real_positive: Tensor


class MixedSourceBatchAdapter:
    """Use one schedule and one verified archive without optimizer operations.

    `next_gpu` requires the actual HIP RX 6600 XT and returns only GPU tensors.
    Integration must call `commit` only after the durable optimizer journal has
    recorded the completed step; an interrupted candidate must never resume.
    """

    def __init__(self, family: FamilyRows, archive: PositiveArchive, schedule: FixedSchedule, *,
                 _fixture_token: object | None = None) -> None:
        if not isinstance(family, FamilyRows) or not isinstance(schedule, FixedSchedule):
            raise TypeError("A bound family population and fixed schedule are required")
        if type(archive) is not PositiveArchive and _fixture_token is not _TEST_ONLY:
            raise TypeError("A fully authenticated PositiveArchive handle is required")
        real_ids = archive.row_identities
        if type(real_ids) is not bytes or len(real_ids) != archive.count * 32 or archive.count <= 0:
            raise ValueError("Authenticated positive population is malformed")
        self._family, self._archive, self._schedule = family, archive, schedule
        self._real_ids = real_ids
        self._pending: StepPlan | None = None
        self._fixture = _fixture_token is _TEST_ONLY

    @classmethod
    def _synthetic_fixture(cls, family: FamilyRows, archive: object, schedule: FixedSchedule) -> "MixedSourceBatchAdapter":
        """In-memory test seam; returned instance cannot transfer to GPU."""
        return cls(family, archive, schedule, _fixture_token=_TEST_ONLY)  # type: ignore[arg-type]

    def _prepare_cpu(self) -> PreparedBatch:
        if self._pending is not None:
            raise RuntimeError("The previous scheduled batch has not been committed")
        plan = self._schedule.reserve()
        try:
            synthetic_rows = [self._family._row(index) for index in plan.synthetic_indices]
            positive_ids = [self._real_ids[index * 32:(index + 1) * 32] for index in plan.real_positive_indices]
            positive_patches = []
            for index in plan.real_positive_indices:
                raw = self._archive.read_patch(index)
                if type(raw) is not bytes or len(raw) != 3 * 33 * 33 * 4:
                    raise ValueError("Positive archive row is malformed")
                # Copy avoids tensor aliases into transient immutable byte buffers.
                positive_patches.append(torch.frombuffer(bytearray(raw), dtype=torch.float32).reshape(3, 33, 33))
            self._schedule.verify(plan, synthetic_row_ids=[item[2] for item in synthetic_rows],
                                  real_positive_row_ids=positive_ids)
            label_bytes = bytes(item[1] for item in synthetic_rows)
            batch = PreparedBatch(plan, torch.stack([item[0] for item in synthetic_rows]),
                                  SyntheticLabels(label_bytes, sha256(label_bytes).hexdigest()),
                                  torch.stack(positive_patches))
            self._pending = plan
            return batch
        except BaseException:
            # A reserved but incomplete step stays pending and cannot resume.
            self._pending = plan
            raise

    def next_gpu(self) -> PreparedBatch:
        if self._fixture:
            raise RuntimeError("Synthetic fixtures cannot enter GPU training")
        _require_training_gpu()
        batch = self._prepare_cpu()
        device = torch.device("cuda:0")
        synthetic = batch.synthetic.to(device=device, non_blocking=False)
        positive = batch.real_positive.to(device=device, non_blocking=False)
        _require_training_gpu(synthetic, positive)
        return PreparedBatch(batch.plan, synthetic, batch.labels, positive)

    def commit(self, plan: StepPlan) -> None:
        if self._pending is None or plan is not self._pending:
            raise RuntimeError("Only the current completed batch can be committed")
        self._schedule.commit(plan)
        self._pending = None
