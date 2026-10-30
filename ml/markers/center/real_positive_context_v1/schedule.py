# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Deterministic, input-bound scheduling for a future mixed-supervision run.

This module reads no image, annotation, private source or sealed corpus, and
performs no training. Row IDs are opaque 32-byte identities supplied by a
separately reviewed adapter. The caller must independently authenticate the
candidate configuration through the canonical TrainingAuthorization and must
verify that each ID maps to its actual preprocessed tensor and labeled center.
"""
from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import json
from typing import Sequence

import torch

ALGORITHM = "torch-cpu-randperm-per-epoch-v1"
_ROW_ID_BYTES = 32
_MAX_ROWS = 10_000_000
_MAX_STEPS = 1_000_000
_MAX_BATCH = 65536
_MAX_POSITIVE_SLOTS_PER_EPOCH = 10_000_000
_CONSTRUCTION_TOKEN = object()


def _sha256_hex(value: object) -> bool:
    return (type(value) is str and len(value) == 64 and
            all(character in "0123456789abcdef" for character in value))


def _integer(value: object, minimum: int, maximum: int, name: str) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError(f"{name} is not an allowed fixed integer")
    return value


def population_sha256(kind: str, identities: bytes) -> str:
    """Hash a fixed order of unique opaque IDs with domain separation."""
    if kind not in {"synthetic", "real-positive"} or type(identities) is not bytes:
        raise ValueError("Population identity requires immutable IDs and a supported kind")
    if not identities or len(identities) % _ROW_ID_BYTES:
        raise ValueError("Population identity requires nonempty 32-byte IDs")
    count = len(identities) // _ROW_ID_BYTES
    if count > _MAX_ROWS:
        raise ValueError("Population exceeds the bounded schedule size")
    digest = sha256()
    digest.update(b"graphreader.real-positive-schedule.population.v1\0")
    digest.update(kind.encode("ascii") + b"\0")
    digest.update(count.to_bytes(8, "big"))
    digest.update(identities)
    return digest.hexdigest()


@dataclass(frozen=True)
class BoundPopulation:
    """Ordered immutable IDs checked against a preregistered digest.

    This binds ordering and uniqueness; it does not prove what a row depicts.
    Source-to-tensor and positive-center validation belong to the adapter.
    """

    kind: str
    identities: bytes
    digest: str
    count: int

    def __post_init__(self) -> None:
        _integer(self.count, 1, _MAX_ROWS, "population count")
        if (not _sha256_hex(self.digest) or population_sha256(self.kind, self.identities) != self.digest or
                len(self.identities) != self.count * _ROW_ID_BYTES):
            raise ValueError("Population bytes, count, or admitted digest changed")
        seen = set()
        for offset in range(0, len(self.identities), _ROW_ID_BYTES):
            identity = self.identities[offset:offset + _ROW_ID_BYTES]
            if identity in seen:
                raise ValueError("Population contains duplicate row identities")
            seen.add(identity)

    @classmethod
    def validate(cls, kind: str, identities: bytes, *, expected_count: int,
                 expected_sha256: str) -> "BoundPopulation":
        return cls(kind, identities, expected_sha256, expected_count)

    def row_id(self, index: int) -> bytes:
        if type(index) is not int or not 0 <= index < self.count:
            raise IndexError("Population row is outside its bound range")
        start = index * _ROW_ID_BYTES
        return self.identities[start:start + _ROW_ID_BYTES]


@dataclass(frozen=True)
class StepPlan:
    """One 1-based epoch/step, containing positions in the two populations."""

    epoch: int
    step: int
    batch_in_epoch: int
    synthetic_indices: tuple[int, ...]
    real_positive_indices: tuple[int, ...]


class FixedSchedule:
    """Reserve, verify and commit every predetermined batch exactly once.

    This object never resumes. Crash-safe optimizer accounting remains the
    responsibility of the separate persistent run journal.
    """

    def __init__(self, *, config: dict, synthetic: BoundPopulation, real_positive: BoundPopulation,
                 _construction_token: object) -> None:
        if _construction_token is not _CONSTRUCTION_TOKEN:
            raise ValueError("Use the config-hash-bound schedule constructor")
        if not isinstance(config, dict) or not isinstance(config.get("recipe"), dict) or not isinstance(config.get("schedule"), dict):
            raise ValueError("Candidate recipe and schedule are missing")
        recipe, schedule = config["recipe"], config["schedule"]
        if (not isinstance(synthetic, BoundPopulation) or synthetic.kind != "synthetic" or
                not isinstance(real_positive, BoundPopulation) or real_positive.kind != "real-positive"):
            raise ValueError("Independently bound synthetic and positive populations are required")
        if (schedule.get("algorithm") != ALGORITHM or schedule.get("torch_version") != str(torch.__version__) or
                schedule.get("synthetic_population_sha256") != synthetic.digest or
                schedule.get("real_positive_population_sha256") != real_positive.digest or
                type(schedule.get("synthetic_population_count")) is not int or
                schedule.get("synthetic_population_count") != synthetic.count or
                type(schedule.get("real_positive_population_count")) is not int or
                schedule.get("real_positive_population_count") != real_positive.count or
                schedule.get("synthetic_source_binding_sha256") != config.get("synthetic_feature_report_sha256") or
                schedule.get("real_source_binding_sha256") != config.get("real_positive_binding_sha256") or
                not _sha256_hex(config.get("synthetic_feature_report_sha256")) or
                not _sha256_hex(config.get("real_positive_binding_sha256"))):
            raise ValueError("Schedule populations or runtime do not match the admitted binding")
        self._seed = _integer(recipe.get("seed"), 0, (1 << 63) - 1, "seed")
        self._epochs = _integer(recipe.get("epochs"), 1, 1000, "epoch count")
        self._synthetic_batch = _integer(recipe.get("synthetic_batch_size"), 1, _MAX_BATCH, "synthetic batch size")
        self._positive_batch = _integer(recipe.get("real_positive_batch_size"), 1, _MAX_BATCH, "positive batch size")
        self._batches = (synthetic.count + self._synthetic_batch - 1) // self._synthetic_batch
        self._total_steps = self._epochs * self._batches
        if (self._total_steps > _MAX_STEPS or config.get("optimizer_steps_expected") != self._total_steps or
                type(config.get("optimizer_steps_expected")) is not int):
            raise ValueError("Optimizer steps do not equal complete per-epoch coverage")
        positive_slots = self._batches * self._positive_batch
        if positive_slots > _MAX_POSITIVE_SLOTS_PER_EPOCH:
            raise ValueError("Positive schedule exceeds the bounded per-epoch size")
        if real_positive.count > positive_slots:
            raise ValueError("Positive batch capacity cannot cover every real-positive row per epoch")
        positive_ids = {real_positive.row_id(index) for index in range(real_positive.count)}
        if any(synthetic.row_id(index) in positive_ids for index in range(synthetic.count)):
            raise ValueError("Synthetic and real-positive row identities overlap")
        self._synthetic = synthetic
        self._positive = real_positive
        self._step = 0
        self._synthetic_order: torch.Tensor | None = None
        self._positive_order: torch.Tensor | None = None
        self._pending: StepPlan | None = None
        self._verified = False
        self._failed = False

    @classmethod
    def from_bound_config(cls, config_bytes: bytes, config_sha256: str, *,
                          synthetic_ids: bytes, real_positive_ids: bytes) -> "FixedSchedule":
        """Check exact config bytes against the canonical admission hash.

        The caller obtains `config_sha256` from the already-verified ledger,
        not from an untrusted adapter. Population bytes must be constructed by
        the independently reviewed runtime-bound source adapter.
        """
        if type(config_bytes) is not bytes or not _sha256_hex(config_sha256) or sha256(config_bytes).hexdigest() != config_sha256:
            raise ValueError("Candidate configuration hash does not match its admission")
        def reject_duplicates(pairs: list[tuple[str, object]]) -> dict:
            result: dict[str, object] = {}
            for key, value in pairs:
                if key in result:
                    raise ValueError("Candidate configuration contains duplicate keys")
                result[key] = value
            return result
        try:
            config = json.loads(config_bytes, object_pairs_hook=reject_duplicates)
        except (json.JSONDecodeError, UnicodeDecodeError, ValueError) as error:
            raise ValueError("Candidate configuration is not valid UTF-8 JSON") from error
        if not isinstance(config, dict) or not isinstance(config.get("schedule"), dict):
            raise ValueError("Candidate schedule binding is missing")
        spec = config["schedule"]
        synthetic = BoundPopulation.validate("synthetic", synthetic_ids,
            expected_count=spec.get("synthetic_population_count"), expected_sha256=spec.get("synthetic_population_sha256"))
        real_positive = BoundPopulation.validate("real-positive", real_positive_ids,
            expected_count=spec.get("real_positive_population_count"), expected_sha256=spec.get("real_positive_population_sha256"))
        return cls(config=config, synthetic=synthetic, real_positive=real_positive,
                   _construction_token=_CONSTRUCTION_TOKEN)

    @property
    def total_steps(self) -> int:
        return self._total_steps

    @property
    def completed_steps(self) -> int:
        return self._step

    def _prepare_epoch(self, epoch: int) -> None:
        # Scheduling indices on CPU is not model training or optimizer work.
        synth_rng = torch.Generator(device="cpu").manual_seed((self._seed + epoch) % (1 << 63))
        self._synthetic_order = torch.randperm(self._synthetic.count, generator=synth_rng, device="cpu")
        slots = self._batches * self._positive_batch
        self._positive_order = torch.empty(slots, dtype=torch.int64, device="cpu")
        offset, cycle = 0, 0
        while offset < slots:
            # Independent deterministic permutation for each cycle. The first
            # cycle covers every row; a partial final cycle may repeat rows.
            generator = torch.Generator(device="cpu").manual_seed(
                (self._seed + 0x5DEECE66D + epoch * 1_000_003 + cycle) % (1 << 63))
            order = torch.randperm(self._positive.count, generator=generator, device="cpu")
            take = min(self._positive.count, slots - offset)
            self._positive_order[offset:offset + take] = order[:take]
            offset += take
            cycle += 1

    def reserve(self) -> StepPlan:
        if self._failed:
            raise RuntimeError("A schedule verification failed; this run cannot continue")
        if self._pending is not None:
            self._failed = True
            raise RuntimeError("The current schedule step must be verified and committed first")
        if self._step >= self._total_steps:
            raise StopIteration("All admitted schedule steps are complete")
        epoch = self._step // self._batches
        batch = self._step % self._batches
        if batch == 0:
            self._prepare_epoch(epoch)
        assert self._synthetic_order is not None and self._positive_order is not None
        synth_start = batch * self._synthetic_batch
        positive_start = batch * self._positive_batch
        plan = StepPlan(epoch + 1, self._step + 1, batch + 1,
            tuple(int(item) for item in self._synthetic_order[synth_start:synth_start + self._synthetic_batch]),
            tuple(int(item) for item in self._positive_order[positive_start:positive_start + self._positive_batch]))
        self._pending, self._verified = plan, False
        return plan

    def verify(self, plan: StepPlan, *, synthetic_row_ids: Sequence[bytes],
               real_positive_row_ids: Sequence[bytes]) -> None:
        if self._failed or self._pending is None or plan is not self._pending or self._verified:
            self._failed = True
            raise RuntimeError("Schedule step is repeated, skipped, or not reserved by this run")
        expected_synthetic = tuple(self._synthetic.row_id(index) for index in plan.synthetic_indices)
        expected_positive = tuple(self._positive.row_id(index) for index in plan.real_positive_indices)
        if (not isinstance(synthetic_row_ids, (tuple, list)) or
                not isinstance(real_positive_row_ids, (tuple, list)) or
                len(synthetic_row_ids) != len(expected_synthetic) or
                len(real_positive_row_ids) != len(expected_positive) or
                tuple(synthetic_row_ids) != expected_synthetic or tuple(real_positive_row_ids) != expected_positive):
            self._failed = True
            raise ValueError("Observed batch identities differ from the fixed schedule")
        self._verified = True

    def commit(self, plan: StepPlan) -> None:
        if self._failed or plan is not self._pending or not self._verified:
            self._failed = True
            raise RuntimeError("An unverified, skipped, or repeated schedule step cannot be committed")
        self._step += 1
        self._pending, self._verified = None, False
