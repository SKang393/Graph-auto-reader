# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Load only the checksum-bound train-only residual-negative cache."""
from __future__ import annotations

import hashlib
import io
import json
from pathlib import Path

import torch

def load_residual_cache(repo: Path, manifest_path: Path, manifest_sha256: str,
                        eligibility_sha256: str) -> dict:
    root = repo.resolve()
    manifest_file = (root / manifest_path).resolve()
    if not manifest_file.is_relative_to(root):
        raise ValueError("Residual cache manifest is missing or changed")
    manifest_bytes = manifest_file.read_bytes()
    if hashlib.sha256(manifest_bytes).hexdigest() != manifest_sha256:
        raise ValueError("Residual cache manifest is missing or changed")
    manifest = json.loads(manifest_bytes)
    if (manifest.get("schema_version") != 1 or
            manifest.get("cache_id") != "marker-center-residual-negative-v38-train" or
            manifest.get("split") != "train" or manifest.get("scope") != "residual_negative" or
            manifest.get("rows") != 90 or
            manifest.get("tensor_file") != "train-residual-negative.pt" or
            manifest.get("source_report_sha256") != eligibility_sha256 or
            any(manifest.get(key) != 0 for key in ("optimizer_steps", "private_reads", "sealed_reads"))):
        raise ValueError("Residual cache identity or read scope changed")
    payload_file = manifest_file.parent / manifest["tensor_file"]
    payload_bytes = payload_file.read_bytes()
    if hashlib.sha256(payload_bytes).hexdigest() != manifest.get("tensor_sha256"):
        raise ValueError("Residual cache tensor changed")
    payload = torch.load(io.BytesIO(payload_bytes), map_location="cpu", weights_only=True)
    if not isinstance(payload, dict) or set(payload) != {
            "patches", "labels", "offsets", "radii", "hard_negative"}:
        raise ValueError("Residual cache fields changed")
    for name, shape in (("patches", (90, 3, 33, 33)), ("labels", (90,)),
                        ("offsets", (90, 2)), ("radii", (90,)), ("hard_negative", (90,))):
        value = payload[name]
        if not isinstance(value, torch.Tensor) or value.shape != shape or value.dtype != torch.float32:
            raise ValueError(f"Residual cache {name} contract changed")
        if not torch.isfinite(value).all():
            raise ValueError(f"Residual cache {name} is non-finite")
    if ((payload["patches"] < 0).any() or (payload["patches"] > 1).any() or
            torch.count_nonzero(payload["labels"]) or torch.count_nonzero(payload["offsets"]) or
            torch.count_nonzero(payload["radii"]) or not torch.all(payload["hard_negative"] == 1)):
        raise ValueError("Residual cache does not contain valid negative rows")
    return payload
