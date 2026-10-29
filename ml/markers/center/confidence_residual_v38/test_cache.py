# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import json
from pathlib import Path

import pytest
import torch

from ml.markers.center.confidence_residual_v38.cache import load_residual_cache
from ml.markers.gate_seal import sha256_file


def test_cache_binds_evidence_and_rejects_changed_payload(tmp_path):
    payload_path = tmp_path / "train-residual-negative.pt"
    payload = {"patches": torch.zeros(90, 3, 33, 33), "labels": torch.zeros(90),
               "offsets": torch.zeros(90, 2), "radii": torch.zeros(90), "hard_negative": torch.ones(90)}
    torch.save(payload, payload_path)
    manifest = {"schema_version": 1, "cache_id": "marker-center-residual-negative-v38-train",
                "split": "train", "scope": "residual_negative", "rows": 90,
                "tensor_file": payload_path.name, "tensor_sha256": sha256_file(payload_path),
                "source_report_sha256": "a" * 64, "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0}
    manifest_path = tmp_path / "manifest.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    manifest_sha = sha256_file(manifest_path)
    assert load_residual_cache(tmp_path, manifest_path, manifest_sha, "a" * 64)["patches"].shape[0] == 90
    with pytest.raises(ValueError):
        load_residual_cache(tmp_path, manifest_path, manifest_sha, "b" * 64)
    payload["labels"][0] = 1
    torch.save(payload, payload_path)
    with pytest.raises(ValueError):
        load_residual_cache(tmp_path, manifest_path, manifest_sha, "a" * 64)
    manifest["tensor_sha256"] = sha256_file(payload_path)
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(ValueError):
        load_residual_cache(tmp_path, manifest_path, sha256_file(manifest_path), "a" * 64)


def test_loads_the_exact_verified_bytes_when_paths_change_after_read(tmp_path, monkeypatch):
    payload_path = tmp_path / "train-residual-negative.pt"
    payload = {"patches": torch.zeros(90, 3, 33, 33), "labels": torch.zeros(90),
               "offsets": torch.zeros(90, 2), "radii": torch.zeros(90), "hard_negative": torch.ones(90)}
    torch.save(payload, payload_path)
    manifest_path = tmp_path / "manifest.json"
    manifest = {"schema_version": 1, "cache_id": "marker-center-residual-negative-v38-train",
                "split": "train", "scope": "residual_negative", "rows": 90,
                "tensor_file": payload_path.name, "tensor_sha256": sha256_file(payload_path),
                "source_report_sha256": "a" * 64, "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0}
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    manifest_sha = sha256_file(manifest_path)
    original_read_bytes = Path.read_bytes
    reads = []

    def swap_after_read(path):
        data = original_read_bytes(path)
        if path in (manifest_path, payload_path):
            reads.append(path.name)
            path.write_bytes(b"changed after verified read")
        return data

    monkeypatch.setattr(Path, "read_bytes", swap_after_read)
    result = load_residual_cache(tmp_path, manifest_path, manifest_sha, "a" * 64)
    assert result["patches"].shape == (90, 3, 33, 33)
    assert reads == ["manifest.json", "train-residual-negative.pt"]
