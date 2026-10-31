# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score raster-only short-tick proposals on the consumed synthetic probe."""

from __future__ import annotations

from hashlib import sha256
import json
import math
from pathlib import Path
import subprocess
import sys
import time
from typing import Any

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.stroke_seeded_xtick_v1 import materialize, probe_materialize
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.opencv_hough_minline_diagnostic import run_hough_min5 as prior

CORPUS = ROOT / "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3"
MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
V4_REPORT = ROOT / "artifacts/stroke-seeded-xtick-v1-axis-stroke-alignment-v4/report.json"
V4_SHA = "7a9aa9b5dc6b73de11eac01f856984b55ed537d4614250836022c73973c77de2"


def digest(payload: bytes) -> str:
    return sha256(payload).hexdigest()


def summarize(panels: list[dict[str, Any]], metric_key: str) -> dict[str, Any]:
    truth = detected = matched = false = 0
    uncertain = 0
    reasons: dict[str,int] = {}
    no_visible_truth_proposals = 0
    proposal_count_on_blank_truth_panels = 0
    hidden_truth_collisions = 0
    for panel in panels:
        metric = panel[metric_key]
        truth += metric["truth_count"]
        detected += metric["usable_detected_x_stroke_count"]
        matched += metric["matched_count"]
        false += metric["unmatched_detected_count"]
        if panel["needs_review"]:
            uncertain += 1
        for reason in panel["uncertainty_reasons"]:
            reasons[reason] = reasons.get(reason,0)+1
        if panel["truth_count"] == 0:
            proposal_count_on_blank_truth_panels += metric["usable_detected_x_stroke_count"]
            if metric["usable_detected_x_stroke_count"]:
                no_visible_truth_proposals += 1
        hidden_truth_collisions += panel["hidden_truth_collision_count"]
    return {"visible_truths":truth,"proposals":detected,"true_matches":matched,
        "unmatched_proposals":false,"precision":matched/detected if detected else 0.0,
        "recall":matched/truth if truth else 0.0,"uncertain_panels":uncertain,
        "uncertainty_reason_counts":reasons,
        "panels_without_visible_truth_with_proposals":no_visible_truth_proposals,
        "proposals_on_panels_without_visible_truth":proposal_count_on_blank_truth_panels,
        "proposals_near_hidden_truth_geometry_after_scoring_only":hidden_truth_collisions}


def run(dll: Path, report_path: Path) -> dict[str, Any]:
    started=time.perf_counter()
    test_run=subprocess.run(["dotnet",str(dll),"--self-test"],cwd=ROOT,capture_output=True,
        text=True,encoding="utf-8",errors="strict",check=True,timeout=30)
    self_test=json.loads(test_run.stdout)
    expected_self_test={"blank_axis":0,"horizontal_marker":0,"thin_horizontal_grid":0,
        "thick_axis_border_confuser":1,"crossing_gridline_confuser":1,
        "nearby_text_stroke_confuser":1,"short_tick":1}
    if self_test!=expected_self_test:
        raise RuntimeError(f"hard-negative self-test result changed: {self_test}")
    manifest_bytes=common.read_pinned(CORPUS/"manifest.json",MANIFEST_SHA,10_000_000)
    manifest=json.loads(manifest_bytes)
    if digest(Path(materialize.__file__).read_bytes())!=manifest["crop_materializer_sha256"]:
        raise RuntimeError("crop materializer source hash mismatch")
    if digest(Path(probe_materialize.__file__).read_bytes())!=manifest["generator_sha256"]:
        raise RuntimeError("probe generator source hash mismatch")
    baseline_bytes=common.read_pinned(V4_REPORT,V4_SHA,2_000_000)
    baseline=json.loads(baseline_bytes)
    frozen={p["panel_id"]:p for p in baseline["panel_results"]}
    inputs=[]; truths={}; visible=invisible=0; source_receipts=[]
    for source in manifest["sources"]:
        scene=materialize.build_train_scene(source["renderer_recipe"])
        image,rendered,_=materialize.render_train_scene(scene)
        pixel_sha=digest(image.tobytes())
        if pixel_sha!=source["source_pixel_sha256"]:
            raise RuntimeError(f"source raster hash mismatch: {source['source_id']}")
        source_truth=prior.source_truths(source["source_id"],rendered["panels"])
        for panel_id,rows in source_truth.items():
            truths[panel_id]=rows
            visible+=sum(t["source_stroke_visible"] for t in rows)
            invisible+=sum(not t["source_stroke_visible"] for t in rows)
        # This serializes only full raster pixels and renderer panel bounds. No truth is sent to C#.
        inputs.append(common.build_source_input(source,[],rendered["panels"],image))
        source_receipts.append({"source_id":source["source_id"],"source_pixel_sha256":pixel_sha,
            "width":image.width,"height":image.height,"panel_count":len(rendered["panels"])})
    if (visible,invisible)!=(181,241): raise RuntimeError("pinned visible/invisible truth inventory mismatch")
    proc=subprocess.Popen(["dotnet",str(dll),"--stdio"],cwd=ROOT,stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,encoding="utf-8",errors="strict",bufsize=1)
    assert proc.stdin and proc.stdout
    outputs=[]
    try:
        for item in inputs:
            proc.stdin.write(json.dumps(item,separators=(",",":"))+"\n");proc.stdin.flush()
            line=proc.stdout.readline()
            if not line: raise RuntimeError("proposal runner exited without output")
            outputs.append(json.loads(line))
    finally: proc.stdin.close()
    stderr=proc.stderr.read() if proc.stderr else ""
    if proc.wait(timeout=60): raise RuntimeError(stderr[-4000:])
    panels=[]
    for source in outputs:
        if "error" in source: raise RuntimeError(source["error"])
        expected=next(i["image_png_sha256"] for i in inputs if i["source_id"]==source["source_id"])
        if source["png_sha256"]!=expected: raise RuntimeError("PNG transport hash mismatch")
        for panel in source["panels"]:
            panel["source_id"]=source["source_id"]
            pid=panel["panel_id"]
            frozen_panel=frozen[pid]
            default_ticks=[t for t in panel["default_x_axis_ticks"] if t["usable_for_ocr_tick_seed"]]
            frozen_ticks=frozen_panel["x_axis_ticks"]
            if len(default_ticks)!=len(frozen_ticks): raise RuntimeError(f"default tick count mismatch: {pid}")
            for new,old in zip(default_ticks,frozen_ticks):
                if (new["tick_id"]!=old["tick_id"] or
                    max(abs(a-b) for a,b in zip(new["center"],old["center"]))>1e-6 or
                    max(abs(a-b) for a,b in zip(new["line"],old["line"]))>1e-6):
                    raise RuntimeError(f"default detector parity mismatch: {pid}")
            visible_rows=[t for t in truths[pid] if t["source_stroke_visible"]]
            hidden_rows=[t for t in truths[pid] if not t["source_stroke_visible"]]
            proposal_rows=panel["ink_proposals"]
            panel["truth_count"]=len(visible_rows)
            panel["default_metrics"]=common.match_panel(visible_rows,default_ticks)
            panel["ink_metrics"]=common.match_panel(visible_rows,proposal_rows)
            hidden_collisions=0
            for proposal in proposal_rows:
                px,py=proposal["center"]
                if any(math.hypot(px-t["stroke_center_original_xy"][0],py-t["stroke_center_original_xy"][1]) <=
                       max(3.0,0.5*math.hypot(t["stroke_line_original_xyxy"][2]-t["stroke_line_original_xyxy"][0],
                                             t["stroke_line_original_xyxy"][3]-t["stroke_line_original_xyxy"][1]))
                       for t in hidden_rows):
                    hidden_collisions+=1
            panel["hidden_truth_collision_count"]=hidden_collisions
            panels.append(panel)
    summaries={"production_default":summarize(panels,"default_metrics"),
               "axis_conditioned_pixel_scan":summarize(panels,"ink_metrics")}
    candidate_deltas=[p["input_candidates"] for p in panels]
    report={"schema":"graphreader.stroke-seeded-xtick-v1-short-ink-proposal-diagnostic.v1",
        "recipe_id":"consumed-probe-axis-conditioned-short-ink-scan-v1",
        "acceptance_status":"consumed synthetic diagnostic only; not formal acceptance",
        "probe_manifest_sha256":digest(manifest_bytes),"frozen_v4_report_sha256":digest(baseline_bytes),
        "runner_project_sha256":digest(Path(__file__).with_name("ShortTickInkPrototype.csproj").read_bytes()),
        "runner_source_sha256":digest(Path(__file__).with_name("Program.cs").read_bytes()),
        "python_evaluator_sha256":digest(Path(__file__).read_bytes()),
        "source_pixel_hashes_verified":source_receipts,
        "proposal_input_contract":{"fields_sent_to_csharp":["source raster PNG bytes","panel bounds","panel identifiers"],
          "truth_sent_to_proposer":False,"truth_used":"Only after proposals returned, for scoring visible matches and hidden-target collisions."},
        "scan_recipe":{"axis":"default AxisGeometryDetector result XAxis.Line","gray_ink_threshold_lt":200,
          "sample_offsets_normal_px":[-12,12],"excluded_axis_core_px":2,
          "required_dark_samples":3,"required_contiguous_normal_run_px":2,
          "minimum_output_line_length_px":4,"maximum_cluster_width_px":8,
          "axis_endpoint_guard_px":4,
          "proposal_rule":"At each along-axis pixel, count actual raster ink on either side outside axis core; group adjacent qualifying positions; reject wide clusters; return a line spanning observed off-axis ink and the selected axis."},
        "sample_counts":{"source_groups":len(inputs),"panels":len(panels),
          "visible_truth_ticks":visible,"invisible_truth_ticks":invisible,
          "source_rasters_written_to_disk":False},
        "matching_rule":common.MATCH_RECIPE,"metrics":summaries,
        "hard_negative_self_test":{"command":"dotnet ShortTickInkPrototype.dll --self-test",
          "covered":["blank axis","horizontal marker stroke","thin horizontal grid",
                      "thick axis border (known confuser)","vertical grid crossing axis (known confuser)",
                      "nearby text stroke (known confuser)","short vertical tick"],
          "fixture_results":self_test},
        "default_detector_raw_candidate_counts":{"total":sum(candidate_deltas),
          "accepted":sum(p["accepted_candidates"] for p in panels),
          "rejected":sum(p["rejected_candidates"] for p in panels)},
        "elapsed_rerender_and_inference_seconds":round(time.perf_counter()-started,6),
        "training_performed":False,"dev_sealed_private_inputs_read":False,"production_behavior_changed":False,
        "limitations":["A vertical grid line crossing the selected axis, a thick border extending past the endpoint guard, and a nearby vertical glyph stroke look like ticks to this local pixel test; the self-test deliberately exposes these confusers.",
          "The 4 px endpoint guard can miss real ticks close to selected axis endpoints.",
          "This is a diagnostic on consumed synthetic material, not held-out acceptance."]}
    report_path.parent.mkdir(parents=True,exist_ok=True)
    report_path.write_text(json.dumps(report,sort_keys=True,separators=(",",":")),encoding="utf-8")
    return report


if __name__=="__main__":
    if len(sys.argv)!=4: raise SystemExit("usage: run_probe.py PROJECT_ROOT RUNNER_DLL OUTPUT_JSON")
    if Path(sys.argv[1]).resolve()!=ROOT.resolve(): raise SystemExit("project root mismatch")
    report=run(Path(sys.argv[2]),Path(sys.argv[3]))
    print(json.dumps(report["metrics"],indent=2))
