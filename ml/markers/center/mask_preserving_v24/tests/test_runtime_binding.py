# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Frozen-file and joined-content barriers at the training entry point."""

from dataclasses import asdict, replace
from hashlib import sha256
import json
from pathlib import Path

import pytest
import torch

from ml.markers.center.mask_preserving_v24 import runtime_binding as binding
from ml.markers.center.mask_preserving_v24.family_scenes import FamilyScene
from ml.markers.center.mask_preserving_v24.runtime_family_scenes import RuntimeFamilySceneJoin
from ml.markers.center.mask_preserving_v24.runtime_inputs import (
    RuntimeAlgorithmIdentity, RuntimeAssemblyIdentity, RuntimeEvidenceBinding,
    RuntimeImplementationIdentity, RuntimeInputError, RuntimeModelIdentity,
    RuntimeSourceIdentity, SYNTHETIC_SOURCE_RELATIVE_PATHS,
)


@pytest.fixture
def exchange(tmp_path, monkeypatch):
    root = tmp_path
    artifacts = root / "artifacts"
    artifacts.mkdir()
    digest = "a" * 64
    assembly = RuntimeAssemblyIdentity("fixture", digest)
    identity = RuntimeImplementationIdentity(
        digest, tuple(RuntimeSourceIdentity(path, digest) for path in SYNTHETIC_SOURCE_RELATIVE_PATHS),
        Path("artifacts/candidate.json"), digest, digest, "development-only",
        (assembly,), RuntimeAlgorithmIdentity("artifact", "v1", digest, digest, "v1", "{}", (assembly,)),
        tuple(RuntimeModelIdentity(stage, "v1", name, "v1", digest, "CPUExecutionProvider")
              for stage, name in (("axis", "axis"), ("ocr", "detector"), ("ocr", "recognizer"))),
    )
    train = FamilyScene("train", "train-family", 1, torch.zeros((3, 2, 2)), (), (), ())
    dev = replace(train, split="validation", family="dev-family", seed=2)
    joined = RuntimeFamilySceneJoin((train,), (dev,), ())
    document = {
        "schema": binding.BINDING_SCHEMA,
        "synthetic_only": True, "private_data": False, "sealed_runs": 0,
        "training_input_ready": True, "production_approved": False, "complete_artifact_mask": False,
        "train": asdict(RuntimeEvidenceBinding("train", Path("artifacts/train.json"), digest, Path("artifacts/train-report.json"), digest)),
        "dev": asdict(RuntimeEvidenceBinding("validation", Path("artifacts/dev.json"), digest, Path("artifacts/dev-report.json"), digest)),
        "implementation": asdict(identity),
        "loader_source_sha256": sha256(Path(binding.runtime_inputs.__file__).read_bytes()).hexdigest(),
        "join_source_sha256": sha256(Path(binding.runtime_family_scenes.__file__).read_bytes()).hexdigest(),
        "train_tensor_set_sha256": binding.tensor_set_sha256(joined.train),
        "dev_tensor_set_sha256": binding.tensor_set_sha256(joined.dev),
        "mapping_audit_sha256": binding.mapping_audit_sha256(joined),
    }
    document = json.loads(json.dumps(document, default=lambda value: value.as_posix()))
    calls = []
    sentinel = object()

    def load(train_binding, dev_binding, implementation, *, repository_root):
        calls.append((train_binding, dev_binding, implementation, repository_root))
        return sentinel

    def join(inputs):
        assert inputs is sentinel
        return joined

    monkeypatch.setattr(binding.runtime_inputs, "load_bound_runtime_inputs", load)
    monkeypatch.setattr(binding.runtime_family_scenes, "join_runtime_family_scenes", join)

    def write():
        path = artifacts / "binding.json"
        payload = json.dumps(document).encode()
        path.write_bytes(payload)
        return path, sha256(payload).hexdigest()

    return root, document, joined, calls, write


def test_frozen_binding_passes_exact_identities_then_checks_joined_content(exchange):
    root, document, joined, calls, write = exchange
    path, digest = write()
    assert binding.load_runtime_family_binding(path, digest, repository_root=root) is joined
    train, dev, implementation, observed_root = calls[0]
    assert observed_root == root
    assert train.manifest_path == root / "artifacts/train.json"
    assert dev.split == "validation"
    assert implementation.candidate_sha256 == document["implementation"]["candidate_sha256"]
    assert implementation.synthetic_sources[0].relative_path == SYNTHETIC_SOURCE_RELATIVE_PATHS[0]
    assert isinstance(implementation.algorithm.dependencies[0], RuntimeAssemblyIdentity)


@pytest.mark.parametrize("field,value", [
    ("production_approved", True), ("private_data", True), ("sealed_runs", 1),
    ("training_input_ready", False), ("complete_artifact_mask", True),
    ("join_source_sha256", "b" * 64), ("loader_source_sha256", "b" * 64),
])
def test_foreign_scope_or_changed_code_fails_before_input_read(exchange, field, value):
    root, document, _, calls, write = exchange
    document[field] = value
    path, digest = write()
    with pytest.raises(RuntimeInputError):
        binding.load_runtime_family_binding(path, digest, repository_root=root)
    assert not calls


def test_changed_binding_file_and_changed_tensor_both_fail(exchange):
    root, document, _, calls, write = exchange
    path, digest = write()
    path.write_bytes(path.read_bytes() + b" ")
    with pytest.raises(RuntimeInputError, match="bytes differ"):
        binding.load_runtime_family_binding(path, digest, repository_root=root)
    assert not calls
    document["train_tensor_set_sha256"] = "b" * 64
    path, digest = write()
    with pytest.raises(RuntimeInputError, match="joined runtime train_tensor"):
        binding.load_runtime_family_binding(path, digest, repository_root=root)


def test_path_escape_fails_before_input_read(exchange):
    root, document, _, calls, write = exchange
    document["train"]["manifest_path"] = "../outside.json"
    path, digest = write()
    with pytest.raises(RuntimeInputError, match="repository-relative"):
        binding.load_runtime_family_binding(path, digest, repository_root=root)
    assert not calls
