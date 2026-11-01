# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score frozen train-holdout proposals without dropping missed labels."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

from propose_dense_tick_windows import OUTPUT, RUN, checked_json
from reviewed_axis_score import MODEL_SHA, score_rows

INPUT_SHA = "469b5ee37f1d2ad80b4dcb638a3d2183c4949e003de3322d9a186e140228779a"
JOIN_SHA = "537684e62452558a6370980e761f9b92e488959ce691b467f73835f3545f412d"
LABELS_SHA = "0d80b0663457f25a89e7d39a0e60f5e156604a0d382dcb9e8f132f7c29331713"


def iou(first: list[float], second: list[float]) -> float:
    intersection = max(0, min(first[2], second[2]) - max(first[0], second[0])) * \
        max(0, min(first[3], second[3]) - max(first[1], second[1]))
    union = (first[2] - first[0]) * (first[3] - first[1]) + \
        (second[2] - second[0]) * (second[3] - second[1]) - intersection
    return intersection / union if union > 0 else 0


def match_labels(labels: list[dict], joins: list[dict]) -> tuple[list[dict], list[str], int]:
    remaining = set(range(len(labels)))
    matched, unmatched = [], []
    if len({row["id"] for row in joins}) != len(joins):
        raise ValueError("duplicate proposal identity")
    for row in joins:
        choices = [(iou(row["box"], labels[index]["box_xyxy"]), index) for index in remaining
                   if labels[index]["sample_id"] == row["sample_id"]]
        overlap, index = max(choices) if choices else (0, None)
        if overlap < 0.5:
            unmatched.append(row["id"])
            continue
        remaining.remove(index)
        matched.append({"id": row["id"], "expected": labels[index]["label"]})
    return matched, unmatched, len(remaining)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    inputs = checked_json(OUTPUT / "tick-ocr-input-v1.json", INPUT_SHA)
    joins = checked_json(OUTPUT / "tick-ocr-join-v1.json", JOIN_SHA)
    labels = checked_json(OUTPUT / "holdout-labels.json", LABELS_SHA)["rows"]
    report = checked_json(RUN / "holdout-axis-server-v1.json", args.report_sha256)
    if (inputs["schema"] != "graphreader.manual-axis-ocr-input.v1" or inputs["scope"] != "exposed-train-only" or
            inputs["source_split"] != "internal_train_holdout" or len(inputs["rows"]) != 62 or
            len(joins) != 62 or len(labels) != 52 or
            report["schema"] != "graphreader.manual-reviewed-server-ocr.v1" or
            report["scope"] != "train-diagnostic-not-acceptance" or report["input_sha256"] != INPUT_SHA or
            report["runtime_description_sha256"] != args.runtime_sha256 or report["model_sha256"] != MODEL_SHA or
            report["source_split"] != "internal_train_holdout" or report["source_count"] != 5 or
            report["input_profile"] != "tick-aligned-proposals" or report["truth_read"] is not False or
            report["optimizer_steps"] != 0 or report["sealed_payload_reads"] != 0):
        raise ValueError("holdout report binding differs")
    input_by_id = {row["id"]: row for row in inputs["rows"]}
    output_by_id = {row["id"]: row for row in report["rows"]}
    if (len(input_by_id) != 62 or len(output_by_id) != 62 or len(report["rows"]) != 62 or
            {row["id"] for row in joins} != set(input_by_id) or set(input_by_id) != set(output_by_id) or
            any(row["box"] != input_by_id[row["id"]]["box_xyxy"] for row in joins)):
        raise ValueError("holdout proposal inventory differs")
    truth, unmatched, missed = match_labels(labels, joins)
    matched_outputs = [output_by_id[row["id"]] for row in truth]
    unmatched_numeric = sum(any(alt["text"] == output_by_id[identifier]["server"]["text"] and alt["numeric"]
                                for alt in output_by_id[identifier]["server"]["alternatives"])
                            for identifier in unmatched)
    result = {"schema": "graphreader.manual-holdout-server-score.v1", "scope": "train-diagnostic-not-acceptance",
              "input_sha256": INPUT_SHA, "join_sha256": JOIN_SHA, "labels_sha256": LABELS_SHA,
              "prediction_sha256": args.report_sha256, "runtime_description_sha256": args.runtime_sha256,
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "base_scorer_sha256": hashlib.sha256(Path(__file__).with_name("reviewed_axis_score.py").read_bytes()).hexdigest(),
              "coverage": {"labels": len(labels), "proposals": len(joins), "matched": len(truth),
                           "missed": missed, "unmatched_proposals": len(unmatched),
                           "unmatched_numeric_proposals": unmatched_numeric},
              **score_rows(truth, matched_outputs),
              "limitations": ["Images and labels were previously exposed during development.",
                              "Labels are incomplete and assistant-reviewed, not independently adjudicated.",
                              "Unmatched proposals are not automatically false positives.",
                              "Threshold comparisons are descriptive and do not approve a policy or release."]}
    blob = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (OUTPUT / "holdout-axis-server-score-v1.json").open("xb") as stream:
        stream.write(blob)
    print(json.dumps({"coverage": result["coverage"], "counts": result["counts"],
                      "thresholds": result["competing_numeric_confidence_thresholds"],
                      "sha256": hashlib.sha256(blob).hexdigest()}))


if __name__ == "__main__":
    main()
