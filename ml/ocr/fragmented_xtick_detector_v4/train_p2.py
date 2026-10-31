# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""V4 P2 uses the common P1 trainer with per-image hard-negative mining."""

from __future__ import annotations

import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parents[2]))
from ml.ocr.fragmented_xtick_detector_v4.train import ROOT, run_training

CONFIG = ROOT / "training" / "p2.json"


if __name__ == "__main__":
    print(json.dumps(run_training(CONFIG), indent=2, sort_keys=True))
