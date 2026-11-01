# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Reuse the frozen assisted point scorer for the phase repair capture."""
import score_assisted_workflow as scorer
from score import RUN

scorer.REPORT = RUN / "assisted-phase-cluster-repair-v1/report.json"
scorer.OUTPUT = RUN / "assisted-phase-cluster-repair-point-score-v1.json"

if __name__ == "__main__":
    scorer.main()
