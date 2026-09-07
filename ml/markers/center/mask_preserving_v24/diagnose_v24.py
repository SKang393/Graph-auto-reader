# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Aggregate-only V24 feasibility run on corrected synthetic real-range dev."""

from __future__ import annotations

import argparse, hashlib, json, math, time
from collections import defaultdict
from pathlib import Path
import numpy as np
import onnxruntime as ort

from ml.markers.center.real_range_generator_v1.generator import audit as generator_audit, build_split
from ml.markers.center.metrics import center_metrics
from . import protocol
from .mask_preserving import extract_proposals, postprocess, prohibited_hits

ROOT = Path(__file__).resolve().parents[4]
DEFAULT_ONNX = ROOT / "artifacts/goal22-worktrees/marker-v21/ml/markers/center/focal_confidence_v21/artifacts/P1-run/marker-center-focal-confidence-v21-p1.onnx"

def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""): h.update(chunk)
    return h.hexdigest()

def _match_coordinates(coordinates, centers):
    edges = sorted((math.hypot(px-x, py-y), i, j) for i,(px,py) in enumerate(coordinates) for j,(x,y) in enumerate(centers) if math.hypot(px-x,py-y) <= 5.0)
    used_p, used_t = set(), set()
    for _, i, j in edges:
        if i not in used_p and j not in used_t: used_p.add(i); used_t.add(j)
    return len(used_p), len(coordinates)-len(used_p), len(centers)-len(used_t), used_t

def _match(predictions, centers):
    return _match_coordinates([(p.x, p.y) for p in predictions], centers)

def _center_mask_hits(scene, center):
    x, y = (int(round(value)) for value in center)
    ocr = float(scene.tensor[1, max(0, y-2):y+3, max(0, x-2):x+3].max()) >= .35
    artifact = float(scene.tensor[2, max(0, y-2):y+3, max(0, x-2):x+3].max()) >= .35
    return ocr, artifact

def summarize(model_path: Path = DEFAULT_ONNX) -> dict:
    started = time.perf_counter()
    for relative, expected in (
        (protocol.GENERATOR_AUDIT_PATH, protocol.GENERATOR_AUDIT_SHA256),
        (protocol.EVIDENCE_POLICY_PATH, protocol.EVIDENCE_POLICY_SHA256),
        (protocol.ACCEPTANCE_BARS_PATH, protocol.ACCEPTANCE_BARS_SHA256),
    ):
        actual = sha256(ROOT / relative)
        if actual != expected: raise ValueError(f"bound input changed: {relative}")
    if not model_path.is_file(): raise FileNotFoundError(f"V21 ONNX artifact is missing: {model_path}")
    model_hash = sha256(model_path)
    if model_hash != protocol.V21_ONNX_SHA256: raise ValueError(f"V21 ONNX hash changed: expected {protocol.V21_ONNX_SHA256}, got {model_hash}")
    split_audit = generator_audit(independent_layout=True)
    layout_audit = split_audit["layout_family_audit"]
    if not layout_audit["independent_layout_required"] or not layout_audit["train_dev_family_disjoint"] or not layout_audit["train_dev_layout_disjoint"]:
        raise RuntimeError("independent family-disjoint dev layout contract failed")
    tier1 = json.loads((ROOT / protocol.ACCEPTANCE_BARS_PATH).read_text(encoding="utf-8"))["tier1_reviewable_error"]
    acceptance_bar = {
        "proposal_recall_minimum": float(tier1["marker_center_recall_minimum"]),
        "precision_minimum": float(tier1["marker_center_precision_minimum"]),
        "recall_minimum": float(tier1["marker_center_recall_minimum"]),
        "prohibited_structure_hit_rate_maximum": float(tier1["prohibited_structure_hit_rate_maximum"]),
    }
    scenes = build_split("dev", independent_layout=True)
    session = ort.InferenceSession(str(model_path), providers=[protocol.PROVIDER])
    if session.get_providers()[0] != protocol.PROVIDER: raise RuntimeError("CPUExecutionProvider was not selected")
    inp, out = session.get_inputs()[0].name, session.get_outputs()[0].name
    totals = defaultdict(int); scenarios = {"unmasked": defaultdict(int), "ocr_mask": defaultdict(int), "artifact_mask": defaultdict(int), "both_masks": defaultdict(int)}
    for scene in scenes:
        raw = extract_proposals(scene.tensor)
        proposal_tp, _, proposal_fn, _ = _match_coordinates(raw.coordinates.tolist(), scene.centers)
        output = session.run([out], {inp: raw.patches.numpy().astype(np.float32, copy=False)})[0]
        predictions = postprocess(scene, raw, output)
        tp, fp, fn, matched_truths = _match(predictions, scene.centers)
        center_result = center_metrics(predictions, scene.centers, 5.0)
        hits = prohibited_hits(predictions, scene)
        for key, value in {"truth": len(scene.centers), "raw_proposals": len(raw.coordinates), "proposal_tp": proposal_tp, "proposal_fn": proposal_fn, "accepted": len(predictions), "tp": tp, "fp": fp, "fn": fn, "duplicates": center_result.duplicate_count, "prohibited": sum(hits.values())}.items(): totals[key] += value
        for local in range(len(scene.centers)):
            ocr_hit, artifact_hit = _center_mask_hits(scene, scene.centers[local])
            name = "both_masks" if ocr_hit and artifact_hit else "ocr_mask" if ocr_hit else "artifact_mask" if artifact_hit else "unmasked"
            scenarios[name]["truth"] += 1
            scenarios[name]["tp"] += int(local in matched_truths)
            scenarios[name]["fn"] += int(local not in matched_truths)
    for values in scenarios.values():
        values["recall"] = values["tp"] / max(1, values["truth"])
    precision = totals["tp"] / max(1, totals["tp"] + totals["fp"]); recall = totals["tp"] / max(1, totals["truth"])
    f1 = 2*precision*recall/max(1e-12, precision+recall)
    proposal_recall = totals["proposal_tp"] / max(1, totals["truth"])
    prohibited_rate = totals["prohibited"] / max(1, totals["accepted"])
    passed = proposal_recall >= acceptance_bar["proposal_recall_minimum"] and precision >= acceptance_bar["precision_minimum"] and recall >= acceptance_bar["recall_minimum"] and prohibited_rate <= acceptance_bar["prohibited_structure_hit_rate_maximum"]
    try: model_binding_path = model_path.relative_to(ROOT).as_posix()
    except ValueError: model_binding_path = model_path.name
    return {"schema":"graphreader.marker-center-mask-preserving-v24-feasibility.v1", "revision":protocol.REVISION,
      "status":"passed_no_training_required" if passed else "failed_frozen_model_training_candidate_startable",
      "scope":{"synthetic_only":True,"fixed_dev_split":"real-range-generator-v1-dev-independent-layout","scene_count":len(scenes),"truth_count":totals["truth"],"private_or_article_images":False,"real_sealed_reads":0,"candidate_consumed":False,"training_performed":False,"optimizer_steps":0,"case_ids_or_pixels_emitted":False},
      "binding":{"v21_onnx_path":model_binding_path,"v21_onnx_sha256":model_hash,"proposal_source":"real_range_generator_v1 ink-supported full-grid; masks retained as channels","generator_audit_path":protocol.GENERATOR_AUDIT_PATH,"generator_audit_sha256":protocol.GENERATOR_AUDIT_SHA256,"generator_split_sha256":protocol.GENERATOR_DEV_SPLIT_SHA256,"generator_layout_family_audit":layout_audit,"evidence_policy_sha256":protocol.EVIDENCE_POLICY_SHA256,"acceptance_bars_sha256":protocol.ACCEPTANCE_BARS_SHA256},
      "runtime":{"provider":session.get_providers()[0],"input_shape":protocol.INPUT_SHAPE,"output_shape":protocol.OUTPUT_SHAPE,"confidence_threshold":.25,"offset_scale":4.0,"radius_clip_px":[2.5,8.0],"ring_radii_px":list(range(3,13)),"nms":"V21 unchanged radius-aware NMS","prohibited_structure_checks":"retained","elapsed_ms":round((time.perf_counter()-started)*1000,3)},
      "proposal_coverage":{"ink_supported_emitted":totals["raw_proposals"],"truths_covered":totals["proposal_tp"],"false_negatives":totals["proposal_fn"],"recall":proposal_recall,"mask_channels_retained":True,"pre_inference_geometry_filter":False},
      "metrics":{"accepted":totals["accepted"],"true_positives":totals["tp"],"false_positives":totals["fp"],"false_negatives":totals["fn"],"precision":precision,"recall":recall,"f1":f1,"duplicates":totals["duplicates"],"prohibited_structure_hits":totals["prohibited"],"prohibited_structure_hit_rate":prohibited_rate},
      "mask_scenarios":{name:dict(value) for name,value in scenarios.items()},"acceptance_bar":acceptance_bar,"production_approval":False,"release_eligible":False}

def main() -> int:
    parser=argparse.ArgumentParser(); parser.add_argument("--model", "--onnx", dest="model", type=Path, default=DEFAULT_ONNX); parser.add_argument("--output", type=Path, required=True); args=parser.parse_args()
    result=summarize(args.model.resolve()); args.output.parent.mkdir(parents=True,exist_ok=True); args.output.write_bytes((json.dumps(result,indent=2,sort_keys=True)+"\n").encode("utf-8")); print(json.dumps(result,indent=2,sort_keys=True)); return 0
if __name__ == "__main__": raise SystemExit(main())
