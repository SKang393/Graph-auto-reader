# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Measure an exploratory crop-level consensus without changing the workflow."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
import math
import re
import statistics
from pathlib import Path

from axis_connectivity_probe import ROOT, RUN, read_checked
from score_crop_padding import (DEV_INPUT_SHA, DEV_LABEL_SHA, LABELS_SHA, JOIN_SHA,
                                MODEL_SHA, TRAIN_INPUT_SHA, capture, match_labels)
from score_dev_ocr import competing_numeric, numeric_value

FEATURE_SHA = "9b1475548adbeab5b34b46ac5e0f87a054701e0f3e2f49007699560f61817107"
BUILD_INPUT_SHA = "06d77e4621e536ea3328e6baed894d2a8f449bc5d6c2534df0c0f31dba2b8d81"
BUILD_TRUTH_SHA = "bb2a2f113a3e42904367c408e145b59538302b9f056d45ce8d217d85d9efc7be"
RUNTIME_SHA = "a700c13055f63fa817043a5667165888587276e7c5a2a8a6fd38c99238185295"
TRAIN_PAD_SHA = "379523781fd5e16b9186cd50d901e69d4319e2b0487c8450020fbefc0e527472"
TRAIN_TIGHT_SHA = "7efa7264a50fe7d29988b8af9d08e3ef36233c176f69257b9cfe63d793ec63c8"
DEV_PAD_SHA = "d4456528962d6dcd22a8d52ab4ebad9cf68ed24a561fd119280050d938a662eb"
DEV_TIGHT_SHA = "c727c0d1a45c0091c998c2278c5a416e3ce0556641294b46437c74314d89ef73"
REVIEW_SHA = "810a87a771b2798c2d720f2a3e392b3cdc8ac587f9342ef08c1719eb0c23c3a8"
SIMPLE_NUMBER = re.compile(r"[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)\Z")


def finite_number(reading: dict) -> float | None:
    number = numeric_value(reading)
    return number if isinstance(number, (int, float)) and math.isfinite(number) else None


def summarize(features: list[dict], padded: list[dict], tight: list[dict], labels: dict[str, dict]) -> dict:
    first = {row["id"]: row["server"] for row in padded}
    second = {row["id"]: row["server"] for row in tight}
    by_id = {row["id"]: row for row in features}
    if (len(first) != len(padded) or len(second) != len(tight) or len(by_id) != len(features) or
            set(first) != set(second) or set(first) != set(by_id) or set(first) != set(labels)):
        raise ValueError("tick consensus inventory differs")
    distances = defaultdict(list)
    for identifier, feature in by_id.items():
        if finite_number(first[identifier]) is not None:
            distances[feature["image_sha256"]].append(feature["gap_height"])
    medians = {key: statistics.median(values) for key, values in distances.items() if len(values) >= 3}
    counts: Counter[str] = Counter()
    unscored: dict[str, Counter] = {"total": Counter(), "qualified": Counter()}
    for identifier, feature in by_id.items():
        a, b, label = first[identifier], second[identifier], labels[identifier]
        value, second_value = finite_number(a), finite_number(b)
        literal = isinstance(a["text"], str) and SIMPLE_NUMBER.fullmatch(a["text"]) is not None
        confidence = a["confidence"]
        valid_confidence = isinstance(confidence, (float, int)) and math.isfinite(confidence) and 0 < confidence <= 1
        median = medians.get(feature["image_sha256"])
        current_review = (value is None or not literal or not valid_confidence or
                          competing_numeric(a, 0) or feature["axis_connected"])
        qualifies = (value is not None and literal and valid_confidence and value == second_value and
                     not feature["axis_connected"] and median is not None and
                     feature["gap_height"] <= median + 1.25)
        counts["inventory"] += 1
        counts["numeric_primary"] += value is not None
        counts["with_cohort_median"] += median is not None
        counts["current_crop_signals_withhold"] += current_review
        counts["candidate_qualifies"] += qualifies
        expected = label.get("expected")
        if expected is None:
            category = label["category"]
            unscored["total"][category] += 1
            unscored["qualified"][category] += qualifies
            continue
        correct = value == float(expected)
        counts["labelled"] += 1
        counts["labelled_numeric_correct"] += correct
        counts["labelled_numeric_wrong"] += not correct
        counts["labelled_qualified"] += qualifies
        counts["labelled_qualified_correct"] += qualifies and correct
        counts["labelled_qualified_wrong"] += qualifies and not correct
        counts["labelled_current_withheld"] += current_review
    return {"counts": dict(counts), "unscored_categories": {key: dict(sorted(value.items())) for key, value in unscored.items()}}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--build-padded-sha256", required=True)
    parser.add_argument("--build-tight-sha256", required=True)
    args = parser.parse_args()
    protocol = ROOT / "tools/GraphReader.ManualPanelDiagnostic/TICK_CONSENSUS_PROTOCOL.md"
    protocol_hash = hashlib.sha256(protocol.read_bytes()).hexdigest()
    features = json.loads(read_checked(RUN / "tick-consensus-features-v1.json", FEATURE_SHA))
    if (features["scope"] != "exposed-train-and-dev-no-transcriptions" or features["truth_read"] is not False or
            features["optimizer_steps"] != 0 or features["sealed_payload_reads"] != 0 or len(features["rows"]) != 272):
        raise ValueError("tick consensus feature scope differs")
    captures = {
        "build": (capture("build-axis-padded-v1.json", args.build_padded_sha256, RUNTIME_SHA,
                          "internal_train_build", BUILD_INPUT_SHA, False),
                  capture("build-axis-tight-v1.json", args.build_tight_sha256, RUNTIME_SHA,
                          "internal_train_build", BUILD_INPUT_SHA, True)),
        "train": (capture("holdout-axis-padded-v2.json", TRAIN_PAD_SHA, RUNTIME_SHA,
                          "internal_train_holdout", TRAIN_INPUT_SHA, False),
                  capture("holdout-axis-tight-v1.json", TRAIN_TIGHT_SHA, RUNTIME_SHA,
                          "internal_train_holdout", TRAIN_INPUT_SHA, True)),
        "dev": (capture("dev-axis-padded-v2.json", DEV_PAD_SHA, RUNTIME_SHA, "internal_dev", DEV_INPUT_SHA, False),
                capture("dev-axis-tight-v1.json", DEV_TIGHT_SHA, RUNTIME_SHA, "internal_dev", DEV_INPUT_SHA, True)),
    }
    build_truth = json.loads(read_checked(RUN / "truth.json", BUILD_TRUTH_SHA))["rows"]
    joins = json.loads(read_checked(RUN / "tick-ocr-join-v1.json", JOIN_SHA))
    train_truth = json.loads(read_checked(RUN / "holdout-labels.json", LABELS_SHA))["rows"]
    reviewed = json.loads(read_checked(RUN / "holdout-unmatched-numeric-visual-labels-v1.json", REVIEW_SHA))["rows"]
    matches, unmatched, missed = match_labels(train_truth, joins)
    if (len(build_truth), len(matches), len(unmatched), missed, len(reviewed)) != (76, 46, 16, 6, 7):
        raise ValueError("training tick consensus labels differ")
    labels = {"build": {row["id"]: {"expected": row["expected"]} for row in build_truth},
              "train": {row["id"]: {"expected": row["expected"]} for row in matches}, "dev": {}}
    reviewed_by_id = {row["id"]: row["classification"] for row in reviewed}
    labels["train"].update({identifier: {"category": reviewed_by_id.get(identifier, "unclassified")}
                            for identifier in unmatched})
    dev_truth = json.loads(read_checked(RUN / "dev-axis-transcriptions-v1.json", DEV_LABEL_SHA))["rows"]
    labels["dev"] = {row["id"]: {"expected": row["expected_text"], "category": row["category"]}
                     for row in dev_truth}
    results = {}
    for split, (a, b) in captures.items():
        prefix = split + ":"
        selected = [{**row, "id": row["id"][len(prefix):]} for row in features["rows"] if row["id"].startswith(prefix)]
        results[split] = summarize(selected, a["rows"], b["rows"], labels[split])
    result = {"schema": "graphreader.tick-consensus-diagnostic.v1", "scope": "exposed-data-not-acceptance",
              "protocol_sha256": protocol_hash, "feature_sha256": FEATURE_SHA, "runtime_sha256": RUNTIME_SHA,
              "build_padded_sha256": args.build_padded_sha256, "build_tight_sha256": args.build_tight_sha256,
              "model_sha256": MODEL_SHA, "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "training_unmatched_labels": missed, "results": results}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "tick-consensus-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"results": results, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
