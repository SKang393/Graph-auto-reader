# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""P3: controlled 60-epoch extension of the source-uniform train-only recipe."""

from __future__ import annotations

from datetime import datetime, timezone
from hashlib import sha256
import importlib.util
import json
from pathlib import Path
import stat
import sys
import time
from typing import Any

ROOT = Path(__file__).resolve().parents[5]
HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
P2_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/train_classifier_p2.py"
P2_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/p2.json"
P1_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/train_classifier.py"
ADMISSION_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/diagnose_train_split.py"
HELPER_REL = "ml/ocr/fragmented_xtick_detector_v3/gpu_train.py"
P3_CONFIG_REL = "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/ink_stroke_classifier_v1/p3.json"
OUTPUT_RELATIVE = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p3-sourceuniform-60ep-rx6600xt"
P2_RUNNER_SHA = "dc86c5ee004794519c6c1ac7fa21699b06a31637914de3ab827353c828e3aa5d"
P2_CONFIG_SHA = "b230e743cadb3d057aef443b3da1dc5821e2da2ce88ecc20ae3175725069aa42"
P2_REPORT_SHA = "5152d2101d51a60254d72b7c8d42f8b900a6258ea7a309fbb1c7fa51bb559263"
P1_TRAIN_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p1-train-diagnostic-rx6600xt-r4/report.json"
P2_REPORT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p2-sourceuniform-rx6600xt/report.json"
P2_CHECKPOINT_REL = "artifacts/stroke-seeded-xtick-v1-ink-stroke-classifier-p2-sourceuniform-rx6600xt/checkpoint.pt"
CHECKPOINT_REL = OUTPUT_RELATIVE + "/checkpoint.pt"
_RUNNER_SHA = ""


class P3Error(RuntimeError):
    pass


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def read_bound(relative: str, limit: int) -> bytes:
    rel = Path(relative)
    if rel.is_absolute() or ".." in rel.parts or limit <= 0:
        raise P3Error("invalid bounded project path")
    current = ROOT.resolve(strict=True)
    for index, part in enumerate(rel.parts):
        current = current / part
        info = current.lstat()
        attributes = getattr(info, "st_file_attributes", 0)
        if stat.S_ISLNK(info.st_mode) or attributes & 0x400:
            raise P3Error(f"reparse path rejected: {relative}")
        last = index == len(rel.parts) - 1
        if not last and not stat.S_ISDIR(info.st_mode):
            raise P3Error(f"non-directory path component: {relative}")
        if last and (not stat.S_ISREG(info.st_mode) or info.st_size > limit):
            raise P3Error(f"input is not a bounded regular file: {relative}")
    with current.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise P3Error(f"input grew beyond bound: {relative}")
    return data


def _load_exact(relative: str, expected_sha: str, module_name: str):
    raw = read_bound(relative, 4_000_000)
    actual = digest(raw)
    if actual != expected_sha:
        raise P3Error(f"pinned source mismatch: {relative}")
    path = ROOT / relative
    spec = importlib.util.spec_from_file_location(module_name, path)
    if spec is None:
        raise P3Error(f"cannot construct source module: {relative}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    exec(compile(raw, str(path), "exec"), module.__dict__)
    if Path(module.__file__).resolve() != path.resolve() or digest(read_bound(relative, 4_000_000)) != actual:
        raise P3Error(f"source identity changed during execution: {relative}")
    return module, actual


def _namespace(name: str):
    return __import__(name, fromlist=["*"])


def load_pinned_stack():
    """Compile each local Python dependency from its validated bytes, never pyc."""
    import ml.ocr.fragmented_xtick_detector_v3 as helper_package
    import ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.ink_stroke_classifier_v1 as classifier_package

    helper_sha = "985180618c262164bc16168e71bc9ba239b890a8f4d62ce8823d19c8dc6777c5"
    p1_sha = "3e8617809584dadb9c6b35cb52b95136fe766212a2f0bb65f4863b0d17ebc594"
    admission_sha = "72947560a68324ca2a6b470898755334e2f6ef98dd7491552e3618a615b30ede"
    helper, h = _load_exact(HELPER_REL, helper_sha, "ml.ocr.fragmented_xtick_detector_v3.gpu_train")
    helper_package.gpu_train = helper
    p1, p = _load_exact(P1_REL, p1_sha, classifier_package.__name__ + ".train_classifier")
    classifier_package.train_classifier = p1
    admission, a = _load_exact(ADMISSION_REL, admission_sha, classifier_package.__name__ + ".diagnose_train_split")
    classifier_package.diagnose_train_split = admission
    p2, p2hash = _load_exact(P2_REL, P2_RUNNER_SHA, classifier_package.__name__ + ".train_classifier_p2")
    classifier_package.train_classifier_p2 = p2
    # P2 executes helper directly too. Its import resolves to a P2-verified
    # exact-source module and the module's post-execution byte check remains active.
    return p2, {"p2_runner_sha256": p2hash, "p2_config_sha256": digest(read_bound(P2_CONFIG_REL, 65_536)),
        "helper_source_sha256": h, "p1_trainer_source_sha256": p,
        "train_admission_source_sha256": a}


def load_and_validate_config(p2_config: dict[str, Any], dependency_hashes: dict[str, str]) -> tuple[dict[str, Any], str]:
    raw = read_bound(P3_CONFIG_REL, 65_536)
    config = json.loads(raw.decode("utf-8"))
    config_sha = digest(raw)
    p2_sha = digest(read_bound(P2_CONFIG_REL, 65_536))
    if p2_sha != P2_CONFIG_SHA or dependency_hashes["p2_runner_sha256"] != P2_RUNNER_SHA:
        raise P3Error("P2 baseline identity differs from frozen recipe")
    _validate_recipe_config(config, p2_config)
    if digest(read_bound(P1_TRAIN_REPORT_REL, 200_000)) != config["dependencies"]["p1_train_diagnostic_report_sha256"]:
        raise P3Error("pinned P1 train-only report changed")
    if digest(read_bound(P2_REPORT_REL, 300_000)) != P2_REPORT_SHA:
        raise P3Error("pinned P2 train-only report changed")
    return config, config_sha


def _validate_recipe_config(config: dict[str, Any], p2_config: dict[str, Any]) -> None:
    expected = dict(p2_config)
    expected.update({"recipe_id": "ink-stroke-validity-source-uniform-p3-60ep",
        "epochs": 60, "output_relative": OUTPUT_RELATIVE,
        "expected_runner_sha256": _RUNNER_SHA})
    if config != expected:
        differing = sorted(k for k in set(config) | set(expected) if config.get(k) != expected.get(k))
        raise P3Error(f"P3 config changes fields beyond duration/identity/output: {differing}")


def _check_baseline(reports: dict[str, Any]) -> None:
    p1 = json.loads(read_bound(P1_TRAIN_REPORT_REL, 200_000).decode("utf-8"))["full_crop_train_metrics"]
    p2 = json.loads(read_bound(P2_REPORT_REL, 300_000).decode("utf-8"))["train_only_metrics"]["p2_overall"]
    p3 = reports["train_only_metrics"]["p2_overall"]
    reports["train_only_comparison"] = {"threshold": 0.5,
        "P1": {k: p1[k] for k in ("count", "true_positive", "false_positive", "false_negative", "true_negative", "precision", "recall", "accuracy")},
        "P2": p2,
        "P3": p3,
        "P3_minus_P2": {k: p3[k] - p2[k] for k in ("true_positive", "false_positive", "false_negative", "true_negative", "precision", "recall", "accuracy")},
        "metric_scope": "train split only; no holdout data or inference read"}


def main() -> None:
    global _RUNNER_SHA
    _RUNNER_SHA = digest(read_bound(Path(__file__).resolve().relative_to(ROOT).as_posix(), 4_000_000))
    p2, source_hashes = load_pinned_stack()
    p2_config, _ = p2.validate_config()
    config, config_sha = load_and_validate_config(p2_config, source_hashes)
    if _RUNNER_SHA != config["expected_runner_sha256"]:
        raise P3Error("P3 runner identity mismatch")
    output = ROOT / OUTPUT_RELATIVE
    if output.exists() or output.is_symlink() or output.parent != ROOT / "artifacts":
        raise P3Error("P3 output must be a new project artifacts child")
    p2.helpers._checked_chain(ROOT, output.parent, "P3 output parent")
    output.mkdir(parents=False, exist_ok=False)
    start = {"status": "started", "recipe_id": config["recipe_id"], "config_sha256": config_sha,
        "p3_runner_sha256": _RUNNER_SHA, "executed_dependencies": source_hashes,
        "started_utc": datetime.now(timezone.utc).isoformat()}
    (output / "started.json").write_text(json.dumps(start, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    p2._RUN["phase"] = "P3-source-uniform-training"
    p2._RUN["optimizer_steps"] = 0
    p2.CHECKPOINT_OUTPUT_RELATIVE = CHECKPOINT_REL
    started_at = time.perf_counter()
    try:
        report = p2._train(config, config_sha, output)
        checkpoint_path = output / "checkpoint.pt"
        import torch
        payload = torch.load(checkpoint_path, map_location="cuda:0", weights_only=False)
        payload.update({"model_identity": "stroke-seeded-ink-validity-rf31-p3-sourceuniform-60ep",
            "recipe_id": config["recipe_id"], "epochs": 60, "p3_config_sha256": config_sha,
            "p3_runner_sha256": _RUNNER_SHA, "p2_base_runner_sha256": P2_RUNNER_SHA,
            "p2_base_config_sha256": P2_CONFIG_SHA})
        torch.save(payload, checkpoint_path)
        report["status"] = "completed-train-only-source-uniform-p3-duration-diagnostic"
        report["recipe_id"] = config["recipe_id"]
        report["dependencies"] = {"p3_config_sha256": config_sha, "p3_runner_sha256": _RUNNER_SHA,
            "p2_base_config_sha256": P2_CONFIG_SHA, "p2_base_runner_sha256": P2_RUNNER_SHA,
            "p2_base_report_sha256": P2_REPORT_SHA, **source_hashes,
            **config["dependencies"], "all_local_source_dependencies_executed_from_exact_bytes": True}
        report["recipe"]["epochs"] = 60
        report["recipe"]["optimizer_steps"] = p2._RUN["optimizer_steps"]
        report["checkpoint"] = {"path": CHECKPOINT_REL, "sha256": digest(checkpoint_path.read_bytes()),
            "model_identity": payload["model_identity"]}
        report["training"]["per_epoch_loss_curve_available"] = False
        report["training"]["per_epoch_loss_limitation"] = "P2 exposes first/last loss and per-epoch sampler coverage; loss curve omitted rather than monkeypatching torch loss or altering the frozen loop."
        report["training"]["elapsed_total_seconds"] = time.perf_counter() - started_at
        report["training"]["p2_epochs"] = 20
        report["training"]["controlled_duration_change"] = "same train split, model/sampler seeds, architecture, loss, optimizer and batch schedule; epochs increased from 20 to 60"
        report["limitations"].append("P2 historical helper provenance caveat: its helper was executed from source bytes, but P1 trainer/admission were normally imported after hash checks. P3 independently source-compiles all pinned local Python dependencies before execution; this does not retroactively strengthen P2 provenance.")
        _check_baseline(report)
        (output / "report.json").write_text(json.dumps(report, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    except BaseException as error:
        failure = {"status": "failed", "phase": p2._RUN.get("phase"),
            "optimizer_steps": p2._RUN.get("optimizer_steps", 0), "error_type": type(error).__name__,
            "error": str(error), "p3_config_sha256": config_sha, "p3_runner_sha256": _RUNNER_SHA,
            "executed_dependencies": source_hashes, "failed_utc": datetime.now(timezone.utc).isoformat()}
        (output / "failure.json").write_text(json.dumps(failure, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        raise


if __name__ == "__main__":
    main()
