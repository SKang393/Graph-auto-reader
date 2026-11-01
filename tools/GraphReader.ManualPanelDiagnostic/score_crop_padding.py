# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare fixed OCR crop-padding settings on previously exposed data."""

from __future__ import annotations

import argparse
from collections import Counter
import hashlib
import json
import sys
from pathlib import Path

from axis_connectivity_probe import ROOT, RUN, read_checked
from score_dev_ocr import INPUT_SHA as DEV_INPUT_SHA, LABEL_SHA as DEV_LABEL_SHA, MODEL_SHA, numeric_value

sys.path.insert(0, str(ROOT / "tools/GraphReader.ManualOverlayCapture"))
from holdout_axis_score import INPUT_SHA as TRAIN_INPUT_SHA, JOIN_SHA, LABELS_SHA, match_labels  # noqa: E402

PROTOCOL_SHA = "51f0f4c0a2ecc1c9e9c72cd2d62ac9bf032055422656a7245eb78daaf524cbd3"
CAPTURE_ROOT = ROOT / "artifacts/manual-marker-input-diagnostic-v1"


def compare(labels: list[dict], padded: list[dict], tight: list[dict], unscored: dict[str, str]) -> dict:
    first = {row["id"]: row["server"] for row in padded}
    second = {row["id"]: row["server"] for row in tight}
    if (len(first) != len(padded) or len(second) != len(tight) or set(first) != set(second) or
            len({row["id"] for row in labels}) != len(labels) or
            not {row["id"] for row in labels}.issubset(first) or not set(unscored).issubset(first) or
            {row["id"] for row in labels} & set(unscored)):
        raise ValueError("crop comparison inventory differs")
    counts: Counter[str] = Counter()
    nontext: dict[str, Counter] = {"padded": Counter(), "tight": Counter()}
    for label in labels:
        expected = label["expected"]
        a, b = first[label["id"]], second[label["id"]]
        before, after = a["text"] == expected, b["text"] == expected
        counts["padded_exact"] += before
        counts["tight_exact"] += after
        counts["exact_improved"] += not before and after
        counts["exact_regressed"] += before and not after
        counts["padded_numeric_value_correct"] += numeric_value(a) == float(expected)
        counts["tight_numeric_value_correct"] += numeric_value(b) == float(expected)
    for identifier, category in unscored.items():
        for name, readings in (("padded", first), ("tight", second)):
            if numeric_value(readings[identifier]) is not None:
                nontext[name][category] += 1
    return {"scored": len(labels), "unscored": len(unscored), "counts": dict(counts),
            "unscored_numeric_primaries": {name: dict(sorted(value.items())) for name, value in nontext.items()}}


def capture(name: str, expected_hash: str, runtime: str, split: str, input_hash: str, tight: bool) -> dict:
    result = json.loads(read_checked(CAPTURE_ROOT / name, expected_hash))
    if (result["schema"] != "graphreader.manual-reviewed-server-ocr.v1" or
            result["scope"] != ("dev-diagnostic-not-acceptance" if split == "internal_dev" else "train-diagnostic-not-acceptance") or
            result["source_split"] != split or result["input_sha256"] != input_hash or
            result["runtime_description_sha256"] != runtime or result["model_sha256"] != MODEL_SHA or
            result["crop_profile"] != ("candidate-zero-horizontal-padding" if tight else "candidate-standard-padding") or
            result["inference_provider"] != "CPUExecutionProvider" or result["truth_read"] is not False or
            result["optimizer_steps"] != 0 or result["sealed_payload_reads"] != 0):
        raise ValueError("crop comparison capture scope differs")
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtime-sha256", required=True)
    parser.add_argument("--train-padded-sha256", required=True)
    parser.add_argument("--train-tight-sha256", required=True)
    parser.add_argument("--dev-padded-sha256", required=True)
    parser.add_argument("--dev-tight-sha256", required=True)
    args = parser.parse_args()
    read_checked(ROOT / "tools/GraphReader.ManualPanelDiagnostic/CROP_PADDING_PROTOCOL.md", PROTOCOL_SHA)
    train_a = capture("holdout-axis-padded-v2.json", args.train_padded_sha256, args.runtime_sha256,
                      "internal_train_holdout", TRAIN_INPUT_SHA, False)
    train_b = capture("holdout-axis-tight-v1.json", args.train_tight_sha256, args.runtime_sha256,
                      "internal_train_holdout", TRAIN_INPUT_SHA, True)
    dev_a = capture("dev-axis-padded-v2.json", args.dev_padded_sha256, args.runtime_sha256,
                    "internal_dev", DEV_INPUT_SHA, False)
    dev_b = capture("dev-axis-tight-v1.json", args.dev_tight_sha256, args.runtime_sha256,
                    "internal_dev", DEV_INPUT_SHA, True)
    joins = json.loads(read_checked(RUN / "tick-ocr-join-v1.json", JOIN_SHA))
    truth = json.loads(read_checked(RUN / "holdout-labels.json", LABELS_SHA))["rows"]
    matches, unmatched, missed = match_labels(truth, joins)
    if (len(truth), len(joins), len(matches), len(unmatched), missed) != (52, 62, 46, 16, 6):
        raise ValueError("training crop inventory differs")
    train = compare(matches, train_a["rows"], train_b["rows"], {item: "unmatched" for item in unmatched})
    labels = json.loads(read_checked(RUN / "dev-axis-transcriptions-v1.json", DEV_LABEL_SHA))["rows"]
    selected = [{"id": row["id"], "expected": row["expected_text"]} for row in labels if row["expected_text"] is not None]
    unscored = {row["id"]: row["category"] for row in labels if row["expected_text"] is None}
    if len(selected) != 115 or len(unscored) != 19:
        raise ValueError("development crop inventory differs")
    dev = compare(selected, dev_a["rows"], dev_b["rows"], unscored)
    result = {"schema": "graphreader.crop-padding-comparison.v1", "scope": "exposed-data-diagnostic-not-acceptance",
              "protocol_sha256": PROTOCOL_SHA, "runtime_description_sha256": args.runtime_sha256,
              "capture_sha256": {"train_padded": args.train_padded_sha256, "train_tight": args.train_tight_sha256,
                                 "dev_padded": args.dev_padded_sha256, "dev_tight": args.dev_tight_sha256},
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "train": {"reviewed_labels": 52, "missed_labels": missed, **train}, "development": dev}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "crop-padding-comparison-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"train": result["train"], "development": dev, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
