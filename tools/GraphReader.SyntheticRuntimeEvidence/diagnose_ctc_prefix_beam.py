# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Owned-development CTC diagnostic. No language model, lexicon, or truth input.

Independent implementation of blank/nonblank prefix path summation described in
https://distill.pub/2017/ctc/ . This is not part of the application runtime.
"""

import argparse
import hashlib
import itertools
import json
import math
from pathlib import Path
import random
import struct
import time


NEGATIVE_INFINITY = float("-inf")
BEAM_WIDTH = 8


def log_add(left, right):
    if left == NEGATIVE_INFINITY:
        return right
    if right == NEGATIVE_INFINITY:
        return left
    high, low = max(left, right), min(left, right)
    return high + math.log1p(math.exp(low - high))


def probability_rows(rows):
    """Match CtcRecognitionDecoder's Auto probability/logit interpretation."""
    converted = []
    classes = len(rows[0]) if rows else 0
    if classes < 2:
        raise ValueError("Expected time by class matrix including blank")
    for row in rows:
        if len(row) != classes or any(not math.isfinite(x) for x in row):
            raise ValueError("Invalid CTC matrix")
        if all(0 <= x <= 1 for x in row) and abs(sum(row) - 1) <= max(1e-5, classes * 1e-6):
            converted.append(list(row))
        else:
            high = max(row)
            exp = [math.exp(x - high) for x in row]
            total = sum(exp)
            converted.append([x / total for x in exp])
    return converted


def prefix_beam(probabilities, width=BEAM_WIDTH, blank=0):
    if width < 1 or not probabilities or not 0 <= blank < len(probabilities[0]):
        raise ValueError("Invalid beam options")
    beam = {(): (0.0, NEGATIVE_INFINITY)}
    for row in probabilities:
        next_beam = {}

        def add(prefix, ending, score):
            if score == NEGATIVE_INFINITY:
                return
            scores = next_beam.setdefault(prefix, [NEGATIVE_INFINITY, NEGATIVE_INFINITY])
            scores[ending] = log_add(scores[ending], score)

        for prefix, (blank_score, nonblank_score) in beam.items():
            total = log_add(blank_score, nonblank_score)
            for character, probability in enumerate(row):
                if not math.isfinite(probability) or not 0 <= probability <= 1:
                    raise ValueError("Invalid probability")
                if probability == 0:
                    continue
                score = math.log(probability)
                if character == blank:
                    add(prefix, 0, total + score)
                elif prefix and prefix[-1] == character:
                    add(prefix, 1, nonblank_score + score)
                    add(prefix + (character,), 1, blank_score + score)
                else:
                    add(prefix + (character,), 1, total + score)
        if not next_beam:
            raise ValueError("No possible CTC path")
        ranked = sorted(next_beam.items(), key=lambda item: (-log_add(*item[1]), item[0]))
        beam = dict(ranked[:width])
    return [(prefix, log_add(*scores)) for prefix, scores in beam.items()]


def collapse(path, blank=0):
    return tuple(value for index, value in enumerate(path)
                 if value != blank and (index == 0 or value != path[index - 1]))


def self_test():
    rng = random.Random(220921)
    checked = 0
    for length in range(1, 6):
        for _ in range(6):
            probabilities = []
            for _ in range(length):
                row = [rng.random() for _ in range(3)]
                probabilities.append([value / sum(row) for value in row])
            exhaustive = {}
            for path in itertools.product(range(3), repeat=length):
                probability = math.prod(probabilities[t][value] for t, value in enumerate(path))
                prefix = collapse(path)
                exhaustive[prefix] = exhaustive.get(prefix, 0.0) + probability
            actual = dict(prefix_beam(probabilities, width=3 ** length))
            assert actual.keys() == exhaustive.keys()
            assert all(math.isclose(math.exp(actual[key]), value, abs_tol=1e-12)
                       for key, value in exhaustive.items())
            assert math.isclose(sum(math.exp(value) for value in actual.values()), 1.0, abs_tol=1e-12)
            checked += 1
    assert prefix_beam([[0, 1, 0], [0, 1, 0]])[0][0] == (1,)
    assert prefix_beam([[0, 1, 0], [1, 0, 0], [0, 1, 0]])[0][0] == (1, 1)
    assert prefix_beam([[1, 0, 0]] * 10)[0] == ((), 0)
    assert prefix_beam([[0.5, 0.5]] * 3) == prefix_beam([[0.5, 0.5]] * 3)
    assert len(prefix_beam([[1 / 3] * 3] * 500)) == BEAM_WIDTH
    assert all(math.isfinite(score) for _, score in prefix_beam([[1 / 3] * 3] * 500))
    assert probability_rows([[1000, 1000]]) == [[0.5, 0.5]]
    assert probability_rows([[0.25, 0.75]]) == [[0.25, 0.75]]
    for rows in ([], [[float("nan"), 1]], [[1, 0], [1]]):
        try:
            probability_rows(rows)
        except ValueError:
            checked += 1
        else:
            raise AssertionError("Invalid matrix accepted")
    for width, blank in ((0, 0), (8, 2)):
        try:
            prefix_beam([[0.5, 0.5]], width, blank)
        except ValueError:
            checked += 1
        else:
            raise AssertionError("Invalid options accepted")
    # Summing paths can select 'a' although the greedy path is blank,blank.
    sample = [[0.6, 0.4], [0.6, 0.4]]
    assert collapse([0, 0]) == () and prefix_beam(sample)[0][0] == (1,)
    print(json.dumps({"status": "passed", "exhaustive_matrices": 30,
                      "additional_assertions": 14, "private_reads": 0, "sealed_reads": 0}))


def decode(capture_path, output_path):
    started = time.perf_counter()
    capture = json.loads(capture_path.read_text(encoding="utf-8-sig"))
    if (capture["scope"] != "owned-open-synthetic-development" or
            capture["private_reads"] != 0 or capture["sealed_reads"] != 0):
        raise ValueError("Only explicit open synthetic capture permitted")
    alphabet = list(capture["alphabet"])
    classes = len(alphabet) + 1
    if capture["blank_class_index"] != 0 or classes != capture["class_count"]:
        raise ValueError("Unexpected alphabet contract")
    results = []
    for row in capture["rows"]:
        path = (capture_path.parent / row["tensor_file"]).resolve()
        if not path.is_relative_to(capture_path.parent.resolve()):
            raise ValueError("Tensor path escaped capture")
        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() != row["tensor_sha256"]:
            raise ValueError("Tensor hash mismatch")
        values = struct.unpack("<" + "f" * (len(data) // 4), data)
        if len(values) != row["time_steps"] * classes:
            raise ValueError("Tensor shape mismatch")
        rows = [values[index:index + classes] for index in range(0, len(values), classes)]
        probabilities = probability_rows(rows)
        greedy_path = [max(range(classes), key=lambda index: time_row[index]) for time_row in probabilities]
        greedy_text = "".join(alphabet[index - 1] for index in collapse(greedy_path))
        native_text = row["native_greedy"][0]["text"] if row["native_greedy"] else ""
        if greedy_text != native_text:
            raise ValueError("Python greedy does not reproduce captured native decoder")
        candidates = [{"text": "".join(alphabet[index - 1] for index in prefix),
                       "log_path_probability": score}
                      for prefix, score in prefix_beam(probabilities)]
        results.append({"id": row["id"], "crop_sha256": row["crop_sha256"],
                        "greedy_text": greedy_text, "beam_text": candidates[0]["text"],
                        "beam_candidates": candidates})
    result = {"scope": capture["scope"], "beam_width": BEAM_WIDTH,
              "language_model": False, "lexicon": False, "truth_used_for_inference": False,
              "capture_sha256": hashlib.sha256(capture_path.read_bytes()).hexdigest(),
              "regions": len(results), "rows": results, "seconds": time.perf_counter() - started,
              "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0,
              "diagnostic_only": True, "production_approved": False}
    with output_path.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2, ensure_ascii=False, allow_nan=False)
        stream.write("\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--capture", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.self_test:
        self_test()
    elif args.capture is not None and args.output is not None:
        decode(args.capture, args.output)
    else:
        parser.error("Specify --self-test or --capture and --output")
