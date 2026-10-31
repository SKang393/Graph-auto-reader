# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unregistered single-use entrypoint. No protocol or candidate is created here."""
from __future__ import annotations

import argparse
import contextlib
from dataclasses import asdict
from hashlib import sha256
from io import BytesIO
from pathlib import Path
import json

import torch

from ml.markers.gate_seal import FIRST_READ_INTENT_NAME, source_bundle_sha256
from ml.markers.training_budget import CANONICAL_LEDGER_PATH, acquire_training_candidate, void_candidate
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..dual_scale_context_v1.runner import configure_project_git
from ..real_positive_context_v1 import runner_foundation as prior
from ..real_positive_context_v1.checkpoint_writer import _ALLOWED_BASES, _cpu_state, _project_file
from ..real_positive_context_v1.entrypoint import _Discard, _load_key, _assert_key_acl
from ..real_positive_context_v1.model import RealPositiveCenterNet
from ..real_positive_context_v1.objective import _require_training_gpu
from ..real_positive_context_v1.positive_archive import PositiveArchive, _open_read_only_no_writers
from ..real_positive_context_v1.registered_source import (RegisteredMixedSource, _ARCHIVE_KEYS, _artifact_path,
    _batch_count, _digest, _json, _locked_bytes, _receipt)
from ..real_positive_context_v1.schedule import ALGORITHM as MIXED_ALGORITHM, FixedSchedule
from ..real_positive_preservation_v1.entrypoint import _locked_family
from ..real_positive_preservation_v1.component_source import ComponentNegativeStore, DATA, REPORT, V37, VIEWS
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from . import runner
from .source import SyntheticTrainFeatures

ROOT = prior.PROJECT_ROOT
CONFIG = runner.LOCAL / "p1.json"
PROTOCOL = runner.PROTOCOL
# The runner requires its own bundle as a subset. The entrypoint itself is also
# locked and included here, without changing the independently reviewed runner.
REQUIRED_SOURCES = tuple(dict.fromkeys((*runner.REQUIRED_SOURCES, runner.LOCAL / "entrypoint.py")))


def _valid_recipe(container: dict) -> bool:
    component, directional = container.get("component_retention"), container.get("directional_preservation")
    return (type(component) is dict and component == runner.preservation.expected_component_config() and
        type(component.get("weight")) is float and all(type(component.get(k)) is int for k in
            ("seed", "batch_size", "rows_per_view", "batches_per_epoch", "epochs")) and
        type(directional) is dict and directional == runner.expected_directional_config() and
        all(type(directional.get(k)) is float for k in ("component_weight", "family_weight")) and
        all(type(directional.get(k)) is int for k in ("seed", "batch_size", "epochs", "batches_per_epoch", "total_steps",
            "component_rows_per_view", "family_rows_per_view")))


def _check_registration(ledger: dict, config: dict, config_raw: bytes, protocol_raw: bytes, bundle_sha256: str) -> None:
    entries = [entry for entry in ledger.get("revisions", []) if isinstance(entry, dict) and
               entry.get("task") == "marker-center" and entry.get("revision") == runner.REVISION]
    if len(entries) != 1:
        raise ValueError("Bidirectional candidate is not uniquely registered")
    entry = entries[0]
    if (entry.get("execution_authorized") is not True or entry.get("authorized_candidate_id") != "P1" or
            entry.get("status") != "candidate_1_preregistered" or entry.get("preregistered_candidate_ids") != ["P1"] or
            "P1" in entry.get("consumed_candidate_ids", []) or entry.get("sealed_runs_authorized") != 0 or
            entry.get("candidate_config_paths", {}).get("P1") != CONFIG.as_posix() or
            entry.get("candidate_config_sha256", {}).get("P1") != sha256(config_raw).hexdigest() or
            entry.get("protocol_path") != PROTOCOL.as_posix() or entry.get("protocol_sha256") != sha256(protocol_raw).hexdigest() or
            entry.get("optimizer_steps_expected") != runner.TOTAL_STEPS):
        raise ValueError("Canonical bidirectional registration, config or protocol differs")
    protocol = _json(protocol_raw)
    if ((protocol.get("schema"), protocol.get("task"), protocol.get("revision"), protocol.get("candidate_id")) !=
            ("graphreader.marker-bidirectional-preservation-protocol.v1", "marker-center", runner.REVISION, "P1") or
            not _valid_recipe(protocol) or protocol.get("production_approval") is not False or protocol.get("sealed_runs_authorized") != 0):
        raise ValueError("Bidirectional protocol or recipe differs")
    if ((config.get("task"), config.get("revision"), config.get("candidate_id")) != ("marker-center", runner.REVISION, "P1") or
            config.get("recipe") != asdict(prior.DRAFT_RECIPE) or not _valid_recipe(config) or
            config.get("expected_runner_source_bundle_sha256") != bundle_sha256 or
            config.get("source_checkpoint_sha256") != prior.V37_CHECKPOINT_SHA256 or
            config.get("synthetic_feature_report_sha256") != prior.V37_FEATURE_REPORT_SHA256 or
            type(config.get("optimizer_steps_expected")) is not int or config["optimizer_steps_expected"] != runner.TOTAL_STEPS or
            config.get("production_approval") is not False or config.get("sealed_runs_authorized") != 0):
        raise ValueError("Bidirectional config or source bundle differs")
    schedule = config.get("schedule")
    if (type(schedule) is not dict or schedule.get("algorithm") != MIXED_ALGORITHM or
            schedule.get("torch_version") != str(torch.__version__) or
            type(schedule.get("synthetic_population_count")) is not int or schedule["synthetic_population_count"] != 36212 or
            type(schedule.get("real_positive_population_count")) is not int or not 0 < schedule["real_positive_population_count"] <= 4528 or
            any(not _digest(schedule.get(name)) for name in ("synthetic_population_sha256", "real_positive_population_sha256")) or
            schedule.get("synthetic_source_binding_sha256") != prior.V37_FEATURE_REPORT_SHA256 or
            not _digest(config.get("real_positive_binding_sha256")) or
            schedule.get("real_source_binding_sha256") != config.get("real_positive_binding_sha256")):
        raise ValueError("Mixed schedule binding differs")


def _preflight() -> tuple[dict, bytes]:
    ledger = _json(_locked_bytes(ROOT / CANONICAL_LEDGER_PATH, 8 * 1024 * 1024))
    config_raw = _locked_bytes(prior._inside_project(ROOT, ROOT / CONFIG), 4 * 1024 * 1024)
    protocol_raw = _locked_bytes(prior._inside_project(ROOT, ROOT / PROTOCOL), 4 * 1024 * 1024)
    config = _json(config_raw)
    _check_registration(ledger, config, config_raw, protocol_raw, source_bundle_sha256(ROOT, REQUIRED_SOURCES))
    output = config.get("candidate_checkpoint_path")
    if type(output) is not str or not output or Path(output).is_absolute():
        raise ValueError("Candidate checkpoint path is invalid")
    source, destination = _artifact_path(config.get("source_checkpoint_path")), _project_file(ROOT / output, existing=False)
    if source not in _ALLOWED_BASES or destination.suffix != ".pt" or destination == source or destination.exists():
        raise ValueError("Candidate checkpoint paths are unavailable")
    spec = config.get("real_positive_archive")
    if (type(spec) is not dict or set(spec) != _ARCHIVE_KEYS or
            any(not _digest(spec[name]) for name in ("receipt_sha256", "file_sha256", "key_sha256")) or
            not _digest(spec["run_id"], 32) or type(spec["provenance_sha256"]) is not list or
            len(spec["provenance_sha256"]) != 8 or any(not _digest(value) for value in spec["provenance_sha256"])):
        raise ValueError("Archive binding is invalid")
    key = _load_key(config)
    _receipt(_locked_bytes(_artifact_path(spec["receipt_path"]), 4 * 1024 * 1024), spec, config, key)
    _artifact_path(spec["archive_path"])
    return config, key


def _locked_synthetic_stores(stack: contextlib.ExitStack) -> None:
    """Keep all four component and family train feature/label files locked."""
    stream = stack.enter_context(_open_read_only_no_writers(DATA / REPORT))
    report = prior._v37_report(stream.read(), prior.V37_FEATURE_REPORT_SHA256)
    refs = [report["model"]["path"], V37.as_posix()]
    for item in report["groups"]:
        if item["scope"].split(":")[0] in {"component", "family"}:
            refs.extend((item["labels"]["path"], item["features"]["path"]))
    for relative in sorted(set(refs)):
        path = (DATA / relative).resolve(strict=True)
        if not path.is_relative_to(DATA.resolve(strict=True)):
            raise ValueError("Synthetic source escaped its bound project root")
        stack.enter_context(_open_read_only_no_writers(path))
    components = {view: ComponentNegativeStore.open_authenticated(view) for view in VIEWS}
    positive = {view: SyntheticTrainFeatures.load("component_positive", view) for view in VIEWS}
    negative = {view: SyntheticTrainFeatures.load("family_negative", view) for view in VIEWS}
    if any(store.fixture or store.view != view or store.count != runner.ROWS_PER_VIEW for view, store in components.items()):
        raise ValueError("Component-negative preflight is not authenticated")
    runner._validate_directional_stores(positive, negative)


def _gpu_model(base_raw: bytes):
    if len(base_raw) > 512 * 1024 * 1024 or sha256(base_raw).hexdigest() != prior.V37_CHECKPOINT_SHA256:
        raise ValueError("V37 source checkpoint changed")
    base = torch.load(BytesIO(base_raw), map_location="cpu", weights_only=True)
    local = SpatialCenterNet(ScaleClassifierNet(ModelConfig()))
    local.load_state_dict(base["state_dict"], strict=True)
    model = RealPositiveCenterNet(local).to("cuda:0")
    _require_training_gpu(*model.parameters(), *model.buffers())
    _cpu_state(model, base["state_dict"])
    optimizer = torch.optim.AdamW(model.residual.parameters(), lr=prior.DRAFT_RECIPE.learning_rate,
                                  weight_decay=prior.DRAFT_RECIPE.weight_decay, capturable=True)
    prior._require_fresh_state(model, optimizer)
    with torch.inference_mode():
        check = model(torch.zeros((1, 3, 33, 33), dtype=torch.float32, device="cuda:0"))
        if not torch.isfinite(check).all():
            raise RuntimeError("GPU model preflight produced invalid output")
    torch.cuda.synchronize(torch.device("cuda:0"))
    return model, optimizer


def _prepare_inputs(config: dict, key: bytes, stack: contextlib.ExitStack):
    ledger_stream = stack.enter_context(_open_read_only_no_writers(ROOT / CANONICAL_LEDGER_PATH))
    config_stream = stack.enter_context(_open_read_only_no_writers(ROOT / CONFIG))
    sources = {path: stack.enter_context(_open_read_only_no_writers(ROOT / path)) for path in REQUIRED_SOURCES}
    ledger = _json(ledger_stream.read(8 * 1024 * 1024 + 1))
    config_raw = config_stream.read(4 * 1024 * 1024 + 1)
    protocol_raw = sources[PROTOCOL].read(4 * 1024 * 1024 + 1)
    if _json(config_raw) != config:
        raise ValueError("Locked candidate configuration changed")
    _check_registration(ledger, config, config_raw, protocol_raw, source_bundle_sha256(ROOT, REQUIRED_SOURCES))
    spec = config["real_positive_archive"]
    key_relative = config.get("archive_key_path")
    if type(key_relative) is not str or not key_relative or Path(key_relative).is_absolute():
        raise ValueError("The admitted project-local key path is missing")
    key_stream = stack.enter_context(_open_read_only_no_writers(_project_file(ROOT / key_relative, existing=True)))
    _assert_key_acl(key_stream)
    held_key = key_stream.read(33)
    if len(held_key) != 32 or held_key != key or sha256(held_key).hexdigest() != spec["key_sha256"]:
        raise ValueError("The locked archive key differs from preflight")
    receipt_stream = stack.enter_context(_open_read_only_no_writers(_artifact_path(spec["receipt_path"])))
    receipt = _receipt(receipt_stream.read(4 * 1024 * 1024 + 1), spec, config, key)
    archive = stack.enter_context(PositiveArchive(_artifact_path(spec["archive_path"]), key=key,
        file_sha256=bytes.fromhex(spec["file_sha256"]), run_id=bytes.fromhex(spec["run_id"]),
        provenance=tuple(bytes.fromhex(value) for value in spec["provenance_sha256"])))
    if archive.count != receipt["positive_count"] or _batch_count(archive) != receipt["batch_count"]:
        raise ValueError("Archive receipt counts differ")
    base_stream = stack.enter_context(_open_read_only_no_writers(_artifact_path(config["source_checkpoint_path"])))
    model, optimizer = _gpu_model(base_stream.read(512 * 1024 * 1024 + 1))
    _locked_synthetic_stores(stack)
    family, _ = _locked_family(stack)
    schedule = FixedSchedule.from_bound_config(config_raw, sha256(config_raw).hexdigest(),
        synthetic_ids=family.row_identities, real_positive_ids=archive.row_identities)
    if schedule.total_steps != runner.TOTAL_STEPS:
        raise ValueError("Mixed source schedule differs")
    return model, optimizer


def run_candidate() -> dict:
    config, key = _preflight()
    _require_training_gpu()
    configure_project_git()
    directory = ROOT / "ml/markers/training-seals/marker-center" / runner.REVISION / "P1"
    forbidden = ("entrypoint-intent.json", "opened.json", "source-snapshot.json", "void.json", "result.json",
                 "consumed.json", "optimizer-run-intent.json", "optimizer-journal.jsonl", "optimizer-run-outcome.json",
                 FIRST_READ_INTENT_NAME)
    if any((directory / name).exists() for name in forbidden):
        raise RuntimeError("Candidate has an existing single-use attempt")
    with contextlib.ExitStack() as locks:
        model, optimizer = _prepare_inputs(config, key, locks)
        directory.mkdir(parents=True, exist_ok=True)
        if any((directory / name).exists() for name in forbidden):
            raise RuntimeError("Candidate has an existing single-use attempt")
        prior._write_new_durable(directory / "entrypoint-intent.json", {
            "schema": "graphreader.bidirectional-preservation-entrypoint-intent.v1", "task": "marker-center",
            "revision": runner.REVISION, "candidate_id": "P1", "config_sha256": sha256((ROOT / CONFIG).read_bytes()).hexdigest(),
            "resume_authorized": False})
        authorization = acquire_training_candidate(ROOT, task="marker-center", revision=runner.REVISION,
            candidate_id="P1", config_path=CONFIG, runner_source_paths=REQUIRED_SOURCES)
        try:
            with RegisteredMixedSource.open_admitted(authorization, archive_key=key) as source:
                result = runner.run_training(model, optimizer, source, authorization)
            if result.get("status") != "training_complete_unapproved" or not _digest(result.get("checkpoint_sha256")):
                raise RuntimeError("Candidate did not produce a verified checkpoint")
            return {"status": result["status"], "checkpoint_sha256": result["checkpoint_sha256"]}
        except BaseException:
            if not (directory / "optimizer-run-intent.json").exists() and not (directory / "result.json").exists():
                try:
                    void_candidate(authorization, RuntimeError("entrypoint_pre_optimizer_failure"))
                except Exception:
                    pass
            raise


def main() -> int:
    argparse.ArgumentParser(description="Run only a separately preregistered bidirectional trial").parse_args()
    try:
        with contextlib.redirect_stdout(_Discard()), contextlib.redirect_stderr(_Discard()):
            result = run_candidate()
    except Exception as error:
        print(json.dumps({"status": "failed", "error_type": type(error).__name__}, sort_keys=True))
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
