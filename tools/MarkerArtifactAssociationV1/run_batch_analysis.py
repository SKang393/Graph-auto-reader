# SPDX-License-Identifier: Apache-2.0
"""Caller-pinned launcher for the review-only fixed V5 train analysis."""
import argparse
import json
import os
from pathlib import Path
import sys

from . import batch_capture


def _safe_input_path(relative):
    path = batch_capture.ROOT
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts:
        raise ValueError("manifest path must stay within the project")
    for part in rel.parts:
        path = path / part
        info = path.lstat()
        if path.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("manifest path contains a link or reparse point")
    resolved = path.resolve(strict=True)
    if not resolved.is_relative_to(batch_capture.ROOT.resolve(strict=True)) or not resolved.is_file():
        raise ValueError("manifest path escapes project or is not a file")
    return resolved


def _read_bound_json(relative, expected_sha256):
    path = _safe_input_path(relative)
    raw = path.read_bytes()
    if batch_capture._digest(raw) != expected_sha256:
        raise ValueError("caller-pinned manifest file hash differs")
    value = json.loads(raw, object_pairs_hook=batch_capture._strict_object)
    if batch_capture._canonical(value) != raw:
        raise ValueError("caller-pinned manifest is not canonical JSON")
    return value, raw


def _ensure_project_directories(relative_parent):
    current = batch_capture.ROOT.resolve(strict=True)
    for part in Path(relative_parent).parts:
        if part in ("", ".", ".."):
            raise ValueError("output directory component is invalid")
        current = current / part
        try:
            info = current.lstat()
        except FileNotFoundError:
            current.mkdir()
            info = current.lstat()
        if current.is_symlink() or getattr(info, "st_file_attributes", 0) & 0x400:
            raise ValueError("output directory contains a link or reparse point")
        if not current.is_dir():
            raise ValueError("output path ancestor is not a directory")
        if not current.resolve(strict=True).is_relative_to(batch_capture.ROOT.resolve(strict=True)):
            raise ValueError("output directory escapes the project")
    return current


def _exclusive_write(relative, raw):
    normalized = batch_capture._validate_result_reference(relative)
    rel = Path(normalized)
    parent = _ensure_project_directories(rel.parent)
    target = parent / rel.name
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    if hasattr(os, "O_BINARY"):
        flags |= os.O_BINARY
    fd = os.open(target, flags, 0o600)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
    except BaseException:
        # An incomplete exclusively-created file remains as evidence and blocks retry.
        raise
    return target


def execute(caller_binding_path, expected_caller_binding_sha256):
    caller_path = _safe_input_path(caller_binding_path)
    caller_raw = caller_path.read_bytes()
    if batch_capture._digest(caller_raw) != expected_caller_binding_sha256:
        raise ValueError("caller binding SHA-256 differs from external pin")
    caller = json.loads(caller_raw, object_pairs_hook=batch_capture._strict_object)
    if batch_capture._canonical(caller) != caller_raw:
        raise ValueError("caller binding is not canonical JSON")
    required = {
        "schema", "input_manifest_path", "input_manifest_sha256",
        "source_runtime_manifest_path", "source_runtime_manifest_sha256",
        "result_reference", "optimizer_steps", "real_64_source_analysis_executed",
        "truth_read",
    }
    if set(caller) != required or caller.get("schema") != "graphreader.marker-artifact-v5-analysis-caller-binding.v1":
        raise ValueError("caller binding schema differs")
    if (caller["optimizer_steps"] != 0 or caller["real_64_source_analysis_executed"] is not False or
            caller["truth_read"] is not False):
        raise ValueError("caller binding exceeds review-only analysis scope")
    result_reference = batch_capture._validate_result_reference(caller["result_reference"])
    receipt_reference = result_reference + ".receipt.json"
    batch_capture._validate_result_reference(receipt_reference)
    input_manifest, input_raw = _read_bound_json(
        caller["input_manifest_path"], caller["input_manifest_sha256"])
    source_runtime, source_runtime_raw = _read_bound_json(
        caller["source_runtime_manifest_path"], caller["source_runtime_manifest_sha256"])
    if batch_capture._digest(batch_capture._canonical(input_manifest)) != caller["input_manifest_sha256"]:
        raise ValueError("input manifest canonical identity differs")
    if batch_capture._digest(batch_capture._canonical(source_runtime)) != caller["source_runtime_manifest_sha256"]:
        raise ValueError("source/runtime manifest canonical identity differs")
    batch_capture.verify_source_runtime_manifest(
        input_manifest, source_runtime,
        expected_source_runtime_sha256=caller["source_runtime_manifest_sha256"],
        result_reference=result_reference)

    result = batch_capture.analyze_bound_full_capture(
        input_manifest,
        expected_manifest_sha256=caller["input_manifest_sha256"],
        source_runtime_manifest=source_runtime,
        expected_source_runtime_sha256=caller["source_runtime_manifest_sha256"],
        result_reference=result_reference)

    # Revalidate caller-controlled bytes and current source/runtime before any output is created.
    if caller_path.read_bytes() != caller_raw:
        raise ValueError("caller binding changed during analysis")
    if _safe_input_path(caller["input_manifest_path"]).read_bytes() != input_raw:
        raise ValueError("input manifest changed during analysis")
    if _safe_input_path(caller["source_runtime_manifest_path"]).read_bytes() != source_runtime_raw:
        raise ValueError("source/runtime manifest changed during analysis")
    batch_capture.verify_source_runtime_manifest(
        input_manifest, source_runtime,
        expected_source_runtime_sha256=caller["source_runtime_manifest_sha256"],
        result_reference=result_reference)

    report_raw = batch_capture._canonical(result)
    report_sha256 = batch_capture._digest(report_raw)
    receipt = {
        "schema": "graphreader.marker-artifact-v5-analysis-receipt.v1",
        "caller_binding_sha256": expected_caller_binding_sha256,
        "input_manifest_sha256": caller["input_manifest_sha256"],
        "source_runtime_manifest_sha256": caller["source_runtime_manifest_sha256"],
        "result_reference": result_reference,
        "report_sha256": report_sha256,
        "source_count": result["source_count"],
        "completed_source_count": result["completed_source_count"],
        "failed_source_count": result["failed_source_count"],
        "training_approved": False,
        "optimizer_steps": 0,
        "truth_content_read": False,
        "private_input_supplied": False,
        "sealed_input_supplied": False,
    }
    receipt_raw = batch_capture._canonical(receipt)

    # Preflight both names, then use O_EXCL for each file to forbid silent replacement.
    batch_capture._validate_result_reference(result_reference)
    batch_capture._validate_result_reference(receipt_reference)
    report_path = _exclusive_write(result_reference, report_raw)
    receipt_path = _exclusive_write(receipt_reference, receipt_raw)
    return {
        "report_path": report_path,
        "report_sha256": report_sha256,
        "receipt_path": receipt_path,
        "receipt_sha256": batch_capture._digest(receipt_raw),
        "status": result["status"],
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--caller-binding", required=True,
                        help="project-relative canonical binding JSON")
    parser.add_argument("--expected-caller-binding-sha256", required=True,
                        help="externally reviewed SHA-256 of caller binding")
    args = parser.parse_args(argv)
    try:
        result = execute(args.caller_binding, args.expected_caller_binding_sha256)
    except Exception as error:
        print(f"analysis refused: {type(error).__name__}: {error}", file=sys.stderr)
        return 2
    print(json.dumps({key: str(value) if isinstance(value, Path) else value
                      for key, value in result.items()}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
