# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""P2 entrypoint: the shared P1 runner with per-image top-32 background BCE."""

from __future__ import annotations

import json
from pathlib import Path
import sys

REPO_ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(REPO_ROOT))
from ml.ocr.stroke_seeded_xtick_v1.train_gpu import ROOT, run_training


if __name__ == "__main__":
    print(json.dumps(run_training(ROOT / "training" / "p2.json"), sort_keys=True, indent=2))
