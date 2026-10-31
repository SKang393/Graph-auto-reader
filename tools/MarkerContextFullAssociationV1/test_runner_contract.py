# SPDX-License-Identifier: Apache-2.0
import importlib.util
import json
from pathlib import Path
import hashlib
import struct
import pytest

PACKAGE = Path(__file__).parent

def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

runner = load("association_runner_under_test", PACKAGE / "associate.py")


def test_fixed_protocol_and_association_implementation_hashes_match_runner_pins():
    protocol = (PACKAGE / "protocol.json").read_bytes()
    association = (PACKAGE / "association.py").read_bytes()
    assert hashlib.sha256(protocol).hexdigest() == runner.PROTOCOL_SHA256
    assert hashlib.sha256(association).hexdigest() == runner.ASSOCIATION_SHA256
    parsed = json.loads(protocol)
    assert parsed["source_count"] == 64
    assert parsed["match_tolerance_px"] == 2.0
    assert "review_isolated" in parsed["candidate_review_only_statuses"]
    assert "matched_isolated_review_only" in parsed["truth_review_only_statuses"]
    assert parsed["training_approved"] is False and parsed["optimizer_steps"] == 0


def test_actual_capture_report_metadata_uses_bound_protocol_sha_without_truth_access():
    association_protocol = json.loads((PACKAGE / "protocol.json").read_text(encoding="utf-8"))
    capture_protocol_path = runner.ROOT / association_protocol["capture_protocol_path"]
    capture_binding_path = runner.ROOT / association_protocol["capture_binding_path"]
    report_path = runner.ROOT / association_protocol["capture_output_path"] / "report.json"
    paths = (capture_protocol_path, capture_binding_path, report_path)
    if any(not path.exists() for path in paths):
        pytest.skip("fixed full synthetic capture metadata is not present in this checkout")
    for path in paths:
        assert path.is_file(), f"present capture metadata path is not a regular file: {path}"

    capture_protocol_raw = capture_protocol_path.read_bytes()
    capture_binding_raw = capture_binding_path.read_bytes()
    report = json.loads(report_path.read_bytes())
    capture_protocol = json.loads(capture_protocol_raw)
    capture_binding = json.loads(capture_binding_raw)
    capture_protocol_sha = hashlib.sha256(capture_protocol_raw).hexdigest()
    assert capture_protocol_sha == association_protocol["capture_protocol_sha256"]
    assert capture_protocol["source_count"] == 64
    assert capture_binding["protocol_sha256"] == capture_protocol_sha
    assert report["protocol_sha256"] == capture_protocol_sha
    assert "capture_protocol_sha256" not in report
    assert report["source_accounting_complete"] is True
    assert report["truth_content_read"] is False
    assert report["private_input_supplied"] is False
    assert report["sealed_input_supplied"] is False
    assert report["dev_input_supplied"] is False
    assert report["events"]
    event_path = report_path.parent / "panels" / report["events"][0]["file"]
    assert event_path.is_file()
    event = json.loads(event_path.read_bytes())
    center = event["center_markers"][0]["center"]
    assert set(center) == {"x", "y", "is_finite"}
    assert center["is_finite"] is True
    assert runner._marker_center(center, event["frame"]["width"], event["frame"]["height"])


def test_marker_center_accepts_only_production_finite_metadata():
    assert runner._marker_center({"x": 3.0, "y": 4.0, "is_finite": True}, 10, 10) == [3.0, 4.0]
    with pytest.raises(ValueError):
        runner._marker_center({"x": 3.0, "y": 4.0, "is_finite": False}, 10, 10)
    with pytest.raises(ValueError):
        runner._marker_center({"x": 3.0, "y": 4.0, "finite": True}, 10, 10)


def test_fake_sidecar_fixture_is_explicitly_empty_and_requires_full_unique_inventory():
    source_ids = [f"synthetic-source-{index:02d}" for index in range(64)]
    fixture = runner._fake_truth_fixture(source_ids)
    assert fixture["schema"] == "graphreader.marker-context-association-fake-sidecar.v1"
    assert fixture["scope"] == "integration-test-only-empty-truth-no-source-annotations"
    assert fixture["training_approved"] is False
    assert [row["source_id"] for row in fixture["sources"]] == source_ids
    assert all(row == {"source_id": source_id, "markers": [], "overlays": []}
               for row, source_id in zip(fixture["sources"], source_ids))
    with pytest.raises(ValueError):
        runner._fake_truth_fixture(source_ids[:-1])
    with pytest.raises(ValueError):
        runner._fake_truth_fixture(source_ids[:-1] + [source_ids[0]])


def valid_report():
    return {
        "schema": "graphreader.marker-context-bound-input-full-capture-report.v1",
        "source_count": 64,
        "source_accounting_complete": True,
        "status": "completed_all_sources",
        "training_approved": False,
        "marker_classifier_model_loaded": False,
        "classification_executed": False,
        "truth_content_read": False,
        "private_input_supplied": False,
        "sealed_input_supplied": False,
        "dev_input_supplied": False,
        "optimizer_steps": 0,
        "model_inference_started": True,
        "initialization_probe_completed": True,
    }


def test_capture_completion_guard_accepts_complete_failure_accounting_only():
    assert runner.require_capture_complete(valid_report()) is True
    import copy
    for field, value in (("source_accounting_complete", False), ("source_count", 63),
                         ("truth_content_read", True), ("private_input_supplied", True),
                         ("training_approved", True), ("model_inference_started", False)):
        bad = copy.deepcopy(valid_report())
        bad[field] = value
        with pytest.raises(ValueError):
            runner.require_capture_complete(bad)


def test_pretruth_freeze_is_durable_before_sidecar_reader_is_invoked(tmp_path):
    freeze_path = tmp_path / "pretruth-freeze.json"
    freeze = b'{"match_tolerance_px":2,"source_count":64}'
    called = []

    def read_truth():
        assert freeze_path.is_file()
        assert freeze_path.read_bytes() == freeze
        called.append(True)
        return b"synthetic-fixture-truth"

    assert runner.persist_freeze_before_truth(freeze_path, freeze, read_truth) == b"synthetic-fixture-truth"
    assert called == [True]


def test_pretruth_freeze_refuses_overwrite_before_truth_reader(tmp_path):
    freeze_path = tmp_path / "pretruth-freeze.json"
    freeze_path.write_bytes(b"existing")
    called = []
    with pytest.raises(FileExistsError):
        runner.persist_freeze_before_truth(freeze_path, b"new", lambda: called.append(True))
    assert not called


def test_captured_float_tensor_rehash_matches_little_endian_float32_bytes():
    values = [0.0] * (32 * 32)
    expected = hashlib.sha256(struct.pack("<f", 0.0) * len(values)).hexdigest()
    runner._verify_tensor(values, expected)
    with pytest.raises(ValueError, match="hash differs"):
        runner._verify_tensor(values, "0" * 64)
    with pytest.raises(ValueError, match="32x32 single-channel"):
        runner._verify_tensor([0.0], expected)
