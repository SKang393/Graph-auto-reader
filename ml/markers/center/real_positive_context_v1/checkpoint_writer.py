# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Publish one candidate checkpoint without overwrite or model promotion.

The caller must separately authenticate candidate admission and completed-step
accounting, and prevent concurrent model mutation while taking the snapshot.
A successful checkpoint is not a validation or release decision.
"""
from __future__ import annotations

import ctypes
from dataclasses import dataclass
from hashlib import sha256
from io import BytesIO
import os
from pathlib import Path
import re
import uuid

import torch

from .model import RealPositiveCenterNet
from .positive_archive import _open_read_only_no_writers
from .model import V37_CHECKPOINT_SHA256

_ROOT = Path(__file__).resolve().parents[4]
_BASE_RELATIVE = Path("artifacts/goal22-runs/center-confidence-reflection-v37/P1/marker-center.pt")
_ALLOWED_BASES = (_ROOT / _BASE_RELATIVE,
                  _ROOT / "artifacts/goal22-worktrees/resume-20260908" / _BASE_RELATIVE)
_MAX_CHECKPOINT_BYTES = 512 * 1024 * 1024


@dataclass(frozen=True)
class CheckpointReceipt:
    relative_path: str
    sha256: str
    base_checkpoint_sha256: str
    revision: str
    candidate_id: str


def _project_file(path: Path, *, existing: bool) -> Path:
    root = _ROOT.resolve(strict=True)
    path = Path(path)
    if not path.is_absolute():
        raise ValueError("Checkpoint paths must be absolute")
    parent = path.parent.resolve(strict=True)
    if not parent.is_relative_to(root / "artifacts"):
        raise ValueError("Checkpoint files must stay inside project artifacts")
    current = path.parent
    while current != root:
        attributes = getattr(current.stat(follow_symlinks=False), "st_file_attributes", 0)
        if current.is_symlink() or (attributes & 0x400):
            raise ValueError("Checkpoint directory ancestry may not contain a reparse point")
        if current == current.parent:
            raise ValueError("Checkpoint directory is outside the project")
        current = current.parent
    resolved = parent / path.name
    if existing and (not resolved.is_file() or resolved.is_symlink()):
        raise ValueError("The retained base checkpoint must be a regular file")
    return resolved


def _cpu_state(model: RealPositiveCenterNet, base: dict[str, torch.Tensor]) -> dict[str, torch.Tensor]:
    if not isinstance(model, RealPositiveCenterNet) or not isinstance(base, dict):
        raise TypeError("A candidate model and authenticated V37 state are required")
    state = {}
    for name, value in model.state_dict().items():
        snapshot = value.detach().to(device="cpu").clone().contiguous()
        if not torch.isfinite(snapshot).all():
            raise ValueError("Candidate contains non-finite weights")
        state[name] = snapshot
    local = {name.removeprefix("local."): value for name, value in state.items() if name.startswith("local.")}
    if local.keys() != base.keys():
        raise ValueError("Candidate retained model state membership differs from V37")
    if (set(state) != {f"local.{name}" for name in base} | {"residual.weight", "residual.bias"} or
            tuple(state["residual.weight"].shape) != (1, 768) or tuple(state["residual.bias"].shape) != (1,) or
            state["residual.weight"].dtype != torch.float32 or state["residual.bias"].dtype != torch.float32):
        raise ValueError("Confidence residual does not match the registered shape and dtype")
    for name, value in local.items():
        reference = base[name]
        if (not isinstance(reference, torch.Tensor) or reference.dtype != value.dtype or
                reference.shape != value.shape or not torch.isfinite(reference).all() or
                not torch.isfinite(value).all() or not torch.equal(value, reference)):
            raise ValueError("Candidate retained weights differ from the authenticated V37 checkpoint")
    return state


def _create_locked(path: Path):
    if os.name != "nt":
        raise OSError("Atomic candidate publication requires Windows handle locking")
    import msvcrt
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    create = kernel.CreateFileW
    create.argtypes = (ctypes.c_wchar_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p,
                       ctypes.c_uint32, ctypes.c_uint32, ctypes.c_void_p)
    create.restype = ctypes.c_void_p
    # Share nothing and include DELETE access to rename this exact open handle.
    handle = create(str(path), 0xC0010000, 0, None, 1, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        fd = msvcrt.open_osfhandle(handle, os.O_RDWR | os.O_BINARY)
    except BaseException:
        kernel.CloseHandle.argtypes = (ctypes.c_void_p,)
        kernel.CloseHandle(handle)
        raise
    try:
        return os.fdopen(fd, "w+b", buffering=0)
    except BaseException:
        os.close(fd)
        raise


def _handle_info(stream, info_class: int, info: ctypes.Structure) -> None:
    import msvcrt
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    update = kernel.SetFileInformationByHandle
    update.argtypes = (ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32)
    update.restype = ctypes.c_int
    if not update(msvcrt.get_osfhandle(stream.fileno()), info_class, ctypes.byref(info), ctypes.sizeof(info)):
        raise ctypes.WinError(ctypes.get_last_error())


def _rename_without_replacement(stream, destination: Path) -> None:
    target = str(destination)
    units = len(target.encode("utf-16-le")) // 2
    class RenameInfo(ctypes.Structure):
        _fields_ = [("replace", ctypes.c_ubyte), ("root", ctypes.c_void_p),
                    ("filename_length", ctypes.c_uint32), ("filename", ctypes.c_wchar * units)]
    info = RenameInfo()
    info.replace = 0
    info.root = None
    info.filename_length = len(target.encode("utf-16-le"))
    info.filename = target
    _handle_info(stream, 3, info)  # FileRenameInfo; replacement is explicitly disabled.


def _delete_owned_handle(stream) -> None:
    class Disposition(ctypes.Structure):
        _fields_ = [("delete", ctypes.c_int)]
    _handle_info(stream, 4, Disposition(1))  # FileDispositionInfo, deleted on close.


def write_candidate_checkpoint(model: RealPositiveCenterNet, *, base_path: Path, destination: Path,
                               revision: str, candidate_id: str) -> CheckpointReceipt:
    """Write a new candidate file and hash reread bytes under an exclusive lock."""
    if (type(revision) is not str or re.fullmatch(r"[a-z0-9][a-z0-9-]{0,95}", revision) is None or
            type(candidate_id) is not str or re.fullmatch(r"P[1-9][0-9]{0,7}", candidate_id) is None):
        raise ValueError("Candidate identity is invalid")
    base_path = _project_file(base_path, existing=True)
    if base_path not in _ALLOWED_BASES:
        raise ValueError("Retained V37 source is not an allowed project checkpoint")
    destination = _project_file(destination, existing=False)
    if destination.suffix != ".pt" or destination == base_path:
        raise ValueError("A separate .pt candidate checkpoint is required")
    with _open_read_only_no_writers(base_path) as source:
        base_raw = source.read(_MAX_CHECKPOINT_BYTES + 1)
        if len(base_raw) > _MAX_CHECKPOINT_BYTES or sha256(base_raw).hexdigest() != V37_CHECKPOINT_SHA256:
            raise ValueError("V37 checkpoint bytes differ from the bound identity")
        value = torch.load(BytesIO(base_raw), map_location="cpu", weights_only=True)
        if not isinstance(value, dict) or not isinstance(value.get("state_dict"), dict):
            raise ValueError("V37 checkpoint has no valid state dictionary")
        state = _cpu_state(model, value["state_dict"])
        payload = {"format": "graphreader.real-positive-candidate.v1", "revision": revision,
                   "candidate_id": candidate_id, "base_checkpoint_sha256": V37_CHECKPOINT_SHA256,
                   "state_dict": state}
        buffer = BytesIO()
        torch.save(payload, buffer)
        encoded = buffer.getvalue()
        if not encoded or len(encoded) > _MAX_CHECKPOINT_BYTES:
            raise ValueError("Serialized candidate checkpoint exceeds its size bound")
        expected_hash = sha256(encoded).hexdigest()
        temporary = destination.with_name(f".{destination.name}.{uuid.uuid4().hex}.tmp")
        stream = _create_locked(temporary)
        try:
            try:
                stream.write(encoded)
                stream.flush()
                os.fsync(stream.fileno())
                stream.seek(0)
                actual = stream.read(_MAX_CHECKPOINT_BYTES + 1)
                if sha256(actual).hexdigest() != expected_hash or actual != encoded:
                    raise ValueError("Candidate checkpoint failed its locked byte reread")
                reread = torch.load(BytesIO(actual), map_location="cpu", weights_only=True)
                if (not isinstance(reread, dict) or reread.keys() != payload.keys() or
                        any(reread[name] != payload[name] for name in payload if name != "state_dict") or
                        not isinstance(reread["state_dict"], dict) or reread["state_dict"].keys() != state.keys() or
                        any(not torch.equal(reread["state_dict"][name], value) for name, value in state.items())):
                    raise ValueError("Candidate checkpoint failed its locked state reread")
                _rename_without_replacement(stream, destination)
                os.fsync(stream.fileno())
                stream.seek(0)
                if sha256(stream.read(_MAX_CHECKPOINT_BYTES + 1)).hexdigest() != expected_hash:
                    raise ValueError("Published candidate checkpoint changed before finalization")
            except BaseException as error:
                try:
                    _delete_owned_handle(stream)
                except OSError as cleanup_error:
                    error.add_note(f"Owned temporary checkpoint could not be removed: {cleanup_error}")
                raise
        finally:
            stream.close()
    return CheckpointReceipt(destination.relative_to(_ROOT).as_posix(), expected_hash, V37_CHECKPOINT_SHA256,
                             revision, candidate_id)
