# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare a reviewer's visual tally with unique point IDs in a panel's audit JSON."""

import argparse
import json
from pathlib import Path
from uuid import UUID


def remember(records, key, value, field):
    if records.setdefault(key, value) != value:
        raise ValueError(f"Conflicting {field} for {key}. Use unchanged files from one export.")


def audit_counts(paths):
    scope = None
    placements = {}
    series_names = {}
    phase_codes = {}
    source_snapshots = {}
    target_sources = {}
    for path in paths:
        audit = json.loads(Path(path).read_text(encoding="utf-8-sig"))
        ids = tuple(UUID(audit[key]) for key in ("project_id", "panel_id", "run_id"))
        if any(identifier.int == 0 for identifier in ids):
            raise ValueError(f"Empty project, panel or run ID in {path}.")
        identity = (ids, audit.get("contract_version"), audit.get("coordinate_space"),
                    audit.get("export_mode"), audit.get("observation_order_confirmed_for_export"))
        if scope is not None and identity != scope:
            raise ValueError("Use files from one export of one panel. Check phase copies separately.")
        scope = identity
        rows = audit["rows"]
        if not isinstance(rows, list) or len(rows) != audit["row_count"]:
            raise ValueError(f"Row count disagrees with the rows in {path}.")
        target = UUID(audit["intervention_series_id"])
        by_source = {}
        for row in rows:
            if UUID(row["target_intervention_series_id"]) != target:
                raise ValueError(f"Row target disagrees with the file target in {path}.")
            series = UUID(row["source_series_id"])
            phase = UUID(row["phase_id"])
            point = UUID(row["point_id"])
            remember(placements, point, (series, phase), "point assignments")
            remember(series_names, series, row["series_name"], "source series names")
            remember(phase_codes, phase, row["phase"], "phase codes")
            # Only the destination and its inclusion reason vary when the exporter
            # copies a source series into another intervention's file.
            evidence = {k: v for k, v in row.items() if k not in ("target_intervention_series_id", "inclusion")}
            remember(by_source.setdefault(series, {}), point, evidence, "point evidence")
        for series, points in by_source.items():
            remember(source_snapshots, series, points, "source series snapshots")
        remember(target_sources, target, set(by_source), "target series contents")
    return placements, series_names, phase_codes


def select_id(labels, value, field):
    for identifier in labels:
        if str(identifier) == value.lower():
            return identifier
    matches = [identifier for identifier, label in labels.items() if label == value]
    if not matches:
        raise ValueError(f"No {field} matches {value!r} in these rows. Tally unresolved. Check the project and export scope.")
    if len(matches) > 1:
        choices = ", ".join(map(str, matches))
        raise ValueError(f"Ambiguous {field} {value!r}. Use an ID from the audit: {choices}")
    return matches[0]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("audits", nargs="+", metavar="FILE.audit.json")
    parser.add_argument("--series", required=True, help="Source series name or source_series_id")
    parser.add_argument("--phase", required=True, help="Phase code or phase_id")
    parser.add_argument("--visible", required=True, type=int, help="Marks counted on the source plot")
    args = parser.parse_args()
    if args.visible < 0:
        parser.error("--visible must be a nonnegative whole number")
    try:
        placements, series_names, phase_codes = audit_counts(args.audits)
        series = select_id(series_names, args.series, "source series")
        phase = select_id(phase_codes, args.phase, "phase")
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as exc:
        parser.error(str(exc))

    placed = sum(group == (series, phase) for group in placements.values())
    difference = placed - args.visible
    print(f"{series_names[series]} / {phase_codes[phase]}")
    print(f"Visible marks = {args.visible}")
    print(f"Distinct audited points = {placed}")
    print(f"Audited minus visible = {difference:+d}")
    print("Counts agree." if difference == 0 else "Counts differ.")
    if len(args.audits) > 1:
        print("Combined tally assumes unchanged files from one export action.")
    if placed == 0:
        print("No rows for this pair in the supplied files. Check which series and phases were exported.")
    print("Source plot completeness is unverified. Check each mark's visit, position and phase.")


if __name__ == "__main__":
    main()
