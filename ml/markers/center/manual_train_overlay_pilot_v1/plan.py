"""Freeze a tiny private train-only overlay pilot plan from the authenticated source manifest."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
from typing import Any

ROOT = Path(r"E:\Graph-auto-reader")
PROTOCOL_PATH = Path(__file__).with_name("protocol.json")
DOMAIN = b"graphreader.marker-train-overlay-pilot-v1\0"


class PilotError(ValueError):
    pass


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _hash_component(rows: list[dict[str, Any]]) -> str:
    signature = "\n".join(
        sorted(f"{row['studyId']}:{row['embeddedImageSha256']}:{row['caseId']}" for row in rows)
    ).encode("ascii")
    return sha256_bytes(DOMAIN + signature)


def _components(rows: list[dict[str, Any]]) -> list[list[dict[str, Any]]]:
    parents = list(range(len(rows)))

    def find(index: int) -> int:
        while parents[index] != index:
            parents[index] = parents[parents[index]]
            index = parents[index]
        return index

    def union(left: int, right: int) -> None:
        a, b = find(left), find(right)
        if a != b:
            parents[max(a, b)] = min(a, b)

    seen: dict[tuple[str, str], int] = {}
    for index, row in enumerate(rows):
        for key in (("study", row["studyId"]), ("image", row["embeddedImageSha256"])):
            if key in seen:
                union(index, seen[key])
            else:
                seen[key] = index
    grouped: dict[int, list[dict[str, Any]]] = {}
    for index, row in enumerate(rows):
        grouped.setdefault(find(index), []).append(row)
    return sorted(grouped.values(), key=_hash_component)


def _validate_row(row: dict[str, Any]) -> None:
    required = {
        "caseId", "studyId", "projectSha256", "embeddedImageSha256", "imageWidth",
        "imageHeight", "labelingScope", "curvePointCount", "curvePoints",
    }
    if set(row) != required:
        raise PilotError("train source row has an unexpected schema")
    for key in ("caseId", "studyId", "projectSha256", "embeddedImageSha256"):
        value = row[key]
        if not isinstance(value, str) or len(value) != 64 or any(c not in "0123456789abcdef" for c in value):
            raise PilotError(f"{key} is not a lower-case SHA-256 identity")
    if not isinstance(row["imageWidth"], int) or not isinstance(row["imageHeight"], int) or min(row["imageWidth"], row["imageHeight"]) < 120:
        raise PilotError("source dimensions are too small for the frozen patch recipe")
    points = row["curvePoints"]
    if not isinstance(points, list) or not points or row["curvePointCount"] != len(points):
        raise PilotError("curve-point count does not match the source row")
    for point in points:
        if set(point) != {"x", "y"} or not all(isinstance(point[k], (int, float)) and not isinstance(point[k], bool) for k in ("x", "y")):
            raise PilotError("curve point is malformed")
        if not (0 <= point["x"] < row["imageWidth"] and 0 <= point["y"] < row["imageHeight"]):
            raise PilotError("curve point is outside its source image")


def build_plan(manifest: dict[str, Any], manifest_sha256: str) -> dict[str, Any]:
    protocol_sha256 = sha256_bytes(PROTOCOL_PATH.read_bytes())
    if manifest_sha256 != "a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57":
        raise PilotError("source capture manifest identity is not the reviewed train-only snapshot")
    if manifest.get("schema") != "graphreader.manual-train-only-source-capture.v1" or manifest.get("status") != "completed_train_only_source_capture_unapproved" or manifest.get("split") != "train":
        raise PilotError("source capture is not a completed unapproved train-only snapshot")
    scope = manifest.get("scope", {})
    counts = manifest.get("counts", {})
    if scope.get("development_payload_reads") != 0 or scope.get("sealed_payload_reads") != 0 or counts.get("train_projects") != 104 or counts.get("train_project_payloads_opened") != 104 or counts.get("development_project_payloads_opened") != 0:
        raise PilotError("capture scope/counts differ from the fixed train-only contract")
    rows = manifest.get("rows")
    if not isinstance(rows, list) or len(rows) != 104:
        raise PilotError("expected exactly 104 train source rows")
    for row in rows:
        _validate_row(row)
        if row["labelingScope"] != "curve-point-centers-only; marker-glyph-correspondence-unverified; no-negative-labels":
            raise PilotError("source labels exceed the permitted weak point-location scope")
    if len({row["caseId"] for row in rows}) != len(rows):
        raise PilotError("duplicate case identity")

    components = _components(rows)
    partitions = []
    for component in components:
        signature = _hash_component(component)
        bucket = "internal_train_holdout" if int(signature[:8], 16) % 4 == 0 else "internal_train_build"
        eligible = []
        for row in component:
            margin = 40
            points = [p for p in row["curvePoints"] if margin <= p["x"] < row["imageWidth"] - margin and margin <= p["y"] < row["imageHeight"] - margin]
            if points:
                eligible.append((row, sorted(points, key=lambda p: (p["y"], p["x"]))[len(points) // 2]))
        partitions.append((signature, bucket, eligible))

    selected: list[dict[str, Any]] = []
    for bucket in ("internal_train_build", "internal_train_holdout"):
        choices = []
        for signature, assigned, eligible in partitions:
            if assigned != bucket:
                continue
            for row, point in eligible:
                choices.append((hashlib.sha256((signature + row["caseId"]).encode("ascii")).hexdigest(), signature, row, point))
        if not choices:
            raise PilotError(f"no source group has a safe injected-marker anchor in {bucket}")
        _, signature, row, point = min(choices, key=lambda item: item[0])
        x, y = int(point["x"] + 0.5), int(point["y"] + 0.5)
        distractor = {"x": x + 22, "y": y + 12}
        if distractor["x"] + 6 >= row["imageWidth"] or distractor["y"] + 2 >= row["imageHeight"]:
            raise PilotError("fixed distractor leaves source bounds")
        selected.append({
            "assigned_partition": bucket,
            "component_sha256": signature,
            "case_id": row["caseId"],
            "study_id": row["studyId"],
            "project_sha256": row["projectSha256"],
            "image_sha256": row["embeddedImageSha256"],
            "image_width": row["imageWidth"],
            "image_height": row["imageHeight"],
            "anchor_pixel": {"x": x, "y": y},
            "anchor_source_point": point,
            "distractor_pixel": distractor,
            "curve_point_count": row["curvePointCount"],
        })
    if selected[0]["component_sha256"] == selected[1]["component_sha256"] or selected[0]["image_sha256"] == selected[1]["image_sha256"]:
        raise PilotError("internal partitions are not source/image disjoint")
    return {
        "schema": "graphreader.manual-train-marker-overlay-pilot-plan.v1",
        "status": "frozen_plan_not_yet_materialized",
        "protocol_sha256": protocol_sha256,
        "source_capture_manifest_sha256": manifest_sha256,
        "source_capture_label_scope": "curve-point-centers-only; marker-glyph-correspondence-unverified; no-negative-labels",
        "source_project_count": len(rows),
        "study_image_connected_component_count": len(components),
        "partition_counts": {bucket: sum(part == bucket for _, part, _ in partitions) for bucket in ("internal_train_build", "internal_train_holdout")},
        "split_claim": "assigned-signature partition over authorized train-only sources; not official dev and not evidence of historical independence",
        "render_recipe": {
            "patch_ltrb_half_open": "centered on anchor; width=72 height=56; clipped only if rejected by bounds",
            "patch_fill": [255, 255, 255, 255],
            "positive": {"kind": "filled_circle", "radius_px": 4, "color_rgba": [0, 0, 0, 255], "label": "injected_positive_only"},
            "distractor": {"kind": "horizontal_line", "length_px": 11, "width_px": 1, "color_rgba": [0, 0, 0, 255], "label": "injected_distractor_only"},
            "native_content": "unknown; never assigned positive or negative labels",
        },
        "sources": selected,
        "limitations": [
            "Engauge points are weak point-location anchors; marker-glyph correspondence is unverified.",
            "Only deliberately injected shapes receive labels; all original image content remains unknown.",
            "The internal build/holdout partition is not an official development split or evidence of historical independence.",
            "This tiny feasibility pilot cannot estimate full-image false-positive precision or support promotion.",
        ],
    }


def build_authenticated_plan(manifest_path: Path) -> dict[str, Any]:
    _require_safe_existing_path(ROOT, manifest_path)
    raw = manifest_path.read_bytes()
    digest = sha256_bytes(raw)
    manifest = json.loads(raw)
    return build_plan(manifest, digest)


def _require_safe_existing_path(root: Path, path: Path, *, directory: bool = False) -> None:
    absolute_root = Path(os.path.abspath(root))
    absolute_path = Path(os.path.abspath(path))
    try:
        relative = absolute_path.relative_to(absolute_root)
    except ValueError as exc:
        raise PilotError("input path escaped the E project") from exc
    # Check lexical ancestors before resolving so an in-project junction cannot
    # redirect a read or write outside the project.
    current = Path(absolute_root.anchor)
    for part in absolute_root.parts[1:] + relative.parts:
        current = current / part
        if current.exists() or current.is_symlink():
            attributes = getattr(os.lstat(current), "st_file_attributes", 0)
            if current.is_symlink() or attributes & 0x400:
                raise PilotError("input path contains a reparse-point ancestor")
    if directory:
        if not absolute_path.is_dir():
            raise PilotError("path is not a regular directory")
    elif not absolute_path.is_file():
        raise PilotError("input is not a regular file")


def write_fresh_plan(plan: dict[str, Any], output_dir: Path) -> Path:
    _require_safe_existing_path(ROOT, output_dir.parent, directory=True)
    if output_dir.exists() or output_dir.is_symlink():
        raise PilotError("output directory already exists")
    output_dir.mkdir()
    target = output_dir / "plan.json"
    data = (json.dumps(plan, sort_keys=True, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    fd = os.open(target, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(data)
    return target


def main() -> int:
    manifest = ROOT / "artifacts/goal22-runs/manual-train-only-source-capture-20261031-v1/manifest.json"
    plan = build_authenticated_plan(manifest)
    output = ROOT / "artifacts/goal22-runs/marker-train-overlay-pilot-plan-v2"
    path = write_fresh_plan(plan, output)
    digest = sha256_bytes(path.read_bytes())
    print(json.dumps({"status": plan["status"], "plan_path": str(path), "plan_sha256": digest,
                      "source_count": len(plan["sources"]), "partition_counts": plan["partition_counts"],
                      "component_count": plan["study_image_connected_component_count"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
