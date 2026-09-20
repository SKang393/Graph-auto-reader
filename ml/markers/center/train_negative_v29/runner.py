# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded warm-start adaptation using authenticated training-only negatives."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import time
import traceback

import onnx
import onnxruntime as ort
import torch

from ml.markers.gate_seal import sha256_file, verify_bound_source_snapshot
from ml.markers.training_budget import acquire_training_candidate, complete_training_candidate, void_candidate
from ml.markers.classifier.native_context_v3.runner import WorkBudget
from .. import train_negative_coverage as coverage
from ..shape_coverage_cache import write_json
from ..shape_coverage_evaluation import evaluate
from ..shape_coverage_v28 import runner as previous
from ..scale_classifier_v16.model import ScaleClassifierNet, ModelConfig

TASK = "marker-center"
REVISION = "marker-center-train-negative-v29"
CONFIG_PATH = Path("ml/markers/center/train_negative_v29/p1.json")
RECIPE = dict(previous.RECIPE)
RUNNER_SOURCES = tuple(dict.fromkeys((*previous.RUNNER_SOURCES,
    *(Path(p) for p in coverage.SOURCE_PATHS), *(Path(p) for p in (
        "ml/markers/center/train_negative_v29/__init__.py",
        "ml/markers/center/train_negative_v29/runner.py",
        "ml/markers/center/train_negative_v29/protocol.json")))))


def validate_config(config: dict) -> None:
    if (config.get("task"),config.get("revision"),config.get("candidate_id")) != (TASK,REVISION,"P1"):
        raise ValueError("Candidate identity changed")
    if config.get("recipe") != RECIPE or config.get("sealed_runs_authorized") != 0:
        raise ValueError("Registered adaptation recipe or read authorization changed")
    if config.get("source_checkpoint_sha256") != "ec343b0d7c1963893f940abbba9b93cf4bf0d35f52026215d9d2050422e25ec2":
        raise ValueError("The authenticated V28 initializer changed")


def run(repo: Path, output: Path, *, resume: Path | None = None, resume_sha256: str | None = None) -> dict:
    started=time.perf_counter()
    config=json.loads((repo/CONFIG_PATH).read_text())
    validate_config(config)
    if (resume is None)!=(resume_sha256 is None) or (resume is not None and sha256_file(resume)!=resume_sha256):
        raise ValueError("Recovery requires its exact recorded checksum")
    authorization=acquire_training_candidate(repo,task=TASK,revision=REVISION,
        candidate_id="P1",config_path=CONFIG_PATH,runner_source_paths=RUNNER_SOURCES)
    try:
        output.mkdir(parents=True,exist_ok=False)
        previous.configure_runtime()
        budget=WorkBudget(output/"CANCEL")
        with budget.work_block():
            scopes,dev,manifest=coverage.load_cache(repo,repo/config["cache_path"],config["cache_manifest_sha256"])
            initializer=repo/config["source_checkpoint_path"]
            if sha256_file(initializer)!=config["source_checkpoint_sha256"]:
                raise ValueError("Initializer payload changed")
            model=ScaleClassifierNet(ModelConfig())
            model.load_state_dict(torch.load(initializer,map_location="cpu",weights_only=True)["state_dict"])
            training=tuple(torch.cat([values[i] for values in scopes.values()]) for i in range(5))
            del scopes
            if len(training[0])!=config["training_rows"]:
                raise ValueError("Complete training population changed")
            optimizer=torch.optim.AdamW(model.parameters(),lr=RECIPE["learning_rate"],weight_decay=RECIPE["weight_decay"])
        training_report=previous.train_epochs(model,optimizer,training,RECIPE,
            previous.recovery_binding(config,len(training[0])),output,budget,resume=resume)
        if training_report["optimizer_steps"]!=config["optimizer_steps_expected"]:
            raise ValueError("Training step count differs from the fixed recipe")
        del training,optimizer
        model.eval()
        checkpoint,exported=output/"marker-center.pt",output/"marker-center.onnx"
        with budget.work_block():
            torch.save({"state_dict":model.state_dict(),"config":model.export_contract()},checkpoint)
            torch.onnx.export(model,torch.zeros(1,3,33,33),exported,
                input_names=["candidate_patches"],output_names=["candidate_predictions"],
                dynamic_axes={"candidate_patches":{0:"candidate_count"},"candidate_predictions":{0:"candidate_count"}},
                opset_version=18,dynamo=False)
            onnx.checker.check_model(onnx.load(exported))
            options=ort.SessionOptions()
            options.intra_op_num_threads=torch.get_num_threads()
            options.inter_op_num_threads=1
            options.add_session_config_entry("session.intra_op.allow_spinning","0")
            options.add_session_config_entry("session.inter_op.allow_spinning","0")
            options.graph_optimization_level=ort.GraphOptimizationLevel.ORT_DISABLE_ALL
            session=ort.InferenceSession(str(exported),sess_options=options,providers=["CPUExecutionProvider"])
        write_json(output/"progress.json",{"status":"development_evaluation",**training_report})
        development=evaluate(repo,dev,model,session,budget,output)
        if development["parity"]["maximum_absolute_error"]>1e-5 or development["parity"]["confidence_decision_changes"]:
            raise RuntimeError("Exported model failed numerical or operating-decision parity")
        verify_bound_source_snapshot(repo,authorization.snapshot_path,authorization.binding["source_snapshot_sha256"])
        report={"task":TASK,"revision":REVISION,"candidate_id":"P1",
            "status":"dev_pass" if development["clears_shared_dev_bars"] else "failed_dev_unconsumed",
            "binding":authorization.binding,"config_sha256":sha256_file(repo/CONFIG_PATH),
            "checkpoint_sha256":sha256_file(checkpoint),"onnx_sha256":sha256_file(exported),
            "cache_manifest_sha256":config["cache_manifest_sha256"],"source_checkpoint_sha256":config["source_checkpoint_sha256"],
            "training":training_report,"development":development,"cpu":budget.report(),
            "torch_threads":torch.get_num_threads(),"onnx_threads":options.intra_op_num_threads,
            "seconds":time.perf_counter()-started,"private_reads":0,"sealed_reads":0,
            "budget_consumed":False,"production_approval":False,"release_eligible":False}
        write_json(output/"report.json",report)
        complete_training_candidate(authorization,status=report["status"],report_sha256=sha256_file(output/"report.json"))
        return report
    except BaseException as error:
        if output.is_dir():
            (output/"exception.txt").write_text(traceback.format_exc(),encoding="utf-8")
        void_candidate(authorization,error)
        raise


if __name__=="__main__":
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output",type=Path,required=True)
    parser.add_argument("--resume-from",type=Path)
    parser.add_argument("--resume-sha256")
    args=parser.parse_args()
    result=run(Path.cwd(),args.output,resume=args.resume_from,resume_sha256=args.resume_sha256)
    print(json.dumps({"status":result["status"],"seconds":result["seconds"]}))
