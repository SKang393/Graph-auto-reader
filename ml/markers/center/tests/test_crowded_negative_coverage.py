# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import copy
import json

import pytest
import torch

from ml.markers.center import crowded_negative_coverage as cache
from ml.markers.gate_seal import sha256_file


def diagnostic():
    return {"scope": "fixed_candidate_on_authenticated_synthetic_train_only",
        "model_sha256": cache.MODEL_SHA256, "parent_train_cache_sha256": cache.PARENT_SHA256,
        "all_v30_training_rows": sum(cache.ORIGINAL_ROWS.values()),
        "training_tensor_inventory_sha256": cache.tensors.HISTORICAL_INVENTORY,
        "operating_threshold": .25, "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
        "records": [{"scene_index": i, "split": "train"} for i in range(28)]}


def test_only_complete_fixed_model_training_diagnostic_is_admitted():
    cache.validate_diagnostic(diagnostic())


@pytest.mark.parametrize("key,value", [
    ("model_sha256", "0" * 64), ("parent_train_cache_sha256", "0" * 64),
    ("all_v30_training_rows", 270251), ("training_tensor_inventory_sha256", "0" * 64),
    ("operating_threshold", .1), ("optimizer_steps", 1), ("private_reads", 1),
    ("sealed_reads", 1), ("scope", "dev")])
def test_unbound_model_data_or_read_scope_is_rejected(key, value):
    report = diagnostic()
    report[key] = value
    with pytest.raises(ValueError, match="diagnostic changed"):
        cache.validate_diagnostic(report)


@pytest.mark.parametrize("defect", ["missing", "duplicate", "dev", "sealed"])
def test_family_membership_cannot_drop_duplicate_or_import_other_splits(defect):
    report = diagnostic()
    if defect == "missing":
        report["records"].pop()
    elif defect == "duplicate":
        report["records"][-1] = copy.deepcopy(report["records"][0])
    else:
        report["records"][-1]["split"] = defect
    with pytest.raises(ValueError, match="training family changed"):
        cache.validate_diagnostic(report)


@pytest.fixture
def bound_cache(tmp_path, monkeypatch):
    source = tmp_path / "source.py"
    source.write_text("# test source\n")
    directory = tmp_path / "cache"
    (directory / "source-snapshot").mkdir(parents=True)
    (directory / "source-snapshot/source.py").write_bytes(source.read_bytes())
    monkeypatch.setattr(cache, "SOURCE_PATHS", ("source.py",))
    counts = {"component": 1, "family": 1, "coverage": 1, "crowded": 1}
    monkeypatch.setattr(cache, "ORIGINAL_ROWS", counts)
    values = (torch.zeros(2, 3, 33, 33), torch.zeros(2), torch.zeros(2, 2), torch.zeros(2), torch.ones(2))
    payload = directory / "train-negative.pt"
    torch.save(dict(zip(cache.tensors.NAMES, values, strict=True)), payload)
    original = {key: tuple(v[:1].clone() for v in values) for key in counts}
    dev = {"frozen": object()}
    old = {"dev": {"inventory_sha256": "test-dev"}}
    manifest = {"version": cache.VERSION, "parent_cache_path": cache.PARENT_PATH,
        "parent_manifest_sha256": cache.PARENT_SHA256, "fixed_model_sha256": cache.MODEL_SHA256,
        "original_rows": counts, "source_sha256": {"source.py": sha256_file(source)},
        "files": {payload.name: sha256_file(payload)}, "dev": old["dev"],
        "counts": cache.tensors.validate_rows(values)}
    monkeypatch.setattr(cache.parent, "load_cache", lambda *args: (dict(original), dev, old))
    return tmp_path, directory, manifest, original, dev


def write_manifest(directory, manifest):
    path = directory / "manifest.json"
    path.write_text(json.dumps(manifest))
    return sha256_file(path)


def test_roundtrip_retains_prior_tensors_and_development_identity(bound_cache):
    repo, directory, manifest, original, dev = bound_cache
    scopes, actual_dev, _ = cache.load_cache(repo, directory, write_manifest(directory, manifest))
    assert actual_dev is dev
    assert scopes["crowded_mined_negative"][1].tolist() == [0., 0.]
    for key, values in original.items():
        assert scopes[key] is values


@pytest.mark.parametrize("defect", ["payload", "archive", "model", "parent", "path"])
def test_tampering_fails_before_prior_data_is_loaded(bound_cache, monkeypatch, defect):
    repo, directory, manifest, _, _ = bound_cache
    if defect == "payload":
        (directory / "train-negative.pt").write_bytes(b"changed")
    elif defect == "archive":
        (directory / "source-snapshot/source.py").write_bytes(b"changed")
    elif defect == "model":
        manifest["fixed_model_sha256"] = "0" * 64
    elif defect == "parent":
        manifest["parent_manifest_sha256"] = "0" * 64
    else:
        manifest["files"] = {"../outside.pt": "0" * 64}
    monkeypatch.setattr(cache.parent, "load_cache", lambda *args: pytest.fail("Unbound cache read prior data"))
    with pytest.raises(ValueError):
        cache.load_cache(repo, directory, write_manifest(directory, manifest))


@pytest.mark.parametrize("column", [1, 4])
def test_cached_rows_cannot_relabel_foreground_or_disable_hard_negatives(bound_cache, column):
    repo, directory, manifest, _, _ = bound_cache
    payload = directory / "train-negative.pt"
    values = torch.load(payload, weights_only=True)
    values[cache.tensors.NAMES[column]].fill_(1 if column == 1 else 0)
    if column == 1:
        values[cache.tensors.NAMES[3]].fill_(3)
    torch.save(values, payload)
    manifest["files"][payload.name] = sha256_file(payload)
    manifest["counts"] = cache.tensors.validate_rows(tuple(values[n] for n in cache.tensors.NAMES))
    with pytest.raises(ValueError, match="Mined negative labels"):
        cache.load_cache(repo, directory, write_manifest(directory, manifest))
