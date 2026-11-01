# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Create a label-free raster list from the fixed train-only overlay receipt."""

from __future__ import annotations

import argparse
from hashlib import sha256
import json
from pathlib import Path

ROOT = Path(r"E:\Graph-auto-reader")
RASTERS = ROOT / "artifacts/goal22-runs/marker-train-overlay-corpus-render-v7"
OUTPUT = ROOT / "artifacts/manual-marker-input-diagnostic-v1"
RECEIPT_SHA = "b7631113c9425cd06c62e4198dd173e8423013d973040e4a05ef5448bcd7a02f"


def digest(path: Path) -> str:
    value = sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("smoke", "full"))
    args = parser.parse_args()
    receipt_path = RASTERS / "receipt.json"
    if digest(receipt_path) != RECEIPT_SHA:
        raise ValueError("train overlay receipt differs")
    receipt = json.loads(receipt_path.read_bytes())
    if (receipt["schema"] != "graphreader.manual-train-marker-overlay-corpus-result.v1" or
            receipt["overlay_count"] != 200 or receipt["source_group_count"] != 20 or
            receipt["development_payload_reads"] != 0 or receipt["sealed_payload_reads"] != 0 or
            receipt["training_admitted"] is not False):
        raise ValueError("train overlay scope differs")
    groups: dict[int, list[dict]] = {}
    for row in receipt["rows"]:
        relative = Path(row["output_path"])
        if (relative.is_absolute() or ".." in relative.parts or len(relative.parts) != 2 or
                relative.parts[0] not in ("internal_train_build", "internal_train_holdout")):
            raise ValueError("overlay path differs")
        path = RASTERS / relative
        if path.is_symlink() or digest(path) != row["output_sha256"]:
            raise ValueError("overlay image differs")
        groups.setdefault(row["source_index"], []).append(row)
    if set(groups) != set(range(20)) or any(len(rows) != 10 for rows in groups.values()):
        raise ValueError("overlay group inventory differs")
    selected = []
    for index, group in sorted(groups.items()):
        ordered = sorted(group, key=lambda row: row["output_path"])
        selected.extend([ordered[index % len(ordered)]] if args.mode == "smoke" else ordered)
    rows = [{"id": f"overlay-{index:03d}",
             "image_path": (RASTERS / row["output_path"]).relative_to(ROOT).as_posix(),
             "image_sha256": row["output_sha256"]}
            for index, row in enumerate(selected)]
    if len({row["image_sha256"] for row in rows}) != len(rows):
        raise ValueError("duplicate overlay bytes")
    result = {"schema": "graphreader.manual-marker-raster-input.v1", "scope": "train-only-raster-no-truth",
              "receipt_sha256": RECEIPT_SHA, "selection": args.mode, "training_approved": False, "rows": rows}
    OUTPUT.mkdir(parents=True, exist_ok=True)
    path = OUTPUT / f"{args.mode}-raster-input-v1.json"
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, sort_keys=True, separators=(",", ":"))
        stream.write("\n")
    print(json.dumps({"selection": args.mode, "images": len(rows), "groups": len(groups), "sha256": digest(path)}))


if __name__ == "__main__":
    main()
