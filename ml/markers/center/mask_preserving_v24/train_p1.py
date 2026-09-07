# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Single-use V24 P1 fine-tune runner; execution is explicitly deferred."""
from __future__ import annotations
import argparse, hashlib, json, random, time
from datetime import datetime, timezone
from pathlib import Path
import numpy as np
import onnx, onnxruntime as ort, torch
from ml.markers.center.focal_confidence_v21.focal_loss import v21_loss
from ml.markers.center.scale_classifier_v16.model import ModelConfig, ScaleClassifierNet
from ml.markers.center.real_range_generator_v1.generator import ANTI_ALIAS_BLUR_RADII, audit as generator_audit, build_split
from ml.markers.center.real_range_generator_v1.negative_sampler import CONNECTOR_ANCHOR_MAX_DISTANCE_PX, CONNECTOR_ENDPOINT_OFFSET_PX, GENERIC_CONNECTOR_BAND_RADIUS_PX, TOPOLOGY_HARD_RADIUS_PX, TOPOLOGY_KINDS, TOPOLOGY_RADIUS_PX, TOPOLOGY_SAMPLER_RADIUS_PX, SampledNegatives, sample_negatives
from ml.markers.center.metrics import center_metrics
from ml.markers.gate_seal import canonical_json_bytes, sha256_file
from ml.markers.training_budget import acquire_training_candidate, complete_training_candidate, void_candidate
from .mask_preserving import extract_proposals, postprocess, prohibited_hits
from . import protocol

REPO_ROOT = Path(__file__).resolve().parents[4]
CONFIG_PATH = Path("ml/markers/center/mask_preserving_v24/training/p1.json")
RUNNER_SOURCE_PATHS = (
    Path("ml/markers/center/mask_preserving_v24/protocol.py"),
    Path("ml/markers/center/mask_preserving_v24/mask_preserving.py"),
    Path("ml/markers/center/mask_preserving_v24/FEASIBILITY.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/diagnose_retry.py"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY2_MORPHOLOGY_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/train_p1.py"),
    Path("ml/markers/center/focal_confidence_v21/focal_loss.py"),
    Path("ml/markers/center/scale_classifier_v16/model.py"),
    Path("ml/markers/center/real_range_generator_v1/generator.py"),
    Path("ml/markers/center/real_range_generator_v1/negative_sampler.py"),
    Path("ml/markers/center/real_range_generator_v1/AUDIT.json"),
    Path("ml/markers/center/real_range_generator_v1/NEGATIVE_PROPOSAL_AUDIT.json"),
    Path("docs/GOAL-22-PHASE-4R-V24-NEGATIVE-PATCH-GAP.json"),
    Path("docs/GOAL-22-PHASE-4R-V24-MORPHOLOGY-GAP.json"),
    Path("docs/GOAL-22-PHASE-4R-V24-RETRY3-MORPHOLOGY-GAP.json"),
    Path("docs/GOAL-22-PHASE-4R-V24-RETRY7-MORPHOLOGY-GAP.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY4_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY4_GENERIC_FP_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY5_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY5_GENERIC_FP_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY6_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY7_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY7_MORPHOLOGY_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/diagnostics/V24_RETRY8_DIAGNOSIS.json"),
    Path("ml/markers/center/mask_preserving_v24/P1_RETRY8_RESULT.json"),
    Path("ml/markers/center/metrics.py"),
    Path("ml/policy/evidence-policy.json"),
    Path("ml/policy/acceptance-bars.json"),
    Path("ml/markers/gate_seal.py"),
    Path("ml/markers/training_budget.py"),
)

def _sha(path: Path) -> str: return hashlib.sha256(path.read_bytes()).hexdigest()
def _configure(seed: int) -> None:
    random.seed(seed); np.random.seed(seed); torch.manual_seed(seed); torch.set_num_threads(1); torch.use_deterministic_algorithms(True)


def _shared_marker_acceptance_bar() -> dict[str, float]:
    """Read the canonical marker-center bars instead of revision-local gates."""
    policy = json.loads((REPO_ROOT / protocol.ACCEPTANCE_BARS_PATH).read_text(encoding="utf-8"))
    tier1 = policy["tier1_reviewable_error"]
    return {
        "proposal_recall_minimum": float(tier1["marker_center_recall_minimum"]),
        "precision_minimum": float(tier1["marker_center_precision_minimum"]),
        "recall_minimum": float(tier1["marker_center_recall_minimum"]),
        "prohibited_structure_hit_rate_maximum": float(
            tier1["prohibited_structure_hit_rate_maximum"]
        ),
    }

def _examples_with_report(scenes, maximum_negative_per_positive: int, generator: torch.Generator):
    if maximum_negative_per_positive != 10:
        raise ValueError("V24 retry requires maximum_negative_per_positive=10")
    values = [[] for _ in range(5)]
    if len(scenes) != 167:
        # Focused unit tests may pass one scene; production training is always the
        # complete 167-scene train split and uses the exact global sampler below.
        for scene in scenes:
            proposals = extract_proposals(scene.tensor)
            centers = torch.tensor(scene.centers, dtype=torch.float32)
            radii = torch.tensor(scene.diameters, dtype=torch.float32) / 2.0
            distance = torch.cdist(proposals.coordinates, centers)
            nearest, nearest_index = distance.min(dim=1)
            labels = nearest.le(3.0).float()
            hard = torch.zeros(len(proposals.coordinates), dtype=torch.bool)
            for kind, x, y in scene.hard_negatives:
                if kind in {"text", "line_intersection", "axis"}:
                    hard |= torch.cdist(proposals.coordinates, torch.tensor(((x, y),), dtype=torch.float32)).squeeze(1).le(8.0)
                elif kind in TOPOLOGY_KINDS:
                    hard |= torch.cdist(proposals.coordinates, torch.tensor(((x, y),), dtype=torch.float32)).squeeze(1).le(TOPOLOGY_HARD_RADIUS_PX)
            positive = torch.nonzero(labels > .5).flatten()
            negative = torch.nonzero((labels <= .5) & ~hard).flatten()
            limit = max(0, len(positive) * maximum_negative_per_positive - int(hard.sum()))
            negative = negative[:limit]
            selected = torch.cat((positive, torch.nonzero(hard & (labels <= .5)).flatten(), negative)).unique(sorted=True)
            values[0].append(proposals.patches[selected]); values[1].append(labels[selected]); values[2].append((centers[nearest_index]-proposals.coordinates).index_select(0, selected)/4.0); values[3].append(radii[nearest_index].index_select(0, selected)); values[4].append(hard[selected])
        return tuple(torch.cat(part) for part in values) + (SampledNegatives(tuple(), {}, {}, "subset-test"),)
    records = []
    prepared = []
    for scene in scenes:
        proposals = extract_proposals(scene.tensor)
        coords = proposals.coordinates; centers = torch.tensor(scene.centers, dtype=torch.float32); radii = torch.tensor(scene.diameters, dtype=torch.float32) / 2.0
        distance = torch.cdist(coords, centers); nearest, nearest_index = distance.min(dim=1); labels = nearest.le(3.0).float()
        hard = torch.zeros(len(coords), dtype=torch.bool)
        for kind, x, y in scene.hard_negatives:
            if kind in {"text", "line_intersection", "axis"}:
                hard |= torch.cdist(coords, torch.tensor(((x, y),), dtype=torch.float32)).squeeze(1).le(8.0)
            elif kind in TOPOLOGY_KINDS:
                hard |= torch.cdist(coords, torch.tensor(((x, y),), dtype=torch.float32)).squeeze(1).le(TOPOLOGY_HARD_RADIUS_PX)
        positive = torch.nonzero(labels > .5).flatten()
        records.append((scene, proposals, labels, hard))
        prepared.append((scene, proposals, labels, hard, centers, nearest_index, radii))
    sampled = sample_negatives(records, split="train", seed=20260904)
    for scene_number, (scene, proposals, labels, hard, centers, nearest_index, radii) in enumerate(prepared):
        positive = torch.nonzero(labels > .5).flatten()
        negative = torch.tensor(sampled.selections[scene_number], dtype=torch.long)
        selected = torch.cat((positive, negative)).unique(sorted=True)
        values[0].append(proposals.patches[selected]); values[1].append(labels[selected]); values[2].append((centers[nearest_index]-proposals.coordinates).index_select(0,selected)/4.0); values[3].append(radii[nearest_index].index_select(0,selected)); values[4].append(hard[selected])
    return tuple(torch.cat(part) for part in values) + (sampled,)

def _examples(scenes, maximum_negative_per_positive: int, generator: torch.Generator):
    return _examples_with_report(scenes, maximum_negative_per_positive, generator)[:5]

def _evaluate(scenes, model, threshold):
    tp=fp=fn=dup=hits=truth=proposal_tp=0
    for scene in scenes:
        proposals=extract_proposals(scene.tensor); output=model(proposals.patches).detach().numpy(); predictions=postprocess(scene, proposals, output) if threshold == .25 else postprocess(scene, proposals, output)
        # Sensitivity thresholds are represented by filtering model probabilities here.
        predictions=tuple(p for p in predictions if p.confidence >= threshold)
        truth += len(scene.centers)
        edges=sorted((float(np.hypot(p.x-x,p.y-y)),i,j) for i,p in enumerate(predictions) for j,(x,y) in enumerate(scene.centers) if np.hypot(p.x-x,p.y-y)<=5)
        usedp=set(); usedt=set()
        for _,i,j in edges:
            if i not in usedp and j not in usedt: usedp.add(i); usedt.add(j)
        proposal_truths = {
            truth_index for x, y in proposals.coordinates.tolist()
            for truth_index, (truth_x, truth_y) in enumerate(scene.centers)
            if np.hypot(x-truth_x, y-truth_y) <= 5
        }
        metrics = center_metrics(predictions, scene.centers, 5.0)
        tp += len(usedp); fp += len(predictions)-len(usedp); fn += len(scene.centers)-len(usedt); proposal_tp += len(proposal_truths); dup += metrics.duplicate_count; hits += sum(prohibited_hits(predictions,scene).values())
    precision=tp/max(1,tp+fp); recall=tp/max(1,truth); f1=2*precision*recall/max(1e-12,precision+recall)
    return {"threshold":threshold,"scene_count":len(scenes),"proposal_true_positives":proposal_tp,"proposal_recall":proposal_tp/max(1,truth),"true_positives":tp,"false_positives":fp,"false_negatives":fn,"precision":precision,"recall":recall,"f1":f1,"duplicate_count":dup,"prohibited_structure_hits":hits,"prohibited_structure_hit_rate":hits/max(1,tp+fp)}

def run(output_dir: Path, checkpoint: Path, v21_onnx: Path) -> dict:
    if output_dir.exists(): raise RuntimeError(f"candidate output already exists: {output_dir}")
    config=json.loads((REPO_ROOT/CONFIG_PATH).read_text(encoding="utf-8"))
    if _sha(checkpoint) != config["checkpoint_sha256"]: raise ValueError("V21 checkpoint hash changed")
    if _sha(v21_onnx) != config["v21_onnx_sha256"]: raise ValueError("V21 ONNX hash changed")
    for path_key, hash_key in (("feasibility_path","feasibility_sha256"),("retry_diagnosis_path","retry_diagnosis_sha256"),("morphology_diagnosis_path","morphology_diagnosis_sha256"),("morphology_gap_path","morphology_gap_sha256"),("retry3_morphology_gap_path","retry3_morphology_gap_sha256"),("retry4_diagnosis_path","retry4_diagnosis_sha256"),("retry4_generic_fp_diagnosis_path","retry4_generic_fp_diagnosis_sha256"),("retry5_diagnosis_path","retry5_diagnosis_sha256"),("retry5_generic_fp_diagnosis_path","retry5_generic_fp_diagnosis_sha256"),("retry6_diagnosis_path","retry6_diagnosis_sha256"),("retry7_diagnosis_path","retry7_diagnosis_sha256"),("retry7_morphology_diagnosis_path","retry7_morphology_diagnosis_sha256"),("retry7_morphology_gap_path","retry7_morphology_gap_sha256"),("retry8_result_path","retry8_result_sha256"),("retry8_diagnosis_path","retry8_diagnosis_sha256"),("generator_audit_path","generator_audit_sha256"),("negative_audit_path","negative_audit_sha256"),("negative_gap_path","negative_gap_sha256"),("evidence_policy_path","evidence_policy_sha256"),("acceptance_bars_path","acceptance_bars_sha256")):
        if _sha(REPO_ROOT/str(config[path_key])) != config[hash_key]: raise ValueError(f"bound input changed: {config[path_key]}")
    if _sha(REPO_ROOT/config["negative_sampler"]["source_path"]) != config["negative_sampler"]["source_sha256"]: raise ValueError("negative sampler source changed")
    anti_aliasing = config.get("anti_aliasing")
    if anti_aliasing is None or tuple(float(value) for value in anti_aliasing.get("blur_radii_px", ())) != ANTI_ALIAS_BLUR_RADII or anti_aliasing.get("scene_index_schedule") != "ANTI_ALIAS_BLUR_RADII[index % len(ANTI_ALIAS_BLUR_RADII)]":
        raise ValueError("anti-aliasing schedule does not match generator constant")
    authorization=acquire_training_candidate(REPO_ROOT,task=protocol.TASK,revision=protocol.TRAINING_REVISION,candidate_id=protocol.TRAINING_CANDIDATE_ID,config_path=CONFIG_PATH,runner_source_paths=RUNNER_SOURCE_PATHS)
    output_dir.mkdir(parents=True); report_path=output_dir/"candidate-report.json"; started=time.perf_counter(); phase="initialization"
    try:
        _configure(config["seed"]); split_audit=generator_audit(independent_layout=True)
        layout_audit = split_audit["layout_family_audit"]
        if not layout_audit["independent_layout_required"] or not layout_audit["train_dev_family_disjoint"] or not layout_audit["train_dev_layout_disjoint"]:
            raise RuntimeError("independent family-disjoint dev layout contract failed")
        train=build_split("train"); dev=build_split("dev", independent_layout=True); gen=torch.Generator().manual_seed(config["seed"]+1); patches,labels,offsets,radii,hard,sampling=_examples_with_report(train,config["maximum_negative_per_positive"],gen)
        sampler_config = config["negative_sampler"]
        if sampling.capacities != sampler_config["expected_capacities"] or sampling.selected_index_sha256 != sampler_config["selected_index_sha256"] or sampling.counts != sampler_config["quotas"]:
            raise RuntimeError("negative sampler contract changed")
        topology_config = sampler_config["topology"]
        if float(topology_config["radius_px"]) != TOPOLOGY_SAMPLER_RADIUS_PX:
            raise RuntimeError("topology radius contract changed")
        if float(topology_config["input_audit_radius_px"]) != TOPOLOGY_RADIUS_PX:
            raise RuntimeError("topology input-audit radius contract changed")
        if sampling.topology_capacity != topology_config["expected_capacity"] or sampling.topology_selected != topology_config["expected_selected"] or sampling.topology_selected_index_sha256 != topology_config["selected_index_sha256"]:
            raise RuntimeError("topology sampling contract changed")
        topology_hard_config = topology_config["hard"]
        if float(topology_hard_config["radius_px"]) != TOPOLOGY_HARD_RADIUS_PX:
            raise RuntimeError("topology hard radius contract changed")
        if sampling.topology_hard_capacity != topology_hard_config["expected_capacity"] or sampling.topology_hard_selected != topology_hard_config["expected_selected"] or sampling.hard_training_total != topology_hard_config["hard_training_total"]:
            raise RuntimeError("topology hard sampling contract changed")
        connector_config = sampler_config["connector"]
        if float(connector_config["max_distance_px"]) != CONNECTOR_ANCHOR_MAX_DISTANCE_PX:
            raise RuntimeError("connector anchor distance contract changed")
        if float(connector_config["endpoint_offset_px"]) != CONNECTOR_ENDPOINT_OFFSET_PX:
            raise RuntimeError("connector endpoint offset contract changed")
        if sampling.connector_anchor_target_count != connector_config["target_count"] or sampling.connector_anchor_capacity != connector_config["expected_capacity"] or sampling.connector_anchor_selected != connector_config["expected_selected"] or sampling.connector_anchor_selected_index_sha256 != connector_config["selected_index_sha256"] or sampling.generic_remainder_selected != connector_config["generic_remainder_selected"]:
            raise RuntimeError("connector anchor sampling contract changed")
        band_config = sampler_config["generic_connector_band"]
        if float(band_config["radius_px"]) != GENERIC_CONNECTOR_BAND_RADIUS_PX or sampling.capacities["generic_connector_band"] != band_config["expected_capacity"] or sampling.counts["generic_connector_band"] != band_config["expected_selected"] or sampling.generic_connector_band_selected_index_sha256 != band_config["selected_index_sha256"]:
            raise RuntimeError("generic connector-band sampling contract changed")
        if len(labels) != config["training_example_count_expected"] or int((labels>.5).sum()) != config["positive_example_count_expected"] or int(hard.sum()) != config["hard_negative_example_count_expected"]: raise RuntimeError("training example contract changed")
        payload=torch.load(checkpoint,map_location="cpu",weights_only=False); model=ScaleClassifierNet(ModelConfig(seed=20260902)); model.load_state_dict(payload["state_dict"]); optimizer=torch.optim.AdamW(model.parameters(),lr=config["learning_rate"],weight_decay=config["weight_decay"]); steps=0; phase="training"; model.train()
        for _ in range(config["epochs"]):
            order=torch.randperm(len(labels),generator=gen)
            for start in range(0,len(labels),config["batch_size"]):
                index=order[start:start+config["batch_size"]]; loss=v21_loss(model.forward_raw(patches[index]),labels[index],offsets[index],radii[index],hard[index],positive_weight=config["positive_loss_weight"],hard_weight=config["hard_negative_loss_weight"],alpha=config["focal_alpha"],gamma=config["focal_gamma"]); optimizer.zero_grad(set_to_none=True); loss.backward(); torch.nn.utils.clip_grad_norm_(model.parameters(),5.0); optimizer.step(); steps+=1
        if steps != config["optimizer_steps_expected"] or steps > config["optimizer_steps_maximum"]: raise RuntimeError(f"optimizer step contract changed: {steps}")
        phase="dev"; model.eval(); comparisons=[_evaluate(dev,model,t) for t in [0.25,*config["selection_thresholds"]]]; selected=comparisons[0]; bar=_shared_marker_acceptance_bar(); dev_passed=selected["proposal_recall"]>=bar["proposal_recall_minimum"] and selected["precision"]>=bar["precision_minimum"] and selected["recall"]>=bar["recall_minimum"] and selected["prohibited_structure_hit_rate"]<=bar["prohibited_structure_hit_rate_maximum"]
        phase="export"; out_pt=output_dir/"marker-center-mask-preserving-v24-p1.pt"; torch.save({"state_dict":model.state_dict(),"config":model.export_contract()},out_pt); out_onnx=output_dir/"marker-center-mask-preserving-v24-p1.onnx"; torch.onnx.export(model,torch.zeros((1,3,33,33)),out_onnx,input_names=["candidate_patches"],output_names=["candidate_predictions"],dynamic_axes={"candidate_patches":{0:"candidate_count"},"candidate_predictions":{0:"candidate_count"}},opset_version=18,dynamo=False); onnx.checker.check_model(onnx.load(out_onnx)); session=ort.InferenceSession(str(out_onnx),providers=[protocol.PROVIDER]);
        if session.get_providers()[0] != protocol.PROVIDER: raise RuntimeError("CPUExecutionProvider was not selected")
        parity=[]; parity_source=extract_proposals(dev[0].tensor).patches
        for count in [1,8,37]:
            x=parity_source[:count].contiguous(); expected=model(x).detach().numpy(); actual=session.run(["candidate_predictions"],{"candidate_patches":x.numpy()})[0]; parity.append({"candidate_count":count,"maximum_absolute_error":float(np.max(np.abs(expected-actual)))})
        report={"schema":"graphreader.marker-center-mask-preserving-v24-candidate.v1","task":protocol.TASK,"revision":protocol.TRAINING_REVISION,"candidate_id":protocol.TRAINING_CANDIDATE_ID,"status":"dev_passed" if dev_passed and max(r["maximum_absolute_error"] for r in parity)<=config["onnx_parity_tolerance"] else "failed_dev","synthetic_only":True,"private_data":False,"real_dev_reads":0,"real_sealed_reads":0,"sealed_runs":0,"optimizer_steps":steps,"training_example_count":len(labels),"positive_example_count":int((labels>.5).sum()),"hard_negative_example_count":int(hard.sum()),"negative_sampling":{"seed":20260904,"split":"train","capacities":sampling.capacities,"counts":sampling.counts,"selected_index_sha256":sampling.selected_index_sha256,"topology_radius_px":topology_config["radius_px"],"topology_capacity":sampling.topology_capacity,"topology_selected":sampling.topology_selected,"topology_selected_index_sha256":sampling.topology_selected_index_sha256,"topology_hard_radius_px":topology_hard_config["radius_px"],"topology_hard_capacity":sampling.topology_hard_capacity,"topology_hard_selected":sampling.topology_hard_selected,"hard_training_total":sampling.hard_training_total,"connector_endpoint_offset_px":connector_config["endpoint_offset_px"],"connector_anchor_max_distance_px":connector_config["max_distance_px"],"connector_anchor_target_count":sampling.connector_anchor_target_count,"connector_anchor_capacity":sampling.connector_anchor_capacity,"connector_anchor_selected":sampling.connector_anchor_selected,"connector_anchor_selected_index_sha256":sampling.connector_anchor_selected_index_sha256,"generic_connector_band_radius_px":band_config["radius_px"],"generic_connector_band_capacity":sampling.capacities["generic_connector_band"],"generic_connector_band_selected":sampling.counts["generic_connector_band"],"generic_connector_band_selected_index_sha256":sampling.generic_connector_band_selected_index_sha256,"generic_remainder_selected":sampling.generic_remainder_selected},"generator_layout_family_audit":layout_audit,"acceptance_bar":bar,"dev_comparisons":comparisons,"selected":selected,"dev_gate_passed":dev_passed,"checkpoint_sha256":_sha(out_pt),"onnx_sha256":_sha(out_onnx),"v21_checkpoint_sha256":config["checkpoint_sha256"],"v21_onnx_sha256":config["v21_onnx_sha256"],"onnx_provider":protocol.PROVIDER,"onnx_dynamic_candidate_counts":parity,"onnx_parity_maximum_absolute_error":max(r["maximum_absolute_error"] for r in parity),"elapsed_ms":round((time.perf_counter()-started)*1000,3),"production_approval":False,"release_eligible":False}
    except Exception as error:
        report={"schema":"graphreader.marker-center-mask-preserving-v24-failure.v1","task":protocol.TASK,"revision":protocol.TRAINING_REVISION,"candidate_id":protocol.TRAINING_CANDIDATE_ID,"status":"failed_runner","phase":phase,"exception_type":type(error).__name__,"exception_message":str(error),"synthetic_only":True,"private_data":False,"real_dev_reads":0,"real_sealed_reads":0,"sealed_runs":0}
        report_path.write_bytes(canonical_json_bytes(report)); void_candidate(authorization,error); raise
    report_path.write_bytes(canonical_json_bytes(report)); complete_training_candidate(authorization,status=report["status"],report_sha256=sha256_file(report_path)); return report

def main():
    p=argparse.ArgumentParser(); p.add_argument("--output-dir",type=Path,required=True); p.add_argument("--checkpoint",type=Path,required=True); p.add_argument("--onnx",type=Path,required=True); a=p.parse_args(); print(json.dumps(run(a.output_dir.resolve(),a.checkpoint.resolve(),a.onnx.resolve()),indent=2,sort_keys=True))
if __name__ == "__main__": main()
