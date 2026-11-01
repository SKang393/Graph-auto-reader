# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Reuse the registered assisted point scorer for the final source snapshot."""
import score_assisted_workflow as scorer
from score import RUN

scorer.REPORT = RUN / "assisted-phase-cluster-final-v1/report.json"
scorer.OUTPUT = RUN / "assisted-phase-cluster-final-point-score-v1.json"

if __name__ == "__main__":
    scorer.main()
