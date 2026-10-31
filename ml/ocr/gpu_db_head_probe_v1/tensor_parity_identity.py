# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Validate an externally pinned run identity for a synthetic parity capture."""
from pathlib import Path

from ml.ocr.gpu_db_head_probe_v1 import feature_preflight as preflight
from ml.ocr.gpu_db_head_probe_v1 import parity

BUILD = "artifacts/synthetic-tensor-parity-build/bin/GraphReader.SyntheticTensorParity/release_win-x64/"
FILES = {
    "capture_source": "tools/GraphReader.SyntheticTensorParity/Program.cs",
    "capture_project": "tools/GraphReader.SyntheticTensorParity/GraphReader.SyntheticTensorParity.csproj",
    "capture_executable": BUILD + "GraphReader.SyntheticTensorParity.dll",
    "decoder_assembly": BUILD + "GraphReader.App.dll",
    "detector_assembly": BUILD + "GraphReader.Ocr.dll",
    "opencv_assembly": BUILD + "OpenCvSharp.dll",
    "opencv_native": BUILD + "OpenCvSharpExtern.dll",
    "preflight_script": "ml/ocr/gpu_db_head_probe_v1/feature_preflight.py",
    "dataset_binding_script": "ml/ocr/gpu_db_head_probe_v1/parity.py",
    "comparator_512": "ml/ocr/gpu_db_head_probe_v1/tensor_parity.py",
    "comparator_960": "ml/ocr/gpu_db_head_probe_v1/tensor_parity_960.py",
    "identity_validator": "ml/ocr/gpu_db_head_probe_v1/tensor_parity_identity.py",
}
for _name in ("Axis", "Domain", "Export", "Imaging", "Inference", "Legends", "Markers", "Pdf", "Phases", "SuperResolution"):
    FILES["project_" + _name.lower()] = BUILD + "GraphReader." + _name + ".dll"
FILES["runtime_deps"] = BUILD + "GraphReader.SyntheticTensorParity.deps.json"
FILES["runtime_config"] = BUILD + "GraphReader.SyntheticTensorParity.runtimeconfig.json"
CAPTURE_FIELDS = {key + "_sha256": key for key in (
    "capture_source", "capture_project", "capture_executable", "decoder_assembly",
    "detector_assembly", "opencv_assembly", "opencv_native")}
CAPTURE_EXTRA = {key for key in FILES if key.startswith("project_")} | {"runtime_deps", "runtime_config"}


def load(path: Path, expected_sha: str) -> dict:
    root = parity.ROOT.resolve(strict=True)
    path = path.resolve(strict=True)
    if not path.is_relative_to((root / "artifacts").resolve(strict=True)):
        raise ValueError("Run identity must be project-local")
    identity = preflight._json(parity._read_locked(path, expected_sha))
    files = identity.get("files")
    if identity.get("schema") != "graphreader.synthetic-tensor-parity-run-identity.v1" or not isinstance(files, dict) or set(files) != set(FILES):
        raise ValueError("Run identity contract differs")
    for key, relative in FILES.items():
        record = files[key]
        if not isinstance(record, dict) or set(record) != {"path", "sha256"} or record["path"] != relative:
            raise ValueError("Run identity file path differs")
        candidate = (root / relative).resolve(strict=True)
        if not candidate.is_relative_to(root):
            raise ValueError("Run identity file escapes project")
        parity._read_locked(candidate, record["sha256"])
    return identity


def check_capture(identity: dict, capture: dict, identity_sha: str) -> None:
    if capture.get("run_identity_sha256") != identity_sha:
        raise ValueError("Capture run identity differs")
    for field, key in CAPTURE_FIELDS.items():
        if capture.get(field) != identity["files"][key]["sha256"]:
            raise ValueError("Capture assembly or source identity differs")
    extra = capture.get("additional_runtime_sha256")
    if not isinstance(extra, dict) or set(extra) != CAPTURE_EXTRA:
        raise ValueError("Capture transitive runtime inventory differs")
    if any(extra[key] != identity["files"][key]["sha256"] for key in CAPTURE_EXTRA):
        raise ValueError("Capture transitive runtime identity differs")
