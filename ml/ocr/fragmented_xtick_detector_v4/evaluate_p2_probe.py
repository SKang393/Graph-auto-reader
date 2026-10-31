# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Evaluate P2 on the same consumed development probe; requires RX 6600 XT."""

from __future__ import annotations

from hashlib import sha256
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parents[2]))
from ml.ocr.fragmented_xtick_detector_v4.evaluate_probe import evaluate
from ml.ocr.fragmented_xtick_detector_v4.materialize import REPO_ROOT, canonical_json

RUN_RELATIVE = "artifacts/fragmented-xtick-v4-p2-hard-negative-v1"
MODEL_ID = "fragmented-xtick-spatial-locator-v4-cell-relative-rf31-logwh-diverse-hard-negative-p2"


def main() -> int:
    result = evaluate(RUN_RELATIVE, MODEL_ID)
    result["evaluator_entrypoint_sha256"] = sha256(Path(__file__).read_bytes()).hexdigest()
    output = REPO_ROOT / RUN_RELATIVE / "consumed-probe-evaluation.json"
    with output.open("xb") as stream:
        stream.write(canonical_json(result))
    print(json.dumps({"output": output.relative_to(REPO_ROOT).as_posix(),
        "result_sha256": sha256(output.read_bytes()).hexdigest(),
        "checkpoint_sha256": result["evaluated_checkpoint_sha256"], "metrics": result["metrics"], "runtime": result["runtime"]}, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
