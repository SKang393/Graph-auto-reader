# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Freeze development OCR boxes without transcriptions or connectivity flags."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from axis_connectivity_probe import RUN, read_checked

PARITY_SHA = "7e5e8dc0bf40521bdbc66fa91a370a7613eb8da65d9cd128bd9fca1af2151dd5"


def crop_rows(parity: dict) -> list[dict]:
    if (parity["scope"] != "train-and-dev-no-label-truth" or parity["truth_read"] is not False or
            parity["optimizer_steps"] != 0 or parity["sealed_payload_reads"] != 0):
        raise ValueError("parity input scope differs")
    rows = []
    for row in parity["rows"]:
        if not row["id"].startswith("dev:"):
            continue
        item = {"id": row["id"][4:], "image_path": row["image_path"],
                "image_sha256": row["image_sha256"], "box_xyxy": row["box"]}
        if (item["id"] != f"dev-proposal-{len(rows):04d}" or
                not item["image_path"].startswith("artifacts/manual-axis-ocr-diagnostic-v1/dev-axis-capture-v1/images/") or
                len(item["box_xyxy"]) != 4):
            raise ValueError("development crop identity differs")
        rows.append(item)
    if len(rows) != 134 or len({row["image_sha256"] for row in rows}) != 15:
        raise ValueError("development crop inventory differs")
    return rows


def main() -> None:
    parity = json.loads(read_checked(RUN / "axis-connectivity-parity-input-v1.json", PARITY_SHA))
    result = {"schema": "graphreader.manual-axis-ocr-input.v1", "scope": "exposed-dev-no-transcriptions",
              "source_split": "internal_dev", "rows": crop_rows(parity)}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    output = RUN / "dev-axis-ocr-input-v1.json"
    with output.open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"rows": len(result["rows"]), "sha256": hashlib.sha256(encoded).hexdigest(),
                      "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}))


if __name__ == "__main__":
    main()
