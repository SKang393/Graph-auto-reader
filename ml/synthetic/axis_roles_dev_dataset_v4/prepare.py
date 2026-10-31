# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Process-isolated, development-only axis-role scene preparation.

The parent validates protocol and artifact bytes but never imports Pillow or
the legacy renderer wrapper. Each scene is rendered by one short-lived child.
"""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from hashlib import sha256
import base64
import math
import json
import os
from pathlib import Path, PurePosixPath
import platform
import stat
import subprocess
import sys
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parents[3]
PACKAGE = Path(__file__).resolve().parent
PROTOCOL_PATH = PACKAGE / "protocol.json"
PROTOCOL_SHA256 = "378cff27a073070107297f91118b6203cd5fae8a62887f37c80be868f2668238"
WORKER_MODULE = "ml.synthetic.axis_roles_dev_dataset_v4.worker"
MAX_CHILDREN = 2
CHILD_TIMEOUT_SECONDS = 120
MAX_REQUEST_BYTES = 16 * 1024
MAX_RESPONSE_BYTES = 8 * 1024 * 1024
OUTPUT_SCHEMA = "graphreader.synthetic-axis-roles-dev-v4"


def _sha(data: bytes) -> str:
    return sha256(data).hexdigest()


def _canonical_json(value: Any) -> bytes:
    return (
        json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)
        + "\n"
    ).encode("utf-8")


def _is_reparse_info(info: Any) -> bool:
    attributes = getattr(info, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return stat.S_ISLNK(info.st_mode) or bool(attributes & reparse_flag)


def _has_reparse_attribute(path: Path) -> bool:
    return _is_reparse_info(path.lstat())


def _safe_inventory(root: Path) -> set[str]:
    """List regular files without following a symlink or reparse directory."""
    root = root.resolve(strict=True)
    pending = [root]
    files: set[str] = set()
    while pending:
        directory = pending.pop()
        with os.scandir(directory) as entries:
            for entry in entries:
                info = entry.stat(follow_symlinks=False)
                if _is_reparse_info(info):
                    raise ValueError("Dataset inventory contains a reparse point")
                path = Path(entry.path)
                if stat.S_ISDIR(info.st_mode):
                    pending.append(path)
                elif stat.S_ISREG(info.st_mode):
                    files.add(path.relative_to(root).as_posix())
                else:
                    raise ValueError("Dataset inventory contains a non-regular file")
    return files


def _assert_no_reparse_ancestors(path: Path, root: Path) -> None:
    if root.is_symlink() or _has_reparse_attribute(root):
        raise ValueError("Project root cannot be a reparse point")
    resolved_root = root.resolve(strict=True)
    candidate = path.absolute()
    try:
        candidate.relative_to(resolved_root)
    except ValueError as exc:
        raise ValueError("Path escapes the project root") from exc
    current = resolved_root
    relative = candidate.relative_to(resolved_root)
    for part in relative.parts:
        current = current / part
        if current.exists() or current.is_symlink():
            if _has_reparse_attribute(current):
                raise ValueError(f"Reparse point is not allowed in bound path: {current}")


def load_protocol() -> dict[str, Any]:
    raw = PROTOCOL_PATH.read_bytes()
    if _sha(raw) != PROTOCOL_SHA256:
        raise ValueError("Pinned v4 protocol bytes differ")
    protocol = json.loads(raw)
    if not isinstance(protocol, dict) or protocol.get("schema") != "graphreader.synthetic-axis-roles-dev-v4.protocol.v1":
        raise ValueError("Unsupported v4 protocol")
    recipes = protocol.get("recipes")
    if not isinstance(recipes, list) or len(recipes) != 16:
        raise ValueError("Protocol recipe inventory differs")
    expected = _expected_recipes()
    if recipes != expected:
        raise ValueError("Protocol recipes do not match the fixed catalog")
    return protocol


def _expected_recipes() -> list[dict[str, Any]]:
    designs = ("ab", "aba", "abab", "multiple_baseline")
    overlays = ("none", "grid", "frame", "grid_frame")
    return [
        {
            "index": index,
            "seed": 20270500 + index,
            "design": designs[index % len(designs)],
            "overlay": overlays[index // 4],
            "participant_format": "full" if index % 2 == 0 else "numeric",
        }
        for index in range(16)
    ]


def _source_bindings(protocol: dict[str, Any] | None = None) -> dict[str, str]:
    protocol = protocol or load_protocol()
    bindings = protocol.get("upstream_source_bindings")
    if not isinstance(bindings, dict) or not bindings:
        raise ValueError("Protocol source binding inventory is empty")
    for relative, expected_hash in bindings.items():
        rel = PurePosixPath(relative)
        if rel.is_absolute() or ".." in rel.parts or "\\" in relative:
            raise ValueError(f"Unsafe source binding path: {relative}")
        path = ROOT.joinpath(*rel.parts)
        _assert_no_reparse_ancestors(path, ROOT)
        if not path.is_file():
            raise ValueError(f"Bound renderer source is missing: {relative}")
        if _sha(path.read_bytes()) != expected_hash:
            raise ValueError(f"Bound renderer source hash differs: {relative}")
    return dict(bindings)


def _runtime_source_bindings(protocol: dict[str, Any]) -> dict[str, str]:
    bindings = _source_bindings(protocol)
    for path in (
        PACKAGE / "__init__.py",
        PACKAGE / "prepare.py",
        PACKAGE / "worker.py",
        PROTOCOL_PATH,
    ):
        rel = path.relative_to(ROOT).as_posix()
        _assert_no_reparse_ancestors(path, ROOT)
        bindings[rel] = _sha(path.read_bytes())
    return dict(sorted(bindings.items()))


def _clean_child_environment() -> dict[str, str]:
    env = dict(os.environ)
    env.pop("PYTHONPATH", None)
    env.pop("PYTHONHOME", None)
    env["PYTHONNOUSERSITE"] = "1"
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    return env


def _expected_recipe_by_index(index: int, protocol: dict[str, Any]) -> dict[str, Any]:
    recipes = protocol["recipes"]
    if isinstance(index, bool) or not isinstance(index, int) or index < 0 or index >= len(recipes):
        raise ValueError("Recipe index is outside the fixed development catalog")
    return recipes[index]


def _validate_scene_references(
    scene: dict[str, Any], annotation: dict[str, Any], overlay: dict[str, Any], recipe: dict[str, Any], protocol: dict[str, Any]
) -> None:
    if scene.get("scene_id") != annotation.get("scene_id"):
        raise ValueError("Scene and annotation identities differ")
    panels = scene.get("panels")
    if not isinstance(panels, list) or not panels:
        raise ValueError("Scene panel inventory is empty")
    panel_ids = [panel.get("panel_id") for panel in panels]
    if any(not isinstance(panel_id, str) for panel_id in panel_ids) or len(set(panel_ids)) != len(panel_ids):
        raise ValueError("Scene panel identities are invalid or duplicated")
    panel_id_set = set(panel_ids)
    for panel in panels:
        phases = [item.get("phase_id") for item in panel.get("phases", [])]
        series = [item.get("series_id") for item in panel.get("series", [])]
        points = {item.get("point_id"): item for item in panel.get("points", [])}
        if len(set(phases)) != len(phases) or len(set(series)) != len(series) or len(points) != len(panel.get("points", [])):
            raise ValueError("Duplicate phase, series, or point identity")
        phase_set = set(phases)
        series_set = set(series)
        for point in points.values():
            if point.get("phase_id") not in phase_set or point.get("series_id") not in series_set:
                raise ValueError("Point references an unknown phase or series")
        for edge in panel.get("connections", []):
            start = points.get(edge.get("from_point_id"))
            end = points.get(edge.get("to_point_id"))
            if (start is None or end is None or edge.get("series_id") not in series_set or
                    start.get("series_id") != edge.get("series_id") or end.get("series_id") != edge.get("series_id")):
                raise ValueError("Connection references inconsistent point or series identities")
        for entry in panel.get("legend", {}).get("entries", []):
            if entry.get("series_id") not in series_set:
                raise ValueError("Legend entry references an unknown series")
    for collection_name in ("text_regions", "artifacts"):
        collection = scene.get("annotations", {}).get(collection_name, [])
        for item in collection:
            if item.get("panel_id") not in panel_id_set:
                raise ValueError(f"{collection_name} references an unknown panel")
    for item in scene.get("hard_negatives", []):
        if item.get("panel_id") not in panel_id_set:
            raise ValueError("Hard negative references an unknown panel")
    annotation_panels = annotation.get("panels")
    if (not isinstance(annotation_panels, list) or
            len({item.get("panel_id") for item in annotation_panels}) != len(annotation_panels) or
            {item.get("panel_id") for item in annotation_panels} != panel_id_set):
        raise ValueError("Rendered annotation panel references differ from scene")
    participant_text: dict[str, str] = {}
    rendered_by_id = {item["panel_id"]: item for item in annotation_panels}
    source_texts = scene.get("annotations", {}).get("text_regions", [])
    source_by_id = {item.get("region_id"): item for item in source_texts}
    if None in source_by_id or len(source_by_id) != len(source_texts):
        raise ValueError("Source text identities are invalid or duplicated")
    rendered_ids: set[str] = set()
    for panel in panels:
        rendered = rendered_by_id[panel["panel_id"]]
        for rendered_text in rendered.get("texts", []):
            text_id = rendered_text.get("text_id")
            source = source_by_id.get(text_id)
            if (not isinstance(text_id, str) or text_id in rendered_ids or source is None or
                    rendered_text.get("region_id") != text_id or
                    rendered_text.get("panel_id") != source.get("panel_id") or
                    rendered_text.get("panel_id") != rendered.get("panel_id") or
                    rendered_text.get("text") != source.get("text") or
                    rendered_text.get("role") != source.get("role") or
                    rendered_text.get("visible") is not source.get("visible")):
                raise ValueError("Rendered text reference or semantics differ from source")
            rendered_ids.add(text_id)
        expected_number = 10 + ((int(recipe["seed"]) * 13 + int(panel["index"]) * 7) % 90)
        expected_text = f"Participant {expected_number:02d}" if recipe["participant_format"] == "full" else f"{expected_number:02d}"
        panel_texts = [item for item in rendered.get("texts", []) if item.get("role") == "participant"]
        if len(panel_texts) != 1 or panel_texts[0].get("text") != expected_text or not panel_texts[0].get("visible"):
            raise ValueError("Rendered participant text differs from fixed recipe")
        participant_text[rendered["panel_id"]] = panel_texts[0]["text"]
    if set(participant_text) != panel_id_set:
        raise ValueError("Rendered participant text panel references differ")
    if rendered_ids != set(source_by_id):
        raise ValueError("Rendered and source text inventories differ")
    font = annotation.get("font", {})
    if font.get("sha256") != protocol["font_provenance"]["sha256"]:
        raise ValueError("Rendered font hash differs from pinned protocol")
    overlay_lines = overlay.get("lines")
    if not isinstance(overlay_lines, list):
        raise ValueError("Overlay line inventory is missing")
    for line in overlay_lines:
        if line.get("panel_id") not in panel_id_set:
            raise ValueError("Overlay line references an unknown panel")
        for key in ("start", "end", "original_start", "original_end"):
            point = line.get(key)
            if (not isinstance(point, list) or len(point) != 2 or
                    any(type(value) not in (float, int) or not math.isfinite(value) for value in point)):
                raise ValueError("Overlay endpoint geometry is invalid")


def _validate_worker_payload(
    payload: dict[str, Any], recipe: dict[str, Any], protocol: dict[str, Any]
) -> dict[str, Any]:
    required = {
        "schema", "recipe_id", "recipe", "scene", "annotation", "overlay", "png_base64",
        "hashes", "child_pid", "child_pil_constructor_restored", "runtime",
    }
    if set(payload) != required or payload.get("schema") != "graphreader.synthetic-axis-roles-dev-v4.worker-result.v1":
        raise ValueError("Worker result shape/schema differs")
    if payload.get("recipe") != recipe or payload.get("recipe_id") != _sha(_canonical_json(recipe)):
        raise ValueError("Worker returned a different fixed recipe")
    if isinstance(payload.get("child_pid"), bool) or not isinstance(payload.get("child_pid"), int) or payload["child_pid"] <= 0:
        raise ValueError("Worker process identity is invalid")
    if payload.get("child_pil_constructor_restored") is not True:
        raise ValueError("Child did not report restored PIL canvas constructor")
    runtime = payload.get("runtime")
    if (not isinstance(runtime, dict) or set(runtime) != {"python", "pillow", "jsonschema"} or
            any(not isinstance(value, str) or not value for value in runtime.values())):
        raise ValueError("Child runtime provenance is malformed")
    scene = payload.get("scene")
    annotation = payload.get("annotation")
    overlay = payload.get("overlay")
    if not isinstance(scene, dict) or not isinstance(annotation, dict) or not isinstance(overlay, dict):
        raise ValueError("Worker structured artifacts are malformed")
    _validate_scene_references(scene, annotation, overlay, recipe, protocol)
    try:
        png = base64.b64decode(payload["png_base64"], validate=True)
    except (ValueError, TypeError) as exc:
        raise ValueError("Worker PNG is not valid base64") from exc
    if not png.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError("Worker image is not PNG")
    expected_hashes = {
        "scene": _sha(_canonical_json(scene)),
        "annotation": _sha(_canonical_json(annotation)),
        "overlay": _sha(_canonical_json(overlay)),
        "image_png": _sha(png),
    }
    if payload.get("hashes") != expected_hashes:
        raise ValueError("Worker artifact hashes differ from returned bytes")
    return {
        "recipe": recipe,
        "recipe_id": payload["recipe_id"],
        "scene": scene,
        "annotation": annotation,
        "overlay": overlay,
        "image_png": png,
        "hashes": expected_hashes,
        "child_pid": payload["child_pid"],
        "runtime": runtime,
    }


def _run_one(recipe: dict[str, Any], protocol: dict[str, Any]) -> dict[str, Any]:
    request = {"schema": "graphreader.synthetic-axis-roles-dev-v4.worker-request.v1", "protocol_sha256": PROTOCOL_SHA256, "recipe": recipe}
    request_bytes = _canonical_json(request)
    if len(request_bytes) > MAX_REQUEST_BYTES:
        raise ValueError("Worker request exceeds fixed size bound")
    command = [sys.executable, "-m", WORKER_MODULE]
    try:
        completed = subprocess.run(
            command,
            input=request_bytes,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=str(ROOT),
            env=_clean_child_environment(),
            timeout=CHILD_TIMEOUT_SECONDS,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(f"Child render exceeded {CHILD_TIMEOUT_SECONDS}s for recipe {recipe['index']}") from exc
    if completed.returncode != 0:
        detail = completed.stderr.decode("utf-8", errors="replace")[-2000:].strip()
        raise RuntimeError(f"Child render failed for recipe {recipe['index']}: {detail}")
    if len(completed.stdout) > MAX_RESPONSE_BYTES:
        raise ValueError("Worker response exceeds fixed size bound")
    try:
        payload = json.loads(completed.stdout.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError("Worker returned malformed JSON") from exc
    return _validate_worker_payload(payload, recipe, protocol)


def run_children(recipes: Iterable[dict[str, Any]], max_workers: int = MAX_CHILDREN) -> list[dict[str, Any]]:
    protocol = load_protocol()
    if isinstance(max_workers, bool) or not isinstance(max_workers, int) or not 1 <= max_workers <= MAX_CHILDREN:
        raise ValueError(f"max_workers must be within 1..{MAX_CHILDREN}")
    source_bindings_before = _runtime_source_bindings(protocol)
    requested = list(recipes)
    fixed = {recipe["index"]: recipe for recipe in protocol["recipes"]}
    if any(fixed.get(recipe.get("index")) != recipe for recipe in requested):
        raise ValueError("Requested recipe is not in the fixed development catalog")
    if len({recipe["index"] for recipe in requested}) != len(requested):
        raise ValueError("Duplicate recipe requested")
    with ThreadPoolExecutor(max_workers=max_workers) as pool:
        futures = [pool.submit(_run_one, recipe, protocol) for recipe in requested]
        results = [future.result() for future in futures]
    if _runtime_source_bindings(protocol) != source_bindings_before:
        raise ValueError("Generator or pinned renderer sources changed while child renders were running")
    return sorted(results, key=lambda result: result["recipe"]["index"])


def _assert_artifact_output_path(path: Path) -> Path:
    artifacts_root = (ROOT / "artifacts").resolve(strict=True)
    if (ROOT / "artifacts").is_symlink() or _has_reparse_attribute(ROOT / "artifacts"):
        raise ValueError("Project artifacts root cannot be a reparse point")
    requested = path if path.is_absolute() else ROOT / path
    if requested.exists():
        raise ValueError("Output directory must be new")
    absolute = requested.absolute()
    try:
        relative = absolute.relative_to(artifacts_root)
    except ValueError as exc:
        raise ValueError("Output must be under this project's artifacts directory") from exc
    if not relative.parts:
        raise ValueError("Output directory cannot be the artifacts root")
    current = artifacts_root
    for part in relative.parts:
        current = current / part
        if current.exists() or current.is_symlink():
            if _has_reparse_attribute(current):
                raise ValueError("Output path crosses a reparse point")
    return absolute


def _write_exclusive(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    _assert_no_reparse_ancestors(path, ROOT)
    with path.open("xb") as handle:
        handle.write(data)


def generate(output: str | Path, max_workers: int = MAX_CHILDREN) -> str:
    protocol = load_protocol()
    upstream = _source_bindings(protocol)
    source_bindings = _runtime_source_bindings(protocol)
    output_path = _assert_artifact_output_path(Path(output))
    results = run_children(protocol["recipes"], max_workers=max_workers)
    child_runtimes = {_canonical_json(result["runtime"]) for result in results}
    if len(child_runtimes) != 1:
        raise ValueError("Child runtime provenance differs between render processes")
    child_runtime = results[0]["runtime"]
    output_path.mkdir(parents=True, exist_ok=False)
    for folder in ("images", "scenes", "annotations", "overlays"):
        (output_path / folder).mkdir()
    hashes: dict[str, str] = {}
    cases: list[dict[str, Any]] = []
    for recipe, result in zip(protocol["recipes"], results, strict=True):
        index = int(recipe["index"])
        artifacts = {
            f"images/{index:02d}.png": result["image_png"],
            f"scenes/{index:02d}.json": _canonical_json(result["scene"]),
            f"annotations/{index:02d}.json": _canonical_json(result["annotation"]),
            f"overlays/{index:02d}.json": _canonical_json(result["overlay"]),
        }
        for relative, data in artifacts.items():
            _write_exclusive(output_path / relative, data)
            hashes[relative] = _sha(data)
        cases.append({
            "index": index,
            "recipe_id": result["recipe_id"],
            **recipe,
            "scene_id": result["scene"]["scene_id"],
            "panel_count": len(result["scene"]["panels"]),
            "artifact_sha256": {relative: hashes[relative] for relative in artifacts},
        })
    manifest = {
        "schema": OUTPUT_SCHEMA,
        "scope": "development_only",
        "protocol_sha256": PROTOCOL_SHA256,
        "source_bindings": source_bindings,
        "upstream_source_bindings": upstream,
        "font_provenance": protocol["font_provenance"],
        "recipes": protocol["recipes"],
        "cases": cases,
        "artifact_sha256": hashes,
        "environment": {
            "python": platform.python_version(),
            "platform": platform.platform(),
            "max_concurrent_children": max_workers,
            "child_runtime": child_runtime,
        },
        "claims_and_limits": protocol["claims_and_limits"],
        "reserved_pixels_accessed": False,
        "private_reads": 0,
        "sealed_reads": 0,
        "optimizer_steps": 0,
        "training_approved": False,
    }
    manifest_bytes = _canonical_json(manifest)
    _write_exclusive(output_path / "manifest.json", manifest_bytes)
    return _sha(manifest_bytes)


def verify(output: str | Path, expected_manifest_sha256: str, max_workers: int = MAX_CHILDREN) -> dict[str, Any]:
    protocol = load_protocol()
    source_bindings = _runtime_source_bindings(protocol)
    output_input = Path(output)
    if not output_input.is_absolute():
        output_input = ROOT / output_input
    _assert_no_reparse_ancestors(output_input.absolute(), ROOT)
    output_path = output_input.resolve(strict=True)
    _assert_no_reparse_ancestors(output_path, ROOT)
    if not output_path.is_relative_to((ROOT / "artifacts").resolve(strict=True)):
        raise ValueError("Dataset directory is outside project artifacts")
    manifest_path = output_path / "manifest.json"
    _assert_no_reparse_ancestors(manifest_path, output_path)
    manifest_bytes = manifest_path.read_bytes()
    if _sha(manifest_bytes) != expected_manifest_sha256:
        raise ValueError("Manifest digest differs")
    manifest = json.loads(manifest_bytes)
    if set(manifest) != {
            "schema", "scope", "protocol_sha256", "source_bindings", "upstream_source_bindings", "font_provenance",
            "recipes", "cases", "artifact_sha256", "environment", "claims_and_limits",
            "reserved_pixels_accessed", "private_reads", "sealed_reads", "optimizer_steps", "training_approved",
    }:
        raise ValueError("Manifest property set differs")
    if (manifest.get("schema") != OUTPUT_SCHEMA or manifest.get("scope") != "development_only" or
            manifest.get("protocol_sha256") != PROTOCOL_SHA256 or manifest.get("source_bindings") != source_bindings or
            manifest.get("upstream_source_bindings") != protocol["upstream_source_bindings"] or
            manifest.get("font_provenance") != protocol["font_provenance"] or
            manifest.get("recipes") != protocol["recipes"] or manifest.get("claims_and_limits") != protocol["claims_and_limits"] or
            manifest.get("training_approved") is not False or
            manifest.get("optimizer_steps") != 0 or manifest.get("private_reads") != 0 or
            manifest.get("sealed_reads") != 0 or manifest.get("reserved_pixels_accessed") is not False):
        raise ValueError("Manifest scope, source, or recipe bindings differ")
    inventory = _safe_inventory(output_path)
    expected_inventory = set(manifest.get("artifact_sha256", {})) | {"manifest.json"}
    if inventory != expected_inventory:
        raise ValueError("Dataset file inventory differs from manifest")
    for relative, expected in manifest["artifact_sha256"].items():
        path = output_path / PurePosixPath(relative)
        _assert_no_reparse_ancestors(path, output_path)
        if _sha(path.read_bytes()) != expected:
            raise ValueError(f"Dataset artifact hash differs: {relative}")
    regenerated = run_children(protocol["recipes"], max_workers=max_workers)
    child_runtimes = {_canonical_json(result["runtime"]) for result in regenerated}
    if len(child_runtimes) != 1 or manifest["environment"].get("child_runtime") != regenerated[0]["runtime"]:
        raise ValueError("Recorded child runtime differs from regenerated runtime")
    if (manifest["environment"].get("python") != platform.python_version() or
            manifest["environment"].get("platform") != platform.platform() or
            manifest["environment"].get("max_concurrent_children") != max_workers):
        raise ValueError("Recorded generator environment differs")
    if not isinstance(manifest.get("cases"), list) or len(manifest["cases"]) != len(protocol["recipes"]):
        raise ValueError("Manifest case inventory differs")
    for recipe, result in zip(protocol["recipes"], regenerated, strict=True):
        index = int(recipe["index"])
        expected_case = manifest["cases"][index]
        expected_files = {
            f"images/{index:02d}.png": result["image_png"],
            f"scenes/{index:02d}.json": _canonical_json(result["scene"]),
            f"annotations/{index:02d}.json": _canonical_json(result["annotation"]),
            f"overlays/{index:02d}.json": _canonical_json(result["overlay"]),
        }
        if any(_sha(data) != manifest["artifact_sha256"].get(relative) for relative, data in expected_files.items()):
            raise ValueError(f"Renderer output differs for recipe {index}")
        expected_case_value = {
            "index": index,
            "recipe_id": result["recipe_id"],
            **recipe,
            "scene_id": result["scene"]["scene_id"],
            "panel_count": len(result["scene"]["panels"]),
            "artifact_sha256": {relative: _sha(data) for relative, data in expected_files.items()},
        }
        if expected_case != expected_case_value:
            raise ValueError(f"Regenerated case metadata differs for recipe {index}")
    return {
        "schema": OUTPUT_SCHEMA,
        "manifest_sha256": expected_manifest_sha256,
        "verified_recipes": len(protocol["recipes"]),
        "child_process_isolation": True,
        "reserved_pixels_accessed": False,
    }


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    generate_parser = subparsers.add_parser("generate")
    generate_parser.add_argument("output")
    verify_parser = subparsers.add_parser("verify")
    verify_parser.add_argument("output")
    verify_parser.add_argument("manifest_sha256")
    args = parser.parse_args()
    if args.command == "generate":
        print("manifest_sha256", generate(args.output))
    else:
        print(json.dumps(verify(args.output, args.manifest_sha256), sort_keys=True))
