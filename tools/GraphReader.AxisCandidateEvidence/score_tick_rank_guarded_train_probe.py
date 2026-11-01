# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Use the fixed scorer on the guarded synthetic-training capture."""
from pathlib import Path

import score_tick_rank_train_probe as scorer
import tick_rank_guarded_train_probe as guarded

scorer.OUTPUT = guarded.OUTPUT
scorer.PROBE = Path(guarded.__file__).resolve()

if __name__ == "__main__":
    scorer.main()
