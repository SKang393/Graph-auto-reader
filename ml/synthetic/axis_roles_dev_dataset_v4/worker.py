# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Render exactly one fixed development recipe in an isolated process."""

from __future__ import annotations

import base64
import importlib.metadata
import json
import os
import sys
from typing import Any

from .prepare import (
    MAX_REQUEST_BYTES,
    PROTOCOL_SHA256,
    _canonical_json,
    _expected_recipe_by_index,
    _sha,
    _source_bindings,
    load_protocol,
)


def _fail(message: str) -> int:
    sys.stderr.write(message + "\n")
    return 2


def main() -> int:
    raw = sys.stdin.buffer.read(MAX_REQUEST_BYTES + 1)
    if len(raw) > MAX_REQUEST_BYTES:
        return _fail("request exceeds fixed size bound")
    try:
        request = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        return _fail(f"invalid request JSON: {type(exc).__name__}")
    if (not isinstance(request, dict) or
            set(request) != {"schema", "protocol_sha256", "recipe"} or
            request.get("schema") != "graphreader.synthetic-axis-roles-dev-v4.worker-request.v1" or
            request.get("protocol_sha256") != PROTOCOL_SHA256):
        return _fail("request schema or protocol pin differs")
    protocol = load_protocol()
    _source_bindings(protocol)
    recipe = request.get("recipe")
    if not isinstance(recipe, dict):
        return _fail("recipe must be a JSON object")
    try:
        expected = _expected_recipe_by_index(recipe.get("index"), protocol)
    except (ValueError, TypeError) as exc:
        return _fail(str(exc))
    if recipe != expected:
        return _fail("recipe is not an exact fixed development recipe")

    # This constructor reference is captured in the child only. The parent
    # process does not import PIL or the renderer and therefore cannot inherit
    # the legacy wrapper's temporary monkeypatch.
    from PIL import Image

    original_new = Image.new
    try:
        from ml.synthetic.axis_roles_dev_dataset_v3 import prepare as v3

        scene, annotation, overlay, png, _roles, _participants = v3._material(recipe, set())
        v3._refs(scene)
    except Exception as exc:
        return _fail(f"renderer rejected recipe {recipe['index']}: {type(exc).__name__}: {exc}")
    if Image.new is not original_new:
        return _fail("PIL Image.new was not restored in the render child")
    if not isinstance(png, bytes) or not png.startswith(b"\x89PNG\r\n\x1a\n"):
        return _fail("renderer returned an invalid PNG")

    scene_bytes = _canonical_json(scene)
    annotation_bytes = _canonical_json(annotation)
    overlay_bytes = _canonical_json(overlay)
    result: dict[str, Any] = {
        "schema": "graphreader.synthetic-axis-roles-dev-v4.worker-result.v1",
        "recipe_id": _sha(_canonical_json(recipe)),
        "recipe": recipe,
        "scene": scene,
        "annotation": annotation,
        "overlay": overlay,
        "png_base64": base64.b64encode(png).decode("ascii"),
        "hashes": {
            "scene": _sha(scene_bytes),
            "annotation": _sha(annotation_bytes),
            "overlay": _sha(overlay_bytes),
            "image_png": _sha(png),
        },
        "child_pid": os.getpid(),
        "child_pil_constructor_restored": Image.new is original_new,
        "runtime": {
            "python": sys.version.split()[0],
            "pillow": importlib.metadata.version("Pillow"),
            "jsonschema": importlib.metadata.version("jsonschema"),
        },
    }
    sys.stdout.buffer.write(_canonical_json(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
