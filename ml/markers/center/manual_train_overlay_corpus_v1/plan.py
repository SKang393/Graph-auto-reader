# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

"""Freeze a deterministic source-grouped expansion plan from authenticated train-only metadata."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
from typing import Any

ROOT = Path(r"E:\Graph-auto-reader")
PROTOCOL_PATH = Path(__file__).with_name("protocol.json")
MANIFEST_SHA = "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57"
PILOT_PLAN_SHA = "eac5d357a7440537d209e2c22e2af957e06d4edc39e2036ac69b03d427a26243"
COMPONENT_DOMAIN = b"graphreader.marker-train-overlay-pilot-v1\0"
PARTITION_DOMAIN = b"graphreader.manual-train-overlay-corpus-v1\0"
HOLDOUT_FIRST_BYTE_LT = 64


class CorpusPlanError(ValueError):
    pass


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _components(rows: list[dict[str, Any]]) -> list[list[dict[str, Any]]]:
    parent = list(range(len(rows)))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    def union(a: int, b: int) -> None:
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[max(ra, rb)] = min(ra, rb)

    seen: dict[tuple[str, str], int] = {}
    for i, row in enumerate(rows):
        for key in (("study", row["studyId"]), ("image", row["embeddedImageSha256"])):
            if key in seen:
                union(i, seen[key])
            else:
                seen[key] = i
    groups: dict[int, list[dict[str, Any]]] = {}
    for i, row in enumerate(rows):
        groups.setdefault(find(i), []).append(row)
    return list(groups.values())


def _component_id(rows: list[dict[str, Any]]) -> str:
    signature = "\n".join(sorted(
        f"{row['studyId']}:{row['embeddedImageSha256']}:{row['caseId']}" for row in rows
    )).encode("ascii")
    return _sha(COMPONENT_DOMAIN + signature)


def _safe_path(path: Path, *, directory: bool = False) -> Path:
    root = Path(os.path.abspath(ROOT))
    absolute = Path(os.path.abspath(path))
    try:
        rel = absolute.relative_to(root)
    except ValueError as exc:
        raise CorpusPlanError("path escaped E project") from exc
    current = Path(root.anchor)
    for part in root.parts[1:] + rel.parts:
        current /= part
        if current.exists() or current.is_symlink():
            attrs = getattr(os.lstat(current), "st_file_attributes", 0)
            if current.is_symlink() or attrs & 0x400:
                raise CorpusPlanError("path has a reparse-point ancestor")
    if directory and not absolute.is_dir():
        raise CorpusPlanError("path is not a directory")
    if not directory and not absolute.is_file():
        raise CorpusPlanError("path is not a regular file")
    return absolute


def build_plan(manifest: dict[str, Any], manifest_sha256: str, pilot_plan: dict[str, Any], pilot_plan_sha256: str) -> dict[str, Any]:
    if manifest_sha256 != MANIFEST_SHA or pilot_plan_sha256 != PILOT_PLAN_SHA:
        raise CorpusPlanError("train manifest or completed pilot plan identity differs")
    protocol_sha = _sha(PROTOCOL_PATH.read_bytes())
    if manifest.get("schema") != "graphreader.manual-train-only-source-capture.v1" or manifest.get("split") != "train" or \
        manifest.get("status") != "completed_train_only_source_capture_unapproved":
        raise CorpusPlanError("input source snapshot is not completed train-only capture")
    counts, scope = manifest.get("counts", {}), manifest.get("scope", {})
    if counts.get("train_projects") != 104 or counts.get("train_project_payloads_opened") != 104 or \
        counts.get("development_project_payloads_opened") != 0 or scope.get("development_payload_reads") != 0 or scope.get("sealed_payload_reads") != 0:
        raise CorpusPlanError("source snapshot scope/counts differ")
    rows = manifest.get("rows")
    if not isinstance(rows, list) or len(rows) != 104 or len({row.get("caseId") for row in rows}) != 104:
        raise CorpusPlanError("expected exactly 104 unique train source rows")
    expected_label_scope = "curve-point-centers-only; marker-glyph-correspondence-unverified; no-negative-labels"
    for row in rows:
        if row.get("labelingScope") != expected_label_scope or row.get("curvePointCount") != len(row.get("curvePoints", [])):
            raise CorpusPlanError("source annotation scope or point count differs")
        for point in row["curvePoints"]:
            if not (0 <= point["x"] < row["imageWidth"] and 0 <= point["y"] < row["imageHeight"]):
                raise CorpusPlanError("source curve point outside image bounds")
    if pilot_plan.get("schema") != "graphreader.manual-train-marker-overlay-pilot-plan.v1" or \
        pilot_plan.get("source_capture_manifest_sha256") != manifest_sha256 or len(pilot_plan.get("sources", [])) != 2:
        raise CorpusPlanError("completed pilot exclusion plan is invalid")

    groups = _components(rows)
    if len(groups) != 22:
        raise CorpusPlanError("source group count differs from fixed inventory snapshot")
    pilot_components = {source["component_sha256"] for source in pilot_plan["sources"]}
    selected: list[dict[str, Any]] = []
    accounted = {"pilot_previously_examined": 0, "no_safe_anchor": 0, "selected_build": 0, "selected_holdout": 0}
    for group in groups:
        component = _component_id(group)
        if component in pilot_components:
            accounted["pilot_previously_examined"] += 1
            continue
        digest = hashlib.sha256(PARTITION_DOMAIN + component.encode("ascii")).digest()
        split = "internal_train_holdout" if digest[0] < HOLDOUT_FIRST_BYTE_LT else "internal_train_build"
        candidates: list[tuple[str, dict[str, Any], tuple[float, float]]] = []
        for row in group:
            if row["imageWidth"] < 160 or row["imageHeight"] < 128:
                continue
            points = [p for p in row["curvePoints"] if 64 <= p["x"] < row["imageWidth"] - 64 and 64 <= p["y"] < row["imageHeight"] - 64]
            if not points:
                continue
            candidate_sha = hashlib.sha256((component + row["caseId"]).encode("ascii")).hexdigest()
            point_index = int(hashlib.sha256((candidate_sha + "anchor").encode("ascii")).hexdigest()[:8], 16) % len(points)
            point = points[point_index]
            candidates.append((candidate_sha, row, (float(point["x"]), float(point["y"]))))
        if not candidates:
            accounted["no_safe_anchor"] += 1
            continue
        _, row, anchor = min(candidates, key=lambda item: item[0])
        selected.append({
            "source_index": -1,
            "assigned_partition": split,
            "component_sha256": component,
            "case_id": row["caseId"],
            "study_id": row["studyId"],
            "project_sha256": row["projectSha256"],
            "image_sha256": row["embeddedImageSha256"],
            "image_width": row["imageWidth"],
            "image_height": row["imageHeight"],
            "curve_point_count": row["curvePointCount"],
            "anchor_source_point": {"x": anchor[0], "y": anchor[1]},
            "anchor_pixel": {"x": int(anchor[0] + 0.5), "y": int(anchor[1] + 0.5)},
        })
    selected.sort(key=lambda row: (row["assigned_partition"], row["component_sha256"]))
    for index, row in enumerate(selected):
        row["source_index"] = index
        accounted["selected_build" if row["assigned_partition"] == "internal_train_build" else "selected_holdout"] += 1
    if accounted["pilot_previously_examined"] != 2 or len(selected) > 20 or not accounted["selected_build"] or not accounted["selected_holdout"]:
        raise CorpusPlanError("source selection accounting or internal split is invalid")
    if len({row["component_sha256"] for row in selected}) != len(selected) or len({row["image_sha256"] for row in selected}) != len(selected):
        raise CorpusPlanError("selected sources are not exact-image/group disjoint")
    return {
        "schema": "graphreader.manual-train-marker-overlay-corpus-plan.v1",
        "status": "frozen_train_only_expansion_plan_unmaterialized",
        "protocol_sha256": protocol_sha,
        "source_capture_manifest_sha256": manifest_sha256,
        "pilot_plan_sha256": pilot_plan_sha256,
        "source_project_count": 104,
        "source_component_count": 22,
        "excluded_pilot_component_count": accounted["pilot_previously_examined"],
        "accounting": accounted,
        "split_claim": "source-study-plus-exact-image connected components assigned by fixed signature inside authorized train sources; not official dev and historical independence unverified",
        "source_rows": selected,
        "recipes": json.loads(PROTOCOL_PATH.read_bytes())["rendering"]["recipes"],
        "limitations": [
            "Engauge curve points are weak source-coordinate placement anchors; correspondence to naturally occurring marker glyphs is unverified.",
            "Only deliberately injected shapes receive labels; all native pixels and native structures remain unknown.",
            "The internal holdout is a small assigned-signature partition within train and is not official dev or historical independence evidence.",
            "This corpus does not estimate full-image false-positive precision or production marker accuracy.",
        ],
    }


def build_authenticated_plan(manifest_path: Path, pilot_plan_path: Path) -> dict[str, Any]:
    manifest_path = _safe_path(manifest_path)
    pilot_plan_path = _safe_path(pilot_plan_path)
    manifest_bytes, pilot_bytes = manifest_path.read_bytes(), pilot_plan_path.read_bytes()
    return build_plan(json.loads(manifest_bytes), _sha(manifest_bytes), json.loads(pilot_bytes), _sha(pilot_bytes))


def write_new_plan(plan: dict[str, Any], output_dir: Path) -> Path:
    output_dir = Path(os.path.abspath(output_dir))
    parent = _safe_path(output_dir.parent, directory=True)
    if output_dir.exists() or output_dir.is_symlink():
        raise CorpusPlanError("plan output directory already exists")
    output_dir.mkdir()
    path = output_dir / "plan.json"
    data = (json.dumps(plan, sort_keys=True, indent=2) + "\n").encode("utf-8")
    fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(data)
    return path


def main() -> int:
    plan = build_authenticated_plan(
        ROOT / "artifacts/goal22-runs/manual-train-only-source-capture-20261031-v1/manifest.json",
        ROOT / "artifacts/goal22-runs/marker-train-overlay-pilot-plan-v2/plan.json")
    out = write_new_plan(plan, ROOT / "artifacts/goal22-runs/marker-train-overlay-corpus-plan-v7")
    print(json.dumps({"status": plan["status"], "plan_sha256": _sha(out.read_bytes()), "component_count": plan["source_component_count"],
        "selected_sources": len(plan["source_rows"]), "accounting": plan["accounting"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
