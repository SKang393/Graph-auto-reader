# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score frozen development crops without dropping omitted or nontext cases."""

from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import math
from pathlib import Path

from axis_connectivity_probe import ROOT, RUN, read_checked

LABEL_SHA = "7c745f3c4f7a843294ab2cd4c972797b9329605d02175d2b0c8f5f886f81d1a5"
INPUT_SHA = "e53c8da9756930b87243a12dbd007c7af9e24ca01c96a44a85e5ab69c0ccb669"
LABEL_PROTOCOL_SHA = "6b2bd4c4158008737e7f88fe91a936679cd898c0e22e551c93d3180d1ab186b4"
SCORE_PROTOCOL_SHA = "d6e50991652c50601e6706431bcf582d66b46f2e2d77def283c8e728955bb38e"
MODEL_SHA = "0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418"


def numeric_value(reading: dict) -> float | None:
    for option in reading["alternatives"]:
        if option["text"] == reading["text"] and option["numeric"] and option["value"] is not None:
            return option["value"]
    return None


def competing_numeric(reading: dict, cutoff: float) -> bool:
    value = numeric_value(reading)
    if value is None:
        return False
    return any(option["numeric"] and option["value"] is not None and option["value"] != value and
               isinstance(option["confidence"], (float, int)) and math.isfinite(option["confidence"]) and
               cutoff < option["confidence"] <= 1 for option in reading["alternatives"])


def summarize(labels: list[dict], predictions: list[dict]) -> dict:
    if (len(labels) != len(predictions) or
            len({row["id"] for row in labels}) != len(labels) or
            {row["id"] for row in labels} != {row["id"] for row in predictions}):
        raise ValueError("prediction and transcription inventories differ")
    by_id = {row["id"]: row["server"] for row in predictions}
    exact = numeric_correct = 0
    category_counts: Counter[str] = Counter()
    unscored_numeric_primaries: Counter[str] = Counter()
    comparisons = {str(cutoff): Counter() for cutoff in (0.0, 0.001, 0.01)}
    for label in labels:
        reading = by_id[label["id"]]
        category_counts[label["category"]] += 1
        expected = label["expected_text"]
        if expected is None:
            if numeric_value(reading) is not None:
                unscored_numeric_primaries[label["category"]] += 1
            continue
        if label["category"] != "numeric_text":
            raise ValueError("transcribed crop is not numeric text")
        is_exact = reading["text"] == expected
        is_numeric_correct = numeric_value(reading) == float(expected)
        exact += is_exact
        numeric_correct += is_numeric_correct
        for cutoff, counts in comparisons.items():
            key = "exact" if is_exact else "wrong"
            counts[key] += 1
            if competing_numeric(reading, float(cutoff)):
                counts[key + "_with_competing_numeric"] += 1
    return {"inventory": len(labels), "categories": dict(sorted(category_counts.items())),
            "transcribed": sum(row["expected_text"] is not None for row in labels),
            "exact": exact, "numeric_value_correct": numeric_correct,
            "unscored_numeric_primaries": dict(sorted(unscored_numeric_primaries.items())),
            "competing_numeric_by_cutoff": {key: dict(counts) for key, counts in comparisons.items()}}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--predictions-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    labels = json.loads(read_checked(RUN / "dev-axis-transcriptions-v1.json", LABEL_SHA))
    predictions = json.loads(read_checked(ROOT / "artifacts/manual-marker-input-diagnostic-v1/dev-axis-server-v1.json",
                                          args.predictions_sha256))
    read_checked(ROOT / "tools/GraphReader.ManualPanelDiagnostic/DEV_OCR_TRANSCRIPTION_PROTOCOL.md", LABEL_PROTOCOL_SHA)
    read_checked(ROOT / "tools/GraphReader.ManualPanelDiagnostic/DEV_OCR_SCORE_PROTOCOL.md", SCORE_PROTOCOL_SHA)
    if (labels["scope"] != "assistant-review-before-ocr-predictions" or
            labels["protocol_sha256"] != LABEL_PROTOCOL_SHA or labels["independently_adjudicated"] is not False or
            predictions["scope"] != "dev-diagnostic-not-acceptance" or predictions["source_split"] != "internal_dev" or
            predictions["input_sha256"] != INPUT_SHA or predictions["runtime_description_sha256"] != args.runtime_sha256 or
            predictions["model_sha256"] != MODEL_SHA or predictions["source_count"] != 15 or
            predictions["inference_provider"] != "CPUExecutionProvider" or predictions["truth_read"] is not False or
            predictions["optimizer_steps"] != 0 or predictions["sealed_payload_reads"] != 0):
        raise ValueError("development OCR evidence scope differs")
    summary = summarize(labels["rows"], predictions["rows"])
    if summary["inventory"] != 134 or summary["transcribed"] != 115:
        raise ValueError("development OCR evidence inventory differs")
    value = {"schema": "graphreader.dev-axis-ocr-score.v1", "scope": "dev-diagnostic-not-acceptance",
             "labels_sha256": LABEL_SHA, "predictions_sha256": args.predictions_sha256,
             "runtime_description_sha256": args.runtime_sha256, "score_protocol_sha256": SCORE_PROTOCOL_SHA,
             "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "summary": summary}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "dev-axis-ocr-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
