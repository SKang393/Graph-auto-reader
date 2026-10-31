# SPDX-License-Identifier: Apache-2.0
import importlib.util
import json
import hashlib
import inspect
from pathlib import Path

import pytest

PACKAGE = Path(__file__).parent


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


adapter = load("truth_association_under_test", PACKAGE / "truth_association.py")
association = load("full_association_under_test", PACKAGE / "association.py")
labels = load("pinned_labels_under_test", Path(__file__).parents[2] / "ml" / "synthetic" /
             "marker_context_catalog_v1" / "labels.py")


def fake_fixture():
    protocol_sources, sidecar_rows, capture_sources = [], [], []
    for index in range(64):
        source_id = f"source-{index:02d}"
        scene_id = f"scene-{index:02d}"
        image_sha = f"{index + 1:064x}"
        scene = {"scene_id": scene_id, "fixture": index}
        scene_sha = adapter.digest(adapter.canonical(scene))
        protocol_sources.append({"source_id": source_id, "scene_id": scene_id,
                                 "scene_sha256": scene_sha, "image_sha256": image_sha,
                                 "case_id": f"case-{index:02d}"})
        annotation = {
            "scene_id": scene_id,
            "coordinate_space": "original_pixels",
            "canvas": {"width": 100, "height": 40},
            "contextual_negative_profile": {
                "id": "synthetic-contextual-negatives-v1", "split": "train",
                "source_scene_id": scene_id, "source_scene_canonical_sha256": scene_sha,
            },
            "panels": [], "hard_negatives": [],
        }
        overlay = {"scene_id": scene_id, "scene_sha256": scene_sha, "split": "train",
                   "status": "review_only", "optimization_allowed": False,
                   "materialized": True, "center": [90.0, 30.0]}
        sidecar_rows.append({"case_id": f"case-{index:02d}", "image_sha256": image_sha,
                             "scene": scene, "annotation": annotation, "overlay": overlay})
        capture_sources.append({"source_id": source_id, "capture_status": "completed",
                                "panel_status_counts": {"completed": 1, "failed": 0, "not_processed": 0},
                                "failure_type_counts": {}, "candidates": []})
    raw = adapter.canonical({"schema": "marker-context-train-truth-v5", "mode": "full",
                             "training_approved": False, "sources": sidecar_rows})
    return protocol_sources, capture_sources, raw


def test_real_association_protocol_is_review_pending_and_hash_bound_without_truth_read():
    protocol_path = PACKAGE / "real-association-protocol-v1.json"
    protocol_raw = protocol_path.read_bytes()
    protocol = json.loads(protocol_raw)
    assert protocol["schema"] == "graphreader.marker-context-full-train-real-association-protocol.v1"
    assert protocol["status"] == "prepared_for_independent_review_not_executed"
    assert protocol["truth_sha256"] == "6b768cc45f0b1d97b3b21ceff41c86766a081b3d22d4dd03c26e34ae0b384951"
    assert protocol["capture_report_sha256"] == "c46e2a909df60d8665a7121c0a60a2bf0f6a64e60ce36b36dad19d40b8ff8de3"
    assert protocol["match_tolerance_px"] == 2.0
    assert protocol["candidate_statuses_review_only"] is True
    assert protocol["real_truth_access_enabled"] is False
    assert protocol["training_approved"] is False and protocol["optimizer_steps"] == 0
    adapter_raw = (PACKAGE / "truth_association.py").read_bytes()
    assert hashlib.sha256(adapter_raw).hexdigest() == protocol["truth_adapter_sha256"]
    validator_raw = (PACKAGE / "associate.py").read_bytes()
    assert hashlib.sha256(validator_raw).hexdigest() == protocol["capture_validator_sha256"]
    assert hashlib.sha256((PACKAGE / "association.py").read_bytes()).hexdigest() == protocol["association_sha256"]
    launcher = json.loads((PACKAGE / "launcher-protocol.json").read_text(encoding="utf-8"))
    versioned_path = PACKAGE / "real-association-protocol-v2.json"
    versioned_raw = versioned_path.read_bytes()
    versioned = json.loads(versioned_raw)
    assert versioned["real_truth_access_enabled"] is False
    assert versioned["output_path"] == "artifacts/goal22-runs/marker-context-train-association-v11-real-review-pending"
    final_path = PACKAGE / "real-association-protocol-v4.json"
    final_raw = final_path.read_bytes()
    final = json.loads(final_raw)
    final_runner = (PACKAGE / "associate_real_v13.py").read_bytes()
    assert hashlib.sha256(final_raw).hexdigest() == "90c248f4f0241c6e5f556d23d5291e0515ba1b264a7bb68c7ec7c922d893553e"
    assert hashlib.sha256(final_runner).hexdigest() == "6aaa985c08adf598304286acb7d7807ef45604fd394cf7077986b05a3ed58c66"
    assert final["real_truth_access_enabled"] is False
    assert final["output_path"] == "artifacts/goal22-runs/marker-context-train-association-v13-real-review-pending"
    armed_path = PACKAGE / "real-association-protocol-v5.json"
    armed_raw = armed_path.read_bytes()
    armed = json.loads(armed_raw)
    assert hashlib.sha256(armed_raw).hexdigest() == "a6033faf2cfc8d70aa8fc8f578245796f65be5e7e979d8a6913517afc8683ca3"
    assert hashlib.sha256((PACKAGE / "associate_real_v14.py").read_bytes()).hexdigest() == "85e1ab49919511ba98aa805c30c8b9f5ef162b360c1bd37b4e5f6d516795f794"
    assert armed["real_truth_access_enabled"] is True
    assert armed["status"] == "prepared_for_independent_review_not_executed"
    assert armed["output_path"] == "artifacts/goal22-runs/marker-context-train-association-v14-real-review-pending"
    reviewed_path = PACKAGE / "real-association-protocol-v6.json"
    reviewed_raw = reviewed_path.read_bytes()
    reviewed = json.loads(reviewed_raw)
    reviewed_runner = (PACKAGE / "associate_real_v15.py").read_bytes()
    assert hashlib.sha256(reviewed_raw).hexdigest() == launcher["real_association_protocol_sha256"]
    assert launcher["real_association_protocol_path"] == "tools/MarkerContextFullAssociationV1/real-association-protocol-v6.json"
    assert hashlib.sha256(reviewed_runner).hexdigest() == launcher["runner_sha256"]
    assert launcher["runner_path"] == "tools/MarkerContextFullAssociationV1/associate_real_v15.py"
    assert reviewed["real_truth_access_enabled"] is True
    assert reviewed["status"] == "reviewed_and_authorized"
    assert reviewed["output_path"] == "artifacts/goal22-runs/marker-context-train-association-v15-real-reviewed"
    parameters = inspect.signature(adapter.build_truth_by_source).parameters
    assert "sidecar_raw" in parameters and "sidecar_path" not in parameters and "truth_reader" not in parameters


def test_fake_sidecar_converts_only_review_only_empty_truth_without_file_access():
    protocol_sources, capture_sources, raw = fake_fixture()
    result = adapter.associate_truth(raw, adapter.digest(raw), protocol_sources, capture_sources,
                                     labels.classify, association)
    assert result["source_count"] == 64
    assert result["candidate_count"] == 0
    assert result["truth_marker_count"] == 0
    assert result["overlay_count"] == 64
    assert result["trainable_rows"] == 0
    assert result["negative_training_rows"] == 0
    assert result["training_approved"] is False
    assert result["optimizer_steps"] == 0


def test_fake_sidecar_hash_and_source_binding_fail_closed():
    protocol_sources, capture_sources, raw = fake_fixture()
    with pytest.raises(ValueError, match="digest"):
        adapter.build_truth_by_source(raw, "0" * 64, protocol_sources, labels.classify)
    bad_sources = [dict(row) for row in protocol_sources]
    bad_sources[0]["scene_sha256"] = "0" * 64
    with pytest.raises(ValueError, match="provenance"):
        adapter.build_truth_by_source(raw, adapter.digest(raw), bad_sources, labels.classify)


def test_fake_sidecar_overlay_outside_source_frame_rejected():
    protocol_sources, _, raw = fake_fixture()
    sidecar = json.loads(raw)
    sidecar["sources"][0]["overlay"]["center"] = [1200, 20]
    tampered = adapter.canonical(sidecar)
    with pytest.raises(ValueError, match="outside the authenticated source raster"):
        adapter.build_truth_by_source(tampered, adapter.digest(tampered), protocol_sources, labels.classify)


def test_freeze_is_persisted_before_fake_sidecar_reader(tmp_path):
    protocol_sources, capture_sources, fake_sidecar = fake_fixture()
    freeze_path = tmp_path / "pretruth-freeze.json"
    freeze = {"schema": "graphreader.marker-context-full-train-association-pretruth-freeze.v1",
              "capture_report_sha256": "a" * 64, "source_count": 64,
              "match_tolerance_px": 2.0,
              "match_metric": "euclidean_original_source_pixels_inclusive",
              "truth_content_read": False, "training_approved": False, "optimizer_steps": 0}
    order = []

    def persist(raw):
        with freeze_path.open("xb") as stream:
            stream.write(raw)
            stream.flush()
            import os
            os.fsync(stream.fileno())
        order.append("freeze")

    def read_fake_sidecar():
        assert freeze_path.is_file()
        assert json.loads(freeze_path.read_bytes()) == freeze
        order.append("read_fake")
        return fake_sidecar

    result = adapter.associate_after_freeze(freeze, persist, read_fake_sidecar, adapter.digest(fake_sidecar),
                                            protocol_sources, capture_sources, labels.classify, association)
    assert order == ["freeze", "read_fake"]
    assert result["source_count"] == 64 and result["truth_marker_count"] == 0
    assert result["training_approved"] is False and result["optimizer_steps"] == 0


def test_failed_freeze_never_calls_truth_reader():
    protocol_sources, capture_sources, fake_sidecar = fake_fixture()
    freeze = {"schema": "graphreader.marker-context-full-train-association-pretruth-freeze.v1",
              "source_count": 64, "match_tolerance_px": 2.0,
              "match_metric": "euclidean_original_source_pixels_inclusive",
              "truth_content_read": False, "training_approved": False, "optimizer_steps": 0}
    called = []
    with pytest.raises(OSError, match="freeze unavailable"):
        adapter.associate_after_freeze(freeze, lambda _: (_ for _ in ()).throw(OSError("freeze unavailable")),
                                       lambda: called.append(True) or fake_sidecar, adapter.digest(fake_sidecar),
                                       protocol_sources, capture_sources, labels.classify, association)
    assert called == []
