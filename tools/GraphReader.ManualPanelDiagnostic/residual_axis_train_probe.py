# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare a frozen axis ranker with imperfect, train-only manual anchors."""
from hashlib import sha256
import json
import math
from pathlib import Path
import sys

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from ml.axis.candidate_ranker_foundation_v1 import features  # noqa: E402
from ml.axis.gpu_candidate_ranker_v1.trainer_v3 import require_gpu  # noqa: E402
from ml.axis.residual_candidate_ranker_v1 import evaluate_dev, evaluate_train, runner  # noqa: E402

DIAGNOSTIC = "artifacts/manual-axis-ocr-diagnostic-v1/panel-diagnostic-original-contrast-v4/report.json"
DIAGNOSTIC_SHA = "71177f3d6c14de7525d1eec0d03596fb35109248ef9b879fc0a81a94ed4a651b"
INPUT = "artifacts/manual-marker-input-diagnostic-v1/original-raster-input-v1.json"
INPUT_SHA = "b9044372d5d6b37257ef0e1823935eaed840b99f9dc5a66a2c5359a1b7beeba6"
ANCHORS = "artifacts/manual-marker-input-diagnostic-v1/assisted-calibration-bindings-v1.json"
ANCHORS_SHA = "e092e6c352b3bd25ccf59debd72c6df60a5bfe2daf0b6a9b2384f0a8ad19cb9c"
OUT = ROOT / "artifacts/manual-axis-ocr-diagnostic-v1/residual-axis-train-v1"


def axis_error(line: dict, points: list[list[float]], height: float) -> float:
    """Mean distance to an infinite candidate line, normalized by panel height."""
    start, end = line["Start"], line["End"]
    x0, y0, x1, y1 = (float(start["X"]), float(start["Y"]), float(end["X"]), float(end["Y"]))
    values = [x0, y0, x1, y1, float(height)] + [float(v) for point in points for v in point]
    if len(points) != 2 or any(len(point) != 2 for point in points) or not all(map(math.isfinite, values)):
        raise ValueError("Axis line or reference anchor is invalid")
    length = math.hypot(x1 - x0, y1 - y0)
    if length == 0 or height <= 0:
        raise ValueError("Axis line or panel height is degenerate")
    return sum(abs((x1 - x0) * (y0 - y) - (x0 - x) * (y1 - y0)) / length
               for x, y in points) / (len(points) * height)


def main() -> None:
    import torch

    if OUT.exists():
        raise FileExistsError("Training-only probe output already exists")
    checkpoint = evaluate_dev.read_bound(
        "artifacts/goal22-runs/axis-residual-ranker-v1/checkpoint.json", evaluate_dev.CHECKPOINT_SHA)
    training = evaluate_dev.read_bound(
        "artifacts/goal22-runs/axis-residual-ranker-v1/report.json", evaluate_dev.REPORT_SHA)
    if (training.get("status") != "training_complete_unapproved" or
            training.get("checkpoint_sha256") != evaluate_dev.CHECKPOINT_SHA or
            training.get("steps") != runner.STEPS or checkpoint.get("production_approved") is not False):
        raise ValueError("Frozen ranker provenance differs")
    diagnostic = evaluate_dev.read_bound(DIAGNOSTIC, DIAGNOSTIC_SHA)
    inputs = evaluate_dev.read_bound(INPUT, INPUT_SHA)
    if (diagnostic.get("source_split") != "internal_train_original" or diagnostic.get("input_sha256") != INPUT_SHA or
            inputs.get("scope") != "train-only-original-raster-no-truth" or
            len(diagnostic.get("rows", [])) != 20 or len(inputs.get("rows", [])) != 20):
        raise ValueError("Training image scope or denominator differs")
    source_rows = {row["id"]: row for row in inputs["rows"]}
    if len(source_rows) != 20 or len({row["id"] for row in diagnostic["rows"]}) != 20:
        raise ValueError("Training source identities are not unique")

    require_gpu(torch)
    device = torch.device("cuda:0")
    state = checkpoint["state_dict"]
    scale, hw, hb, ow, ob = [torch.tensor(state[key], dtype=torch.float32, device=device) for key in
                              ("feature_scale", "hidden.weight", "hidden.bias", "output.weight", "output.bias")]
    require_gpu(torch, scale, hw, hb, ow, ob)
    predictions, panel_index, max_difference = [], {}, 0.0
    for source in diagnostic["rows"]:
        metadata = source_rows[source["id"]]
        for panel in source["panels"]:
            candidates = panel["axis"]["ranked_candidates"]
            if not candidates:
                raise ValueError("A captured training panel has no axis candidates")
            group = features.snapshot_group(metadata["image_sha256"], panel["crop_sha256"],
                                            panel["Width"], panel["Height"], candidates)
            values = [row["features"] for row in group["rows"]]
            cpu = evaluate_train.score(values, checkpoint)
            x = torch.tensor(values, dtype=torch.float32, device=device)
            gpu = x[:, 0] + runner.RESIDUAL_SCALE * torch.nn.functional.linear(
                torch.tanh(torch.nn.functional.linear(x * scale, hw, hb)), ow, ob).squeeze(-1)
            require_gpu(torch, x, gpu)
            gpu_values = gpu.detach().cpu().numpy()
            max_difference = max(max_difference, float(np.max(np.abs(cpu - gpu_values))))
            if int(np.argmax(cpu)) != int(np.argmax(gpu_values)):
                raise ValueError("CPU and GPU candidate ranking differ")
            key = (source["id"], panel["crop_sha256"])
            if key in panel_index:
                raise ValueError("Training panel identity is not unique")
            panel_index[key] = panel
            predictions.append({"id": source["id"], "panel_sha256": panel["crop_sha256"],
                                "source_sha256": metadata["image_sha256"], "rank": int(np.argmax(cpu)) + 1})
    if len(predictions) != 21:
        raise ValueError("Captured training panel denominator differs")
    prediction_document = {"schema": "graphreader.residual-axis-manual-train-predictions.v1",
                           "scope": "train-only-private", "diagnostic_sha256": DIAGNOSTIC_SHA,
                           "checkpoint_sha256": evaluate_dev.CHECKPOINT_SHA, "anchor_reads_before_prediction": 0,
                           "cpu_gpu_ranking_disagreements": 0, "max_absolute_score_difference": max_difference,
                           "panels": predictions}
    prediction_raw = (json.dumps(prediction_document, sort_keys=True, separators=(",", ":")) + "\n").encode()
    OUT.mkdir()
    runner.write_new(OUT / "predictions.json", prediction_raw)

    # Only after all predictions are durable may the anchor-derived reference be opened.
    anchors = evaluate_dev.read_bound(ANCHORS, ANCHORS_SHA)
    if (anchors.get("scope") != "train-only-unapproved" or anchors.get("eligible_images") != 7 or
            anchors.get("excluded_images") != 1 or anchors.get("sealed_payload_reads") != 0 or
            anchors.get("recorded_anchors_are_not_user_confirmation") is not True):
        raise ValueError("Manual calibration reference scope differs")
    by_panel = {(row["id"], row["panel_sha256"]): row for row in predictions}
    pairs, changed, needs_review = [], 0, 0
    for reference in anchors["rows"]:
        key = (reference["id"], reference["panel_sha256"])
        prediction, panel = by_panel[key], panel_index[key]
        if reference["source_sha256"] != prediction["source_sha256"]:
            raise ValueError("Training reference source hash differs")
        candidates = panel["axis"]["ranked_candidates"]
        selected = prediction["rank"] - 1
        if not 0 <= selected < len(candidates):
            raise ValueError("Frozen prediction rank differs")
        points = [reference["origin"], reference["x_anchor"]]
        baseline = axis_error(candidates[0]["XAxisLine"], points, panel["Height"])
        candidate = axis_error(candidates[selected]["XAxisLine"], points, panel["Height"])
        pairs.append((baseline, candidate))
        changed += selected != 0
        needs_review += panel["axis"]["needs_review"] is True
    if len(pairs) != 7 or len({row["id"] for row in anchors["rows"]}) != 7:
        raise ValueError("Manual reference denominator differs")
    result = {"schema": "graphreader.residual-axis-manual-train-evaluation.v1", "scope": "exposed-train-only",
              "captured_sources": 20, "captured_panels": 21, "anchor_examples": 8,
              "excluded_anchor_examples": 1, "native_needs_review": needs_review,
              "input_sha256": INPUT_SHA, "diagnostic_sha256": DIAGNOSTIC_SHA, "anchors_sha256": ANCHORS_SHA,
              "checkpoint_sha256": evaluate_dev.CHECKPOINT_SHA, "prediction_sha256": sha256(prediction_raw).hexdigest(),
              "anchor_reads_before_prediction": 0, "cpu_gpu_ranking_disagreements": 0,
              "max_absolute_score_difference": max_difference, "optimizer_steps": 0,
              "development_reads": 0, "sealed_reads": 0, "production_approved": False,
              **evaluate_dev.summarize(pairs, changed, 0)}
    result["native_mean_normalized_anchor_line_distance"] = result.pop("native_mean_normalized_corner_error")
    result["candidate_mean_normalized_anchor_line_distance"] = result.pop("candidate_mean_normalized_corner_error")
    raw = (json.dumps(result, sort_keys=True, separators=(",", ":"), allow_nan=False) + "\n").encode()
    runner.write_new(OUT / "score.json", raw)
    print(raw.decode(), end="")


if __name__ == "__main__":
    main()
