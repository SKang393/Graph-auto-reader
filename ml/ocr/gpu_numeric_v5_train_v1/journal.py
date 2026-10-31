# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Crash-conservative step budget journal for a future registered run."""
from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
import json
import os
from pathlib import Path
import re


_STEP_NAME = re.compile(r"^step-(\d{6})\.(in_progress|complete)$")


class JournalError(ValueError):
    """The run journal is corrupt, incomplete, or over budget."""


def file_sha256(path: Path) -> str:
    digest = sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _atomic_json(path: Path, value: dict) -> None:
    temporary = path.with_name(path.name + ".tmp")
    payload = (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    with temporary.open("xb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


class StepBudgetJournal:
    """Reserve every step before execution and never reuse an interrupted slot.

    A crash after `begin_step` leaves an `.in_progress` directory. The engine
    refuses to continue that run rather than replaying a possibly executed
    optimizer step. A completed step requires an atomic checkpoint hash receipt.
    """

    def __init__(self, run_root: Path, *, run_id: str, maximum_steps: int,
                 recipe_sha256: str, input_bundle_sha256: str, create: bool):
        self.root = Path(run_root)
        self.journal_dir = self.root / "journal"
        if type(maximum_steps) is not int or maximum_steps <= 0:
            raise JournalError("step budget must be a positive integer")
        if not run_id or not recipe_sha256 or not input_bundle_sha256:
            raise JournalError("run identity and input hashes are required")
        self.run_id = run_id
        self.maximum_steps = maximum_steps
        self.recipe_sha256 = recipe_sha256
        self.input_bundle_sha256 = input_bundle_sha256
        if create:
            if self.root.exists():
                raise FileExistsError("run directory must be fresh")
            self.root.mkdir(parents=True, exist_ok=False)
            self.journal_dir.mkdir()
            budget = {"schema": "graphreader.gpu-v5-step-budget.v1", "run_id": run_id,
                      "maximum_steps": maximum_steps, "recipe_sha256": recipe_sha256,
                      "input_bundle_sha256": input_bundle_sha256, "created_utc": datetime.now(timezone.utc).isoformat()}
            with (self.root / "budget.json").open("xb") as stream:
                payload = (json.dumps(budget, sort_keys=True, indent=2) + "\n").encode("utf-8")
                stream.write(payload)
                stream.flush()
                os.fsync(stream.fileno())
        else:
            self._validate_budget()
            self._recover_or_block_incomplete()

    def _validate_budget(self) -> dict:
        try:
            payload = (self.root / "budget.json").read_bytes()
            value = json.loads(payload)
        except (OSError, json.JSONDecodeError) as error:
            raise JournalError("run budget file is missing or invalid") from error
        expected = {"schema": "graphreader.gpu-v5-step-budget.v1", "run_id": self.run_id,
                    "maximum_steps": self.maximum_steps, "recipe_sha256": self.recipe_sha256,
                    "input_bundle_sha256": self.input_bundle_sha256}
        if any(value.get(key) != expected_value for key, expected_value in expected.items()):
            raise JournalError("run budget identity differs")
        if not self.journal_dir.is_dir():
            raise JournalError("step journal directory is missing")
        return value

    def _slots(self) -> list[tuple[int, str, Path]]:
        rows = []
        for path in self.journal_dir.iterdir():
            match = _STEP_NAME.fullmatch(path.name)
            if not match or not path.is_dir():
                raise JournalError("unexpected step journal entry")
            rows.append((int(match.group(1)), match.group(2), path))
        rows.sort()
        if [step for step, _, _ in rows] != list(range(1, len(rows) + 1)):
            raise JournalError("step journal is not contiguous")
        if len(rows) > self.maximum_steps:
            raise JournalError("step journal exceeds immutable budget")
        return rows

    def _recover_or_block_incomplete(self) -> None:
        rows = self._slots()
        for step, status, path in rows:
            if status == "complete":
                self._validate_step_receipts(step, path)
                continue
            if status != "in_progress":
                continue
            complete_path = path / "complete.json"
            begin_path = path / "begin.json"
            checkpoint_path = self.root / "checkpoint.pt"
            if not begin_path.is_file() or not complete_path.is_file() or not checkpoint_path.is_file():
                raise JournalError("interrupted optimizer step consumed its slot; run cannot resume")
            try:
                begin = json.loads(begin_path.read_text(encoding="utf-8"))
                completion = json.loads(complete_path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError) as error:
                raise JournalError("interrupted step completion receipt is invalid") from error
            if (begin.get("schema") != "graphreader.gpu-v5-step-begin.v1" or
                    completion.get("schema") != "graphreader.gpu-v5-step-complete.v1" or
                    begin.get("step") != step or begin.get("run_id") != self.run_id or
                    begin.get("recipe_sha256") != self.recipe_sha256 or
                    begin.get("input_bundle_sha256") != self.input_bundle_sha256 or
                    completion.get("step") != step or completion.get("run_id") != self.run_id or
                    completion.get("checkpoint_sha256") != file_sha256(checkpoint_path)):
                raise JournalError("interrupted step checkpoint does not match completion receipt")
            path.rename(self.journal_dir / f"step-{step:06d}.complete")

    def _validate_step_receipts(self, step: int, path: Path) -> dict:
        try:
            begin = json.loads((path / "begin.json").read_text(encoding="utf-8"))
            completion = json.loads((path / "complete.json").read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            raise JournalError("completed step receipt is missing or invalid") from error
        if (begin.get("schema") != "graphreader.gpu-v5-step-begin.v1" or
                begin.get("run_id") != self.run_id or begin.get("step") != step or
                begin.get("recipe_sha256") != self.recipe_sha256 or
                begin.get("input_bundle_sha256") != self.input_bundle_sha256 or
                completion.get("schema") != "graphreader.gpu-v5-step-complete.v1" or
                completion.get("run_id") != self.run_id or completion.get("step") != step or
                not isinstance(completion.get("checkpoint_sha256"), str) or
                len(completion["checkpoint_sha256"]) != 64):
            raise JournalError("completed step receipt identity differs")
        return completion

    @property
    def completed_steps(self) -> int:
        rows = self._slots()
        if any(status != "complete" for _, status, _ in rows):
            raise JournalError("run contains an incomplete optimizer step")
        for step, _, path in rows:
            self._validate_step_receipts(step, path)
        return len(rows)

    def begin_step(self) -> int:
        rows = self._slots()
        if any(status != "complete" for _, status, _ in rows):
            raise JournalError("cannot start while a previous step is incomplete")
        step = len(rows) + 1
        if step > self.maximum_steps:
            raise JournalError("optimizer step budget is exhausted")
        slot = self.journal_dir / f"step-{step:06d}.in_progress"
        slot.mkdir(exist_ok=False)
        _atomic_json(slot / "begin.json", {"schema": "graphreader.gpu-v5-step-begin.v1",
                     "run_id": self.run_id, "step": step, "recipe_sha256": self.recipe_sha256,
                     "input_bundle_sha256": self.input_bundle_sha256,
                     "started_utc": datetime.now(timezone.utc).isoformat()})
        return step

    def complete_step(self, step: int) -> str:
        slot = self.journal_dir / f"step-{step:06d}.in_progress"
        checkpoint_path = self.root / "checkpoint.pt"
        if not slot.is_dir() or not (slot / "begin.json").is_file() or not checkpoint_path.is_file():
            raise JournalError("step reservation or checkpoint is missing")
        rows = self._slots()
        if not rows or rows[-1][0] != step or rows[-1][1] != "in_progress":
            raise JournalError("step completion order differs")
        try:
            begin = json.loads((slot / "begin.json").read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            raise JournalError("step begin receipt is missing or invalid") from error
        if (begin.get("schema") != "graphreader.gpu-v5-step-begin.v1" or begin.get("run_id") != self.run_id or
                begin.get("step") != step or begin.get("recipe_sha256") != self.recipe_sha256 or
                begin.get("input_bundle_sha256") != self.input_bundle_sha256):
            raise JournalError("step begin receipt identity differs")
        digest = file_sha256(checkpoint_path)
        _atomic_json(slot / "complete.json", {"schema": "graphreader.gpu-v5-step-complete.v1",
                     "run_id": self.run_id, "step": step, "checkpoint_sha256": digest,
                     "completed_utc": datetime.now(timezone.utc).isoformat()})
        slot.rename(self.journal_dir / f"step-{step:06d}.complete")
        return digest

    def verify_latest_checkpoint(self) -> None:
        rows = self._slots()
        if not rows:
            if (self.root / "checkpoint.pt").exists():
                raise JournalError("checkpoint exists without a completed step")
            return
        if any(status != "complete" for _, status, _ in rows):
            raise JournalError("cannot resume an incomplete step")
        checkpoint_path = self.root / "checkpoint.pt"
        if not checkpoint_path.is_file():
            raise JournalError("latest checkpoint is missing")
        completion = json.loads((rows[-1][2] / "complete.json").read_text(encoding="utf-8"))
        if file_sha256(checkpoint_path) != completion.get("checkpoint_sha256"):
            raise JournalError("latest checkpoint hash differs from journal")
