# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Compare a reviewer's visual tally with unique point IDs in a panel's audit JSON."""

import argparse
import json
from pathlib import Path
from uuid import UUID


def audit_counts(paths):
    scope = None
    placements = {}
    series_names = {}
    phase_codes = {}
    for path in paths:
        audit = json.loads(Path(path).read_text(encoding="utf-8-sig"))
        identity = tuple(UUID(audit[key]) for key in ("project_id", "panel_id", "run_id"))
        if scope is not None and identity != scope:
            raise ValueError("Use files from one export of one panel. Check phase copies separately.")
        scope = identity
        rows = audit["rows"]
        if not isinstance(rows, list) or len(rows) != audit["row_count"]:
            raise ValueError(f"Row count disagrees with the rows in {path}.")
        for row in rows:
            series = UUID(row["source_series_id"])
            phase = UUID(row["phase_id"])
            point = UUID(row["point_id"])
            if placements.setdefault(point, (series, phase)) != (series, phase):
                raise ValueError(f"Conflicting assignments for point {point}.")
            series_names[series] = row["series_name"]
            phase_codes[phase] = row["phase"]
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
    if placed == 0:
        print("No rows for this pair in the supplied files. Check which series and phases were exported.")
    print("Source plot completeness is unverified. Check each mark's visit, position and phase.")


if __name__ == "__main__":
    main()
