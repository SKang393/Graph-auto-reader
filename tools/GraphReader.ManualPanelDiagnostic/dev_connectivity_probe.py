# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Freeze proposal connectivity on study-partitioned development images."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path
import sys

import cv2
import numpy as np

from axis_connectivity_probe import ROOT, RUN, connected_to_axes, read_checked
from tick_rank_probe import choose

sys.path.insert(0, str(ROOT))
from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.manual_y_axis_candidate_v1.candidate import propose  # noqa: E402

MANIFEST_SHA = "d0adfe55915cc323ff408acf342110972eca2a92ae188ac43b8090d91054afd4"
REPORT_SHA = "bc6eaa24389cf78eb637785bc7b3a79cab37542c3259da0ed58fdea880f85904"
CANDIDATE_SHA = "b4ffb2712c0eac839916f920d22245faec222ffd37aaf9cb85bec6fbae91a823"
RANKER_SHA = "e61192366927c3f48b580a9fade972a2e1286777488b4490c633523ea1d0b69d"
CONNECTIVITY_SHA = "e839bedb02767bcbb23470657cd8fda40935824f5f03d5d40ecdce27e9c64598"
PROTOCOL_SHA = "3e8f703e4ade6595b3ccfd9a72d901dbe2ebee8833b9b8eee6d4178363c68c2c"


def panel_boxes(image: np.ndarray, crop: dict, selected: dict) -> tuple[list[list[int]], tuple]:
    x, y, width, height = (int(crop[key]) for key in ("X", "Y", "Width", "Height"))
    if ((x, y, width, height) != (crop["X"], crop["Y"], crop["Width"], crop["Height"]) or
            x < 0 or y < 0 or x + width > image.shape[1] or y + height > image.shape[0]):
        raise ValueError("development panel crop differs")
    points = sorted((selected["Intersection"], selected["TopLeft"]), key=lambda point: point["Y"])
    top, bottom = points
    axes = tuple((selected[key]["X"] + x, selected[key]["Y"] + y)
                 for key in ("Intersection", "TopLeft", "BottomRight"))
    if bottom["Y"] - top["Y"] < 10:
        return [], axes
    geometry = [
        {"GraphX": 0, "GraphY": 0, "ScreenX": bottom["X"], "ScreenY": bottom["Y"]},
        {"GraphX": 1, "GraphY": 0, "ScreenX": bottom["X"] + 1, "ScreenY": bottom["Y"]},
        {"GraphX": 0, "GraphY": 1, "ScreenX": top["X"], "ScreenY": top["Y"]},
    ]
    boxes = [[left + x, upper + y, right + x, lower + y]
             for left, upper, right, lower in propose(image[y:y + height, x:x + width], geometry)]
    return boxes, axes


def main() -> None:
    manifest = json.loads(read_checked(RUN / "dev-axis-capture-v1/manifest.json", MANIFEST_SHA))
    report = json.loads(read_checked(RUN / "panel-diagnostic-dev-contrast-v4/report.json", REPORT_SHA))
    candidate = ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/manual_y_axis_candidate_v1/candidate.py"
    read_checked(candidate, CANDIDATE_SHA)
    read_checked(Path(__file__).with_name("tick_rank_probe.py"), RANKER_SHA)
    read_checked(Path(__file__).with_name("axis_connectivity_probe.py"), CONNECTIVITY_SHA)
    protocol = read_checked(Path(__file__).with_name("DEV_CONNECTIVITY_PROTOCOL.md"), PROTOCOL_SHA)
    sources = {row["id"]: row for row in manifest["rows"]}
    if (manifest["source_split"] != "internal_dev" or manifest["sealed_payload_reads"] != 0 or
            manifest["optimizer_steps"] != 0 or len(sources) != 16 or
            any(row["train_overlap"] for row in sources.values()) or
            report["source_split"] != "internal_dev" or report["input_sha256"] != MANIFEST_SHA or
            report["optimizer_steps"] != 0 or {row["id"] for row in report["rows"]} != set(sources)):
        raise ValueError("development source scope differs")
    rows = []
    total = 0
    for item in report["rows"]:
        source = sources[item["id"]]
        relative = source["image_path"]
        if not relative.startswith("artifacts/manual-axis-ocr-diagnostic-v1/dev-axis-capture-v1/images/"):
            raise ValueError("development source path differs")
        path = (ROOT / relative).resolve(strict=True)
        if not path.is_relative_to(ROOT.resolve()):
            raise ValueError("development source escaped project")
        data = read_checked(path, source["image_sha256"])
        image = cv2.imdecode(np.frombuffer(data, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
        if image is None or image.shape != (source["height"], source["width"]):
            raise ValueError("development image dimensions differ")
        panels = []
        for panel in item["panels"]:
            if not panel["axis"]["success"]:
                panels.append({"order": panel["Order"], "status": "axis_failed", "proposals": []})
                continue
            selected, _, _ = choose(panel)
            boxes, axes = panel_boxes(image, panel["crop"], selected)
            proposals = []
            for box in boxes:
                dark = connected_to_axes(image, box, axes, 128)
                faint = connected_to_axes(image, box, axes, 200)
                proposals.append({"id": f"dev-proposal-{total:04d}", "box": box,
                                  "dark_vertical": dark[0], "dark_horizontal": dark[1],
                                  "faint_vertical": faint[0], "faint_horizontal": faint[1]})
                total += 1
            panels.append({"order": panel["Order"], "status": "measured", "proposals": proposals})
        rows.append({"id": item["id"], "panels": panels})
    result = {"schema": "graphreader.dev-axis-connectivity-proposals.v1", "scope": "dev-diagnostic-not-acceptance",
              "manifest_sha256": MANIFEST_SHA, "axis_report_sha256": REPORT_SHA,
              "protocol_sha256": hashlib.sha256(protocol).hexdigest(),
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "candidate_sha256": CANDIDATE_SHA, "ranker_sha256": RANKER_SHA, "connectivity_sha256": CONNECTIVITY_SHA,
              "truth_read": False, "optimizer_steps": 0, "sealed_payload_reads": 0, "rows": rows}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "dev-connectivity-proposals-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"images": len(rows), "panels": sum(len(row["panels"]) for row in rows),
                      "proposals": total, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
