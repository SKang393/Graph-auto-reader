# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Reuse the registered phase comparison for the final source snapshot."""
import score_phase_cluster_repair as scorer
from score import RUN

scorer.AFTER = RUN / "assisted-phase-cluster-final-v1/report.json"
scorer.OUTPUT = RUN / "assisted-phase-cluster-final-score-v1.json"

if __name__ == "__main__":
    scorer.main()
