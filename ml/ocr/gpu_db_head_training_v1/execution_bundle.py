# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Path-bound project execution inventory, not operating-system attestation.

At run start the complete project-local GPU venv and fixed project-authored
sources are hashed. Per-step verification covers only the explicitly listed
critical files. Unlisted venv changes during the run, externally installed
base Python, OS, drivers, hardware, and preloaded code are not attested.
"""
from hashlib import sha256
import json
import os
from pathlib import Path
import stat
import sys
from threading import Lock

from ml.markers.center.real_positive_context_v1.positive_archive import _open_read_only_no_writers

ROOT = Path(__file__).resolve().parents[3]
VENV = Path("tools/gpu-training/venv")
MANIFEST = ROOT / "artifacts/ocr-gpu-db-head-unregistered-v1/execution-bundle-v2.json"
PREVIOUS_MANIFEST = ROOT / "artifacts/ocr-gpu-db-head-unregistered-v1/execution-bundle-v1.json"
PREVIOUS_SHA = "fe98bbb60472bd8c0ecaa9619e332a9e69a92feb81fe630cc23609287e0904c4"
SOURCE_PATHS = (
    "ml/ocr/__init__.py", "ml/ocr/official_bakeoff/__init__.py",
    "ml/ocr/official_bakeoff/frozen_trunk_head.py", "ml/ocr/gpu_db_head_probe_v1/__init__.py",
    "ml/ocr/gpu_db_head_probe_v1/probe.py", "ml/markers/center/__init__.py",
    "ml/markers/center/model.py", "ml/markers/center/postprocess.py",
    "ml/markers/center/real_positive_context_v1/__init__.py",
    "ml/markers/center/real_positive_context_v1/positive_archive.py",
    "ml/ocr/gpu_db_head_training_v1/__init__.py", "ml/ocr/gpu_db_head_training_v1/source.py",
    "ml/ocr/gpu_db_head_training_v1/journal.py", "ml/ocr/gpu_db_head_training_v1/runner.py",
    "ml/ocr/gpu_db_head_training_v1/execution_bundle.py", "ml/ocr/gpu_db_head_training_v1/source_derivation.py",
    "ml/ocr/gpu_db_head_training_v1/prepare_masks.py", "ml/ocr/gpu_db_head_probe_v1/feature_preflight_960.py",
    "ml/ocr/gpu_db_head_probe_v1/target_geometry_audit.py", "ml/ocr/gpu_db_head_probe_v1/conflict_pixel_counterfactual.py",
    "ml/ocr/gpu_db_head_probe_v1/loss_mask_counterfactual.py", "ml/ocr/official_bakeoff/db_inverse_targets.py",
    "tools/gpu-training/Run-GpuPython.ps1",
)
_SITE = "tools/gpu-training/venv/Lib/site-packages/"
CRITICAL_RUNTIME = (
    "tools/gpu-training/venv/Scripts/python.exe", "tools/gpu-training/venv/pyvenv.cfg",
    _SITE + "torch/__init__.py", _SITE + "torch/_C.cp313-win_amd64.pyd",
    _SITE + "torch/nn/functional.py", _SITE + "torch/optim/adamw.py", _SITE + "torch/optim/optimizer.py",
    _SITE + "torch/cuda/__init__.py", _SITE + "torch/lib/torch_python.dll", _SITE + "torch/lib/torch_cpu.dll",
    _SITE + "torch/lib/torch_hip.dll", _SITE + "torch/lib/c10.dll", _SITE + "torch/lib/c10_hip.dll",
    _SITE + "_rocm_sdk_core/bin/amdhip64_7.dll", _SITE + "_rocm_sdk_libraries/bin/MIOpen.dll",
    _SITE + "_rocm_sdk_libraries/bin/hipblas.dll", _SITE + "_rocm_sdk_libraries/bin/rocblas.dll",
    _SITE + "numpy/__init__.py", _SITE + "numpy/_core/_multiarray_umath.cp313-win_amd64.pyd",
    _SITE + "onnx/__init__.py", _SITE + "onnx/onnx_cpp2py_export.pyd", _SITE + "typing_extensions.py",
)


def _digest(path):
    digest = sha256()
    with _open_read_only_no_writers(path) as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _venv_inventory(root):
    base = (root / VENV).resolve(strict=True)
    if not base.is_relative_to(root.resolve(strict=True)):
        raise ValueError("GPU environment escapes project")
    result = []
    for directory, subdirectories, files in os.walk(base, followlinks=False):
        for name in subdirectories + files:
            path = Path(directory) / name
            info = path.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
                raise ValueError("GPU environment contains a link or reparse point")
        result.extend((Path(directory) / name).relative_to(root).as_posix() for name in files)
    return sorted(result)


def _paths(root):
    result = list(SOURCE_PATHS) + _venv_inventory(root)
    if len(set(path.casefold() for path in result)) != len(result):
        raise ValueError("Execution inventory repeats a path")
    return sorted(result)


def _inside(root, relative):
    path = Path(relative)
    if path.is_absolute() or ".." in path.parts:
        raise ValueError("Execution bundle path is unsafe")
    base = root.resolve(strict=True)
    current = base
    for part in path.parts:
        current = current / part
        info = current.lstat()
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
            raise ValueError("Execution bundle path contains a link or reparse point")
    if not current.resolve(strict=True).is_relative_to(base) or not current.is_file():
        raise ValueError("Execution bundle file escapes project")
    return current


def _full_path(root, relative):
    # _venv_inventory validates every venv entry and reparse point before and
    # after this scan. Avoid repeating every ancestor lstat for every file.
    return _inside(root, relative) if relative in SOURCE_PATHS else root / relative


def _object(raw):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate execution bundle field")
            result[key] = value
        return result
    value = json.loads(raw, object_pairs_hook=pairs)
    if not isinstance(value, dict):
        raise ValueError("Execution bundle must be an object")
    return value


def generate():
    """Capture a new unapproved bundle; only project files are read."""
    if MANIFEST.exists():
        raise FileExistsError("Execution bundle already exists")
    paths = _paths(ROOT)
    files = {rel: _digest(_full_path(ROOT, rel)) for rel in paths}
    if any(path not in files for path in CRITICAL_RUNTIME):
        raise ValueError("Critical runtime file is absent from inventory")
    if _paths(ROOT) != paths:
        raise ValueError("Execution inventory changed during capture")
    report = {"schema": "graphreader.ocr-gpu-execution-bundle.v1", "source_paths": list(SOURCE_PATHS),
              "critical_runtime": list(CRITICAL_RUNTIME),
              "venv_path": VENV.as_posix(), "files": files,
              "boundary": "full project-local venv checked at start; only named files checked per step; external base Python, OS, driver, hardware and preloaded code are not attested",
              "training_approved": False}
    raw = (json.dumps(report, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    with MANIFEST.open("xb") as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())
    return sha256(raw).hexdigest()


def generate_rebased():
    """Reuse a hash-pinned prior venv capture; startup still rehashes it all."""
    if MANIFEST.exists():
        raise FileExistsError("Execution bundle already exists")
    with _open_read_only_no_writers(PREVIOUS_MANIFEST) as stream:
        raw = stream.read()
    if sha256(raw).hexdigest() != PREVIOUS_SHA:
        raise ValueError("Previous execution bundle differs")
    previous = _object(raw)
    if (previous.get("schema") != "graphreader.ocr-gpu-execution-bundle.v1" or
            previous.get("venv_path") != VENV.as_posix() or previous.get("critical_runtime") != list(CRITICAL_RUNTIME) or
            previous.get("training_approved") is not False or not isinstance(previous.get("files"), dict)):
        raise ValueError("Previous execution bundle scope differs")
    venv_paths = _venv_inventory(ROOT)
    prefix = VENV.as_posix() + "/"
    old_venv = {path: value for path, value in previous["files"].items() if path.startswith(prefix)}
    if sorted(old_venv) != venv_paths:
        raise ValueError("GPU venv inventory differs from previous capture")
    files = {**old_venv, **{path: _digest(_inside(ROOT, path)) for path in SOURCE_PATHS}}
    if any(path not in files for path in CRITICAL_RUNTIME) or _venv_inventory(ROOT) != venv_paths:
        raise ValueError("Critical files or venv inventory differ")
    report = {"schema": "graphreader.ocr-gpu-execution-bundle.v1", "source_paths": list(SOURCE_PATHS),
              "critical_runtime": list(CRITICAL_RUNTIME), "venv_path": VENV.as_posix(), "files": files,
              "venv_hash_source_manifest_sha256": PREVIOUS_SHA, "venv_rehashed_during_rebase": False,
              "boundary": "full project-local venv checked at start; only named files checked per step; external base Python, OS, driver, hardware and preloaded code are not attested",
              "training_approved": False}
    payload = (json.dumps(report, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    with MANIFEST.open("xb") as stream:
        stream.write(payload)
        stream.flush()
        os.fsync(stream.fileno())
    return sha256(payload).hexdigest()


def _manifest(expected_sha):
    if not isinstance(expected_sha, str) or len(expected_sha) != 64 or any(c not in "0123456789abcdef" for c in expected_sha):
        raise ValueError("Execution bundle SHA-256 is invalid")
    with _open_read_only_no_writers(MANIFEST) as stream:
        raw = stream.read()
    if sha256(raw).hexdigest() != expected_sha:
        raise ValueError("Execution bundle manifest differs")
    report = _object(raw)
    if (report.get("schema") != "graphreader.ocr-gpu-execution-bundle.v1" or report.get("source_paths") != list(SOURCE_PATHS) or
            report.get("critical_runtime") != list(CRITICAL_RUNTIME) or
            report.get("venv_path") != VENV.as_posix() or report.get("training_approved") is not False or not isinstance(report.get("files"), dict)):
        raise ValueError("Execution bundle scope differs")
    return report


def verify_full(expected_sha):
    report = _manifest(expected_sha)
    paths = _paths(ROOT)
    if sorted(report["files"]) != paths:
        raise ValueError("Execution bundle inventory changed")
    for relative in paths:
        expected = report["files"][relative]
        if not isinstance(expected, str) or len(expected) != 64 or _digest(_full_path(ROOT, relative)) != expected:
            raise ValueError("Execution bundle file changed")
    if _paths(ROOT) != paths:
        raise ValueError("Execution bundle inventory changed during verification")
    return report


def verify_critical(expected_sha):
    report = _manifest(expected_sha)
    paths = sorted(set(SOURCE_PATHS + CRITICAL_RUNTIME))
    for relative in paths:
        expected = report["files"].get(relative)
        if not isinstance(expected, str) or len(expected) != 64 or _digest(_inside(ROOT, relative)) != expected:
            raise ValueError("Critical execution bundle file changed")
    return report


_RECEIPT_KEY = object()


class _StartupReceipt:
    def __init__(self, expected_sha, key):
        if key is not _RECEIPT_KEY:
            raise ValueError("A full execution verification is required")
        self._expected_sha = expected_sha
        self._used = False
        self._lock = Lock()


def verify_startup(expected_sha):
    verify_full(expected_sha)
    return _StartupReceipt(expected_sha, _RECEIPT_KEY)


def consume_startup(receipt, expected_sha):
    if type(receipt) is not _StartupReceipt:
        raise ValueError("A verified startup receipt is required")
    with receipt._lock:
        if receipt._used or receipt._expected_sha != expected_sha:
            raise ValueError("Startup receipt is already used or has a different bundle")
        receipt._used = True


def require_runtime():
    expected = (ROOT / VENV / "Scripts/python.exe").resolve(strict=True)
    if (Path(sys.executable).resolve(strict=True) != expected or os.environ.get("PYTHONNOUSERSITE") != "1" or
            os.environ.get("PYTHONDONTWRITEBYTECODE") != "1" or os.environ.get("PYTHONPATH")):
        raise RuntimeError("Project GPU launcher with isolated Python environment is required")


if __name__ == "__main__":
    if len(sys.argv) != 2 or sys.argv[1] not in ("generate", "rebase"):
        raise SystemExit("Expected generate or rebase; no optimizer is invoked")
    digest = generate() if sys.argv[1] == "generate" else generate_rebased()
    print(json.dumps({"status": "captured_unapproved", "manifest_sha256": digest,
                      "venv_rehashed_during_capture": sys.argv[1] == "generate"}))
