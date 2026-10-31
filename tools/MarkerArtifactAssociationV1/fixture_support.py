# SPDX-License-Identifier: Apache-2.0
"""Cached deterministic in-memory fixture used by focused tests only."""
from functools import lru_cache

from .producer import produce_legend_symbol_visibility


@lru_cache(maxsize=1)
def train_fixture():
    return produce_legend_symbol_visibility(seed=20820000, expected_split="train")
