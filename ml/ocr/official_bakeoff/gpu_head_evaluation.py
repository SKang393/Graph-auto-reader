# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Adapt one authenticated GPU-head export to the existing fixed open-dev evaluator.

No optimizer or model session runs here. The historical 37-panel capture stays
immutable; its existing source-snapshot-only evaluation request is reused.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
from copy import deepcopy
import importlib
import json
import math
from pathlib import Path
import shutil
import sys

from ml.markers.gate_seal import (canonical_json_bytes, sha256_file,
                                 source_bundle_sha256, verify_bound_source_snapshot)

ROOT = Path(__file__).resolve().parents[3]
CONFIG = Path("ml/ocr/official_bakeoff/marker_style_gpu_candidate.json")
REVISION = "marker_style_coverage_gpu_head_v1"
MODEL_ID = "graph-marker-style-gpu-db-head-v1-p1"
TASK = "ocr-detection"
ASSEMBLIES = ("GraphReader.SyntheticRuntimeEvidence", "GraphReader.App", "GraphReader.Ocr", "GraphReader.Inference")
OLD_PREFIX = "C:\\Users\\Sungwoo\\Desktop\\Graph-auto-reader\\"
SOURCE_N_COMMIT = "6d9af5a56b741a89fc7d5683090d52fe1405d074"
SOURCE_N_RUNTIME_SHA256 = "d5d90061666ebd367f71b6caab81f054f99ae3e9308c678b67ce96a1abfe1b16"
SOURCE_N_APP_DLL_SHA256 = "2b514958a8664c5a9529eaba8c9fba1939e2e595c7dba687719617560d8ee009"
APP_SOURCE = "src/GraphReader.App/Integration/Workflow/ProductionAutomaticDetectionAdapter.cs"
FROZEN_APP_SOURCE_SHA256 = "d46d619cdf475e1adf24fe8090d732db8c198dd7e2f1e82739557b435523377a"
SOURCE_N_APP_SOURCE_SHA256 = "7f63b2ffb339cd17f881132a26799b3f0d521b0de38e5c383bf5a00193353ec3"
EVALUATOR_SOURCE = "tools/GraphReader.SyntheticRuntimeEvidence/OfficialHeadCandidateEvaluation.cs"
LEGACY_EVALUATOR_SOURCE_SHA256 = "1575e1f47669a0cb7f644d3318100384ac4cb2bf583cd60685fdcb8bd4c1560d"
SOURCE_N_EVALUATOR_SOURCE_SHA256 = "6b787b433cebaa736011144e8615970aeec144b83c6e4a6c02055dcaf2a8dac3"
DIAGNOSTIC_MODEL_SHA256 = "f063fc9dbe874f1de68da5aa51168392aa9d8b9d29a3e3af259b3bd8614102eb"
DIAGNOSTIC_CHECKPOINT_SHA256 = "0db4ad9300c6987c2b22c1fdcb821de938c973aa8d2ee69cbaca27111c9ee2fc"
DIAGNOSTIC_CONFIG_SHA256 = "b78a88c8b7b28c0fdb1a5a1b49783decfc77991134b21fefa0dd242bb14c8c36"
DIAGNOSTIC_SOURCE_BUNDLE_SHA256 = "469866eb243b2fed096c02036651566f8287af592c52b24f359b8eaeea761b19"


def _framework():
    tools = str(ROOT / "tools/GraphReader.SyntheticRuntimeEvidence")
    if tools not in sys.path:
        sys.path.insert(0, tools)
    return importlib.import_module("score_legend_coverage_candidate")


def _inside(root: Path, path: Path) -> Path:
    path = (path if path.is_absolute() else root / path).resolve()
    if not path.is_relative_to(root.resolve()):
        raise ValueError("Evaluation artifact escaped its explicit source root")
    return path


def _read(root: Path, path: Path, digest: str):
    path = _inside(root, path)
    if sha256_file(path) != digest:
        raise ValueError(f"Evaluation input checksum changed: {path.name}")
    return json.loads(path.read_bytes())


def _descriptor(root: Path, path: Path) -> dict:
    return {"path": _inside(root, path).relative_to(root).as_posix(), "sha256": sha256_file(path)}


def _stage(stage_root: Path, stage_path: Path, stage_sha: str, config_sha: str):
    config = _read(stage_root, CONFIG, config_sha)
    stage = _read(stage_root, stage_path, stage_sha)
    fixed = {"schema": "graphreader.marker-style-gpu-training-stage.v1", "status": "trained_pending_open_dev",
             "task": TASK, "revision": REVISION, "candidate_id": "P1", "private_reads": 0,
             "sealed_reads": 0, "production_approved": False}
    if any(type(stage.get(k)) is not type(v) or stage.get(k) != v for k, v in fixed.items()):
        raise ValueError("GPU stage identity or scope changed")
    if (config.get("revision") != REVISION or config.get("task") != TASK or config.get("candidate_id") != "P1"
        or config.get("candidate_count") != 1 or config.get("sealed_budget") != 0
        or config.get("synthetic_only") is not True or config.get("private_data") is not False
        or config.get("sealed_data") is not False):
        raise ValueError("GPU configuration identity or scope changed")
    auth = stage["authorization"]
    expected = {"task": TASK, "revision": REVISION, "candidate_id": "P1",
                "candidate_config_path": CONFIG.as_posix(), "candidate_config_sha256": config_sha,
                "runner_source_bundle_sha256": config["expected_runner_source_bundle_sha256"]}
    if any(auth.get(k) != v for k, v in expected.items()):
        raise ValueError("GPU stage authorization changed")
    paths = [Path(p) for p in auth["runner_source_paths"]]
    if (not paths or any(p.is_absolute() or ".." in p.parts for p in paths)
        or source_bundle_sha256(stage_root, paths) != config["expected_runner_source_bundle_sha256"]):
        raise ValueError("Authorized runner source inventory changed")
    verify_bound_source_snapshot(stage_root, _inside(stage_root, Path(auth["source_snapshot_path"])), auth["source_snapshot_sha256"])
    training = stage["training"]
    required = {"epochs": 240, "optimizer_steps": 43 * 240, "panel_count": 43,
                "seed": 20260939, "batch_size": 1, "learning_rate": 0.0001,
                "weight_decay": 0.0001, "dice_epsilon": 1e-6,
                "torch_backend": "rocm-rx6600xt-cuda0-float64-conv0"}
    if any(training.get(k) != v for k, v in required.items()):
        raise ValueError("GPU training schedule or backend changed")
    from .frozen_head_training import TRAINABLE_PARAMETER_NAMES
    if (tuple(training["trainable_parameter_names"]) != TRAINABLE_PARAMETER_NAMES
        or training["positive_panel_count"] + training["empty_positive_panels"] != 43):
        raise ValueError("GPU trainable parameter or panel inventory changed")
    epochs = training["epoch_losses"]
    if (len(epochs) != 240 or [r["epoch"] for r in epochs] != list(range(1, 241))
        or [r["optimizer_steps"] for r in epochs] != [43 * i for i in range(1, 241)]):
        raise ValueError("GPU epoch inventory changed")
    losses = [r["full_train_loss"] for r in epochs]
    if any(type(v) not in (int, float) or not math.isfinite(v) for v in losses):
        raise ValueError("GPU training loss is not finite")
    if training["selected_epoch"] != min(range(240), key=lambda i: losses[i]) + 1:
        raise ValueError("Checkpoint was not the earliest minimum full-train loss")
    if training["frozen_batch_norm_sha256_before"] != training["frozen_batch_norm_sha256_after"]:
        raise ValueError("Frozen batch normalization changed")
    error = stage["trained_onnx_parity_maximum_absolute_error"]
    if type(error) not in (int, float) or not math.isfinite(error) or not 0 <= error <= 1e-5:
        raise ValueError("Trained ONNX parity failed")
    patch = stage["patch"]
    from .frozen_trunk_head import TRAINABLE_CONSTANT_SHAPES, REVIEWED_DETECTOR_SHA256
    if (patch["source_sha256"] != REVIEWED_DETECTOR_SHA256 or patch["output_sha256"] != stage["model_sha256"]
        or tuple(patch["changed_constants"]) != tuple(TRAINABLE_CONSTANT_SHAPES)
        or patch["unchanged_trainable_constants"]):
        raise ValueError("Export did not replace exactly the nine reviewed constants")
    stage_path = _inside(stage_root, stage_path)
    model, checkpoint = stage_path.parent / "candidate.onnx", stage_path.parent / "selected-head.pt"
    if sha256_file(model) != stage["model_sha256"] or sha256_file(checkpoint) != stage["checkpoint_sha256"]:
        raise ValueError("GPU model or checkpoint checksum changed")
    return config, stage, model


def _verify_export_identity(parent_path: Path, model_path: Path, checkpoint_path: Path, parent_sha: str) -> dict:
    """Read existing bytes only; verify checkpoint, nine constants, four BN buffers and unchanged graph."""
    import numpy as np
    import onnx
    import torch
    from . import frozen_trunk_head as trunk
    parent_bytes, parent = trunk._load_reviewed_model(parent_path, parent_sha)
    _, model = trunk._load_reviewed_model(model_path, sha256_file(model_path))
    original, actual = trunk._extract_required_constants(parent), trunk._extract_required_constants(model)
    bundle = trunk.extract_frozen_trunk_head(parent_path, parent_sha)
    bundle.head.load_state_dict(torch.load(checkpoint_path, map_location="cpu", weights_only=True), strict=True)
    trained = bundle.head.trainable_constant_values()
    bn = bundle.head.frozen_batch_norm_values()
    for name in trunk.TRAINABLE_CONSTANT_SHAPES:
        if not np.isfinite(trained[name]).all() or not np.array_equal(trained[name], actual[name]):
            raise ValueError("Diagnostic checkpoint and model head constants differ")
    for name in trunk.FROZEN_BATCH_NORM_SHAPES:
        if not np.array_equal(bn[name], original[name]) or not np.array_equal(actual[name], original[name]):
            raise ValueError("Diagnostic frozen batch-normalization constants changed")
    before, after = trunk._semantic_constant_hashes(parent), trunk._semantic_constant_hashes(model)
    if set(before) != set(after) or {name for name in before if before[name] != after[name]} != set(trunk.TRAINABLE_CONSTANT_SHAPES):
        raise ValueError("Diagnostic export changed outside its nine trainable constants")
    restored = onnx.ModelProto()
    restored.CopyFrom(model)
    for name in trunk.TRAINABLE_CONSTANT_SHAPES:
        trunk._replace_constant(restored, name, original[name])
    if restored.SerializeToString() != parent_bytes:
        raise ValueError("Diagnostic export changed graph structure outside the head")
    return {"trainable_constants_match": 9, "frozen_bn_constants_match": 4}


def _diagnostic(stage_root: Path, receipt_path: Path, receipt_sha: str, historical_root: Path):
    receipt = _read(stage_root, receipt_path, receipt_sha)
    fixed = {"schema": "graphreader.marker-style-gpu-void-diagnostic.v1", "scope": "diagnostic-only",
             "candidate_acceptance": False, "private_reads": 0, "sealed_reads": 0, "production_approved": False,
             "selected_epoch": "not recorded", "loss_history": "not recorded"}
    if any(type(receipt.get(k)) is not type(v) or receipt.get(k) != v for k, v in fixed.items()):
        raise ValueError("Diagnostic receipt scope or unknown training history changed")
    records = {}
    for key in ("config", "source_snapshot", "opened_record", "void_record", "launch_log", "model", "checkpoint"):
        ref = receipt[key]
        path = _inside(stage_root, Path(ref["path"]))
        if sha256_file(path) != ref["sha256"]:
            raise ValueError(f"Diagnostic {key} checksum changed")
        records[key] = path
    if (receipt["config"]["sha256"] != DIAGNOSTIC_CONFIG_SHA256
        or receipt["model"]["sha256"] != DIAGNOSTIC_MODEL_SHA256
        or receipt["checkpoint"]["sha256"] != DIAGNOSTIC_CHECKPOINT_SHA256
        or receipt["source_bundle_sha256"] != DIAGNOSTIC_SOURCE_BUNDLE_SHA256
        or receipt["source_commit"] != SOURCE_N_COMMIT):
        raise ValueError("Diagnostic input is not the one authorized frozen P1 export")
    opened, void, snapshot, config = (json.loads(records[k].read_bytes()) for k in
                                     ("opened_record", "void_record", "source_snapshot", "config"))
    auth = opened["binding"]
    if (opened.get("status") != "opened" or void.get("status") != "void" or void.get("binding") != auth
        or void.get("sealed_split_read") is not False or void.get("budget_consumed") is not False
        or auth.get("task") != TASK or auth.get("revision") != REVISION or auth.get("candidate_id") != "P1"
        or auth.get("candidate_config_path") != CONFIG.as_posix()
        or auth.get("candidate_config_sha256") != receipt["config"]["sha256"]
        or auth.get("source_snapshot_sha256") != receipt["source_snapshot"]["sha256"]
        or auth.get("runner_source_bundle_sha256") != DIAGNOSTIC_SOURCE_BUNDLE_SHA256
        or snapshot.get("base_commit") != SOURCE_N_COMMIT
        or snapshot.get("identity") != {"task": TASK, "revision": REVISION, "candidate_id": "P1"}):
        raise ValueError("Diagnostic void, authorization or archived snapshot identity changed")
    # The runner was repaired after this void. Authenticate the archived pre-run
    # source hashes, without pretending the current checkout still has those bytes.
    saved = {row["path"]: row["sha256"] for row in snapshot["sources"]}
    paths = auth["runner_source_paths"]
    if len(saved) != len(snapshot["sources"]) or len(set(paths)) != len(paths):
        raise ValueError("Diagnostic archived source inventory repeats")
    from hashlib import sha256
    bundle = sha256("".join(f"{p}={saved[p]}\n" for p in sorted(paths)).encode()).hexdigest()
    if bundle != DIAGNOSTIC_SOURCE_BUNDLE_SHA256 or config.get("expected_runner_source_bundle_sha256") != bundle:
        raise ValueError("Diagnostic archived source bundle changed")
    parity = receipt["parity"]
    if (parity.get("absolute_only_passed") is not False or parity.get("mixed_tolerance_passed") is not False
        or parity.get("absolute_tolerance") != 1e-5 or parity.get("mixed_tolerance_atol") != 1e-5
        or parity.get("mixed_tolerance_rtol") != 1.3e-6):
        raise ValueError("Diagnostic failed export parity record changed")
    parent_ref = config["historical_bound_files"]["parent_model"]
    from .frozen_trunk_head import REVIEWED_DETECTOR_SHA256
    parent = _inside(historical_root, Path(parent_ref["path"]))
    if parent_ref["sha256"] != REVIEWED_DETECTOR_SHA256 or sha256_file(parent) != REVIEWED_DETECTOR_SHA256:
        raise ValueError("Diagnostic parent is not the reviewed source model")
    identity = _verify_export_identity(parent, records["model"], records["checkpoint"], REVIEWED_DETECTOR_SHA256)
    if identity != receipt["export_identity"]:
        raise ValueError("Diagnostic head identity receipt changed")
    return config, {"model_sha256": receipt["model"]["sha256"], "patch": {"source_sha256": REVIEWED_DETECTOR_SHA256},
        "diagnostic_receipt": receipt, "export_identity": identity}, records["model"]


def _prepare(*, stage_root: Path, stage_path: Path, stage_sha256: str, config_sha256: str,
            historical_root: Path, runtime_root: Path, runtime_snapshot: Path,
            runtime_snapshot_sha256: str, output: Path, diagnostic: bool = False) -> dict:
    stage_root, historical_root, runtime_root = stage_root.resolve(), historical_root.resolve(), runtime_root.resolve()
    output = _inside(historical_root / "artifacts", output if output.is_absolute() else historical_root / output)
    if output.exists():
        raise ValueError("Use a new evaluation directory")
    config, stage, model = (_diagnostic(stage_root, stage_path, stage_sha256, historical_root) if diagnostic
                            else _stage(stage_root, stage_path, stage_sha256, config_sha256))
    framework = _framework()
    original_ref = config["historical_bound_files"]["capture_request"]
    original = _read(historical_root, Path(original_ref["path"]), original_ref["sha256"])
    if original_ref["sha256"] != framework.ORIGINAL_REQUEST_SHA256:
        raise ValueError("Original capture request is not frozen")
    framework._validate_historical_source(historical_root)
    derived = deepcopy(original)
    derived["capture_source"]["path"] = framework.HISTORICAL_SOURCE.as_posix()
    framework._validate_historical_request_delta(original, derived)
    template_ref = config["historical_bound_files"]["candidate_template"]
    framework._require_candidate_template_binding(historical_root, historical_root / template_ref["path"], template_ref["sha256"])
    template = _read(historical_root, Path(template_ref["path"]), template_ref["sha256"])
    manifest_ref = template["detector"]
    manifest = _read(historical_root, Path(manifest_ref["manifest_path"]), manifest_ref["manifest_sha256"])
    if (manifest.get("license", {}).get("spdx") != "Apache-2.0" or manifest.get("license", {}).get("reviewed") is not True
        or manifest.get("commercial_use") is not True or manifest.get("redistribution") is not True):
        raise ValueError("Parent manifest license is not reviewed for redistribution")
    if runtime_snapshot_sha256 != SOURCE_N_RUNTIME_SHA256:
        raise ValueError("Evaluator must use the exact reviewed source-N runtime snapshot")
    snapshot = _read(runtime_root, runtime_snapshot, runtime_snapshot_sha256)
    app_source = _inside(stage_root, Path(APP_SOURCE))
    if snapshot.get("source_commit") != SOURCE_N_COMMIT or sha256_file(app_source) != SOURCE_N_APP_SOURCE_SHA256:
        raise ValueError("Reviewed source-N App source or commit changed")
    evaluator_source = _inside(stage_root, Path(EVALUATOR_SOURCE))
    if sha256_file(evaluator_source) != SOURCE_N_EVALUATOR_SOURCE_SHA256:
        raise ValueError("Reviewed source-N evaluator source changed")
    runtime_files = []
    for row in snapshot["files"]:
        path = _inside(runtime_root, Path(row["path"]))
        if sha256_file(path) != row["sha256"] or path.stat().st_size != row["byte_count"]:
            raise ValueError("Frozen evaluator runtime bytes changed")
        runtime_files.append(path)
    if not runtime_files or len({p.parent for p in runtime_files}) != 1 or len(set(runtime_files)) != len(runtime_files):
        raise ValueError("Frozen evaluator runtime inventory changed")
    names = {p.name for p in runtime_files}
    if not {*(name + ".dll" for name in ASSEMBLIES), "GraphReader.SyntheticRuntimeEvidence.deps.json",
            "GraphReader.SyntheticRuntimeEvidence.runtimeconfig.json"} <= names:
        raise ValueError("Frozen evaluator runtime is incomplete")
    if sha256_file(next(p for p in runtime_files if p.name == "GraphReader.App.dll")) != SOURCE_N_APP_DLL_SHA256:
        raise ValueError("Reviewed source-N App DLL changed")
    output.mkdir(parents=True)
    binary = output / "runtime"
    binary.mkdir()
    for path in runtime_files:
        shutil.copyfile(path, binary / path.name)
        if sha256_file(binary / path.name) != sha256_file(path):
            raise ValueError("Evaluator runtime copy changed")
    target = output / "candidate.onnx"
    shutil.copyfile(model, target)
    if sha256_file(target) != stage["model_sha256"]:
        raise ValueError("Exported model copy changed")
    model_id = MODEL_ID + "-diagnostic" if diagnostic else MODEL_ID
    manifest.update(model_id=model_id, model_version="0.0.1", sha256=stage["model_sha256"], files=[target.name])
    manifest["benchmarks"] = [{"scope": template["scope"], "production_approved": False,
        "accuracy_status": "diagnostic-only-export-parity-failed" if diagnostic else "pending-fixed-open-dev",
        "parent_model_sha256": stage["patch"]["source_sha256"]}]
    if diagnostic:
        manifest["benchmarks"][0].update(diagnostic_receipt_sha256=stage_sha256, export_parity_failed=True, acceptance_blocked=True)
    else:
        manifest["benchmarks"][0].update(training_stage_sha256=stage_sha256,
            trained_onnx_parity_maximum_absolute_error=stage["trained_onnx_parity_maximum_absolute_error"])
    manifest_path = output / "detector.json"
    manifest_path.write_bytes(canonical_json_bytes(manifest))
    notice = output / "MODIFICATION-NOTICE.txt"
    notice.write_text("Derived from Apache-2.0 PaddleOCR PP-OCRv5 mobile detector.\n"
        "Copyright 2026 Sungwoo Kang. Nine DB head constants trained on 43 project-owned synthetic train panels.\n"
        "Frozen trunk and batch-normalization statistics unchanged. Not production approved.\n"
        + ("Diagnostic only. Export parity failed; acceptance blocked. Selected epoch and losses not recorded.\n" if diagnostic else "") +
        "Original PaddlePaddle license and notices remain in the candidate license inputs.\n", encoding="utf-8")
    candidate = deepcopy(template)
    candidate["execution_assemblies"] = [{"name": name, **_descriptor(historical_root, binary / (name + ".dll"))} for name in ASSEMBLIES]
    candidate["detector"] = {"model_path": target.relative_to(historical_root).as_posix(), "model_id": model_id,
        "model_version": "0.0.1", "model_sha256": stage["model_sha256"],
        "manifest_path": manifest_path.relative_to(historical_root).as_posix(), "manifest_sha256": sha256_file(manifest_path)}
    candidate["license_inputs"].append(_descriptor(historical_root, notice))
    candidate_path = output / "candidate.json"
    candidate_path.write_bytes(canonical_json_bytes(candidate))
    framework.metric.geometry._validate_candidate(historical_root, candidate_path, sha256_file(candidate_path))
    request_path = output / "evaluation-request.json"
    request_path.write_bytes(canonical_json_bytes(derived))
    source_copy = output / "metric-sources/ProductionAutomaticDetectionAdapter.cs"
    source_copy.parent.mkdir()
    shutil.copyfile(app_source, source_copy)
    if sha256_file(source_copy) != SOURCE_N_APP_SOURCE_SHA256:
        raise ValueError("Reviewed App source copy changed")
    evaluator_copy = source_copy.parent / "OfficialHeadCandidateEvaluation.cs"
    shutil.copyfile(evaluator_source, evaluator_copy)
    if sha256_file(evaluator_copy) != SOURCE_N_EVALUATOR_SOURCE_SHA256:
        raise ValueError("Reviewed evaluator source copy changed")
    preparation = {"schema": "graphreader.gpu-head-open-dev-preparation.v1", "revision": REVISION,
        "candidate_id": "P1", "training_stage": {"path": str(_inside(stage_root, stage_path)), "sha256": stage_sha256},
        "stage_root": str(stage_root), "historical_root": str(historical_root),
        "config_sha256": config_sha256, "original_request": original_ref,
        "capture_report": config["historical_bound_files"]["capture_report"],
        "candidate_template": template_ref, "candidate": _descriptor(historical_root, candidate_path),
        "evaluation_request": _descriptor(historical_root, request_path),
        "runtime_snapshot": {"path": str(_inside(runtime_root, runtime_snapshot)), "sha256": runtime_snapshot_sha256},
        "reviewed_source_substitutions": [{"path": APP_SOURCE, "frozen_sha256": FROZEN_APP_SOURCE_SHA256,
            "actual_sha256": SOURCE_N_APP_SOURCE_SHA256, "snapshot": _descriptor(historical_root, source_copy),
            "source_commit": SOURCE_N_COMMIT}, {"path": EVALUATOR_SOURCE, "frozen_sha256": LEGACY_EVALUATOR_SOURCE_SHA256,
            "actual_sha256": SOURCE_N_EVALUATOR_SOURCE_SHA256, "snapshot": _descriptor(historical_root, evaluator_copy),
            "source_commit": SOURCE_N_COMMIT}],
        "evaluator_dll": _descriptor(historical_root, binary / "GraphReader.SyntheticRuntimeEvidence.dll"),
        "primary_open_dev": {"source_count": 3, "panel_count": 9, "truth_count": 183},
        "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0, "production_approved": False}
    if diagnostic:
        preparation["schema"] = "graphreader.gpu-head-open-dev-diagnostic-preparation.v1"
        preparation["diagnostic_receipt"] = preparation.pop("training_stage")
        preparation.update(export_parity_failed=True, acceptance_blocked=True, candidate_acceptance=False,
            selected_epoch="not recorded", loss_history="not recorded", parity=stage["diagnostic_receipt"]["parity"],
            source_verification_mode="archived_pre_run_snapshot_hashes_not_current_runner_bytes")
    (output / "preparation.json").write_bytes(canonical_json_bytes(preparation))
    return preparation


def prepare(**kwargs):
    return _prepare(**kwargs)


def prepare_diagnostic(*, stage_root: Path, receipt_path: Path, receipt_sha256: str, **kwargs):
    return _prepare(stage_root=stage_root, stage_path=receipt_path, stage_sha256=receipt_sha256,
                    config_sha256=DIAGNOSTIC_CONFIG_SHA256, diagnostic=True, **kwargs)


@contextmanager
def _historical_capture_paths(framework, root):
    """Resolve only the four existing mirror allowlist entries after the checkout move."""
    geometry = framework.metric.geometry
    original = geometry._read_historical_capture_artifact
    primary = root.parent.parent.parent

    def read(checked_root, value, digest, label):
        if isinstance(value, str) and value.startswith(OLD_PREFIX):
            relative = Path(value[len(OLD_PREFIX):])
            if relative not in geometry.HISTORICAL_CAPTURE_ARTIFACT_MIRRORS:
                raise ValueError("Historical capture path is not in its existing mirror allowlist")
            value = primary / relative
        return original(checked_root, value, digest, label)

    try:
        geometry._read_historical_capture_artifact = read
        yield
    finally:
        geometry._read_historical_capture_artifact = original


def score(*, historical_root: Path, preparation_path: Path, preparation_sha256: str,
          evaluation_path: Path, evaluation_sha256: str, output: Path) -> dict:
    """Reuse frozen source-global matching on exactly the existing 183 open-dev truths."""
    root = historical_root.resolve()
    preparation = _read(root, preparation_path, preparation_sha256)
    diagnostic = preparation.get("schema") == "graphreader.gpu-head-open-dev-diagnostic-preparation.v1"
    if (preparation.get("schema") not in ("graphreader.gpu-head-open-dev-preparation.v1", "graphreader.gpu-head-open-dev-diagnostic-preparation.v1")
        or preparation.get("revision") != REVISION or preparation.get("candidate_id") != "P1"
        or preparation.get("historical_root") != str(root)
        or preparation.get("primary_open_dev") != {"source_count": 3, "panel_count": 9, "truth_count": 183}):
        raise ValueError("Open-dev preparation identity changed")
    output = _inside(root / "artifacts", output if output.is_absolute() else root / output)
    if output.exists():
        raise ValueError("Use a new score output")
    stage_ref = preparation["diagnostic_receipt" if diagnostic else "training_stage"]
    if diagnostic:
        if preparation.get("export_parity_failed") is not True or preparation.get("acceptance_blocked") is not True or preparation.get("candidate_acceptance") is not False:
            raise ValueError("Diagnostic acceptance block changed")
        _, stage, _ = _diagnostic(Path(preparation["stage_root"]), Path(stage_ref["path"]), stage_ref["sha256"], root)
    else:
        _, stage, _ = _stage(Path(preparation["stage_root"]), Path(stage_ref["path"]), stage_ref["sha256"], preparation["config_sha256"])
    framework = _framework()
    geometry = framework.metric.geometry
    sources = _scoring_sources(framework, root, preparation)
    candidate_ref = preparation["candidate"]
    candidate, _ = geometry._validate_candidate(root, root / candidate_ref["path"], candidate_ref["sha256"])
    if candidate["detector"]["model_sha256"] != stage["model_sha256"]:
        raise ValueError("Scored candidate is not the validated GPU export")
    original_ref, derived_ref, capture_ref = (preparation[k] for k in ("original_request", "evaluation_request", "capture_report"))
    if capture_ref["sha256"] != framework.ORIGINAL_CAPTURE_SHA256:
        raise ValueError("Historical capture report changed")
    with _historical_capture_paths(framework, root):
        original, _, panels = framework._validate_historical_requests(root,
            root / original_ref["path"], original_ref["sha256"], root / derived_ref["path"], derived_ref["sha256"])
        geometry._prevalidate_capture_report(root, root / capture_ref["path"], capture_ref["sha256"],
            root / original_ref["path"], original_ref["sha256"], original, panels)
    evidence = geometry._validate_evaluation(root, _inside(root, evaluation_path), evaluation_sha256,
        root / derived_ref["path"], derived_ref["sha256"], root / candidate_ref["path"], candidate_ref["sha256"], panels, candidate)
    # Existing authentication finishes before any truth regeneration.
    preflight = framework.historical_truth._read(root, original["binding"])
    dev = framework.historical_truth._fixed_dev_truth(root, preflight)
    dev_panels = [p for p in evidence.panels.values() if p.split == "validation"]
    if (len(dev_panels) != 9 or len(dev) != 183
        or len({t.source_sha256 for t in dev}) != 3
        or {p.source_sha256 for p in dev_panels} != {t.source_sha256 for t in dev}):
        raise ValueError("Fixed open-dev source, panel or truth inventory changed")
    raw, recognized, failures = framework.metric._geometry_and_failure_metrics(evidence, {"validation": dev})
    bars_sha, precision, recall = geometry._load_acceptance_bar(root)
    result = {"schema": "graphreader.gpu-head-open-dev-score.v1", "status": "diagnostic_only_unapproved",
        "revision": REVISION, "candidate_id": "P1", "source_count": 3, "panel_count": 9, "truth_count": 183,
        "preparation": _descriptor(root, _inside(root, preparation_path)),
        "candidate": candidate_ref, "evaluation": {"path": _inside(root, evaluation_path).relative_to(root).as_posix(), "sha256": evaluation_sha256},
        "raw_detector_geometry": raw, "successfully_recognized_region_geometry": recognized,
        "recognition_failures": failures, "scoring_source_bindings": sources,
        "reviewed_source_substitutions": preparation["reviewed_source_substitutions"],
        "existing_metric_sha256": framework.METRIC_SHA256,
        "acceptance_bar_reference": {"path": geometry.ACCEPTANCE_BARS_PATH.as_posix(), "sha256": bars_sha,
            "text_region_detection_precision_minimum": precision, "text_region_detection_recall_minimum": recall},
        "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0, "production_approved": False,
        "scope": "current_source_n_runtime_candidate_on_fixed_open_dev_no_checkpoint_or_threshold_selection",
        "pure_training_only_delta_claimed": False}
    if diagnostic:
        result.update(schema="graphreader.gpu-head-open-dev-diagnostic-score.v1", status="diagnostic_only_export_parity_failed",
            export_parity_failed=True, acceptance_blocked=True, candidate_acceptance=False,
            selected_epoch="not recorded", loss_history="not recorded", parity=preparation["parity"])
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as stream:
        stream.write(canonical_json_bytes(result))
    return result


def _scoring_sources(framework, root: Path, preparation: dict) -> dict:
    """Keep all eleven matching/truth helpers frozen and bind two reviewed source-N receipts."""
    expected = dict(framework.metric.geometry.SOURCE_BINDINGS)
    for key, value in framework.metric.SOURCE_BINDINGS.items():
        if key in expected and expected[key] != value:
            raise ValueError("Frozen scoring source bindings disagree")
        expected[key] = value
    expected[framework.CURRENT_EVALUATOR_SOURCE] = framework.CURRENT_EVALUATOR_SOURCE_SHA256
    rows = preparation.get("reviewed_source_substitutions")
    if not isinstance(rows, list) or len(rows) != 2:
        raise ValueError("Exactly the two reviewed source-N substitutions are required")
    reviewed = {APP_SOURCE: (FROZEN_APP_SOURCE_SHA256, SOURCE_N_APP_SOURCE_SHA256),
                EVALUATOR_SOURCE: (LEGACY_EVALUATOR_SOURCE_SHA256, SOURCE_N_EVALUATOR_SOURCE_SHA256)}
    by_path = {row["path"]: row for row in rows}
    if set(by_path) != set(reviewed):
        raise ValueError("Reviewed source-N receipt inventory changed")
    for key, (frozen, actual) in reviewed.items():
        row = by_path[key]
        if (row.get("frozen_sha256") != frozen or row.get("actual_sha256") != actual
            or row.get("source_commit") != SOURCE_N_COMMIT or expected.get(key) != frozen):
            raise ValueError("Reviewed source-N substitution changed")
    observed = {}
    for key, value in expected.items():
        if key in reviewed:
            actual = reviewed[key][1]
            ref = by_path[key]["snapshot"]
            if ref.get("sha256") != actual:
                raise ValueError("Reviewed source-N snapshot receipt changed")
            framework.metric.geometry._read_exact(root, ref["path"], actual, "reviewed source-N snapshot")
            observed[key] = actual
        else:
            framework.metric.geometry._read_exact(root, key, value, f"frozen scoring dependency {key}")
            observed[key] = value
    return observed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    prepare_command = commands.add_parser("prepare")
    for name in ("stage-root", "stage", "stage-sha256", "config-sha256", "historical-root", "runtime-root",
                 "runtime-snapshot", "runtime-snapshot-sha256", "output"):
        prepare_command.add_argument("--" + name, required=True)
    score_command = commands.add_parser("score")
    for name in ("historical-root", "preparation", "preparation-sha256", "evaluation", "evaluation-sha256", "output"):
        score_command.add_argument("--" + name, required=True)
    diagnostic_command = commands.add_parser("prepare-diagnostic")
    for name in ("stage-root", "receipt", "receipt-sha256", "historical-root", "runtime-root",
                 "runtime-snapshot", "runtime-snapshot-sha256", "output"):
        diagnostic_command.add_argument("--" + name, required=True)
    args = parser.parse_args()
    if args.command == "prepare":
        result = prepare(stage_root=Path(args.stage_root), stage_path=Path(args.stage), stage_sha256=args.stage_sha256,
            config_sha256=args.config_sha256, historical_root=Path(args.historical_root), runtime_root=Path(args.runtime_root),
            runtime_snapshot=Path(args.runtime_snapshot), runtime_snapshot_sha256=args.runtime_snapshot_sha256, output=Path(args.output))
    elif args.command == "prepare-diagnostic":
        result = prepare_diagnostic(stage_root=Path(args.stage_root), receipt_path=Path(args.receipt), receipt_sha256=args.receipt_sha256,
            historical_root=Path(args.historical_root), runtime_root=Path(args.runtime_root), runtime_snapshot=Path(args.runtime_snapshot),
            runtime_snapshot_sha256=args.runtime_snapshot_sha256, output=Path(args.output))
    else:
        result = score(historical_root=Path(args.historical_root), preparation_path=Path(args.preparation),
            preparation_sha256=args.preparation_sha256, evaluation_path=Path(args.evaluation),
            evaluation_sha256=args.evaluation_sha256, output=Path(args.output))
    print(json.dumps(result))


if __name__ == "__main__":
    main()
