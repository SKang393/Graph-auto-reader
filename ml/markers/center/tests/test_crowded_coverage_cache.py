# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import copy
import pytest

from ml.markers.center import crowded_coverage_cache as cache
from ml.markers.center import crowded_coverage_data as data


@pytest.fixture
def population(monkeypatch):
    recipes = tuple(data.CrowdedCase("circle", "filled", context, "target_first", 0) for context in data.CONTEXTS)
    monkeypatch.setattr(data, "cases", lambda: recipes)
    return [data.prepare_case(case).record for case in recipes]


def test_inventory_counts_every_neighbor_in_all_contexts(population):
    result = cache.audit_records(population)
    assert result["scenes"] == 4 and result["truths"] == 11
    assert sum(result["shape_counts"].values()) == 11
    assert sum(result["context_counts"].values()) == 4


@pytest.mark.parametrize("defect", ["missing", "duplicate", "dev", "invisible_neighbor", "target_count"])
def test_missing_or_unusable_supervision_is_rejected(population, defect):
    rows = copy.deepcopy(population)
    if defect == "missing":
        rows.pop()
    elif defect == "duplicate":
        rows[-1] = rows[0]
    elif defect == "dev":
        rows[-1]["split"] = "dev"
    elif defect == "invisible_neighbor":
        rows[-1]["visible_pixels_by_marker"][-1] = 0
    else:
        rows[-1]["truth_count"] -= 1
    with pytest.raises(ValueError):
        cache.audit_records(rows)


def test_changed_cache_is_rejected_before_loading_any_training_or_dev(tmp_path, monkeypatch):
    (tmp_path/"manifest.json").write_text("{}")
    monkeypatch.setattr(cache, "load_parent", lambda *args: pytest.fail("Unbound cache accessed prior data"))
    with pytest.raises(ValueError, match="manifest changed"):
        cache.load_cache(tmp_path, tmp_path, "0"*64)
