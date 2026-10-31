# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Hash-gated trace of visible probe ticks through production geometry filters."""

from __future__ import annotations

from hashlib import sha256
import json
import math
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))
from ml.ocr.stroke_seeded_xtick_v1 import materialize, probe_materialize
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.axis_stroke_alignment import run_axis_alignment as common
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.opencv_hough_minline_diagnostic import run_hough_min5 as previous

CORPUS = ROOT / "artifacts/stroke-seeded-xtick-v1-p2-probe-corpus-v3"
MANIFEST_SHA = "127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba"
V4_REPORT = ROOT / "artifacts/stroke-seeded-xtick-v1-axis-stroke-alignment-v4/report.json"
V4_SHA = "7a9aa9b5dc6b73de11eac01f856984b55ed537d4614250836022c73973c77de2"
TOLERANCE = 3.0


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def distance_point_segment(x: float, y: float, line: list[float]) -> float:
    x0, y0, x1, y1 = line
    dx, dy = x1 - x0, y1 - y0
    d2 = dx * dx + dy * dy
    if d2 == 0:
        return math.hypot(x - x0, y - y0)
    t = max(0.0, min(1.0, ((x - x0) * dx + (y - y0) * dy) / d2))
    return math.hypot(x - (x0 + t * dx), y - (y0 + t * dy))


def distance_point_infinite_line(x: float, y: float, line: list[float]) -> float:
    x0, y0, x1, y1 = line
    dx, dy = x1-x0, y1-y0
    return abs(dy*x-dx*y+x1*y0-y1*x0)/math.hypot(dx,dy)


def eligibility(candidate: dict, pair: dict, panel_size: tuple[int, int], divider_ids: set[str]) -> tuple[str, dict]:
    x0, y0, x1, y1 = candidate["line"]
    mx, my = (x0 + x1) / 2, (y0 + y1) / 2
    length = math.hypot(x1 - x0, y1 - y0)
    theta = math.degrees(math.atan2(abs(y1 - y0), abs(x1 - x0)))
    vertical = 90 - theta <= 15
    horizontal = theta <= 15 and theta <= 90 - theta
    valid = (all(math.isfinite(v) for v in candidate["line"]) and length >= 1 and
        all(0 <= v <= maxd for v, maxd in ((x0, panel_size[0] - 1), (x1, panel_size[0] - 1),
                                            (y0, panel_size[1] - 1), (y1, panel_size[1] - 1))))
    if not valid or not (vertical or horizontal):
        return "candidate_rejected_by_observation_gate", {"accepted_observation": False, "angle_from_vertical_deg": 90-theta}
    if candidate["candidate_id"] in divider_ids:
        return "excluded_as_phase_divider", {"accepted_observation": True, "angle_from_vertical_deg": 90-theta}
    if candidate["selected_axis_member"] and length > (pair["x_span"] * .08 if horizontal else pair["y_span"] * .08):
        return "selected_axis_member_too_long_for_tick", {"accepted_observation": True, "angle_from_vertical_deg": 90-theta}
    if not vertical:
        return "horizontal_observation_is_not_x_tick", {"accepted_observation": True, "angle_from_vertical_deg": 90-theta}
    axis = pair["x_axis_line"]
    axis_distance = distance_point_infinite_line(mx, my, axis)
    dx, dy = axis[2]-axis[0], axis[3]-axis[1]
    span = math.hypot(dx, dy)
    ux, uy = dx/span, dy/span
    origin = pair["intersection"]
    projection = (mx-origin[0])*ux+(my-origin[1])*uy
    extent = pair["x_span"]
    detail = {"accepted_observation": True, "angle_from_vertical_deg": 90-theta,
              "length_px": length, "distance_to_selected_x_axis_px": axis_distance,
              "projection_px": projection, "selected_x_span_px": extent}
    if length > pair["y_span"]*.08:
        return "too_long_for_x_tick", detail
    if axis_distance > 4 + length/2:
        return "too_far_from_selected_x_axis", detail
    if projection < -3 or projection > extent+3:
        return "outside_selected_plot_projection", detail
    return "passed_x_tick_eligibility", detail


def run(dll: Path, output: Path) -> dict:
    started = time.perf_counter()
    manifest_bytes = common.read_pinned(CORPUS / "manifest.json", MANIFEST_SHA, 10_000_000)
    manifest = json.loads(manifest_bytes)
    if digest(Path(materialize.__file__).read_bytes()) != manifest["crop_materializer_sha256"]:
        raise RuntimeError("crop materializer hash differs from pinned manifest")
    if digest(Path(probe_materialize.__file__).read_bytes()) != manifest["generator_sha256"]:
        raise RuntimeError("probe generator hash differs from pinned manifest")
    baseline_bytes = common.read_pinned(V4_REPORT, V4_SHA, 2_000_000)
    baseline = json.loads(baseline_bytes)
    base_panels = {p["panel_id"]: p for p in baseline["panel_results"]}
    inputs, truth, source_hashes, truth_axes = [], {}, [], {}
    for source in manifest["sources"]:
        scene = materialize.build_train_scene(source["renderer_recipe"])
        image, rendered, _ = materialize.render_train_scene(scene)
        source_sha = digest(image.tobytes())
        if source_sha != source["source_pixel_sha256"]:
            raise RuntimeError(f"source pixel hash mismatch: {source['source_id']}")
        inp = common.build_source_input(source, [], rendered["panels"], image)
        source_truths = previous.source_truths(source["source_id"], rendered["panels"])
        for pi, panel in enumerate(rendered["panels"]):
            truths = source_truths[panel["panel_id"]]
            visible = [row for row in truths if row["source_stroke_visible"]]
            truth[panel["panel_id"]] = visible
            inp["panels"][pi]["visible_truths"] = [
                {"case_id": row["case_id"], "line": row["stroke_line_original_xyxy"]}
                for row in visible]
            for axis in panel.get("axes", []):
                if axis.get("axis") == "x" and axis.get("visible"):
                    pts = axis["line"]
                    truth_axes[axis["panel_id"]] = [pts[0][0], pts[0][1], pts[1][0], pts[1][1]]
        inputs.append(inp)
        source_hashes.append({"source_id": source["source_id"], "sha256": source_sha})
    proc = subprocess.Popen(["dotnet", str(dll), "--stdio"], cwd=ROOT,
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", errors="strict", bufsize=1)
    assert proc.stdin and proc.stdout
    results = []
    try:
        for inp in inputs:
            proc.stdin.write(json.dumps(inp, separators=(",", ":"))+"\n")
            proc.stdin.flush()
            line = proc.stdout.readline()
            if not line: raise RuntimeError("runner exited without result")
            results.append(json.loads(line))
    finally:
        proc.stdin.close()
    stderr = proc.stderr.read() if proc.stderr else ""
    if proc.wait(timeout=60): raise RuntimeError(stderr[-4000:])
    panels, category_counts, nearest_case_counts, axis_comparisons = [], {}, {}, []
    accepted_total = rejected_total = reimplemented_accepted_total = 0
    visible_count = matched_count = 0
    misses_with_emitted_eligible_candidate = 0
    eligible_alternative_offsets = []
    for source_result in results:
        if "error" in source_result: raise RuntimeError(source_result["error"])
        inp = next(i for i in inputs if i["source_id"] == source_result["source_id"])
        if source_result["png_sha256"] != inp["image_png_sha256"]:
            raise RuntimeError("PNG bytes changed in transport")
        for panel in source_result["panels"]:
            pid = panel["panel_id"]
            old = base_panels[pid]
            if len(old["x_axis_ticks"]) != len(panel["x_axis_ticks"]):
                raise RuntimeError(f"default geometry tick count changed: {pid}")
            for a,b in zip(old["x_axis_ticks"],panel["x_axis_ticks"]):
                if a["tick_id"] != b["tick_id"] or max(abs(x-y) for x,y in zip(a["center"],b["center"]))>1e-6:
                    raise RuntimeError(f"frozen baseline parity failed: {pid}")
            # Use the top-ranked pair snapshot. It exposes selected baseline and plot spans.
            pair = panel["top_axis_pairs"][0]
            pair_geom = {"x_axis_line": [pair["x_axis_line"]["start"]["x"],pair["x_axis_line"]["start"]["y"],
                                          pair["x_axis_line"]["end"]["x"],pair["x_axis_line"]["end"]["y"]],
                         "intersection": [pair["intersection"]["x"],pair["intersection"]["y"]],
                         "x_span": math.hypot(pair["bottom_right"]["x"]-pair["intersection"]["x"],
                                              pair["bottom_right"]["y"]-pair["intersection"]["y"]),
                         "y_span": math.hypot(pair["top_left"]["x"]-pair["intersection"]["x"],
                                              pair["top_left"]["y"]-pair["intersection"]["y"]),
                         "rank": pair["rank"], "score": pair["score"]}
            # Observer X axis line is panel-local. Translate truth lines to the crop frame.
            left,top,right,bottom=panel["bounds"]
            panel_size=(right-left,bottom-top)
            predicted_axis = panel["selected_x_axis_line"]
            truth_axis = truth_axes[pid]
            true_theta=math.atan2(truth_axis[3]-truth_axis[1],truth_axis[2]-truth_axis[0])
            pred_theta=math.atan2(predicted_axis[3]-predicted_axis[1],predicted_axis[2]-predicted_axis[0])
            angle_error=abs((pred_theta-true_theta+math.pi/2)%math.pi-math.pi/2)*180/math.pi
            axis_comparisons.append({"panel_id":pid,"source_id":source_result["source_id"],
                "selected_axis_line":predicted_axis,"rendered_truth_axis_line":truth_axis,
                "endpoint_rms_error_px":common.endpoint_error(predicted_axis,truth_axis),
                "orientation_error_deg":angle_error,
                "predicted_midpoint_to_truth_line_px":distance_point_infinite_line(
                    (predicted_axis[0]+predicted_axis[2])/2,(predicted_axis[1]+predicted_axis[3])/2,truth_axis),
                "truth_midpoint_to_predicted_line_px":distance_point_infinite_line(
                    (truth_axis[0]+truth_axis[2])/2,(truth_axis[1]+truth_axis[3])/2,predicted_axis)})
            missed = {row["case_id"] for row in truth[pid]}
            pairs = common.match_panel(truth[pid], old["x_axis_ticks"])["matched_pairs"]
            matched_ids={p["case_id"] for p in pairs}
            missed -= matched_ids
            candidate_rows=[]
            divider_ids=set(panel["divider_support_ids"])
            for candidate in panel["nearby_candidates"]:
                line=candidate["line"]
                # Candidate and selected geometry are local to the panel crop.
                cls, detail=eligibility(candidate,pair_geom,panel_size,divider_ids)
                candidate_rows.append({**candidate,"eligibility":cls,"eligibility_detail":detail})
                category_counts[cls]=category_counts.get(cls,0)+1
            for truth_row in truth[pid]:
                visible_count += 1
                if truth_row["case_id"] in matched_ids: matched_count += 1
            for tid in sorted(missed):
                gt=next(t for t in truth[pid] if t["case_id"]==tid)
                cx,cy=(gt["stroke_center_original_xy"][0]-left,gt["stroke_center_original_xy"][1]-top)
                near=sorted((c for c in candidate_rows if c["nearest_truth_case_id"]==tid),
                            key=lambda c:c["midpoint_distance_to_truth_center"])
                nearest_reason = near[0]["eligibility"] if near else "no_candidate_midpoint_within_10px"
                nearest_case_counts[nearest_reason] = nearest_case_counts.get(nearest_reason,0)+1
                passing = [c for c in near if c["eligibility"] == "passed_x_tick_eligibility"]
                emitted_supports = {candidate_id for tick in panel["x_axis_ticks"]
                                    for candidate_id in tick["supporting_candidate_ids"]}
                emitted_passing = [c for c in passing if c["candidate_id"] in emitted_supports]
                if emitted_passing:
                    misses_with_emitted_eligible_candidate += 1
                    eligible_alternative_offsets.append(min(c["midpoint_distance_to_truth_center"] for c in emitted_passing))
                selected_line=pair_geom["x_axis_line"]
                axis_dist=distance_point_infinite_line(cx,cy,selected_line)
                panels.append({"panel_id":pid,"truth_case_id":tid,"selected_pair_rank":pair_geom["rank"],
                    "selected_pair_score":pair_geom["score"],"truth_center_to_selected_x_axis_px":axis_dist,
                    "nearby_candidates_within_10px":[{k:v for k,v in c.items() if k!="nearest_truth_case_id"} for c in near],
                    "raw_provider_candidate_count":panel["input_candidates"],
                    "accepted_candidate_count":panel["accepted_candidates"],"rejected_candidate_count":panel["rejected_candidates"],
                    "x_axis_ticks_emitted":panel["x_axis_ticks"]})
            accepted_total += panel["accepted_candidates"]
            rejected_total += panel["rejected_candidates"]
            reimplemented_accepted_total += panel["reimplemented_accepted_candidates"]
    if reimplemented_accepted_total != accepted_total:
        raise RuntimeError(f"offline observation gate parity failed: actual={accepted_total}, reproduced={reimplemented_accepted_total}")
    report={"schema":"graphreader.axis-missed-visible-tick-attribution.v1",
      "acceptance_status":"consumed synthetic diagnostic only; not formal acceptance",
      "probe_manifest_sha256":digest(manifest_bytes),"frozen_v4_report_sha256":digest(baseline_bytes),
      "source_pixel_hashes_verified":source_hashes,"panel_count":len(base_panels),
      "visible_truth_count":visible_count,"baseline_matched_truth_count":matched_count,
      "baseline_missed_truth_count":visible_count-matched_count,
      "matching_tolerance":"unique nearest center, max(3 px, half stroke length)",
      "axis_pair_options":"top-ranked pair returned by production candidate observer; score/rank recorded",
      "provider_options":"production OpenCvLineCandidateProvider defaults: Hough votes=24, min line=12 px, max gap=4 px; LSD enabled",
      "selected_axis_comparison":{"denominator_panels":len(axis_comparisons),
        "mean_endpoint_rms_px":sum(x["endpoint_rms_error_px"] for x in axis_comparisons)/len(axis_comparisons),
        "p50_endpoint_rms_px":common.percentile([x["endpoint_rms_error_px"] for x in axis_comparisons],.5),
        "p90_endpoint_rms_px":common.percentile([x["endpoint_rms_error_px"] for x in axis_comparisons],.9),
        "max_endpoint_rms_px":max(x["endpoint_rms_error_px"] for x in axis_comparisons),
        "mean_orientation_error_deg":sum(x["orientation_error_deg"] for x in axis_comparisons)/len(axis_comparisons),
        "panels":axis_comparisons},
      "candidate_trace_neighborhood":"provider candidate midpoint assigned to nearest visible truth center; retained if <=10 px. Gate-count summary includes matched and missed truths; per-missed nearest outcome is separate.",
      "observed_candidate_gate_counts_near_visible_truths":category_counts,
      "diagnostic_gate_note":"Exact numeric eligibility thresholds mirror AxisGeometryDetector.CreateTicks for X ticks; provider candidates and final detector ticks are actual production outputs. Candidate classification is an offline explanation of the gates, not injected into the detector.",
      "aggregate_detector_counts":{"input":sum(p["input_candidates"] for sr in results for p in sr["panels"]),
        "accepted":accepted_total,"reimplemented_accepted":reimplemented_accepted_total,
        "rejected":rejected_total,"observation_gate_parity":True},
      "nearest_candidate_outcome_per_missed_truth":nearest_case_counts,
      "misses_with_emitted_eligible_candidate":{"count":misses_with_emitted_eligible_candidate,
        "denominator":visible_count-matched_count,"offset_px":eligible_alternative_offsets,
        "note":"Candidates are cross-checked against final X tick SupportingCandidateIds; their measured offsets exceed the match tolerance."},
      "misses":panels,"elapsed_seconds":round(time.perf_counter()-started,4),
      "training_performed":False,"dev_sealed_private_inputs_read":False,"production_behavior_changed":False,
      "limitations":["Synthetic consumed probe only.","Only provider candidates with midpoint distance <=10 px from a visible truth center are retained for the per-miss trace.","Candidate eligibility explanations reproduce detector gates from source; the detector itself exposes aggregate accepted/rejected counts but not rejection reasons."]}
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(report,sort_keys=True,separators=(",",":")),encoding="utf-8")
    return report


if __name__ == "__main__":
    if len(sys.argv)!=4: raise SystemExit("usage: run_attribution.py PROJECT_ROOT RUNNER_DLL OUTPUT_JSON")
    project=Path(sys.argv[1]).resolve()
    if project!=ROOT.resolve(): raise SystemExit("project root mismatch")
    result=run(Path(sys.argv[2]),Path(sys.argv[3]))
    summary={k:result[k] for k in ("visible_truth_count","baseline_matched_truth_count",
        "baseline_missed_truth_count","aggregate_detector_counts",
        "observed_candidate_gate_counts_near_visible_truths","elapsed_seconds")}
    summary["selected_axis_comparison"]={k:v for k,v in result["selected_axis_comparison"].items() if k!="panels"}
    print(json.dumps(summary,indent=2))
