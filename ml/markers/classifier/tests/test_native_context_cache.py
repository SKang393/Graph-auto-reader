# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from copy import deepcopy
import hashlib
import json

import numpy as np
import pytest

from ml.markers.classifier import native_context_cache as cache
from ml.markers.classifier.native_context_cache import audit_records, observable_targets
from ml.markers.classifier.native_context_data import VERSION, cases, prepare_case


def row(digest, shape, fill, artifact=False):
    return dict(patch_sha256=digest, shape_index=shape, fill_index=fill,
                artifact=artifact, family="synthetic", context="isolated")


def bound(rows):
    return [dict(r, **target) for r, target in zip(rows, observable_targets(rows), strict=True)]


def test_identical_ambiguous_shape_and_fill_keep_authored_truth():
    rows = [row("same", 0, 0), row("same", 4, 1)]
    before = deepcopy(rows)
    targets = observable_targets(rows)
    assert rows == before
    assert targets[0] == targets[1]
    assert targets[0]["shape_target_indices"] == [0, 4]
    assert targets[0]["fill_target_index"] == 2
    audit = audit_records({"train": bound(rows), "dev": bound([row("different", 1, 0)])})
    assert audit["status"] == "valid"
    assert audit["conflicting_label_tensors"] == ["same"]
    assert audit["splits"]["train"]["count"] == 2


def test_overlapping_split_pixels_cannot_be_hidden_by_matching_labels():
    rows = bound([row("same", 0, 0)])
    audit = audit_records({"train": rows, "dev": rows})
    assert audit["status"] == "invalid"
    assert audit["cross_split_identical_tensors"] == ["same"]


def test_artifact_marker_conflict_cannot_be_resolved_as_shape_ambiguity():
    rows = bound([row("same", 0, 0), row("same", -1, -1, True)])
    audit = audit_records({"train": rows, "dev": bound([row("different", 1, 0)])})
    assert audit["status"] == "invalid"
    assert audit["conflicting_training_targets"] == ["same"]


def small_cache(tmp_path, monkeypatch):
    recipes = {split: (cases(split)[0],) for split in ("train", "dev")}
    monkeypatch.setattr(cache, "cases", lambda split: recipes[split])
    records, files = {}, {}
    for split, values in recipes.items():
        patch, record = prepare_case(values[0])
        records[split] = bound([record])
        np.save(tmp_path / f"{split}.npy", patch[None], allow_pickle=False)
        cache.write_json(tmp_path / f"{split}.json", records[split])
        files[split] = {suffix: cache.sha256(tmp_path / f"{split}.{suffix}") for suffix in ("npy", "json")}
    cache.write_json(tmp_path / "manifest.json", dict(version=VERSION, audit=audit_records(records), files=files))
    return cache.sha256(tmp_path / "manifest.json")


def test_cache_load_rejects_changed_bytes(tmp_path, monkeypatch):
    digest = small_cache(tmp_path, monkeypatch)
    assert len(cache.load_cache(tmp_path, digest)["train"][1]) == 1
    with (tmp_path / "train.npy").open("ab") as stream:
        stream.write(b"unbound")
    with pytest.raises(ValueError, match="checksum"):
        cache.load_cache(tmp_path, digest)


def test_rehashed_wrong_authored_label_cannot_pass_recipe_check(tmp_path, monkeypatch):
    small_cache(tmp_path, monkeypatch)
    path = tmp_path / "dev.json"
    rows = json.loads(path.read_text())
    rows[0]["shape_index"] = 8
    path.write_text(json.dumps(rows))
    manifest_path = tmp_path / "manifest.json"
    manifest = json.loads(manifest_path.read_text())
    manifest["files"]["dev"]["json"] = cache.sha256(path)
    manifest_path.write_text(json.dumps(manifest))
    with pytest.raises(ValueError, match="labels"):
        cache.load_cache(tmp_path, cache.sha256(manifest_path))
