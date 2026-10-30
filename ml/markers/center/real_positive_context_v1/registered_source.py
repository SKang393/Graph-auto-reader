# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Canonical-admission bridge for the mixed positive-source training draft.

The authenticated producer receipt describes the fixed Engauge development
train partition. Its assignment and inventory hashes are bound to admission;
this module does not independently prove labels or source-image provenance,
approve masks, or make a candidate eligible for release.
"""
from __future__ import annotations

from dataclasses import asdict
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import struct

import torch

from . import runner_foundation as runner
from .checkpoint_writer import _ALLOWED_BASES, _cpu_state
from .mixed_source import MixedSourceBatchAdapter, load_bound_family
from .positive_archive import PositiveArchive, _open_read_only_no_writers
from .schedule import FixedSchedule

_PROVENANCE_KEYS = ("binding_sha256", "protocol_sha256", "assignment_sha256", "selected_inventory_sha256",
                    "ocr_detection_sha256", "ocr_recognition_sha256", "marker_center_sha256", "marker_classifier_sha256")
_ARCHIVE_KEYS = {"receipt_path", "receipt_sha256", "archive_path", "file_sha256", "run_id", "key_sha256", "provenance_sha256"}
_HEADER_BYTES = 8 + 16 + 3 * 4
_ROW_BYTES = 16 + 3 * 33 * 33 * 4


def _digest(value: object, length: int = 64) -> bool:
    return type(value) is str and len(value) == length and all(c in "0123456789abcdef" for c in value)


def _json(raw: bytes) -> dict:
    def unique(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise ValueError("Authenticated JSON has duplicate keys")
            value[key] = item
        return value
    try:
        value = json.loads(raw, object_pairs_hook=unique)
    except (ValueError, UnicodeError) as error:
        raise ValueError("Authenticated JSON is invalid") from error
    if type(value) is not dict:
        raise ValueError("Authenticated JSON root is invalid")
    return value


def _artifact_path(relative: object) -> Path:
    if type(relative) is not str or not relative or Path(relative).is_absolute():
        raise ValueError("Artifact reference must be project-relative")
    root = runner.PROJECT_ROOT.resolve(strict=True)
    path = runner._inside_project(root, root / relative)
    if not path.is_relative_to(root / "artifacts"):
        raise ValueError("Input artifacts must stay in the project artifacts directory")
    return path


def _locked_bytes(path: Path, maximum: int) -> bytes:
    with _open_read_only_no_writers(path) as handle:
        data = handle.read(maximum + 1)
    if len(data) > maximum:
        raise ValueError("Bound input exceeds its size limit")
    return data


def _receipt(raw: bytes, spec: dict, config: dict, key: bytes) -> dict:
    if (sha256(raw).hexdigest() != spec["receipt_sha256"] or len(key) != 32 or
            sha256(key).hexdigest() != spec["key_sha256"]):
        raise ValueError("Producer receipt or key differs from canonical admission")
    result = _json(raw)
    receipt, preparation = result.get("receipt"), result.get("preparation")
    if result.get("status") != "pass" or type(receipt) is not dict or type(preparation) is not dict:
        raise ValueError("A successful authenticated producer receipt is required")
    if (receipt.get("sha256") != spec["file_sha256"] or receipt.get("run_id") != spec["run_id"] or
            [preparation.get(name) for name in _PROVENANCE_KEYS] != spec["provenance_sha256"] or
            preparation.get("binding_sha256") != config["real_positive_binding_sha256"]):
        raise ValueError("Producer provenance does not match the admitted archive")
    batch_count, positive_count = receipt.get("batch_count"), receipt.get("positive_count")
    train_count = preparation.get("train_group_count")
    if (type(batch_count) is not int or batch_count <= 0 or type(positive_count) is not int or positive_count <= 0 or
            type(train_count) is not int or train_count != batch_count or
            type(preparation.get("processed_train_group_count")) is not int or
            preparation.get("processed_train_group_count") != train_count or
            type(preparation.get("positive_proposal_count")) is not int or
            preparation.get("positive_proposal_count") != positive_count or
            type(preparation.get("development_group_count")) is not int or preparation["development_group_count"] < 0 or
            type(preparation.get("selected_source_count")) is not int or preparation["selected_source_count"] < train_count or
            preparation.get("aggregate_only") is not True or preparation.get("source_images_output") is not False or
            preparation.get("per_case_output") is not False or preparation.get("inference_cache_output") is not False or
            preparation.get("production_approved") is not False or type(preparation.get("optimizer_steps")) is not int or
            preparation["optimizer_steps"] != 0 or type(preparation.get("sealed_reads")) is not int or preparation["sealed_reads"] != 0):
        raise ValueError("Producer receipt does not describe the fixed train-only preparation")
    return receipt


def _batch_count(archive: PositiveArchive) -> int:
    """Count already-authenticated records while retaining the same locked handle."""
    with archive._lock:
        handle = archive._handle
        if handle.closed:
            raise ValueError("Authenticated archive handle is closed")
        handle.seek(_HEADER_BYTES)
        batches = positives = 0
        while True:
            tag = handle.read(1)
            if tag == b"\x00":
                break
            if tag != b"\x01" or len(handle.read(32)) != 32:
                raise ValueError("Authenticated archive structure changed")
            count_bytes = handle.read(4)
            if len(count_bytes) != 4:
                raise ValueError("Authenticated archive structure changed")
            count = struct.unpack("<I", count_bytes)[0]
            handle.seek(count * _ROW_BYTES, 1)
            batches += 1
            positives += count
        if positives != archive.count:
            raise ValueError("Authenticated archive population changed")
        return batches


class RegisteredMixedSource:
    """Single-use source constructed only under canonical training admission."""

    def __init__(self) -> None:
        raise RuntimeError("Use open_admitted with a canonical authorization")

    @classmethod
    def open_admitted(cls, authorization, *, archive_key: bytes) -> "RegisteredMixedSource":
        instance = cls.__new__(cls)
        # This checks canonical ledger, immutable source snapshot and that this
        # class's own source path is in the admitted runner bundle.
        config = runner._require_bound_source(authorization, instance)
        root = runner.PROJECT_ROOT.resolve(strict=True)
        config_path = runner._inside_project(root, root / authorization.binding["candidate_config_path"])
        config_raw = _locked_bytes(config_path, 4 * 1024 * 1024)
        expected_config_sha = authorization.binding.get("candidate_config_sha256")
        if not _digest(expected_config_sha) or sha256(config_raw).hexdigest() != expected_config_sha or _json(config_raw) != config:
            raise ValueError("Admitted configuration bytes do not match the canonical ledger")
        spec = config.get("real_positive_archive")
        if (type(spec) is not dict or set(spec) != _ARCHIVE_KEYS or
                any(not _digest(spec[name]) for name in ("receipt_sha256", "file_sha256", "key_sha256")) or
                not _digest(spec["run_id"], 32) or type(spec["provenance_sha256"]) is not list or
                len(spec["provenance_sha256"]) != 8 or any(not _digest(item) for item in spec["provenance_sha256"]) or
                type(archive_key) is not bytes):
            raise ValueError("Admitted producer receipt and archive bindings are missing or invalid")
        receipt_path, archive_path = _artifact_path(spec["receipt_path"]), _artifact_path(spec["archive_path"])
        receipt_raw = _locked_bytes(receipt_path, 4 * 1024 * 1024)
        receipt = _receipt(receipt_raw, spec, config, archive_key)
        base_path = _artifact_path(config.get("source_checkpoint_path"))
        if base_path not in _ALLOWED_BASES or config.get("source_checkpoint_sha256") != runner.V37_CHECKPOINT_SHA256:
            raise ValueError("The retained V37 checkpoint is not admitted")
        instance._archive = None
        instance._base_handle = None
        try:
            instance._base_handle = _open_read_only_no_writers(base_path)
            base_raw = instance._base_handle.read(512 * 1024 * 1024 + 1)
            if len(base_raw) > 512 * 1024 * 1024 or sha256(base_raw).hexdigest() != runner.V37_CHECKPOINT_SHA256:
                raise ValueError("The retained V37 checkpoint hash differs")
            base = torch.load(BytesIO(base_raw), map_location="cpu", weights_only=True)
            if not isinstance(base, dict) or not isinstance(base.get("state_dict"), dict):
                raise ValueError("The retained V37 checkpoint state is missing")
            instance._base_state = base["state_dict"]
            instance._archive = PositiveArchive(archive_path, key=archive_key, file_sha256=bytes.fromhex(spec["file_sha256"]),
                run_id=bytes.fromhex(spec["run_id"]), provenance=tuple(bytes.fromhex(item) for item in spec["provenance_sha256"]))
            if instance._archive.count != receipt["positive_count"] or _batch_count(instance._archive) != receipt["batch_count"]:
                raise ValueError("Producer receipt counts do not match the authenticated archive")
            family, provenance = load_bound_family()
            schedule = FixedSchedule.from_bound_config(config_raw, expected_config_sha,
                synthetic_ids=family.row_identities, real_positive_ids=instance._archive.row_identities)
            instance._adapter = MixedSourceBatchAdapter(family, instance._archive, schedule)
            instance._schedule = schedule
            instance._provenance = provenance
            instance._authorization = authorization
            instance._config_raw = config_raw
            instance._config_path = config_path
            instance._receipt_raw = receipt_raw
            instance._receipt_path = receipt_path
            instance._spec = spec
            instance._config = config
            instance._started = False
            instance._closed = False
            return instance
        except BaseException:
            if instance._archive is not None:
                instance._archive.close()
            if instance._base_handle is not None:
                instance._base_handle.close()
            raise

    @property
    def family_provenance(self) -> dict:
        # Preserve the loader's incomplete-mask/disjointness flags as reported.
        return dict(self._provenance)

    @property
    def schedule(self) -> FixedSchedule:
        return self._schedule

    def verify_input_binding(self, real_hash: str, synthetic_hash: str) -> bool:
        if self._closed or self._base_handle.closed or self._archive._handle.closed:
            return False
        try:
            return (real_hash == self._config["real_positive_binding_sha256"] and
                synthetic_hash == self._config["synthetic_feature_report_sha256"] and
                _locked_bytes(self._config_path, 4 * 1024 * 1024) == self._config_raw and
                _locked_bytes(self._receipt_path, 4 * 1024 * 1024) == self._receipt_raw)
        except OSError:
            return False

    def verify_model(self, model, expected_sha256: str) -> bool:
        if self._closed or expected_sha256 != runner.V37_CHECKPOINT_SHA256 or self._base_handle.closed:
            return False
        try:
            self._base_handle.seek(0)
            if sha256(self._base_handle.read()).hexdigest() != expected_sha256:
                return False
            _cpu_state(model, self._base_state)
            return True
        except (OSError, ValueError, TypeError):
            return False

    def begin_fixed_run(self, recipe, authorization) -> bool:
        if (not isinstance(recipe, runner.DraftRecipe) or self._closed or self._started or authorization is not self._authorization or
                asdict(recipe) != self._config.get("recipe") or self._schedule.completed_steps != 0):
            return False
        self._started = True
        return True

    def next_gpu(self):
        if self._closed or not self._started:
            raise RuntimeError("The admitted source has not started its single-use run")
        return self._adapter.next_gpu()

    def verify_batch(self, batch) -> bool:
        return not self._closed and self._started and self._adapter.verify_batch(batch)

    def commit(self, plan) -> None:
        if self._closed or not self._started:
            raise RuntimeError("The admitted source is unavailable")
        self._adapter.commit(plan)

    def close(self) -> None:
        if not self._closed:
            self._archive.close()
            self._base_handle.close()
            self._closed = True

    def __enter__(self):
        if self._closed:
            raise ValueError("The admitted source is closed")
        return self

    def __exit__(self, exc_type, exc, traceback):
        self.close()
