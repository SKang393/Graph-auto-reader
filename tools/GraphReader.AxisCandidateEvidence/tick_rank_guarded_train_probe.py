# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Test an observed-axis extension guard on the frozen tick-rank probe."""
from __future__ import annotations

import importlib.util
import math
from pathlib import Path

import tick_rank_train_probe as runner

BASE_PROBE = runner.ROOT / "tools/GraphReader.ManualPanelDiagnostic/tick_rank_probe.py"
OUTPUT = runner.ROOT / "artifacts/goal22-runs/axis-tick-rank-guarded-train-probe-v1"
if runner.digest(BASE_PROBE.read_bytes()) != "e61192366927c3f48b580a9fade972a2e1286777488b4490c633523ea1d0b69d":
    raise ValueError("Existing tick-rank implementation differs")
# Twice AxisGeometryOptions.MergeDistancePixels, not a fitted threshold.
MAX_LEFT_EXTENSION_PIXELS = 6.0
spec = importlib.util.spec_from_file_location("base_tick_rank_probe", BASE_PROBE)
if spec is None or spec.loader is None:
    raise RuntimeError("Existing tick-rank probe cannot be loaded")
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)


def left_extension(candidate: dict) -> float:
    origin = candidate["Intersection"]
    right = candidate["BottomRight"]
    dx, dy = right["X"] - origin["X"], right["Y"] - origin["Y"]
    length = math.hypot(dx, dy)
    if not math.isfinite(length) or length <= 0:
        raise ValueError("Candidate has no finite horizontal direction")
    line = candidate["XAxisLine"]
    projections = [
        ((point["X"] - origin["X"]) * dx + (point["Y"] - origin["Y"]) * dy) / length
        for point in (line["Start"], line["End"])
    ]
    if not all(math.isfinite(value) for value in projections):
        raise ValueError("Candidate axis extension is not finite")
    return max(0.0, -min(projections))


def choose(panel: dict) -> tuple[dict, int, int]:
    axis = panel["axis"]
    candidates, lines = axis["ranked_candidates"], axis["line_candidates"]
    baseline = candidates[0]
    width, height = panel["Width"], panel["Height"]
    groups = []
    ranked = []
    for candidate in candidates:
        if candidate["Score"] < baseline["Score"] - 0.12:
            break
        x, bottom = candidate["Intersection"]["X"], candidate["Intersection"]["Y"]
        top = candidate["TopLeft"]["Y"]
        if (candidate["BottomRight"]["X"] - x < width * 0.15 or bottom - top < height * 0.15 or
                left_extension(candidate) > MAX_LEFT_EXTENSION_PIXELS):
            continue
        if any(abs(x - px) < 7 and abs(bottom - py) < 7 and abs(top - pt) < 10 for px, py, pt in groups):
            continue
        groups.append((x, bottom, top))
        regular, count = base.tick_evidence(candidate, lines, width, height)
        ranked.append((regular, candidate["Score"], -candidate["Rank"], candidate, count))
        if len(groups) >= 60:
            break
    if not ranked:
        return baseline, 0, 0
    regular, _, _, selected, count = max(ranked, key=lambda item: item[:3])
    return (selected, regular, count) if regular >= 4 else (baseline, regular, count)


if __name__ == "__main__":
    runner.PROBE = Path(__file__).resolve()
    runner.OUTPUT = OUTPUT
    runner.main()
