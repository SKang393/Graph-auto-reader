# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score frozen dense-tick crop predictions after private label review."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from propose_dense_tick_windows import OUTPUT, RUN, checked_json

SPLITS = {
    "internal_train_build": {
        "input": "0de2129cc8e5c3c9b98625df095f48fe23cb159c23e9d8f29c091c2d03ed2f05",
        "truth": "05c2a6daca6abca9ce496b1a753345506891f443bad321232ae64712572d1c7f",
        "mobile": "9384d14c89ba75aa039842752d9fc10a33042edf256c81ba2b7aa092a69f1385",
        "dense": "62d1437a2c5e8ae8b09ef43c4d0bfde73dfde74c44d2cdf1a632468d55f2bf56",
        "standard": "9ebb033f54ba7fb8b4f1a3f47f7992e3fe8d076d4c76a2af1855314882ccadfd",
    },
    "internal_train_holdout": {
        "input": "26e65944663b12902df7ae9630fee968b5db8435d9ed8e899c6d45f5e36ef86e",
        "truth": "f9daad83ee9a785f4995bbda60ec1b30fc73533523cfd0d4e276899aa8422bd7",
        "mobile": "ca143f0465e0b6a374bf4012eab053d1680cde325d1aea72473dbc0d4854b993",
        "dense": "841ad81c13183fe70b8b033b391014729d5c0df5c13204f30ce36d865f1f24db",
        "standard": "eb2d684e7a10ff444a494deab83bb1b6d68da0e146962a4c3a7e32391f2ff7bf",
    },
}
RUNTIME_SHA = "31e551a0d78fdd2ad9f1e46fe5b52a9cc3729b36503a073b0d13c9a2e65ec677"
SERVER_SHA = "0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418"
NUMERIC_SHA = "9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84"
MOBILE_SHA = "7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743"
AMBIGUITY_SHA = "b8e2773ca3966469081875fc36b3981ef4eb458356d8dfdae2be2722602f0096"


def prediction_map(rows: list[dict], reader: str, text_key: str) -> dict[str, str | None]:
    result = {row["id"]: row[reader][text_key] for row in rows}
    if len(result) != len(rows):
        raise ValueError("duplicate prediction identity")
    if any(value is not None and not isinstance(value, str) for value in result.values()):
        raise ValueError("invalid prediction text")
    return result


def score_rows(truth: list[dict], predictions: dict[str, str | None]) -> dict:
    if len({row["id"] for row in truth}) != len(truth) or set(predictions) != {row["id"] for row in truth}:
        raise ValueError("truth and prediction identities differ")
    exact = wrong = empty = visible_exact = visible = 0
    for row in truth:
        actual = predictions[row["id"]]
        expected = row["text"]
        if not isinstance(expected, str) or not expected or not isinstance(row["crop_partially_clipped"], bool):
            raise ValueError("invalid reviewed label")
        if actual is None or actual == "":
            empty += 1
        elif actual == expected:
            exact += 1
        else:
            wrong += 1
        if not row["crop_partially_clipped"]:
            visible += 1
            visible_exact += int(actual == expected)
    return {"labels": len(truth), "exact": exact, "wrong": wrong, "empty": empty,
            "fully_visible_labels": visible, "fully_visible_exact": visible_exact}


def main() -> None:
    totals = {}
    for split, hashes in SPLITS.items():
        inputs = checked_json(OUTPUT / f"dense-window-input-{split}-v1.json", hashes["input"])
        truth = checked_json(OUTPUT / f"dense-window-truth-{split}-v1.json", hashes["truth"])
        mobile = checked_json(OUTPUT / f"dense-window-predictions-{split}-v2.json", hashes["mobile"])
        dense = checked_json(RUN / f"dense-server-dense-{split}-v2.json", hashes["dense"])
        standard = checked_json(RUN / f"dense-server-standard-{split}-v2.json", hashes["standard"])
        if (inputs["schema"] != "graphreader.manual-axis-ocr-input.v1" or inputs["scope"] != "train-only-no-truth" or
                inputs["source_split"] != split or len({row["id"] for row in inputs["rows"]}) != len(inputs["rows"]) or
                {row["id"] for row in inputs["rows"]} != {row["id"] for row in truth["rows"]} or
                truth["schema"] != "graphreader.manual-dense-tick-truth.v1" or truth["input_sha256"] != hashes["input"] or
                mobile["schema"] != "graphreader.manual-axis-ocr-predictions.v1" or
                mobile["input_sha256"] != hashes["input"] or mobile["crop_profile"] != "dense-tick-no-horizontal-padding" or
                mobile["source_split"] != split or mobile["optimizer_steps"] != 0 or
                mobile["numeric_model_sha256"] != NUMERIC_SHA or mobile["official_model_sha256"] != MOBILE_SHA or
                mobile["ambiguity_model_sha256"] != AMBIGUITY_SHA):
            raise ValueError("mobile prediction binding differs")
        for report, profile in ((dense, "dense-tick-no-horizontal-padding"), (standard, "candidate-standard-padding")):
            if (report["schema"] != "graphreader.manual-dense-server-ocr.v1" or report["input_sha256"] != hashes["input"] or
                    report["runtime_description_sha256"] != RUNTIME_SHA or report["model_sha256"] != SERVER_SHA or
                    report["source_split"] != split or report["crop_profile"] != profile or
                    report["optimizer_steps"] != 0 or report["truth_read"] is not False or report["sealed_payload_reads"] != 0):
                raise ValueError("server prediction binding differs")
        totals[split] = {
            "numeric": score_rows(truth["rows"], prediction_map(mobile["rows"], "numeric", "Text")),
            "mobile": score_rows(truth["rows"], prediction_map(mobile["rows"], "official", "Text")),
            "server_dense": score_rows(truth["rows"], prediction_map(dense["rows"], "server", "text")),
            "server_standard": score_rows(truth["rows"], prediction_map(standard["rows"], "server", "text")),
        }
    result = {"schema": "graphreader.manual-dense-tick-score.v1", "scope": "train-diagnostic-not-acceptance",
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "totals": totals,
              "limitations": ["Two previously inspected training graphs and 19 selected measured ticks are not acceptance.",
                              "Assistant-reviewed labels are not independently adjudicated.",
                              "The proposal set does not measure whole-axis recall or false positives.",
                              "Readers ran on frozen crops, not the integrated automatic workflow."]}
    output = OUTPUT / "dense-window-score-v2.json"
    blob = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with output.open("xb") as stream:
        stream.write(blob)
    print(json.dumps({"totals": totals, "sha256": hashlib.sha256(blob).hexdigest()}))


if __name__ == "__main__":
    main()
