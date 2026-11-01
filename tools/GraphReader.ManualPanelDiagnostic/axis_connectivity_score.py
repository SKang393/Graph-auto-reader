# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score frozen connectivity observations against reviewed train examples."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys

from axis_connectivity_probe import BUILD_INPUT_SHA, BUILD_REPORT_SHA, INPUT_SHA, JOIN_SHA, ROOT, RUN, read_checked

sys.path.insert(0, str(ROOT / "tools/GraphReader.ManualOverlayCapture"))
from holdout_axis_score import LABELS_SHA, match_labels  # noqa: E402

FEATURES_SHA = "7b4993a21fde2e6322774f85439ef6ee25634dc170186232e76291dd76ebfd96"
VISUAL_SHA = "810a87a771b2798c2d720f2a3e392b3cdc8ac587f9342ef08c1719eb0c23c3a8"
PREDICTIONS_SHA = "314e6698241d34f9c11b6076c22d75d720dc99046849ea70c526d6ceb714ca37"
BUILD_FEATURES_SHA = "d890336d4c5fd5004a72cd6b208e17bc550c2407902c1a2925ceb0ddc88ff5e2"
BUILD_TRUTH_SHA = "bb2a2f113a3e42904367c408e145b59538302b9f056d45ce8d217d85d9efc7be"


def summarize(features: dict[str, dict], identifiers: set[str]) -> dict:
    if not identifiers <= set(features):
        raise ValueError("missing feature identity")
    result = {"items": len(identifiers), "unmeasured": 0}
    for prefix in ("dark", "faint"):
        result[f"{prefix}_vertical"] = 0
        result[f"{prefix}_horizontal"] = 0
        result[f"{prefix}_either"] = 0
    for identifier in identifiers:
        row = features[identifier]
        if row["status"] != "measured":
            result["unmeasured"] += 1
            continue
        for prefix in ("dark", "faint"):
            vertical, horizontal = row[f"{prefix}_vertical"], row[f"{prefix}_horizontal"]
            if not isinstance(vertical, bool) or not isinstance(horizontal, bool):
                raise ValueError("invalid connectivity flag")
            result[f"{prefix}_vertical"] += int(vertical)
            result[f"{prefix}_horizontal"] += int(horizontal)
            result[f"{prefix}_either"] += int(vertical or horizontal)
    return result


def score_build() -> None:
    features = json.loads(read_checked(RUN / "axis-connectivity-build-features-v1.json", BUILD_FEATURES_SHA))
    inputs = json.loads(read_checked(RUN / "input.json", BUILD_INPUT_SHA))
    truth = json.loads(read_checked(RUN / "truth.json", BUILD_TRUTH_SHA))
    input_ids = {row["id"] for row in inputs["rows"]}
    truth_ids = {row["id"] for row in truth["rows"]}
    feature_map = {row["id"]: row for row in features["rows"]}
    if (features["scope"] != "train-diagnostic-not-acceptance" or features["input_sha256"] != BUILD_INPUT_SHA or
            features["axis_report_sha256"] != BUILD_REPORT_SHA or features["truth_read"] is not False or
            features["optimizer_steps"] != 0 or features["sealed_payload_reads"] != 0 or
            inputs["source_split"] != "internal_train_build" or truth["schema"] != "graphreader.manual-axis-ocr-truth.v1" or
            len(inputs["rows"]) != 76 or len(truth["rows"]) != 76 or len(features["rows"]) != 76 or
            len(input_ids) != 76 or input_ids != truth_ids or input_ids != set(feature_map)):
        raise ValueError("build connectivity binding differs")
    result = {"schema": "graphreader.train-axis-connectivity-build-score.v1",
              "scope": "train-diagnostic-not-acceptance", "features_sha256": BUILD_FEATURES_SHA,
              "input_sha256": BUILD_INPUT_SHA, "truth_sha256": BUILD_TRUTH_SHA,
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "reviewed_text": summarize(feature_map, input_ids),
              "limitations": ["Label boxes were selected and reviewed by an assistant before this experiment.",
                              "Images were previously inspected during development and are not acceptance data.",
                              "Connected-component observations do not prove a production filter is safe."]}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "axis-connectivity-build-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"reviewed_text": result["reviewed_text"], "sha256": hashlib.sha256(encoded).hexdigest()}))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--build", action="store_true")
    if parser.parse_args().build:
        score_build()
        return
    features = json.loads(read_checked(RUN / "axis-connectivity-features-v1.json", FEATURES_SHA))
    joins = json.loads(read_checked(RUN / "tick-ocr-join-v1.json", JOIN_SHA))
    labels = json.loads(read_checked(RUN / "holdout-labels.json", LABELS_SHA))["rows"]
    visual = json.loads(read_checked(RUN / "holdout-unmatched-numeric-visual-labels-v1.json", VISUAL_SHA))
    predictions = json.loads(read_checked(ROOT / "artifacts/manual-marker-input-diagnostic-v1/holdout-axis-server-v1.json",
                                          PREDICTIONS_SHA))
    if (features["scope"] != "train-diagnostic-not-acceptance" or features["input_sha256"] != INPUT_SHA or
            features["join_sha256"] != JOIN_SHA or features["truth_read"] is not False or
            features["optimizer_steps"] != 0 or features["sealed_payload_reads"] != 0 or
            visual["scope"] != "train-assistant-reviewed-not-independent" or
            visual["review_sheet_sha256"] != "f2cdad3410c7fc4e241741fcdd201af060ce2578c8da097bf12d04736547c716" or
            predictions["input_sha256"] != INPUT_SHA or predictions["truth_read"] is not False):
        raise ValueError("connectivity evidence binding differs")
    matched, unmatched, missed = match_labels(labels, joins)
    positive = {row["id"] for row in matched}
    negative = {row["id"] for row in visual["rows"]}
    outputs = {row["id"]: row["server"] for row in predictions["rows"]}
    numeric_unmatched = {identifier for identifier in unmatched if any(
        alt["text"] == outputs[identifier]["text"] and alt["numeric"] for alt in outputs[identifier]["alternatives"])}
    feature_map = {row["id"]: row for row in features["rows"]}
    if (len(labels) != 52 or len(positive) != 46 or missed != 6 or len(unmatched) != 16 or
            len(visual["rows"]) != 7 or len(negative) != 7 or negative != numeric_unmatched or
            len(feature_map) != 62 or len(features["rows"]) != 62 or positive & negative or
            any(row["classification"] not in {"marker", "boxed_symbol", "axis_stroke"} for row in visual["rows"])):
        raise ValueError("review or proposal inventory differs")
    result = {"schema": "graphreader.train-axis-connectivity-score.v1", "scope": "train-diagnostic-not-acceptance",
              "features_sha256": FEATURES_SHA, "visual_review_sha256": VISUAL_SHA,
              "labels_sha256": LABELS_SHA, "predictions_sha256": PREDICTIONS_SHA,
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "reviewed_text": summarize(feature_map, positive),
              "reviewed_structural": summarize(feature_map, negative),
              "unreviewed_unmatched": len(set(unmatched) - negative), "missed_labels": missed,
              "limitations": ["The visual review and labels are assistant-reviewed, not independent.",
                              "Only seven unmatched numeric crops were visually classified.",
                              "Selected proposals and image exposure prevent acceptance or safe threshold claims."]}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "axis-connectivity-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"reviewed_text": result["reviewed_text"], "reviewed_structural": result["reviewed_structural"],
                      "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
