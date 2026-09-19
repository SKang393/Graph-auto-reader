# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Exercise V45 candidate admission without weights, private data, or sealed reads."""
import json
from pathlib import Path

import pytest

from ml.ocr.legend_coverage_db_head_v45 import prepare_candidate as candidate
from ml.ocr.legend_coverage_db_head_v45.tests.test_verify_export import _result


def _fixture(tmp_path, monkeypatch):
    def write(name, value):
        path = tmp_path / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value if isinstance(value, bytes) else candidate.canonical_json_bytes(value))
        return {"path": name, "sha256": candidate.sha256_file(path)}

    config = write(candidate.runner.CONFIG_PATH.as_posix(), {})
    historical = write(
        "artifacts/historical-capture.json",
        {
            "panels": [
                {
                    "panel_id": f"historical-{index}",
                    "split": "train" if index < 28 else "validation",
                    "tensor": {"sha256": f"{index + 1:064x}"},
                }
                for index in range(37)
            ]
        },
    )
    supplemental = write(
        "artifacts/supplemental-capture.json",
        {
            "panels": [
                {
                    "panel_id": f"supplemental-{index}",
                    "split": "train",
                    "tensor": {"sha256": f"{index + 100:064x}"},
                }
                for index in range(6)
            ]
        },
    )
    monkeypatch.setattr(candidate, "verify_bound_source_snapshot", lambda *args: None)
    model = write(f"artifacts/run/{candidate.MODEL_NAME}", b"fixture model")
    checkpoint = write("artifacts/run/selected-head.pt", b"fixture checkpoint")
    stage = {
        "schema": candidate.verify_export.STAGE_SCHEMA,
        "task": candidate.runner.TASK,
        "status": "trained_pending_export_parity_and_dev",
        "revision": candidate.runner.REVISION,
        "candidate_id": "P1",
        "production_approved": False,
        "private_reads": 0,
        "sealed_reads": 0,
        "training_authorization": {
            "task": candidate.runner.TASK,
            "revision": candidate.runner.REVISION,
            "candidate_id": "P1",
            "candidate_config_path": candidate.runner.CONFIG_PATH.as_posix(),
            "candidate_config_sha256": config["sha256"],
            "runner_source_bundle_sha256": "e" * 64,
            "runner_source_paths": sorted(
                path.as_posix() for path in candidate.runner.RUNNER_SOURCE_PATHS
            ),
            "source_snapshot_path": "artifacts/snapshot.json",
            "source_snapshot_sha256": "a" * 64,
        },
        "training": _result(),
        "model_sha256": model["sha256"],
        "checkpoint_sha256": checkpoint["sha256"],
        "feature_inventory_sha256": "b" * 64,
    }
    stage_file = write("artifacts/run/training-stage.json", stage)
    capture_rows = json.loads((tmp_path / historical["path"]).read_text())["panels"]
    capture_rows += json.loads((tmp_path / supplemental["path"]).read_text())["panels"]
    rows = [
        {
            "panel_id": row["panel_id"],
            "split": row["split"],
            "input_sha256": row["tensor"]["sha256"],
            "maximum_absolute_error": 0.0,
        }
        for row in capture_rows
    ]
    parity = {
        "schema": candidate.verify_export.PARITY_SCHEMA,
        "stage": stage_file,
        "source_sha256": candidate.sha256_file(Path(candidate.verify_export.__file__)),
        "model_sha256": model["sha256"],
        "checkpoint_sha256": checkpoint["sha256"],
        "feature_inventory_sha256": "b" * 64,
        "panel_count": 43,
        "train_panel_count": 34,
        "development_panel_count": 9,
        "tolerance": 1e-5,
        "torch_threads": 12,
        "onnx_threads": 1,
        "graph_optimization": "ORT_ENABLE_ALL",
        "optimizer_steps": 0,
        "private_reads": 0,
        "sealed_reads": 0,
        "production_approved": False,
        "passed": True,
        "panels": rows,
        "maximum_absolute_error": 0.0,
    }
    manifest = write(
        "artifacts/template/detector.json",
        {
            "preprocessing": {"unchanged": True},
            "postprocessing": {"unchanged": True},
            "execution_providers": ["CPUExecutionProvider"],
            "license": {"spdx": "Apache-2.0"},
        },
    )
    recognizer = write("artifacts/template/recognizer.onnx", b"fixture recognizer")
    recognizer_manifest = write("artifacts/template/recognizer.json", {"license": "Apache-2.0"})
    native = write("artifacts/template/native.dll", b"fixture native")
    assemblies = [
        write(f"artifacts/template/runtime/{name}.dll", f"fixture {name}".encode())
        | {"name": name}
        for name in sorted(candidate.ASSEMBLY_NAMES)
    ]
    license_file = write("LICENSES/fixture.txt", b"fixture license")
    base = write(
        "artifacts/template/candidate.json",
        {
            "schema": "graphreader.frozen-db-head-ocr-candidate.v1",
            "scope": "project-owned-synthetic-train-dev-unapproved-frozen-candidate",
            "production_approved": False,
            "training_input_ready": False,
            "composition_version": "original-db-head-candidate-v1",
            "native_path": native["path"],
            "native_sha256": native["sha256"],
            "native_scope": "reviewed-source-runtime-local-diagnostic",
            "execution_assemblies": assemblies,
            "license_inputs": [license_file],
            "detector": {
                "model_path": model["path"],
                "model_sha256": model["sha256"],
                "manifest_path": manifest["path"],
                "manifest_sha256": manifest["sha256"],
            },
            "recognizer": {
                "model_path": recognizer["path"],
                "model_sha256": recognizer["sha256"],
                "manifest_path": recognizer_manifest["path"],
                "manifest_sha256": recognizer_manifest["sha256"],
            },
        },
    )
    pinned = write("artifacts/template/pinned-candidate.json", json.loads((tmp_path / base["path"]).read_text()))
    monkeypatch.setattr(candidate, "PINNED_BASE_PATH", pinned["path"])
    monkeypatch.setattr(candidate, "PINNED_BASE_SHA256", pinned["sha256"])
    monkeypatch.setattr(candidate, "PARENT_SHA256", model["sha256"])
    monkeypatch.setattr(candidate, "RECOGNIZER_SHA256", recognizer["sha256"])
    monkeypatch.setattr(
        candidate.runner,
        "_config",
        lambda root: {
            "expected_runner_source_bundle_sha256": "e" * 64,
            "bound_files": {
                "capture_report": historical,
                "supplemental_capture_report": supplemental,
                "candidate_template": base,
            }
        },
    )
    state = {"template": base, "config": config}
    return write, stage_file, parity, native, state


def _prepare(tmp_path, write, stage, parity):
    bound = write("artifacts/parity.json", parity)
    return candidate.prepare(
        stage["path"], stage["sha256"], bound["path"], bound["sha256"],
        "artifacts/candidate", tmp_path,
    )


def _rewrite_template(tmp_path, write, state, mutation):
    descriptor = state["template"]
    value = json.loads((tmp_path / descriptor["path"]).read_text())
    mutation(value)
    descriptor.update(write(descriptor["path"], value))


def test_candidate_preserves_runtime_processing_and_unapproved_status(tmp_path, monkeypatch):
    write, stage, parity, _, _ = _fixture(tmp_path, monkeypatch)
    result = _prepare(tmp_path, write, stage, parity)
    path = tmp_path / result["path"]
    assert candidate.sha256_file(path) == result["sha256"]
    value = json.loads(path.read_text())
    manifest = json.loads((tmp_path / value["detector"]["manifest_path"]).read_text())
    assert value["production_approved"] is False
    assert value["training_input_ready"] is False
    assert value["composition_version"] == "original-db-head-candidate-v1"
    assert manifest["preprocessing"] == {"unchanged": True}
    assert manifest["postprocessing"] == {"unchanged": True}
    assert manifest["files"] == [Path(value["detector"]["model_path"]).name]
    assert value["detector"]["model_id"] == candidate.MODEL_ID
    assert len(value["license_inputs"]) == 2


@pytest.mark.parametrize(
    "defect",
    ["missing", "duplicate", "input", "error", "nonfinite", "boolean", "maximum", "passed", "source", "counts"],
)
def test_candidate_rejects_incomplete_or_invalid_43_panel_parity(tmp_path, monkeypatch, defect):
    write, stage, parity, _, _ = _fixture(tmp_path, monkeypatch)
    if defect == "missing":
        parity["panels"].pop()
    elif defect == "duplicate":
        parity["panels"][-1] = parity["panels"][0]
    elif defect == "input":
        parity["panels"][0]["input_sha256"] = "c" * 64
    elif defect == "error":
        parity["panels"][0]["maximum_absolute_error"] = 0.01
    elif defect == "nonfinite":
        parity["panels"][0]["maximum_absolute_error"] = float("inf")
    elif defect == "boolean":
        parity["panels"][0]["maximum_absolute_error"] = False
    elif defect == "maximum":
        parity["maximum_absolute_error"] = False
    elif defect == "passed":
        parity["passed"] = 1
    elif defect == "source":
        parity["source_sha256"] = "d" * 64
    else:
        parity["train_panel_count"] = 33
    with pytest.raises(ValueError):
        _prepare(tmp_path, write, stage, parity)
    assert not (tmp_path / "artifacts/candidate").exists()


def test_candidate_rejects_changed_runtime_before_copy(tmp_path, monkeypatch):
    write, stage, parity, native, _ = _fixture(tmp_path, monkeypatch)
    (tmp_path / native["path"]).write_bytes(b"changed")
    with pytest.raises(ValueError):
        _prepare(tmp_path, write, stage, parity)
    assert not (tmp_path / "artifacts/candidate").exists()


@pytest.mark.parametrize(
    "defect",
    [
        "manifest_option",
        "manifest_preprocessing",
        "manifest_postprocessing",
        "other_template_field",
        "assembly_count",
        "assembly_name",
        "assembly_directory",
    ],
)
def test_candidate_rejects_nonassembly_template_drift_and_wrong_runtime_inventory(
    tmp_path, monkeypatch, defect,
):
    write, stage, parity, _, state = _fixture(tmp_path, monkeypatch)
    template = json.loads((tmp_path / state["template"]["path"]).read_text())
    if defect.startswith("manifest_"):
        manifest_descriptor = template["detector"]
        manifest = json.loads((tmp_path / manifest_descriptor["manifest_path"]).read_text())
        if defect == "manifest_option":
            manifest["execution_providers"] = ["ChangedExecutionProvider"]
        elif defect == "manifest_preprocessing":
            manifest["preprocessing"] = {"changed": True}
        else:
            manifest["postprocessing"] = {"changed": True}
        rebound = write(manifest_descriptor["manifest_path"], manifest)
        _rewrite_template(
            tmp_path,
            write,
            state,
            lambda value: value["detector"].update(manifest_sha256=rebound["sha256"]),
        )
    elif defect == "other_template_field":
        _rewrite_template(tmp_path, write, state, lambda value: value.update(unexpected=True))
    elif defect == "assembly_count":
        _rewrite_template(tmp_path, write, state, lambda value: value["execution_assemblies"].pop())
    elif defect == "assembly_name":
        _rewrite_template(
            tmp_path,
            write,
            state,
            lambda value: value["execution_assemblies"][0].update(name="Unexpected"),
        )
    else:
        moved = write("artifacts/template/other/GraphReader.App.dll", b"moved fixture")
        def move_one(value):
            row = next(item for item in value["execution_assemblies"] if item["name"] == "GraphReader.App")
            row.update(path=moved["path"], sha256=moved["sha256"])
        _rewrite_template(tmp_path, write, state, move_one)
    with pytest.raises(ValueError):
        _prepare(tmp_path, write, stage, parity)
    assert not (tmp_path / "artifacts/candidate").exists()


@pytest.mark.parametrize("field", ["task", "revision", "candidate_id", "candidate_config_path",
                                     "runner_source_bundle_sha256", "runner_source_paths"])
def test_candidate_rejects_incomplete_training_authorization_identity(
    tmp_path, monkeypatch, field,
):
    write, stage, parity, _, _ = _fixture(tmp_path, monkeypatch)
    document = json.loads((tmp_path / stage["path"]).read_text())
    document["training_authorization"][field] = "changed"
    stage = write(stage["path"], document)
    parity["stage"] = stage
    with pytest.raises(ValueError, match="authorization identity"):
        _prepare(tmp_path, write, stage, parity)
    assert not (tmp_path / "artifacts/candidate").exists()


@pytest.mark.parametrize(
    "key,value",
    [
        ("schema", "other"),
        ("task", "other"),
        ("production_approved", True),
        ("sealed_reads", False),
        ("private_reads", 1),
    ],
)
def test_candidate_rejects_wrong_training_scope(tmp_path, monkeypatch, key, value):
    write, stage, parity, _, _ = _fixture(tmp_path, monkeypatch)
    document = json.loads((tmp_path / stage["path"]).read_text())
    document[key] = value
    stage = write(stage["path"], document)
    parity["stage"] = stage
    with pytest.raises(ValueError):
        _prepare(tmp_path, write, stage, parity)
    assert not (tmp_path / "artifacts/candidate").exists()
