# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Score another registered phase capture with the existing fixed scorers."""
import argparse
from pathlib import Path
import sys

import score_assisted_workflow as point_scorer
import score_phase_cluster_repair as phase_scorer
from score import RUN


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--kind", choices=("phase", "points"), required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--report-sha256", required=True)
    parser.add_argument("--runtime-sha256", required=True)
    args = parser.parse_args()
    root = RUN.resolve()
    report = (root / args.report).resolve()
    output = (root / args.output).resolve()
    if not report.is_relative_to(root) or not output.is_relative_to(root):
        raise ValueError("Capture and score must remain inside the diagnostic run folder")
    if args.kind == "phase":
        phase_scorer.AFTER = report
        phase_scorer.OUTPUT = output
        scorer = phase_scorer
    else:
        point_scorer.REPORT = report
        point_scorer.OUTPUT = output
        scorer = point_scorer
    sys.argv = [str(Path(__file__)), "--report-sha256", args.report_sha256,
                "--runtime-sha256", args.runtime_sha256]
    scorer.main()


if __name__ == "__main__":
    main()
