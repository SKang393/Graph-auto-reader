# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Export project-owned train/dev rasters for the local C# input pipeline.

Only image bytes and identities cross this boundary. Renderer annotations stay
in the Python evaluation/training-label path and cannot become runtime masks.
"""

from __future__ import annotations

import argparse
import hashlib
from io import BytesIO
import json
from pathlib import Path

from ml.synthetic.dataset import PRESETS, _build_scenes, _scene_split
from ml.synthetic.renderer import render_scene

from .family_scenes import _family_identity


def _write_immutable(path: Path, content: bytes) -> None:
    if path.exists():
        if path.read_bytes() != content:
            raise ValueError(f"Refusing to replace different synthetic input: {path.name}")
        return
    with path.open("xb") as stream:
        stream.write(content)


def export_family_rasters(output_root: Path, split: str, seed: int = 393) -> Path:
    """Write deterministic PNG inputs, never annotations, masks, or sealed data."""
    normalized = "validation" if split == "dev" else split
    if normalized not in {"train", "validation"}:
        raise ValueError("Only synthetic train and dev inputs may be exported")
    output_root = Path(output_root)
    scenes = _build_scenes(PRESETS["smoke"], seed, require_complete_style_catalog=True)
    selected = [scene for scene in scenes if _scene_split(scene) == normalized]
    records = []
    outputs: dict[Path, bytes] = {}
    for scene in selected:
        image, _, _ = render_scene(scene)
        rgb = image.convert("RGB")
        buffer = BytesIO()
        rgb.save(buffer, format="PNG")
        image_bytes = buffer.getvalue()
        image_sha256 = hashlib.sha256(image_bytes).hexdigest()
        file_name = f"{normalized}-{int(scene['seed'])}-{image_sha256[:12]}.png"
        outputs[output_root / file_name] = image_bytes
        records.append({
            "image": file_name,
            "image_sha256": image_sha256,
            "width": rgb.width,
            "height": rgb.height,
            "split": normalized,
            "family": _family_identity(scene),
            "seed": int(scene["seed"]),
        })
    manifest = {
        "schema": "graphreader.synthetic-runtime-raster-inputs.v1",
        "source": "project-owned-synthetic-five-axis-family-v1",
        "preset": "smoke",
        "seed": seed,
        "split": normalized,
        "contains_truth": False,
        "contains_precomputed_masks": False,
        "images": records,
    }
    manifest_path = output_root / "input-manifest.json"
    outputs[manifest_path] = (json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode("utf-8")
    # Validate the entire exchange before writing any new files. A mismatched
    # existing manifest must not leave unreferenced image outputs behind.
    for path, content in outputs.items():
        if path.exists() and path.read_bytes() != content:
            raise ValueError(f"Refusing to replace different synthetic input: {path.name}")
    output_root.mkdir(parents=True, exist_ok=True)
    for path, content in outputs.items():
        _write_immutable(path, content)
    return manifest_path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--split", choices=("train", "dev"), required=True)
    parser.add_argument("--seed", type=int, default=393)
    arguments = parser.parse_args()
    print(export_family_rasters(arguments.output_root, arguments.split, arguments.seed))


if __name__ == "__main__":
    main()
