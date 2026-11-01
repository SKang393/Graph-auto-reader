# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare direct rereads with frozen workflow outputs and visual labels."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path
import sys

from propose_dense_tick_windows import ROOT, RUN, checked_json
from score_outlying_lane import PREVIOUS_SHA
from score_workflow_y_review import LABEL_SHA, MANIFEST_SHA

sys.path.insert(0, str(ROOT / "tools/GraphReader.ManualPanelDiagnostic"))
from score_dev_ocr import MODEL_SHA, numeric_value  # noqa: E402

INPUT_SHA = "95bf2f5e3bec1ba4db708242850fc98d56a7a5b17cacf86f8e7633a1376dc5ad"
EXCLUSIONS_SHA = "52ffb758ee270cb9be33510fde6d25e77019e25f8efd89b706868ade4afe1ab1"


def summarize(report: dict, manifest: list[dict], labels: list[dict], padded: list[dict], tight: list[dict]) -> dict:
    panels = {panel["panel_id"]: panel for row in report["rows"] for panel in row["panels"]}
    reviewed = {row["id"]: row for row in labels}
    first = {row["id"]: row["server"] for row in padded}
    second = {row["id"]: row["server"] for row in tight}
    if (len(reviewed) != len(labels) or len(first) != len(padded) or len(second) != len(tight) or
            len({row["id"] for row in manifest}) != len(manifest) or set(first) != set(second) or
            {row["id"] for row in manifest} != set(reviewed) or not set(first).issubset(reviewed)):
        raise ValueError("workflow reread inventory differs")
    categories = defaultdict(Counter)
    for item in manifest:
        label = reviewed[item["id"]]
        bucket = categories[label["classification"]]
        bucket["total"] += 1
        if item["id"] not in first:
            bucket["excluded"] += 1
            continue
        bucket["reread"] += 1
        panel = panels[item["panel_id"]]
        matches = [region for region in panel["ocr_tick_regions"] or [] if region["region_id"] == item["region_id"]]
        if len(matches) != 1:
            raise ValueError("workflow reread region differs")
        original, a, b = matches[0], first[item["id"]], second[item["id"]]
        expected = label["expected_text"]
        if expected is None:
            bucket["padded_numeric_primary"] += numeric_value(a) is not None
            bucket["tight_numeric_primary"] += numeric_value(b) is not None
            continue
        if label["classification"] != "numeric_tick":
            raise ValueError("workflow reread transcription classification differs")
        baseline = original["numeric_value"] == float(expected)
        pad_correct = numeric_value(a) == float(expected)
        tight_correct = numeric_value(b) == float(expected)
        bucket["workflow_exact"] += original["text"] == expected
        bucket["workflow_numeric_correct"] += baseline
        bucket["padded_exact"] += a["text"] == expected
        bucket["padded_numeric_correct"] += pad_correct
        bucket["tight_exact"] += b["text"] == expected
        bucket["tight_numeric_correct"] += tight_correct
        bucket["padded_recovers_workflow_error"] += not baseline and pad_correct
        bucket["padded_regresses_workflow_correct"] += baseline and not pad_correct
        bucket["tight_recovers_workflow_error"] += not baseline and tight_correct
        bucket["tight_regresses_workflow_correct"] += baseline and not tight_correct
    return {key: dict(value) for key, value in sorted(categories.items())}


def capture(name: str, expected: str, runtime: str, tight: bool) -> dict:
    result = checked_json(RUN / name, expected)
    if (result["schema"] != "graphreader.manual-reviewed-server-ocr.v1" or
            result["scope"] != "train-diagnostic-not-acceptance" or result["source_split"] != "internal_train_workflow" or
            result["input_sha256"] != INPUT_SHA or result["runtime_description_sha256"] != runtime or
            result["model_sha256"] != MODEL_SHA or result["source_count"] != 19 or
            result["input_profile"] != "workflow-emitted-boxes" or
            result["crop_profile"] != ("candidate-zero-horizontal-padding" if tight else "candidate-standard-padding") or
            result["truth_read"] is not False or result["optimizer_steps"] != 0 or result["sealed_payload_reads"] != 0 or
            result["inference_provider"] != "CPUExecutionProvider" or len(result["rows"]) != 114):
        raise ValueError("workflow reread capture differs")
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--padded-sha256", required=True)
    parser.add_argument("--tight-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    report = checked_json(RUN / "original-workflow-full-v5/report.json", PREVIOUS_SHA)
    manifest = checked_json(RUN / "workflow-y-review-v1/manifest.json", MANIFEST_SHA)
    labels = checked_json(RUN / "workflow-y-review-v1/visual-labels.json", LABEL_SHA)
    inputs = checked_json(RUN / "workflow-y-reread-input-v1.json", INPUT_SHA)
    excluded = checked_json(RUN / "workflow-y-reread-exclusions-v1.json", EXCLUSIONS_SHA)
    padded = capture("workflow-y-padded-v1.json", args.padded_sha256, args.runtime_sha256, False)
    tight = capture("workflow-y-tight-v1.json", args.tight_sha256, args.runtime_sha256, True)
    if (labels["manifest_sha256"] != MANIFEST_SHA or excluded["manifest_sha256"] != MANIFEST_SHA or
            excluded["input_sha256"] != INPUT_SHA or len(manifest["rows"]) != 115 or len(excluded["rows"]) != 1 or
            {row["id"] for row in inputs["rows"]} != {row["id"] for row in padded["rows"]} or
            {row["id"] for row in excluded["rows"]} | {row["id"] for row in inputs["rows"]} !=
            {row["id"] for row in manifest["rows"]}):
        raise ValueError("workflow reread source inventory differs")
    summary = summarize(report, manifest["rows"], labels["rows"], padded["rows"], tight["rows"])
    value = {"schema": "graphreader.workflow-y-reread-score.v1", "scope": "train-diagnostic-not-acceptance",
             "workflow_report_sha256": PREVIOUS_SHA, "input_sha256": INPUT_SHA, "labels_sha256": LABEL_SHA,
             "padded_sha256": args.padded_sha256, "tight_sha256": args.tight_sha256,
             "runtime_sha256": args.runtime_sha256, "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
             "summary": summary}
    encoded = (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "workflow-y-reread-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"summary": summary, "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
