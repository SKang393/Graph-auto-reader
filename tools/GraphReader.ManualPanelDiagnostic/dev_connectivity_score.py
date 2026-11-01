# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Score flag-blind assistant review against frozen development proposals."""

from __future__ import annotations

from collections import Counter
import hashlib
import json
from pathlib import Path

from axis_connectivity_probe import RUN, read_checked
from dev_connectivity_probe import MANIFEST_SHA, PROTOCOL_SHA
from dev_connectivity_review import PROPOSAL_SHA

SHEETS_SHA = "b9f90e3a2aca3ecd725c1596d6769fd0c5f3fb939bff490a327aa0e82f7977dc"
LABEL_SHAS = (
    "df8f64236893dea73ea3f7b07a337eacdc5a5e5da7951b8a30dca7a142da3161",
    "2dad05b7742779e07358e8a8512a1e18bbbe61272ef65e8bd321e7dc2ffd5fc9",
    "5356d29b38bb82eea93a5499230a388ba2fdd77977876cb11cff52b19df8dc22",
    "0a7c3c74a94fb98cb1ec53969b00ee068b4ca92239df075cb418a1ab477808b6",
    "b2d6e5107a9d5e4152e2cc51cd19e2683874c07231407abe6227fcc98b67045d",
    "a34fb2bfdb7b29d2f5c6db89250454cfbeb6c286648fb3ab66771a333b361846",
    "498f2d4e0ee66d808082b343cbcdd86a46c3a5086b0e7e62751ac35afdb0816b",
    "a262ffa30b78d47054f28548949545043a5d342ba1b8ab1bd674fe40b14354b2",
    "e157822e88dd46009c164685aaf745c053142442d15d2d16932a9a9360229107",
)


def score_categories(proposals: list[dict], labels: list[dict]) -> dict:
    observations = {row["id"]: row for row in proposals}
    classifications = {row["id"]: row["classification"] for row in labels}
    allowed = {"numeric_text", "other_text", "graph_structure", "uncertain"}
    if (len(observations) != len(proposals) or len(classifications) != len(labels) or
            set(observations) != set(classifications) or not set(classifications.values()) <= allowed):
        raise ValueError("development review inventory differs")
    counts = Counter(classifications.values())
    flags = {category: {"items": counts[category], "dark_vertical": 0, "faint_vertical": 0}
             for category in sorted(allowed)}
    for identifier, category in classifications.items():
        row = observations[identifier]
        for key in ("dark_vertical", "faint_vertical"):
            if not isinstance(row[key], bool):
                raise ValueError("development connectivity flag differs")
            flags[category][key] += int(row[key])
    return flags


def main() -> None:
    proposal = json.loads(read_checked(RUN / "dev-connectivity-proposals-v1.json", PROPOSAL_SHA))
    directory = RUN / "dev-connectivity-review-v1"
    sheets = json.loads(read_checked(directory / "manifest.json", SHEETS_SHA))
    if (proposal["scope"] != "dev-diagnostic-not-acceptance" or proposal["manifest_sha256"] != MANIFEST_SHA or
            proposal["protocol_sha256"] != PROTOCOL_SHA or proposal["truth_read"] is not False or
            proposal["optimizer_steps"] != 0 or proposal["sealed_payload_reads"] != 0 or
            sheets["scope"] != "private-dev-review-no-flags" or sheets["proposal_sha256"] != PROPOSAL_SHA or
            sheets["manifest_sha256"] != MANIFEST_SHA or len(sheets["sheets"]) != len(LABEL_SHAS)):
        raise ValueError("development review binding differs")
    labels = []
    for index, (sheet, label_sha) in enumerate(zip(sheets["sheets"], LABEL_SHAS), 1):
        read_checked(directory / sheet["file"], sheet["sha256"])
        label = json.loads(read_checked(directory / f"labels-{index:02d}.json", label_sha))
        if (label["schema"] != "graphreader.dev-connectivity-review-labels.v1" or
                label["scope"] != "assistant-reviewed-not-independent" or label["sheet_sha256"] != sheet["sha256"] or
                [row["id"] for row in label["rows"]] != sheet["proposal_ids"]):
            raise ValueError("development sheet labels differ")
        labels.extend(label["rows"])
    panels = [panel for row in proposal["rows"] for panel in row["panels"]]
    proposals = [item for panel in panels for item in panel["proposals"]]
    if len(proposal["rows"]) != 16 or len(panels) != 16 or len(proposals) != 134:
        raise ValueError("development source coverage differs")
    result = {"schema": "graphreader.dev-axis-connectivity-score.v1", "scope": "dev-diagnostic-not-acceptance",
              "proposal_sha256": PROPOSAL_SHA, "review_sheets_sha256": SHEETS_SHA, "label_sha256": LABEL_SHAS,
              "source_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
              "coverage": {"images": len(proposal["rows"]), "panels": len(panels),
                           "failed_panels": sum(panel["status"] != "measured" for panel in panels),
                           "proposals": len(proposals)},
              "categories": score_categories(proposals, labels),
              "limitations": ["Assistant classifications are not independently adjudicated.",
                              "The development images were previously inspected for axis geometry.",
                              "Unproposed labels are not counted; this is not detection recall.",
                              "Repeated layouts and few structural examples limit generalization."]}
    encoded = (json.dumps(result, sort_keys=True, indent=2) + "\n").encode()
    with (RUN / "dev-connectivity-score-v1.json").open("xb") as stream:
        stream.write(encoded)
    print(json.dumps({"coverage": result["coverage"], "categories": result["categories"],
                      "sha256": hashlib.sha256(encoded).hexdigest()}))


if __name__ == "__main__":
    main()
