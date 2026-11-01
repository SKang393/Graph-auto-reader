# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Replay the existing tick-rank probe on frozen synthetic training panels.

Python OpenCV supplies diagnostic lines, so this is not production-provider
parity and cannot authorize a runtime change or model.
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
EXTRACTION = ROOT / "artifacts/goal22-runs/axis-ranker-full-extraction-run-v1"
OUTPUT = ROOT / "artifacts/goal22-runs/axis-tick-rank-train-probe-v1"
RECEIPT_SHA = "588f1375544477505864a228922452266c396b0008e2d66efe20beb56f95bd58"
PROBE = ROOT / "tools/GraphReader.ManualPanelDiagnostic/tick_rank_probe.py"


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def checked(path: Path, expected: str) -> bytes:
    data = path.read_bytes()
    if digest(data) != expected:
        raise ValueError("Frozen input checksum differs")
    return data


def candidate_identity(panel: dict, snapshot: dict) -> str:
    identity = {
        "source_sha256": panel["source_sha256"],
        "panel_sha256": panel["panel_sha256"],
        "original_rank": snapshot["Rank"] + 1,
        "snapshot": snapshot,
    }
    return digest(json.dumps(identity, sort_keys=True, separators=(",", ":"), allow_nan=False).encode())


def line_rows(lines: np.ndarray | None) -> list[dict]:
    if lines is None:
        return []
    rows = []
    for x1, y1, x2, y2 in np.asarray(lines).reshape(-1, 4):
        values = [float(x1), float(y1), float(x2), float(y2)]
        if not all(np.isfinite(values)):
            raise ValueError("Nonfinite diagnostic line")
        rows.append({"Segment": {"Start": {"X": values[0], "Y": values[1]},
                                 "End": {"X": values[2], "Y": values[3]}}})
    return rows


def diagnostic_lines(encoded: bytes, width: int, height: int) -> list[dict]:
    image = cv2.imdecode(np.frombuffer(encoded, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
    if image is None or image.shape != (height, width):
        raise ValueError("Diagnostic image dimensions differ")
    lsd = cv2.createLineSegmentDetector(cv2.LSD_REFINE_STD).detect(image)[0]
    edges = cv2.Canny(image, 40, 120, apertureSize=3, L2gradient=True)
    hough = cv2.HoughLinesP(edges, 1, np.pi / 180, 24, minLineLength=12, maxLineGap=4)
    return line_rows(lsd) + line_rows(hough)


def main() -> None:
    if OUTPUT.exists():
        raise FileExistsError("Exploratory output already exists")
    receipt = json.loads(checked(EXTRACTION / "receipt.json", RECEIPT_SHA))
    if (receipt["schema"] != "graphreader.axis-ranker-full-extraction-receipt.v1" or
            receipt["source_count"] != 64 or receipt["panel_count"] != 96 or
            receipt["dev_read"] is not False or receipt["reserved_read"] is not False or
            receipt["truth_read"] is not False or receipt["optimizer_steps"] != 0):
        raise ValueError("Frozen extraction scope differs")
    spec = importlib.util.spec_from_file_location("existing_tick_rank_probe", PROBE)
    if spec is None or spec.loader is None:
        raise RuntimeError("Existing tick-rank probe cannot be loaded")
    probe = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(probe)
    cv2.setNumThreads(1)
    rows = []
    for index, source in enumerate(receipt["sources"]):
        report = json.loads(checked(EXTRACTION / f"{index:03}.json", source["report_sha256"]))
        if (source["process_status"] != "completed" or source["exit_code"] != 0 or
                report["schema"] != "graphreader.axis-candidate-panel-batch-evidence.v1" or
                report["scope"] != "public_synthetic_train" or report["split"] != "train"):
            raise ValueError("Synthetic source capture failed")
        for panel in report["panel_results"]:
            if panel["status"] != "success" or not panel["candidate_snapshots"]:
                rows.append({"status": "failed", "source_sha256": panel["source_sha256"],
                             "panel_sha256": panel["panel_sha256"]})
                continue
            image = (ROOT / panel["panel_image_path"]).resolve(strict=True)
            if not image.is_relative_to(ROOT.resolve()):
                raise ValueError("Panel image leaves the project")
            encoded = checked(image, panel["panel_sha256"])
            lines = diagnostic_lines(encoded, panel["panel_width"], panel["panel_height"])
            snapshots = panel["candidate_snapshots"]
            selected, regular, count = probe.choose({
                "Width": panel["panel_width"], "Height": panel["panel_height"],
                "axis": {"ranked_candidates": snapshots, "line_candidates": lines}})
            rank = selected["Rank"]
            if not isinstance(rank, int) or rank < 0 or rank >= len(snapshots) or snapshots[rank] != selected:
                raise ValueError("Candidate rank no longer matches its snapshot")
            rows.append({
                "status": "completed", "source_sha256": panel["source_sha256"],
                "panel_sha256": panel["panel_sha256"], "selected_rank": rank,
                "baseline_identity_sha256": candidate_identity(panel, snapshots[0]),
                "selected_identity_sha256": candidate_identity(panel, selected),
                "regular_tick_run": regular, "tick_clusters": count,
            })
    if len(rows) != 96:
        raise ValueError("All 96 imported panels must remain in the denominator")
    output = {
        "schema": "graphreader.axis-tick-rank-synthetic-train-probe.v1",
        "scope": "exploratory_synthetic_train_only_not_acceptance",
        "extraction_receipt_sha256": RECEIPT_SHA,
        "source_sha256": digest(Path(__file__).read_bytes()),
        "reused_probe_sha256": digest(PROBE.read_bytes()),
        "opencv_version": cv2.__version__,
        "opencv_build_sha256": digest(cv2.getBuildInformation().encode()),
        "production_line_provider_parity": False,
        "truth_read": False, "optimizer_steps": 0, "development_reads": 0,
        "sealed_reads": 0, "production_approval": False, "rows": rows,
    }
    OUTPUT.mkdir(parents=True, exist_ok=False)
    path = OUTPUT / "predictions.json"
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(output, stream, sort_keys=True, indent=2, allow_nan=False)
        stream.write("\n")
    print(json.dumps({"panels": len(rows), "completed": sum(r["status"] == "completed" for r in rows),
                      "changed": sum(r.get("selected_rank", 0) != 0 for r in rows),
                      "sha256": digest(path.read_bytes())}))


if __name__ == "__main__":
    main()
