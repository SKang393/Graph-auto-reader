# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score the frozen train-only reviewed-label OCR run after inference."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path

from propose_dense_tick_windows import OUTPUT, RUN, checked_json

INPUT_SHA = "06d77e4621e536ea3328e6baed894d2a8f449bc5d6c2534df0c0f31dba2b8d81"
TRUTH_SHA = "bb2a2f113a3e42904367c408e145b59538302b9f056d45ce8d217d85d9efc7be"
MODEL_SHA = "0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418"


def score_rows(truth: list[dict], rows: list[dict]) -> dict:
    expected = {row["id"]: row["expected"] for row in truth}
    predictions = {row["id"]: row["server"] for row in rows}
    if (len(expected) != len(truth) or len(predictions) != len(rows) or
            set(expected) != set(predictions) or
            any(not isinstance(value, str) or not value for value in expected.values())):
        raise ValueError("reviewed-label identity differs")
    counts = {"labels": len(truth), "exact": 0, "wrong": 0, "empty": 0,
              "numeric_primary": 0, "non_numeric_primary": 0, "recognition_failures": 0}
    thresholds = {str(value): {"correct_flagged": 0, "correct_unflagged": 0,
                                "wrong_flagged": 0, "wrong_unflagged": 0}
                  for value in (0, 0.001, 0.01, 0.05, 0.1)}
    for identifier, item in predictions.items():
        text = item["text"]
        exact = text == expected[identifier]
        counts["recognition_failures"] += int(item["failure"] is not None)
        if text is None or text == "":
            counts["empty"] += 1
            continue
        if not isinstance(text, str):
            raise ValueError("invalid OCR text")
        counts["exact" if exact else "wrong"] += 1
        primary = [alt for alt in item["alternatives"] if alt["text"] == text]
        if not primary:
            raise ValueError("primary reading is absent from alternatives")
        primary.sort(key=lambda alt: -alt["confidence"])
        best = primary[0]
        if not best["numeric"]:
            counts["non_numeric_primary"] += 1
            continue
        if best["value"] is None or not math.isfinite(best["value"]):
            raise ValueError("numeric primary value is invalid")
        counts["numeric_primary"] += 1
        competing = [alt["confidence"] for alt in item["alternatives"]
                     if alt["numeric"] and alt["value"] is not None and math.isfinite(alt["value"]) and
                     alt["value"] != best["value"] and math.isfinite(alt["confidence"]) and
                     0 < alt["confidence"] <= 1]
        largest = max(competing, default=0)
        for threshold, bucket in thresholds.items():
            bucket[("correct_" if exact else "wrong_") +
                   ("flagged" if largest > float(threshold) else "unflagged")] += 1
    return {"counts": counts, "competing_numeric_confidence_thresholds": thresholds}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    inputs = checked_json(OUTPUT / "input.json", INPUT_SHA)
    truth = checked_json(OUTPUT / "truth.json", TRUTH_SHA)
    report = checked_json(RUN / "reviewed-axis-server-v1.json", args.report_sha256)
    if (inputs["scope"] != "assistant-reviewed-training-only" or inputs["source_split"] != "internal_train_build" or
            len(inputs["rows"]) != 76 or truth["schema"] != "graphreader.manual-axis-ocr-truth.v1" or
            truth["labels_sha256"] != "868f256c1bbabc635585dbda27d9de01c472e17c30edfe54da2b8b0ffd377e2d" or
            {row["id"] for row in inputs["rows"]} != {row["id"] for row in truth["rows"]} or
            report["schema"] != "graphreader.manual-reviewed-server-ocr.v1" or
            report["scope"] != "train-diagnostic-not-acceptance" or report["input_sha256"] != INPUT_SHA or
            report["runtime_description_sha256"] != args.runtime_sha256 or report["model_sha256"] != MODEL_SHA or
            report["source_count"] != 8 or report["source_split"] != "internal_train_build" or
            report["crop_profile"] != "candidate-standard-padding" or report["truth_read"] is not False or
            report["optimizer_steps"] != 0 or report["sealed_payload_reads"] != 0):
        raise ValueError("reviewed-label report binding differs")
    result = {"schema": "graphreader.manual-reviewed-server-score.v1", "scope": "train-diagnostic-not-acceptance",
              "input_sha256": INPUT_SHA, "truth_sha256": TRUTH_SHA, "prediction_sha256": args.report_sha256,
              "runtime_description_sha256": args.runtime_sha256,
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              **score_rows(truth["rows"], report["rows"]),
              "limitations": ["Known, visually selected boxes from eight training images are not detection results.",
                              "Labels were assistant-reviewed and not independently adjudicated.",
                              "Threshold counts are descriptive comparisons, not an approved calibration policy.",
                              "The run does not establish axis, marker, workflow, or release acceptance."]}
    blob = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (OUTPUT / "reviewed-axis-server-score-v1.json").open("xb") as stream:
        stream.write(blob)
    print(json.dumps({"counts": result["counts"], "thresholds": result["competing_numeric_confidence_thresholds"],
                      "sha256": hashlib.sha256(blob).hexdigest()}))


if __name__ == "__main__":
    main()
