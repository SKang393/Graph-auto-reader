# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score frozen tick-probe ranks against synthetic training targets."""
from __future__ import annotations

import argparse
from collections import Counter
import json
import math
from pathlib import Path

from tick_rank_train_probe import OUTPUT, PROBE, ROOT, checked, digest

GROUPS = ROOT / "artifacts/goal22-runs/axis-ranker-gpu-training-data-v1/groups.json"
GROUPS_SHA = "d9c5928ffea0407fd5b1041d3c3ca9146f128d71054cca0026a51e88a3dc67b5"


def summarize(predictions: list[dict], groups: list[dict]) -> dict:
    def key(row):
        return row["source_sha256"], row["panel_sha256"]

    by_panel = {key(row): row for row in predictions}
    if len(by_panel) != len(predictions) or len({key(row) for row in groups}) != len(groups):
        raise ValueError("Panel identities must be unique")
    counts = Counter(total_predictions=len(predictions), labelable_panels=len(groups))
    baseline_total = selected_total = 0.0
    labelled = {key(row) for row in groups}
    for group in groups:
        identifier = key(group)
        prediction = by_panel.get(identifier)
        if prediction is None or prediction["status"] != "completed":
            counts["unscored_labelable_panels"] += 1
            continue
        rows = group["rows"]
        rank = prediction["selected_rank"]
        if (not isinstance(rank, int) or isinstance(rank, bool) or rank < 0 or rank >= len(rows) or
                rows[0]["candidate_identity_sha256"] != prediction["baseline_identity_sha256"] or
                rows[rank]["candidate_identity_sha256"] != prediction["selected_identity_sha256"] or
                rows[0]["rank"] != 1 or rows[rank]["rank"] != rank + 1):
            raise ValueError("Frozen candidate identity differs")
        before, after = rows[0]["target_error"], rows[rank]["target_error"]
        if not all(isinstance(x, (int, float)) and not isinstance(x, bool) and
                   math.isfinite(x) and x >= 0 for x in (before, after)):
            raise ValueError("Synthetic target must be finite and nonnegative")
        baseline_total += before
        selected_total += after
        counts["scored_panels"] += 1
        counts["changed_selections"] += int(rank != 0)
        counts["improved"] += int(after < before - 1e-12)
        counts["unchanged_error"] += int(abs(after - before) <= 1e-12)
        counts["worsened"] += int(after > before + 1e-12)
    counts["unlabelled_or_fallback_panels"] = len(set(by_panel) - labelled)
    scored = counts["scored_panels"]
    return {"counts": dict(counts),
            "mean_baseline_normalized_corner_error": baseline_total / scored if scored else None,
            "mean_selected_normalized_corner_error": selected_total / scored if scored else None}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prediction-sha256", required=True)
    args = parser.parse_args()
    target = OUTPUT / "score.json"
    if target.exists():
        raise FileExistsError("Exploratory score already exists")
    prediction = json.loads(checked(OUTPUT / "predictions.json", args.prediction_sha256))
    if (prediction["schema"] != "graphreader.axis-tick-rank-synthetic-train-probe.v1" or
            prediction["scope"] != "exploratory_synthetic_train_only_not_acceptance" or
            prediction["production_line_provider_parity"] is not False or prediction["truth_read"] is not False or
            prediction["optimizer_steps"] != 0 or prediction["development_reads"] != 0 or
            prediction["sealed_reads"] != 0 or prediction["production_approval"] is not False or
            prediction["source_sha256"] != digest((Path(__file__).parent / "tick_rank_train_probe.py").read_bytes()) or
            prediction["reused_probe_sha256"] != digest(PROBE.read_bytes())):
        raise ValueError("Frozen prediction scope or sources differ")
    groups = json.loads(checked(GROUPS, GROUPS_SHA))
    if groups["split"] != "train" or len(groups["groups"]) != 93 or len(prediction["rows"]) != 96:
        raise ValueError("Synthetic training denominator differs")
    result = {
        "schema": "graphreader.axis-tick-rank-synthetic-train-score.v1",
        "scope": "exploratory_synthetic_train_only_not_acceptance",
        "prediction_sha256": args.prediction_sha256, "group_sha256": GROUPS_SHA,
        "source_sha256": digest(Path(__file__).read_bytes()), "optimizer_steps": 0,
        "development_reads": 0, "sealed_reads": 0, "production_approval": False,
        **summarize(prediction["rows"], groups["groups"]),
    }
    with target.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, sort_keys=True, indent=2, allow_nan=False)
        stream.write("\n")
    print(json.dumps({**result["counts"],
                      "mean_baseline": result["mean_baseline_normalized_corner_error"],
                      "mean_selected": result["mean_selected_normalized_corner_error"],
                      "sha256": digest(target.read_bytes())}))


if __name__ == "__main__":
    main()
