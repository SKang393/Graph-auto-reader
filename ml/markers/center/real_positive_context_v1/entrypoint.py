# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fail-closed CLI for an independently preregistered GPU candidate.

This entry point does not register a candidate, create a receipt, authorize a
dataset, read sealed inputs or promote a checkpoint. The runner's reviewed
schedule gate must explicitly advertise readiness before acquisition.
"""
from __future__ import annotations

import argparse
import contextlib
import ctypes
from dataclasses import asdict
from hashlib import sha256
from io import BytesIO
import json
import os
from pathlib import Path
import re
import sys

import torch

from ml.markers.gate_seal import source_bundle_sha256
from ml.markers.training_budget import CANONICAL_LEDGER_PATH, acquire_training_candidate, void_candidate
from ..dual_scale_context_v1.runner import SOURCES as FAMILY_SOURCES, configure_project_git
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from . import runner_foundation as runner
from .checkpoint_writer import _ALLOWED_BASES, _cpu_state, _project_file
from .model import RealPositiveCenterNet
from .objective import _require_training_gpu
from .mixed_source import load_bound_family
from .positive_archive import PositiveArchive, _open_read_only_no_writers
from .registered_source import RegisteredMixedSource, _ARCHIVE_KEYS, _artifact_path, _batch_count, _digest, _json, _locked_bytes, _receipt
from .schedule import ALGORITHM, FixedSchedule

_LOCAL = Path("ml/markers/center/real_positive_context_v1")
RUNNER_SOURCES = tuple(dict.fromkeys((*FAMILY_SOURCES, *(_LOCAL / name for name in (
    "__init__.py", "entrypoint.py", "registered_source.py", "mixed_source.py", "schedule.py",
    "positive_archive.py", "checkpoint_writer.py", "runner_foundation.py", "model.py", "objective.py", "protocol.json")))))
_ROOT = runner.PROJECT_ROOT


def _assert_key_acl(stream) -> None:
    """Require a protected DACL allowing only the owner, SYSTEM and admins."""
    if os.name != "nt":
        raise OSError("Windows protected key ACL is required")
    import msvcrt
    advapi = ctypes.WinDLL("advapi32", use_last_error=True)
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    get_info = advapi.GetSecurityInfo
    get_info.argtypes = (ctypes.c_void_p, ctypes.c_uint, ctypes.c_uint, ctypes.POINTER(ctypes.c_void_p),
                         ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p),
                         ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p))
    get_info.restype = ctypes.c_uint
    owner, dacl, descriptor = ctypes.c_void_p(), ctypes.c_void_p(), ctypes.c_void_p()
    code = get_info(msvcrt.get_osfhandle(stream.fileno()), 1, 0x5, ctypes.byref(owner), None,
                    ctypes.byref(dacl), None, ctypes.byref(descriptor))
    if code:
        raise OSError("Cannot inspect key security descriptor")
    token = ctypes.c_void_p()
    try:
        if not owner.value or not dacl.value:
            raise ValueError("Key must have an owner and an explicit DACL")
        control, revision = ctypes.c_ushort(), ctypes.c_uint()
        get_control = advapi.GetSecurityDescriptorControl
        get_control.argtypes = (ctypes.c_void_p, ctypes.POINTER(ctypes.c_ushort), ctypes.POINTER(ctypes.c_uint))
        if not get_control(descriptor, ctypes.byref(control), ctypes.byref(revision)) or not control.value & 0x1000:
            raise ValueError("Key DACL must block inherited permissions")
        kernel.GetCurrentProcess.restype = ctypes.c_void_p
        open_token = advapi.OpenProcessToken
        open_token.argtypes = (ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p))
        if not open_token(kernel.GetCurrentProcess(), 0x0008, ctypes.byref(token)):
            raise OSError("Cannot inspect current security token")
        token_info = advapi.GetTokenInformation
        token_info.argtypes = (ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32))
        needed = ctypes.c_uint32()
        token_info(token, 1, None, 0, ctypes.byref(needed))
        if not needed.value or needed.value > 65536:
            raise OSError("Cannot inspect current user identity")
        buf = ctypes.create_string_buffer(needed.value)
        if not token_info(token, 1, buf, needed.value, ctypes.byref(needed)):
            raise OSError("Cannot inspect current user identity")
        user_sid = ctypes.cast(buf, ctypes.POINTER(ctypes.c_void_p)).contents.value
        equal = advapi.EqualSid
        equal.argtypes = (ctypes.c_void_p, ctypes.c_void_p)
        equal.restype = ctypes.c_int
        if not user_sid or not equal(owner, user_sid):
            raise ValueError("Key owner must be the current user")

        class AclSize(ctypes.Structure):
            _fields_ = [("count", ctypes.c_uint32), ("used", ctypes.c_uint32), ("free", ctypes.c_uint32)]
        info = AclSize()
        acl_info = advapi.GetAclInformation
        acl_info.argtypes = (ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_int)
        if not acl_info(dacl, ctypes.byref(info), ctypes.sizeof(info), 2) or not 1 <= info.count <= 64:
            raise ValueError("Key DACL is invalid")
        get_ace = advapi.GetAce
        get_ace.argtypes = (ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p))
        well_known = advapi.IsWellKnownSid
        well_known.argtypes = (ctypes.c_void_p, ctypes.c_int)
        owner_read = False
        for index in range(info.count):
            ace = ctypes.c_void_p()
            if not get_ace(dacl, index, ctypes.byref(ace)) or not ace.value:
                raise ValueError("Key DACL entry is invalid")
            header = (ctypes.c_ubyte * 4).from_address(ace.value)
            size = int(header[2]) | (int(header[3]) << 8)
            if header[0] != 0 or header[1] != 0 or size < 16:
                raise ValueError("Key DACL contains an unsupported or inherited entry")
            mask = ctypes.c_uint32.from_address(ace.value + 4).value
            sid = ctypes.c_void_p(ace.value + 8)
            if equal(sid, user_sid):
                owner_read |= bool(mask & (0x1 | 0x80000000 | 0x10000000))
            elif not (well_known(sid, 22) or well_known(sid, 26)):
                raise ValueError("Key DACL allows an unauthorized principal")
        if not owner_read:
            raise ValueError("Current user must be allowed to read the key")
    finally:
        if token.value:
            kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
            kernel.CloseHandle(token)
        kernel.LocalFree.argtypes = (ctypes.c_void_p,)
        kernel.LocalFree(descriptor)


def _load_key(config: dict) -> bytes:
    value = config.get("archive_key_path")
    if type(value) is not str or not value or Path(value).is_absolute():
        raise ValueError("The admitted project-local key path is missing")
    key_path = _project_file(_ROOT / value, existing=True)
    with _open_read_only_no_writers(key_path) as stream:
        _assert_key_acl(stream)
        key = stream.read(33)
    if len(key) != 32:
        raise ValueError("The admitted key must contain exactly 32 bytes")
    return key


def _preflight(revision: str, candidate_id: str) -> tuple[dict, Path, bytes]:
    if (not isinstance(revision, str) or re.fullmatch(r"[a-z0-9][a-z0-9-]{0,95}", revision) is None or
            not isinstance(candidate_id, str) or re.fullmatch(r"P[1-9][0-9]{0,7}", candidate_id) is None):
        raise ValueError("Candidate identity is invalid")
    ledger = _json(_locked_bytes(_ROOT / CANONICAL_LEDGER_PATH, 8 * 1024 * 1024))
    entries = [item for item in ledger.get("revisions", []) if isinstance(item, dict) and
               item.get("task") == "marker-center" and item.get("revision") == revision]
    if len(entries) != 1:
        raise RuntimeError("Canonical ledger does not uniquely register this candidate")
    entry = entries[0]
    if (entry.get("execution_authorized") is not True or entry.get("authorized_candidate_id") != candidate_id or
            entry.get("status") != f"candidate_{int(candidate_id[1:])}_preregistered" or
            entry.get("preregistered_candidate_ids") != [candidate_id] or
            candidate_id in entry.get("consumed_candidate_ids", []) or entry.get("sealed_runs_authorized") != 0):
        raise RuntimeError("Canonical ledger does not authorize an unused train-only candidate")
    protocol = _LOCAL / "protocol.json"
    if (entry.get("protocol_path") != protocol.as_posix() or
            not _digest(entry.get("protocol_sha256")) or
            sha256(_locked_bytes(runner._inside_project(_ROOT, _ROOT / protocol), 4 * 1024 * 1024)).hexdigest()
            != entry["protocol_sha256"]):
        raise ValueError("Canonical training protocol is missing or changed")
    paths, hashes = entry.get("candidate_config_paths"), entry.get("candidate_config_sha256")
    if not isinstance(paths, dict) or not isinstance(hashes, dict):
        raise ValueError("Canonical candidate configuration binding is missing")
    config_value, expected = paths.get(candidate_id), hashes.get(candidate_id)
    if type(config_value) is not str or not config_value or Path(config_value).is_absolute() or not _digest(expected):
        raise ValueError("Canonical candidate configuration binding is invalid")
    config_path = Path(config_value)
    file_path = runner._inside_project(_ROOT, _ROOT / config_path)
    raw = _locked_bytes(file_path, 4 * 1024 * 1024)
    if sha256(raw).hexdigest() != expected:
        raise ValueError("Candidate configuration differs from the canonical ledger")
    config = _json(raw)
    if ((config.get("task"), config.get("revision"), config.get("candidate_id")) != ("marker-center", revision, candidate_id) or
            config.get("recipe") != asdict(runner.DRAFT_RECIPE) or config.get("production_approval") is not False or
            config.get("sealed_runs_authorized") != 0 or config.get("source_checkpoint_sha256") != runner.V37_CHECKPOINT_SHA256 or
            config.get("synthetic_feature_report_sha256") != runner.V37_FEATURE_REPORT_SHA256 or
            type(config.get("optimizer_steps_expected")) is not int or not 0 < config["optimizer_steps_expected"] <= 1_000_000 or
            entry.get("optimizer_steps_expected") != config["optimizer_steps_expected"] or
            config.get("expected_runner_source_bundle_sha256") != source_bundle_sha256(_ROOT, RUNNER_SOURCES)):
        raise ValueError("Candidate configuration or exact runner source bundle is not admitted")
    schedule = config.get("schedule")
    if (type(schedule) is not dict or schedule.get("algorithm") != ALGORITHM or
            schedule.get("torch_version") != str(torch.__version__) or
            any(not _digest(schedule.get(name)) for name in ("synthetic_population_sha256", "real_positive_population_sha256")) or
            any(type(schedule.get(name)) is not int or not 0 < schedule[name] <= 10_000_000 for name in
                ("synthetic_population_count", "real_positive_population_count")) or
            schedule.get("synthetic_source_binding_sha256") != config["synthetic_feature_report_sha256"] or
            schedule.get("real_source_binding_sha256") != config.get("real_positive_binding_sha256")):
        raise ValueError("Admitted schedule binding is incomplete")
    batches = (schedule["synthetic_population_count"] + runner.DRAFT_RECIPE.synthetic_batch_size - 1) // runner.DRAFT_RECIPE.synthetic_batch_size
    slots = batches * runner.DRAFT_RECIPE.real_positive_batch_size
    if (slots > 10_000_000 or schedule["real_positive_population_count"] > slots or
            config["optimizer_steps_expected"] != batches * runner.DRAFT_RECIPE.epochs):
        raise ValueError("Admitted schedule cannot cover its fixed populations")
    source_path = _artifact_path(config.get("source_checkpoint_path"))
    output = config.get("candidate_checkpoint_path")
    if type(output) is not str or not output or Path(output).is_absolute():
        raise ValueError("Admitted candidate checkpoint path is invalid")
    output_path = _project_file(_ROOT / output, existing=False)
    if source_path not in _ALLOWED_BASES or output_path.suffix != ".pt" or output_path == source_path or output_path.exists():
        raise ValueError("Admitted checkpoint paths are unavailable or invalid")
    spec = config.get("real_positive_archive")
    if (type(spec) is not dict or set(spec) != _ARCHIVE_KEYS or
            any(not _digest(spec[name]) for name in ("receipt_sha256", "file_sha256", "key_sha256")) or
            not _digest(spec["run_id"], 32) or type(spec["provenance_sha256"]) is not list or
            len(spec["provenance_sha256"]) != 8 or any(not _digest(x) for x in spec["provenance_sha256"])):
        raise ValueError("Admitted archive bindings are invalid")
    key = _load_key(config)
    _receipt(_locked_bytes(_artifact_path(spec["receipt_path"]), 4 * 1024 * 1024), spec, config, key)
    _artifact_path(spec["archive_path"])
    return config, config_path, key


def _prepare_inputs(config: dict, config_path: Path, key: bytes, stack: contextlib.ExitStack):
    """Authenticate all data and GPU state before reserving a candidate.

    The supplied stack owns read locks across candidate acquisition. The
    registered source must open its own archive and base locks before release.
    """
    ledger_handle = stack.enter_context(_open_read_only_no_writers(_ROOT / CANONICAL_LEDGER_PATH))
    config_handle = stack.enter_context(_open_read_only_no_writers(_ROOT / config_path))
    receipt_handle = stack.enter_context(_open_read_only_no_writers(_artifact_path(config["real_positive_archive"]["receipt_path"])))
    config_raw = config_handle.read(4 * 1024 * 1024 + 1)
    if len(config_raw) > 4 * 1024 * 1024 or _json(config_raw) != config:
        raise ValueError("Candidate configuration changed before acquisition")
    locked_ledger = _json(ledger_handle.read(8 * 1024 * 1024 + 1))
    entries = [item for item in locked_ledger.get("revisions", []) if isinstance(item, dict) and
               item.get("task") == config["task"] and item.get("revision") == config["revision"]]
    if (len(entries) != 1 or entries[0].get("candidate_config_sha256", {}).get(config["candidate_id"]) !=
            sha256(config_raw).hexdigest()):
        raise ValueError("Locked ledger and configuration do not match")
    # Recheck the complete ledger/config/source bundle while both are locked.
    second_config, second_path, second_key = _preflight(config["revision"], config["candidate_id"])
    if second_config != config or second_path != config_path or second_key != key:
        raise ValueError("Candidate admission changed before acquisition")
    spec = config["real_positive_archive"]
    receipt_raw = receipt_handle.read(4 * 1024 * 1024 + 1)
    if len(receipt_raw) > 4 * 1024 * 1024:
        raise ValueError("Producer receipt exceeds its size limit")
    receipt = _receipt(receipt_raw, spec, config, key)
    archive = stack.enter_context(PositiveArchive(_artifact_path(spec["archive_path"]), key=key,
        file_sha256=bytes.fromhex(spec["file_sha256"]), run_id=bytes.fromhex(spec["run_id"]),
        provenance=tuple(bytes.fromhex(value) for value in spec["provenance_sha256"])))
    if archive.count != receipt["positive_count"] or _batch_count(archive) != receipt["batch_count"]:
        raise ValueError("Producer receipt does not match the authenticated archive")
    base_handle = stack.enter_context(_open_read_only_no_writers(_artifact_path(config["source_checkpoint_path"])))
    base_raw = base_handle.read(512 * 1024 * 1024 + 1)
    if len(base_raw) > 512 * 1024 * 1024 or sha256(base_raw).hexdigest() != runner.V37_CHECKPOINT_SHA256:
        raise ValueError("Retained V37 checkpoint does not match admission")
    base = torch.load(BytesIO(base_raw), map_location="cpu", weights_only=True)
    if not isinstance(base, dict) or not isinstance(base.get("state_dict"), dict):
        raise ValueError("Retained V37 checkpoint state is invalid")
    local = SpatialCenterNet(ScaleClassifierNet(ModelConfig()))
    local.load_state_dict(base["state_dict"], strict=True)
    model = RealPositiveCenterNet(local).to(torch.device("cuda:0"))
    _require_training_gpu(*model.parameters())
    _cpu_state(model, base["state_dict"])
    optimizer = torch.optim.AdamW(model.residual.parameters(), lr=runner.DRAFT_RECIPE.learning_rate,
        weight_decay=runner.DRAFT_RECIPE.weight_decay, capturable=True)
    runner._require_fresh_state(model, optimizer)
    family, _provenance = load_bound_family()
    schedule = FixedSchedule.from_bound_config(config_raw, sha256(config_raw).hexdigest(),
        synthetic_ids=family.row_identities, real_positive_ids=archive.row_identities)
    if schedule.total_steps != config["optimizer_steps_expected"]:
        raise ValueError("Authenticated schedule steps do not match admission")
    return model, optimizer


def run_candidate(revision: str, candidate_id: str) -> dict:
    config, config_path, key = _preflight(revision, candidate_id)
    # Require the registered-source schedule gate before acquiring a candidate.
    if getattr(runner, "REGISTERED_SOURCE_SCHEDULE_GATE_READY", False) is not True:
        raise RuntimeError("The registered-source schedule gate is not reviewed")
    _require_training_gpu()
    # Snapshot creation invokes Git. Validate this checkout with a process-local
    # safe-directory entry before writing the irreversible single-use intent.
    configure_project_git()
    directory = _ROOT / "ml/markers/training-seals/marker-center" / revision / candidate_id
    forbidden = ("entrypoint-intent.json", "opened.json", "source-snapshot.json", "void.json", "result.json",
                 "consumed.json", "optimizer-run-intent.json", "optimizer-journal.jsonl", "optimizer-run-outcome.json")
    if any((directory / name).exists() for name in forbidden):
        raise RuntimeError("Candidate has an existing single-use attempt")
    with contextlib.ExitStack() as input_locks:
        model, optimizer = _prepare_inputs(config, config_path, key, input_locks)
        directory.mkdir(parents=True, exist_ok=True)
        if any((directory / name).exists() for name in forbidden):
            raise RuntimeError("Candidate has an existing single-use attempt")
        runner._write_new_durable(directory / "entrypoint-intent.json", {
            "schema": "graphreader.registered-entrypoint-intent.v1", "task": "marker-center",
            "revision": revision, "candidate_id": candidate_id,
            "config_sha256": sha256((_ROOT / config_path).read_bytes()).hexdigest(), "resume_authorized": False})
        authorization = acquire_training_candidate(_ROOT, task="marker-center", revision=revision,
            candidate_id=candidate_id, config_path=config_path, runner_source_paths=RUNNER_SOURCES)
        try:
            with RegisteredMixedSource.open_admitted(authorization, archive_key=key) as source:
                # The source now owns its own no-writer handles to both files.
                input_locks.close()
                if not source.verify_model(model, runner.V37_CHECKPOINT_SHA256):
                    raise RuntimeError("Retained model does not match the V37 checkpoint")
                result = runner.run_training(model, optimizer, source, authorization)
            if result.get("status") != "training_complete_unapproved" or not _digest(result.get("checkpoint_sha256")):
                raise RuntimeError("Candidate did not produce a verified checkpoint")
            return {"status": result["status"], "checkpoint_sha256": result["checkpoint_sha256"]}
        except BaseException:
            # A pre-optimizer failure gets a sanitized void, but the permanent
            # entrypoint intent still prevents a retry or misleading open seal.
            if not (directory / "optimizer-run-intent.json").exists() and not (directory / "result.json").exists():
                try:
                    void_candidate(authorization, RuntimeError("entrypoint_pre_optimizer_failure"))
                except Exception:
                    pass
            raise


class _Discard:
    def write(self, value):
        return len(value)
    def flush(self):
        pass


def main() -> int:
    parser = argparse.ArgumentParser(description="Run one admitted GPU-only marker-center candidate")
    parser.add_argument("--revision", required=True)
    parser.add_argument("--candidate-id", required=True)
    args = parser.parse_args()
    try:
        with contextlib.redirect_stdout(_Discard()), contextlib.redirect_stderr(_Discard()):
            result = run_candidate(args.revision, args.candidate_id)
    except Exception as error:
        print(json.dumps({"status": "failed", "error_type": type(error).__name__}, sort_keys=True))
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
