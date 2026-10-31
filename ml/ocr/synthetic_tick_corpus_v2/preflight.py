# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Full public-generator gate for selecting a fixed corpus seed list.

Each candidate is rendered through the unchanged ``real_range`` generator,
including its required distribution gate, in a project-local temporary folder.
No generated pixels are retained by this preflight and no test-family scene is
requested.
"""
from __future__ import annotations

import argparse
from pathlib import Path
from tempfile import TemporaryDirectory

from ml.synthetic.dataset import generate_dataset
from ml.synthetic.io import canonical_json_bytes

from .prepare import ROOT, PRESET

CANDIDATES = tuple(range(20261040, 20261280))
REQUIRED_PASSING_SEEDS = 24


def run(output: Path) -> dict:
    output = Path(output).resolve()
    if not output.is_relative_to((ROOT / "artifacts").resolve()) or output.exists():
        raise ValueError("output must be a new project-local artifact file")
    passing, failures = [], []
    with TemporaryDirectory(dir=output.parent, prefix=".synthetic-tick-full-gate-") as temporary:
        temporary_root = Path(temporary)
        for seed in CANDIDATES:
            if len(passing) == REQUIRED_PASSING_SEEDS:
                break
            try:
                generate_dataset(PRESET, seed, temporary_root / str(seed))
            except (AssertionError, RuntimeError, ValueError) as error:
                failures.append({"seed": seed, "error": str(error)})
                continue
            passing.append(seed)
    if len(passing) != REQUIRED_PASSING_SEEDS:
        raise RuntimeError("candidate range did not provide enough full-gate-passing seeds")
    report = {"schema": "graphreader.synthetic-tick-corpus.full-gate-preflight.v1", "preset": PRESET,
              "candidate_range": [CANDIDATES[0], CANDIDATES[-1]], "selected_seeds": passing,
              "full_generator_distribution_gate_passed": True, "failures": failures,
              "synthetic_only": True, "private_reads": 0, "sealed_reads": 0, "optimizer_steps": 0,
              "reserved_test_family_opened": False,
              "limitations": ["Historical benchmark independence is unproven.",
                              "The generator writes an empty test split manifest but this preflight requests no test scenes."]}
    output.write_bytes(canonical_json_bytes(report))
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    result = run(arguments.output)
    print({"selected_seed_count": len(result["selected_seeds"]), "failure_count": len(result["failures"])})
