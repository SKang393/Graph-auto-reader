# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Exclusive, process-crash-aware per-recipe experiment journal.

This is not the canonical training ledger or an approval. A used recipe is not
resumable, even if its process stops before completing an optimizer step.
"""
from dataclasses import asdict, dataclass
from hashlib import sha256
import json
import math
import os
from pathlib import Path
import random

from .source import MASK_SHA, REPORT_SHA
from . import execution_bundle

ROOT = Path(__file__).resolve().parents[3]
ATTEMPTS = ROOT / "artifacts/ocr-gpu-db-head-unregistered-v1/attempts"


@dataclass(frozen=True)
class Recipe:
    seed: int
    epochs: int
    learning_rate: float
    weight_decay: float
    bundle_sha256: str

    def validate(self):
        if (type(self.seed) is not int or not 0 <= self.seed < 2**31 or
                type(self.epochs) is not int or not 1 <= self.epochs <= 2 or
                type(self.learning_rate) is not float or not math.isfinite(self.learning_rate) or
                not 0 < self.learning_rate <= 1e-3 or type(self.weight_decay) is not float or
                not math.isfinite(self.weight_decay) or not 0 <= self.weight_decay <= 1e-2 or
                not isinstance(self.bundle_sha256, str) or len(self.bundle_sha256) != 64 or
                any(c not in "0123456789abcdef" for c in self.bundle_sha256)):
            raise ValueError("Explicit bounded recipe is invalid")


def recipe_id(recipe: Recipe) -> str:
    if type(recipe) is not Recipe:
        raise ValueError("Exact recipe type is required")
    recipe.validate()
    raw = json.dumps({"recipe": asdict(recipe), "preflight_sha256": REPORT_SHA, "mask_sha256": MASK_SHA},
                     sort_keys=True, separators=(",", ":"), allow_nan=False).encode()
    return sha256(raw).hexdigest()


def schedule(recipe: Recipe) -> tuple[int, ...]:
    if type(recipe) is not Recipe:
        raise ValueError("Exact recipe type is required")
    recipe.validate()
    result = []
    for epoch in range(recipe.epochs):
        indexes = list(range(4))
        random.Random(recipe.seed + epoch).shuffle(indexes)
        result.extend(indexes)
    return tuple(result)


def _create(path: Path, payload: dict):
    data = (json.dumps(payload, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
    with path.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())


class SingleUseJournal:
    def __init__(self, recipe: Recipe, startup_receipt):
        self.run_id = recipe_id(recipe)
        execution_bundle.require_runtime()
        execution_bundle.consume_startup(startup_receipt, recipe.bundle_sha256)
        execution_bundle.verify_critical(recipe.bundle_sha256)
        self._bundle_sha256 = recipe.bundle_sha256
        self.steps = recipe.epochs * 4
        self._schedule = schedule(recipe)
        self._source_paths = {name: Path(__file__).with_name(name + ".py") for name in ("source", "journal", "runner")}
        self._source_hashes = {name: sha256(path.read_bytes()).hexdigest() for name, path in self._source_paths.items()}
        self._next = 0
        self._pending = False
        self._closed = False
        self._root = ATTEMPTS.resolve()
        if not self._root.is_relative_to((ROOT / "artifacts").resolve()):
            raise ValueError("Attempt journal escapes project artifacts")
        self._root.mkdir(parents=True, exist_ok=True)
        self._prefix = self._root / self.run_id
        _create(self._prefix.with_suffix(".intent"), {"run_id": self.run_id, "recipe": asdict(recipe),
                                                     "preflight_sha256": REPORT_SHA, "steps": self.steps,
                                                     "execution_bundle_path": execution_bundle.MANIFEST.relative_to(ROOT).as_posix(),
                                                     "execution_bundle_sha256": recipe.bundle_sha256,
                                                     "source_sha256_descriptive": self._source_hashes})

    def _verify_sources(self):
        execution_bundle.verify_critical(self._bundle_sha256)
        if any(sha256(path.read_bytes()).hexdigest() != self._source_hashes[name] for name, path in self._source_paths.items()):
            raise ValueError("Experiment source changed during attempt")

    def reserve(self, index: int, sample_index: int):
        if (self._closed or self._pending or type(index) is not int or not 0 <= index < self.steps or
                index != self._next or type(sample_index) is not int or sample_index != self._schedule[index]):
            raise ValueError("Duplicate, skipped or invalid optimizer step")
        self._verify_sources()
        _create(self._root / f"{self.run_id}.step-{index:03}.intent", {"index": index, "sample_index": sample_index})
        self._pending = True

    def complete_step(self, index: int):
        if self._closed or not self._pending or index != self._next:
            raise ValueError("Step completion has no matching intent")
        self._verify_sources()
        _create(self._root / f"{self.run_id}.step-{index:03}.complete", {"index": index})
        self._pending = False
        self._next += 1

    def finish(self):
        if self._closed or self._pending or self._next != self.steps:
            raise ValueError("Incomplete attempt cannot be finished")
        self._verify_sources()
        _create(self._root / f"{self.run_id}.complete", {"steps": self.steps})
        self._closed = True
