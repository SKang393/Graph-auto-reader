# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Compare in-memory C# metrics with the frozen V2 Python scorer, without models or data files."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import random
import subprocess
from types import SimpleNamespace

import score_full_ocr_candidate_v2 as reference


def fixtures() -> list[dict]:
    def truth(box, text, role="annotation"):
        return {"box": box, "text": text, "generator_role": role}

    def prediction(box, text, role="annotation"):
        return {"box": box, "text": text, "role": role if text is not None else None}

    cases = [
        {"sources": [{"truths": [truth([0, 0, 10, 10], "first"),
                                  truth([4, 0, 14, 10], "second")],
                      "predictions": [prediction([2, 0, 12, 10], "wrong", "other"),
                                      prediction([0, 0, 10, 10], "first")]}]},
        {"sources": [{"truths": [truth([0, 0, 10, 10], "A😀", "condition_label"),
                                  truth([20, 0, 30, 10], "cat", "participant")],
                      "predictions": [prediction([0, 0, 10, 10], None),
                                      prediction([20, 0, 30, 10], "cut", "participant"),
                                      prediction([40, 0, 50, 10], "😀", "other")]}]},
        {"sources": [{"truths": [truth([0, 0, 10, 10], "A", "condition_label")],
                      "predictions": [prediction([0, 0, 10, 10], "A", "phaseheading")]}]},
        {"sources": [{"truths": [truth([0, 0, 10, 10], "é")],
                      "predictions": [prediction([0, 0, 10, 10], "e\u0301")]}]},
        {"sources": [{"truths": [], "predictions": []}]},
    ]
    rng = random.Random(20260910)
    texts = ["", "A", "20", "70", "A B", "ab", "😀", "A😀B", "한글", "é", "e\u0301"]
    generator_roles = list(reference.TRUTH_ROLE_TO_RUNTIME_ROLE)
    runtime_roles = sorted(reference.RUNTIME_ROLES)
    for _ in range(192):
        sources = []
        for _ in range(rng.randint(1, 3)):
            truths = []
            for _ in range(rng.randint(0, 8)):
                x, y = rng.randrange(6) * 4, rng.randrange(3) * 8
                truths.append(truth([x, y, x + 10, y + 10], rng.choice(texts), rng.choice(generator_roles)))
            predictions = []
            for _ in range(rng.randint(0, 11)):
                if truths and rng.random() < 0.7:
                    box = list(rng.choice(truths)["box"])
                    offset = rng.choice([-4, -2, 0, 2, 4])
                    box[0] += offset
                    box[2] += offset
                    # Original-pixel boxes are nonnegative.
                    if box[0] < 0:
                        box[2] -= box[0]
                        box[0] = 0
                else:
                    x, y = rng.randrange(10) * 4, rng.randrange(3) * 8
                    box = [x, y, x + 10, y + 10]
                text = None if rng.random() < 0.2 else rng.choice(texts)
                predictions.append(prediction(box, text, rng.choice(runtime_roles)))
            sources.append({"truths": truths, "predictions": predictions})
        cases.append({"sources": sources})
    return cases


def expected(fixture: dict) -> tuple[dict, dict]:
    truths = []
    recognized = {}
    raw_count = raw_matches = 0
    for index, source in enumerate(fixture["sources"]):
        source_id = f"{index:064x}"
        current_truths = [reference.FullTextTruth(
            f"{index}-{number}", source_id, reference.Box(*row["box"]), row["text"],
            row["generator_role"], reference._canonical_role(row["generator_role"]))
            for number, row in enumerate(source["truths"])]
        truths.extend(current_truths)
        raw = [SimpleNamespace(box=reference.Box(*row["box"])) for row in source["predictions"]]
        raw_count += len(raw)
        raw_matches += reference.geometry.maximum_cardinality_matches(raw, tuple(row.box for row in current_truths))
        recognized[source_id] = tuple(reference.FullTextPrediction(
            str(number), source_id, reference.Box(*row["box"]), row["text"], row["role"])
            for number, row in enumerate(source["predictions"]) if row["text"] is not None)
    metrics = reference._score_split(truths, recognized)
    raw = {"truth_region_count": len(truths), "predicted_region_count": raw_count,
           "true_positives": raw_matches, "false_positives": raw_count - raw_matches,
           "false_negatives": len(truths) - raw_matches,
           "precision": raw_matches / max(1, raw_count), "recall": raw_matches / max(1, len(truths)),
           "intersection_over_union_minimum": 0.5}
    return raw, metrics


def compare(expected_value, actual_value, location="aggregate") -> None:
    if isinstance(expected_value, dict):
        if not isinstance(actual_value, dict) or set(actual_value) != set(expected_value):
            raise AssertionError(f"Aggregate fields differ: {location}")
        for key, value in expected_value.items():
            if key not in actual_value:
                raise AssertionError(f"Missing field: {location}.{key}")
            compare(value, actual_value[key], f"{location}.{key}")
    elif isinstance(expected_value, float):
        if not isinstance(actual_value, (int, float)) or not math.isclose(expected_value, actual_value, rel_tol=0, abs_tol=1e-12):
            raise AssertionError(f"Metric differs: {location}")
    elif expected_value != actual_value:
        raise AssertionError(f"Count differs: {location}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--executable", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise FileExistsError("Preserve the prior parity result; use a new output path.")
    cases = fixtures()
    request = json.dumps({"schema": "graphreader.synthetic-ocr-metric-fixtures.v1",
                          "scope": "hand-authored-model-free-metric-fixtures", "fixtures": cases},
                         ensure_ascii=True).encode("utf-8")
    result = subprocess.run(["dotnet", str(args.executable), "--score-synthetic-ocr-metric-fixtures"],
                            input=request, capture_output=True, timeout=60, check=True)
    actual = json.loads(result.stdout)["results"]
    if len(actual) != len(cases):
        raise AssertionError("Fixture denominator changed.")
    for index, (case, output) in enumerate(zip(cases, actual, strict=True)):
        if set(output) != {"raw_detector_geometry", "full_ocr_metrics",
                           "successfully_recognized_region_geometry", "recognition_failures"}:
            raise AssertionError("Only aggregate result fields are permitted.")
        raw, metrics = expected(case)
        compare(raw, output["raw_detector_geometry"], f"fixture[{index}].raw")
        compare(metrics, output["full_ocr_metrics"], f"fixture[{index}].full")
        recognized_count = metrics["predicted_region_count"]
        matched = metrics["geometry_matched_region_count"]
        compare({"truth_region_count": metrics["truth_region_count"],
                 "predicted_region_count": recognized_count, "true_positives": matched,
                 "false_positives": recognized_count - matched,
                 "false_negatives": metrics["truth_region_count"] - matched,
                 "precision": matched / max(1, recognized_count),
                 "recall": matched / max(1, metrics["truth_region_count"]),
                 "intersection_over_union_minimum": 0.5},
                output["successfully_recognized_region_geometry"], f"fixture[{index}].recognized")
        compare({"raw_regions_without_successful_recognition": raw["predicted_region_count"] - recognized_count},
                output["recognition_failures"], f"fixture[{index}].failures")
    invalid = {"schema": "graphreader.synthetic-ocr-metric-fixtures.v1",
               "scope": "hand-authored-model-free-metric-fixtures", "fixtures": [{"sources": [{
                   "truths": [], "predictions": [{"box": [0, 0, 10, 10],
                   "text": "DO_NOT_ECHO_FIXTURE_TEXT", "role": "unreviewed_role"}]}]}]}
    rejected = subprocess.run(["dotnet", str(args.executable), "--score-synthetic-ocr-metric-fixtures"],
                              input=json.dumps(invalid).encode(), capture_output=True, timeout=30)
    if (rejected.returncode != 1 or rejected.stdout.strip() or
            rejected.stderr.strip() != b"SYNTHETIC_OCR_METRIC_FIXTURE_INVALID"):
        raise AssertionError("Malformed metric fixtures must fail without echoing case data.")
    report = {"schema": "graphreader.original-db-ocr-aggregate-parity.v1", "passed": True,
              "fixture_count": len(cases), "seed": 20260910, "absolute_tolerance": 1e-12,
              "invalid_fixture_rejected_without_case_output": True,
              "executable_sha256": hashlib.sha256(args.executable.read_bytes()).hexdigest(),
              "reference_sha256": hashlib.sha256(Path(reference.__file__).read_bytes()).hexdigest(),
              "harness_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "model_inference": False, "optimizer_steps": 0, "private_reads": 0, "sealed_reads": 0,
              "scope": "Metric accounting parity only; not model accuracy or production approval."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes((json.dumps(report, indent=2) + "\n").encode())
    print(json.dumps(report))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
