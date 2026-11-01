# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Generate fixed label proposals on the untouched internal train holdout."""

from hashlib import sha256
import json
from pathlib import Path

import cv2
import numpy as np

from ml.ocr.stroke_seeded_xtick_v1.recognition_diagnostic.manual_y_axis_candidate_v1.candidate import propose

ROOT = Path(r"E:\Graph-auto-reader")
RUN = ROOT / "artifacts/manual-axis-ocr-diagnostic-v1"
INPUT_SHA = "c49f7693ce9401c8e90b7b196fc280d528c2a21f012c1e4a41d410a1ae4199fc"
AXIS_SHA = "771136edf0cb411e0d0e3e424950068964c616dbf208ce54a57a8af33e2488a6"
CANDIDATE_SHA = "b4ffb2712c0eac839916f920d22245faec222ffd37aaf9cb85bec6fbae91a823"


def checked_json(path: Path, expected: str) -> dict:
    raw = path.read_bytes()
    if sha256(raw).hexdigest() != expected:
        raise ValueError("pinned input differs")
    return json.loads(raw)


def main() -> None:
    source = ROOT / "ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/manual_y_axis_candidate_v1/candidate.py"
    if sha256(source.read_bytes()).hexdigest() != CANDIDATE_SHA:
        raise ValueError("frozen proposal source differs")
    input_data = checked_json(RUN / "holdout-axis-input.json", INPUT_SHA)
    axes = checked_json(RUN / "holdout-axis.json", AXIS_SHA)
    if input_data["source_split"] != "internal_train_holdout" or axes["source_split"] != "internal_train_holdout" or axes["optimizer_steps"] != 0:
        raise ValueError("holdout scope differs")
    samples = {row["id"]: row for row in input_data["rows"]}
    if len(samples) != 5 or {row["id"] for row in axes["rows"]} != set(samples):
        raise ValueError("holdout source identities differ")
    rows = []
    for row in axes["rows"]:
        sample = samples[row["id"]]
        if not row["success"]:
            rows.append({"id": row["id"], "axis_failed": True, "boxes": []})
            continue
        relative = sample["image_path"]
        if not relative.startswith("artifacts/goal22-runs/marker-train-overlay-corpus-extract-v7/"):
            raise ValueError("source image is outside the frozen holdout capture")
        path = (ROOT / relative).resolve(strict=True)
        if not path.is_relative_to(ROOT.resolve()):
            raise ValueError("source image escaped project root")
        data = path.read_bytes()
        if sha256(data).hexdigest() != sample["image_sha256"]:
            raise ValueError("holdout image hash differs")
        image = cv2.imdecode(np.frombuffer(data, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
        if image is None or image.shape != (sample["height"], sample["width"]):
            raise ValueError("holdout image decode differs")
        top, bottom = sorted((row["y_axis"]["Start"], row["y_axis"]["End"]), key=lambda point: point["Y"])
        geometry = [
            {"GraphX": 0, "GraphY": 0, "ScreenX": bottom["X"], "ScreenY": bottom["Y"]},
            {"GraphX": 1, "GraphY": 0, "ScreenX": bottom["X"] + 1, "ScreenY": bottom["Y"]},
            {"GraphX": 0, "GraphY": 1, "ScreenX": top["X"], "ScreenY": top["Y"]},
        ]
        rows.append({"id": row["id"], "axis_failed": False, "boxes": propose(image, geometry)})
    output = RUN / "holdout-proposals.json"
    if output.exists():
        raise ValueError("holdout proposals already exist")
    result = {"schema": "graphreader.manual-y-axis-holdout-proposals.v1", "source_split": "internal_train_holdout",
              "axis_sha256": AXIS_SHA, "candidate_sha256": CANDIDATE_SHA, "truth_read": False,
              "optimizer_steps": 0, "rows": rows}
    output.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"images": len(rows), "proposals": sum(len(row["boxes"]) for row in rows),
                      "sha256": sha256(output.read_bytes()).hexdigest()}))


if __name__ == "__main__":
    main()
