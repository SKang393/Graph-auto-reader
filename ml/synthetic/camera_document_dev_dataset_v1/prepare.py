# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bounded exploratory camera-document development data; never trains."""
from collections import Counter
from hashlib import sha256
from importlib.metadata import version
from io import BytesIO
import json
import math
import os
from pathlib import Path
import platform
import stat
import subprocess
import sys

from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
PROFILE = ROOT / "ml/synthetic/camera_document_profile_v1"
PROFILE_REL = "ml/synthetic/camera_document_profile_v1/"
SNAPSHOT_SHA = "01a01e3b81e524c4428884a063280cc18a18237328547732f7ae7b166ef7ab71"
RECIPES = (("ab", 7621), ("aba", 7622), ("abab", 7623), ("multiple_baseline", 7624))
FAMILY = "camera_document_dev"
SCHEMA = "graphreader.camera-document-dev-dataset.v1"


def _hash(data):
    return sha256(data).hexdigest()


def _json_bytes(value):
    return (json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False) + "\n").encode("utf-8")


def _read_json(data):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result:
                raise ValueError("Duplicate JSON key")
            result[key] = value
        return result
    value = json.loads(data, object_pairs_hook=pairs)
    if not isinstance(value, dict):
        raise ValueError("Expected JSON object")
    return value


def _git(*arguments):
    result = subprocess.run(["git", "-c", f"safe.directory={ROOT.as_posix()}", "-C", str(ROOT), *arguments],
                            capture_output=True, check=True)
    return result.stdout


def _snapshot(revision=None):
    revision = revision or _git("rev-parse", "HEAD").decode("ascii").strip()
    if len(revision) != 40 or any(c not in "0123456789abcdef" for c in revision):
        raise ValueError("Invalid source revision")
    raw = (PROFILE / "snapshot_bindings.json").read_bytes()
    if _hash(raw) != SNAPSHOT_SHA or _git("show", revision + ":" + PROFILE_REL + "snapshot_bindings.json") != raw:
        raise ValueError("Profile snapshot is not the committed fixed identity")
    snapshot = _read_json(raw)
    expected = {name: item["local_copy_sha256"] for name, item in snapshot["upstream_paths"].items()}
    expected.update(snapshot["profile_files"])
    if set(expected) != {"templates.py", "renderer.py", "fonts.py", "schema.py", "scene.schema.json",
                         "__init__.py", "README.md", "assets/LICENSE", "assets/NotoSans-Regular.ttf"}:
        raise ValueError("Profile file inventory differs")
    for relative, digest in expected.items():
        data = (PROFILE / relative).read_bytes()
        if _hash(data) != digest or _git("show", revision + ":" + PROFILE_REL + relative) != data:
            raise ValueError("Profile file differs from committed snapshot")
    for binding in snapshot["upstream_paths"].values():
        if _hash(_git("show", revision + ":" + binding["source_path"])) != binding["source_git_blob_sha256"]:
            raise ValueError("Committed upstream snapshot differs")
    asset = snapshot["font_asset"]
    if (asset["sha256"] != expected["assets/NotoSans-Regular.ttf"] or
            asset["license_evidence_sha256"] != expected["assets/LICENSE"] or
            asset["asset_license_id"] != "OFL-1.1"):
        raise ValueError("Packaged font or license identity differs")
    return {"commit": revision, "snapshot_sha256": SNAPSHOT_SHA, "profile_files_sha256": expected,
            "font_sha256": asset["sha256"], "font_license_sha256": asset["license_evidence_sha256"],
            "font_license_id": asset["asset_license_id"], "font_source_url": asset["source_url"],
            "font_license_url": asset["license_evidence_url"]}


def _inventory(root):
    files = set()
    for base, directories, names in os.walk(root, followlinks=False):
        for name in directories + names:
            path = Path(base) / name
            info = path.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
                raise ValueError("Dataset contains a link or reparse point")
        files.update((Path(base) / name).relative_to(root).as_posix() for name in names)
    return files


def _box(value, width, height):
    if (not isinstance(value, list) or len(value) != 4 or
            any(type(v) not in (float, int) or not math.isfinite(v) for v in value)):
        raise ValueError("Rendered text geometry invalid")
    x, y, w, h = value
    if x < 0 or y < 0 or w <= 0 or h <= 0 or x + w > width or y + h > height:
        raise ValueError("Rendered text geometry outside canvas")


def _project(matrix, position):
    if (not isinstance(matrix, list) or len(matrix) != 9 or not isinstance(position, list) or len(position) != 2 or
            any(type(v) not in (float, int) or not math.isfinite(v) for v in matrix + position)):
        raise ValueError("Text transform geometry invalid")
    x, y = position
    denominator = matrix[6] * x + matrix[7] * y + matrix[8]
    if abs(denominator) < 1e-12:
        raise ValueError("Text transform is singular")
    return ((matrix[0] * x + matrix[1] * y + matrix[2]) / denominator,
            (matrix[3] * x + matrix[4] * y + matrix[5]) / denominator)


def validate_labels(scene, annotation, seen_ids):
    if (annotation.get("scene_id") != scene.get("scene_id") or annotation.get("coordinate_space") != "original_pixels" or
            annotation.get("canvas", {}).get("width") != scene["canvas"]["width"] or
            annotation.get("canvas", {}).get("height") != scene["canvas"]["height"]):
        raise ValueError("Annotation and scene identity differ")
    degradations = annotation.get("degradations")
    if (not isinstance(degradations, list) or len(degradations) != 1 or
            degradations[0].get("kind") != "camera_document" or degradations[0].get("geometry_transformed") is not True):
        raise ValueError("Camera-document transform is absent")
    matrix = degradations[0].get("forward_matrix_3x3")
    declared = scene["annotations"]["text_regions"]
    source = {item["region_id"]: item for item in declared}
    if len(source) != len(declared):
        raise ValueError("Scene contains duplicate text regions")
    panels = {panel["panel_id"]: panel for panel in scene["panels"]}
    if len(panels) != len(scene["panels"]):
        raise ValueError("Scene contains duplicate panel identities")
    observed, roles, text_values = set(), Counter(), set()
    width, height = scene["canvas"]["width"], scene["canvas"]["height"]
    for panel in annotation["panels"]:
        for item in panel["texts"]:
            text_id = item.get("text_id")
            if not isinstance(text_id, str) or text_id in observed or text_id in seen_ids or text_id not in source:
                raise ValueError("Text identity missing or duplicated")
            record = source[text_id]
            if (item.get("region_id") != text_id or item.get("panel_id") != record.get("panel_id") or
                    item.get("panel_id") != panel.get("panel_id") or item.get("text") != record.get("text") or
                    item.get("role") != record.get("role") or item.get("visible") is not record.get("visible") or
                    item.get("coordinate_space") != "original_pixels"):
                raise ValueError("Rendered label semantics differ from source scene")
            expected = _project(matrix, record["box"][:2])
            actual = item.get("position")
            if (not isinstance(actual, list) or len(actual) != 2 or
                    any(type(v) not in (float, int) or not math.isfinite(v) for v in actual) or
                    max(abs(a - b) for a, b in zip(actual, expected, strict=True)) > 0.00002):
                raise ValueError("Rendered text position differs from source transform")
            if item["visible"]:
                if not isinstance(item["text"], str) or not item["text"]:
                    raise ValueError("Visible text is empty")
                _box(item.get("rendered_pixel_box"), width, height)
                if item["role"] in ("x_tick", "y_tick"):
                    axis = item["role"][0]
                    try:
                        numeric_label = float(item["text"])
                    except ValueError as error:
                        raise ValueError("Tick label is not numeric") from error
                    if not math.isfinite(numeric_label):
                        raise ValueError("Tick label is not finite")
                    if record["panel_id"] not in panels:
                        raise ValueError("Tick label panel is absent")
                    source_box = record["box"]
                    coordinate = source_box[0] + source_box[2] / 2 if axis == "x" else source_box[1] + source_box[3] / 2
                    position_index = 0 if axis == "x" else 1
                    matches = [tick for tick in panels[record["panel_id"]].get("ticks", [])
                               if tick.get("axis") == axis and tick.get("hidden") is False and
                               str(tick.get("label")) == item["text"] and
                               isinstance(tick.get("center"), list) and len(tick["center"]) == 2 and
                               type(tick["center"][position_index]) in (float, int) and
                               math.isfinite(tick["center"][position_index]) and
                               abs(tick["center"][position_index] - coordinate) < 0.01 and
                               type(tick.get("value")) in (float, int) and math.isfinite(tick["value"]) and
                               tick["value"] == numeric_label]
                    if len(matches) != 1:
                        raise ValueError("Tick label does not bind exactly one source tick")
                roles[item["role"]] += 1
                text_values.add(item["text"])
            elif item.get("rendered_pixel_box") is not None:
                raise ValueError("Hidden text unexpectedly has rendered pixels")
            observed.add(text_id)
    if observed != set(source):
        raise ValueError("Rendered text inventory differs from source scene")
    seen_ids.update(observed)
    return roles, text_values


def _scene(design, seed):
    from ml.synthetic.camera_document_profile_v1.templates import build_scene
    return build_scene(design, seed, FAMILY)


def _render(scene):
    from ml.synthetic.camera_document_profile_v1.renderer import render_scene
    return render_scene(scene)


def _check_scene(scene, design, seed):
    if (scene.get("seed") != seed or scene.get("design") != design or
            scene.get("families", {}).get("renderer") != {"key": FAMILY, "split": "validation"} or
            any(item.get("split") != "validation" for item in scene["families"].values()) or
            len(scene.get("degradations", [])) != 1 or scene["degradations"][0].get("kind") != "camera_document" or
            scene["degradations"][0].get("family_key") != FAMILY or b"hand_drawn" in _json_bytes(scene)):
        raise ValueError("Generated scene is outside the fixed development profile")


def generate(output):
    output = Path(output).resolve()
    if not output.is_relative_to((ROOT / "artifacts").resolve()) or output.exists():
        raise ValueError("Use a new project artifacts directory")
    snapshot = _snapshot()
    output.mkdir(parents=True, exist_ok=False)
    for folder in ("images", "scenes", "annotations"):
        (output / folder).mkdir()
    cases, hashes, seen_ids, values, roles = [], {}, set(), set(), Counter()
    seen_scenes, seen_images = set(), set()
    for index, (design, seed) in enumerate(RECIPES):
        scene = _scene(design, seed)
        _check_scene(scene, design, seed)
        scene_bytes = _json_bytes(scene)
        image, annotation, mask = _render(scene)
        if image.size != (scene["canvas"]["width"], scene["canvas"]["height"]) or mask.size != image.size or mask.mode != "L":
            raise ValueError("Rendered image or marker-mask dimensions differ")
        if annotation.get("font", {}).get("sha256") != snapshot["font_sha256"] or annotation["font"].get("source") != "package_asset":
            raise ValueError("Rendered font identity differs")
        case_roles, case_values = validate_labels(scene, annotation, seen_ids)
        roles.update(case_roles)
        values.update(case_values)
        image_buffer = BytesIO()
        image.save(image_buffer, format="PNG")
        payloads = {f"images/{index}.png": image_buffer.getvalue(), f"scenes/{index}.json": scene_bytes,
                    f"annotations/{index}.json": _json_bytes(annotation)}
        image_hash, pixel_hash, scene_id = _hash(payloads[f"images/{index}.png"]), _hash(image.convert("RGB").tobytes()), scene["scene_id"]
        if pixel_hash in seen_images or scene_id in seen_scenes:
            raise ValueError("Generated image or scene is duplicated")
        seen_images.add(pixel_hash)
        seen_scenes.add(scene_id)
        for rel, data in payloads.items():
            with (output / rel).open("xb") as stream:
                stream.write(data)
            hashes[rel] = _hash(data)
        cases.append({"index": index, "design": design, "seed": seed, "scene_id": scene_id,
                      "image_sha256": image_hash, "rgb_pixels_sha256": pixel_hash,
                      "visible_text_count": sum(case_roles.values())})
    if _snapshot(snapshot["commit"]) != snapshot:
        raise ValueError("Source snapshot changed during generation")
    report = {"schema": SCHEMA, "profile": FAMILY, "recipes": [list(item) for item in RECIPES],
              "source_snapshot": snapshot, "cases": cases, "artifact_sha256": hashes,
              "visible_text_roles": dict(sorted(roles.items())), "distinct_visible_text_contents": len(values),
              "text_identity_count": len(seen_ids), "script_sha256": _hash(Path(__file__).read_bytes()),
              "source_binding": "committed profile and upstream source bytes; script hash descriptive",
              "rendered_box_semantics": "transformed and canvas-clipped pre-degradation glyph bounds; not post-JPEG exact pixel segmentation",
              "historical_benchmark_independence": "unproven", "cross_host_bitwise_reproducibility": "unproven",
              "internal_split_independence": "not claimed", "rendered_output_license_assessment": "not_assessed",
              "python_version": sys.version.split()[0], "pillow_version": version("Pillow"),
              "jsonschema_version": version("jsonschema"), "platform": platform.platform(),
              "training_approved": False, "sealed": False, "private_data": False, "optimizer_steps": 0}
    manifest = _json_bytes(report)
    with (output / "manifest.json").open("xb") as stream:
        stream.write(manifest)
    if _inventory(output) != set(hashes) | {"manifest.json"}:
        raise ValueError("Generated output inventory differs")
    return report, _hash(manifest)


def verify(dataset, expected_manifest_sha):
    dataset = Path(dataset).resolve(strict=True)
    if not dataset.is_relative_to((ROOT / "artifacts").resolve()):
        raise ValueError("Dataset must be under project artifacts")
    payload = (dataset / "manifest.json").read_bytes()
    if _hash(payload) != expected_manifest_sha:
        raise ValueError("Dataset manifest hash differs")
    report = _read_json(payload)
    if (report.get("schema") != SCHEMA or report.get("profile") != FAMILY or
            report.get("recipes") != [list(item) for item in RECIPES] or report.get("training_approved") is not False or
            report.get("private_data") is not False or report.get("sealed") is not False or report.get("optimizer_steps") != 0 or
            report.get("source_snapshot") != _snapshot(report["source_snapshot"]["commit"])):
        raise ValueError("Dataset scope or source binding differs")
    expected = {f"{folder}/{index}.{extension}" for index in range(len(RECIPES)) for folder, extension in
                (("images", "png"), ("scenes", "json"), ("annotations", "json"))}
    if set(report.get("artifact_sha256", {})) != expected or _inventory(dataset) != expected | {"manifest.json"}:
        raise ValueError("Dataset output inventory differs")
    seen_ids, images, scenes, roles, values = set(), set(), set(), Counter(), set()
    if len(report.get("cases", [])) != len(RECIPES):
        raise ValueError("Dataset case inventory differs")
    for index, (design, seed) in enumerate(RECIPES):
        files = {name: (dataset / f"{name}/{index}.{extension}").read_bytes() for name, extension in
                 (("images", "png"), ("scenes", "json"), ("annotations", "json"))}
        for name, extension in (("images", "png"), ("scenes", "json"), ("annotations", "json")):
            if _hash(files[name]) != report["artifact_sha256"][f"{name}/{index}.{extension}"]:
                raise ValueError("Dataset artifact hash differs")
        scene, annotation = _read_json(files["scenes"]), _read_json(files["annotations"])
        _check_scene(scene, design, seed)
        if scene != _scene(design, seed):
            raise ValueError("Scene differs from fixed source recipe")
        with Image.open(BytesIO(files["images"])) as image:
            image.load()
            if image.format != "PNG" or image.size != (scene["canvas"]["width"], scene["canvas"]["height"]):
                raise ValueError("Rendered PNG dimensions differ")
            pixel_hash = _hash(image.convert("RGB").tobytes())
        if annotation.get("font", {}).get("sha256") != report["source_snapshot"]["font_sha256"]:
            raise ValueError("Rendered font identity differs")
        case_roles, case_values = validate_labels(scene, annotation, seen_ids)
        roles.update(case_roles)
        values.update(case_values)
        image_hash, scene_id = _hash(files["images"]), scene["scene_id"]
        if pixel_hash in images or scene_id in scenes:
            raise ValueError("Image or scene duplicated")
        images.add(pixel_hash)
        scenes.add(scene_id)
        if report["cases"][index] != {"index": index, "design": design, "seed": seed, "scene_id": scene_id,
                                     "image_sha256": image_hash, "rgb_pixels_sha256": pixel_hash,
                                     "visible_text_count": sum(case_roles.values())}:
            raise ValueError("Case metadata differs")
    if (report.get("visible_text_roles") != dict(sorted(roles.items())) or report.get("text_identity_count") != len(seen_ids) or
            report.get("distinct_visible_text_contents") != len(values)):
        raise ValueError("Dataset text summary differs")
    return report


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "generate":
        result, digest = generate(sys.argv[2])
        print(json.dumps({"status": "generated", "manifest_sha256": digest, "scenes": len(result["cases"]), "training_approved": False}))
    elif len(sys.argv) == 4 and sys.argv[1] == "verify":
        result = verify(sys.argv[2], sys.argv[3])
        print(json.dumps({"status": "verified", "scenes": len(result["cases"]), "training_approved": False}))
    else:
        raise SystemExit("Expected generate <new-output-dir> OR verify <dataset-dir> <manifest-sha256>")
