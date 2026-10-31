# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare default Hough settings with threshold=5 and min-line=5 on frozen synthetic panels."""

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
import subprocess
import sys
import time
from typing import Any

ROOT = Path(__file__).resolve().parents[6]
sys.path.insert(0, str(ROOT))
from ml.ocr.stroke_seeded_xtick_v1 import materialize, probe_materialize
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.opencv_hough_minline_diagnostic import run_hough_min5 as prior

CORPUS = ROOT / "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3"
MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
V4_PATH = ROOT / "artifacts/stroke-seeded-xtick-v1-axis-stroke-alignment-v4/report.json"
V4_SHA = "7a9aa9b5dc6b73de11eac01f856984b55ed537d4614250836022c73973c77de2"
MINLINE_PATH = ROOT / "artifacts/stroke-seeded-xtick-v1-axis-hough-minline-12-vs-5-v1/report.json"
MINLINE_SHA = "acc2c0bf20153647b5f1470c812c6de7e567c7a35869970b276dde17bd83640e"


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def variant_summary(panels: list[dict[str, Any]], name: str) -> dict[str, Any]:
    key = "default" if name == "default_24_12" else "dual_threshold5"
    metric = "default_metrics" if name == "default_24_12" else "dual_metrics"
    visible = sum(p["truth_count"] for p in panels)
    detected = matched = false = 0
    uncertain = 0
    reasons: dict[str,int] = {}
    candidates = accepted = rejected = 0
    prefixes: dict[str,int] = {}
    for panel in panels:
        run = panel[key]
        m = panel[metric]
        if run["status"] != "ok":
            raise RuntimeError(f"{name} detector failed in {panel['panel_id']}: {run['failure']}")
        detected += m["usable_detected_x_stroke_count"]
        matched += m["matched_count"]
        false += m["unmatched_detected_count"]
        uncertain += int(bool(run["needs_review"]))
        candidates += run["input_candidates"]
        accepted += run["accepted_candidates"]
        rejected += run["rejected_candidates"]
        for reason in run["uncertainty_reasons"]:
            reasons[reason] = reasons.get(reason,0)+1
        for tick in run["x_axis_ticks"]:
            prefix=tick["tick_id"].rsplit("-",1)[0]
            prefixes[prefix]=prefixes.get(prefix,0)+1
    return {"visible_truths":visible,"x_tick_detections":detected,"true_matches":matched,
        "unmatched_detections":false,"precision":matched/detected if detected else 0.0,
        "recall":matched/visible if visible else 0.0,"uncertain_panels":uncertain,
        "uncertainty_reason_counts":reasons,"input_candidates":candidates,
        "accepted_candidates":accepted,"rejected_candidates":rejected,
        "tick_id_prefix_counts":prefixes}


def run(dll: Path, report_path: Path) -> dict[str, Any]:
    started=time.perf_counter()
    manifest_bytes=common.read_pinned(CORPUS/"manifest.json",MANIFEST_SHA,10_000_000)
    manifest=json.loads(manifest_bytes)
    if digest(Path(materialize.__file__).read_bytes())!=manifest["crop_materializer_sha256"]:
        raise RuntimeError("crop materializer source hash mismatch")
    if digest(Path(probe_materialize.__file__).read_bytes())!=manifest["generator_sha256"]:
        raise RuntimeError("probe generator source hash mismatch")
    v4_bytes=common.read_pinned(V4_PATH,V4_SHA,2_000_000)
    v4=json.loads(v4_bytes)
    prior_bytes=common.read_pinned(MINLINE_PATH,MINLINE_SHA,8_000_000)
    prior_report=json.loads(prior_bytes)
    if v4["probe_manifest_sha256"]!=MANIFEST_SHA or prior_report["probe_manifest_sha256"]!=MANIFEST_SHA:
        raise RuntimeError("pinned comparison report belongs to another probe")
    v4_by_panel={p["panel_id"]:p for p in v4["panel_results"]}
    prior_by_panel={p["panel_id"]:p for p in prior_report["panel_results"]}
    inputs=[]; truth={}; source_receipts=[]; visible=invisible=0
    for source in manifest["sources"]:
        scene=materialize.build_train_scene(source["renderer_recipe"])
        image,rendered,_=materialize.render_train_scene(scene)
        pixel_hash=digest(image.tobytes())
        if pixel_hash!=source["source_pixel_sha256"]:
            raise RuntimeError(f"source raster hash mismatch for {source['source_id']}")
        panel_truth=prior.source_truths(source["source_id"],rendered["panels"])
        for pid,rows in panel_truth.items():
            truth[pid]=rows
            visible+=sum(bool(t["source_stroke_visible"]) for t in rows)
            invisible+=sum(not bool(t["source_stroke_visible"]) for t in rows)
        inputs.append(common.build_source_input(source,[],rendered["panels"],image))
        source_receipts.append({"source_id":source["source_id"],"source_pixel_sha256":pixel_hash,
            "width":image.width,"height":image.height,"panels":len(rendered["panels"])})
    if (visible,invisible)!=(181,241): raise RuntimeError("pinned truth inventory mismatch")
    proc=subprocess.Popen(["dotnet",str(dll),"--stdio"],cwd=ROOT,stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding="utf-8",errors="strict",bufsize=1)
    assert proc.stdin and proc.stdout
    outputs=[]
    try:
        for item in inputs:
            proc.stdin.write(json.dumps(item,separators=(",",":"))+"\n");proc.stdin.flush()
            line=proc.stdout.readline()
            if not line: raise RuntimeError("runner exited without a panel result")
            outputs.append(json.loads(line))
    finally: proc.stdin.close()
    stderr=proc.stderr.read() if proc.stderr else ""
    if proc.wait(timeout=60): raise RuntimeError(stderr[-4000:])
    panels=[]
    for source in outputs:
        if "error" in source: raise RuntimeError(source["error"])
        inp=next(i for i in inputs if i["source_id"]==source["source_id"])
        if source["png_sha256"]!=inp["image_png_sha256"]: raise RuntimeError("PNG identity mismatch")
        for panel in source["panels"]:
            panel["source_id"]=source["source_id"]
            pid=panel["panel_id"]
            frozen=v4_by_panel[pid]
            old=prior_by_panel[pid]
            default=panel["default"]
            if default["status"]!="ok": raise RuntimeError(f"default detector failed: {pid}")
            for candidate,frozen_tick in zip(default["x_axis_ticks"],frozen["x_axis_ticks"]):
                if (candidate["tick_id"]!=frozen_tick["tick_id"] or
                    max(abs(a-b) for a,b in zip(candidate["center"],frozen_tick["center"]))>1e-6 or
                    max(abs(a-b) for a,b in zip(candidate["line"],frozen_tick["line"]))>1e-6):
                    raise RuntimeError(f"default geometry changed from frozen v4 for {pid}")
            if len(default["x_axis_ticks"])!=len(frozen["x_axis_ticks"]):
                raise RuntimeError(f"default tick count differs from v4 for {pid}")
            for candidate,previous_tick in zip(default["x_axis_ticks"],old["default_hough12"]["x_axis_ticks"]):
                if (candidate["tick_id"]!=previous_tick["tick_id"] or
                    max(abs(a-b) for a,b in zip(candidate["center"],previous_tick["center"]))>1e-6):
                    raise RuntimeError(f"default geometry changed from prior A/B for {pid}")
            visible_rows=[t for t in truth[pid] if t["source_stroke_visible"]]
            panel["truth_count"]=len(visible_rows)
            panel["default_metrics"]=common.match_panel(visible_rows,default["x_axis_ticks"])
            panel["dual_metrics"]=common.match_panel(visible_rows,panel["dual_threshold5"]["x_axis_ticks"])
            panels.append(panel)
    summaries={"default_24_12":variant_summary(panels,"default_24_12"),
        "minline_only_24_5":prior_report["metrics"]["diagnostic_hough5"],
        "dual_threshold_5_5":variant_summary(panels,"dual_threshold5")}
    default_matches={p["case_id"] for p in panels for p in p["default_metrics"]["matched_pairs"]}
    dual_matches={p["case_id"] for p in panels for p in p["dual_metrics"]["matched_pairs"]}
    minline_matches={p["case_id"] for p in prior_report["panel_results"] for p in p["hough_min5_metrics"]["matched_pairs"]}
    deltas=[p["dual_threshold5"]["input_candidates"]-p["default"]["input_candidates"] for p in panels]
    minline_input=sum(p["diagnostic_hough5"]["input_candidates"] for p in prior_report["panel_results"])
    report={"schema":"graphreader.stroke-seeded-xtick-v1-hough-threshold-minline-comparison.v1",
        "recipe_id":"consumed-probe-default-vs-hough-votes5-minline5-v1",
        "acceptance_status":"consumed synthetic diagnostic only; not formal acceptance",
        "probe_manifest_sha256":digest(manifest_bytes),"frozen_v4_report_sha256":digest(v4_bytes),
        "minline_only_report_sha256":digest(prior_bytes),
        "runner_source_sha256":digest(Path(__file__).with_name("Program.cs").read_bytes()),
        "runner_project_sha256":digest(Path(__file__).with_name("DualThresholdDiagnostic.csproj").read_bytes()),
        "python_source_sha256":digest(Path(__file__).read_bytes()),
        "provider_identity":"OpenCvLineCandidateProvider; detector AxisGeometryDetector; ProductionRasterFrameDecoder",
        "variants":{"default_24_12":"OpenCvLineCandidateOptions defaults (HoughThreshold=24, HoughMinimumLineLengthPixels=12)",
          "minline_only_24_5":"prior pinned A/B (HoughThreshold=24, HoughMinimumLineLengthPixels=5)",
          "dual_threshold_5_5":"only changes HoughThreshold=5 and HoughMinimumLineLengthPixels=5; all remaining options and detector settings default"},
        "sample_counts":{"source_groups":len(inputs),"panels":len(panels),"visible_truth_ticks":visible,
          "invisible_truth_ticks_excluded_from_recall":invisible,"source_pixel_hashes_verified":len(source_receipts),
          "panel_images_written_to_disk":False},
        "metrics":summaries,
        "matched_case_changes":{"dual_new_vs_default":len(dual_matches-default_matches),
          "dual_lost_vs_default":len(default_matches-dual_matches),
          "dual_new_vs_minline_only":len(dual_matches-minline_matches),
          "dual_lost_vs_minline_only":len(minline_matches-dual_matches)},
        "candidate_volume":{"dual_vs_default_extra":sum(deltas),"dual_vs_default_per_panel_delta":deltas,
          "panels_with_more":sum(d>0 for d in deltas),"panels_with_fewer":sum(d<0 for d in deltas),
          "minline_only_total_candidates":minline_input,
          "dual_vs_minline_only_extra":summaries["dual_threshold_5_5"]["input_candidates"]-minline_input},
        "verified_source_rasters":source_receipts,"panel_results":panels,
        "elapsed_rerender_and_inference_seconds":round(time.perf_counter()-started,6),
        "training_performed":False,"dev_sealed_private_inputs_read":False,"production_behavior_changed":False,
        "limitations":["This is a consumed synthetic diagnostic, not held-out acceptance.",
          "Only Hough vote and minimum line thresholds changed together; any effect from the two cannot be separated in this run.",
          "Downstream geometry selection and tick filtering remain production defaults."]}
    report_path.parent.mkdir(parents=True,exist_ok=True)
    report_path.write_text(json.dumps(report,sort_keys=True,separators=(",",":")),encoding="utf-8")
    return report


if __name__=="__main__":
    if len(sys.argv)!=4: raise SystemExit("usage: run_dual_threshold5.py PROJECT_ROOT RUNNER_DLL OUTPUT_JSON")
    if Path(sys.argv[1]).resolve()!=ROOT.resolve(): raise SystemExit("project root mismatch")
    result=run(Path(sys.argv[2]),Path(sys.argv[3]))
    print(json.dumps({"metrics":result["metrics"],"matched_case_changes":result["matched_case_changes"],
        "candidate_volume":{k:v for k,v in result["candidate_volume"].items() if k!="dual_vs_default_per_panel_delta"},
        "elapsed_seconds":result["elapsed_rerender_and_inference_seconds"]},indent=2))
