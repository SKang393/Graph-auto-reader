# SPDX-License-Identifier: Apache-2.0
"""Produce review-only scenes. Assigned family labels are not independence proof."""
from __future__ import annotations

import argparse
from hashlib import sha256
from importlib.metadata import version
from io import BytesIO
import json
from pathlib import Path
from unittest.mock import patch

from ml.synthetic import contextual_negatives, renderer, templates, schema
from ml.synthetic import fonts as font_resolver
from ml.synthetic.fonts import ResolvedFont
from ml.synthetic.public_fonts_v1 import fonts

ROOT = Path(__file__).resolve().parents[3]
REVISION = "candidate-context-profiles-v1"
PROFILES = {
    "train": (("vector_clean", "sans"), ("print_monochrome", "serif")),
    "evaluation": (("scan_rough", "mono"),),
}
AXES = ("renderer", "font", "template", "marker", "degradation")
FONT_BY_ASSIGNED_FAMILY = {"system_sans": "sans", "system_serif": "serif", "system_mono": "mono"}


def recipes():
    """A fixed inventory; evaluation is exploratory and not a release gate."""
    rows = []
    for split, profiles in PROFILES.items():
        for profile_index, (family, font) in enumerate(profiles):
            for index, design in enumerate(("ab", "abab", "multiple_baseline", "alternating_treatments")):
                seed = 73100000 + (0 if split == "train" else 1000) + profile_index * 100 + index
                rows.append(dict(split=split, scene_split="train" if split == "train" else "validation",
                                 contextual_split="train" if split == "train" else "dev",
                                 family=family, font=font, seed=seed, design=design))
    return rows


def family_inventory():
    inventory = {split: {axis: set() for axis in AXES} for split in PROFILES}
    verified = fonts.verify()
    byte_hashes = {split: set() for split in PROFILES}
    for split, profiles in PROFILES.items():
        for family, selected_font in profiles:
            assigned_font = templates._RENDERER_PROFILES[family]["font"]
            if FONT_BY_ASSIGNED_FAMILY.get(assigned_font) != selected_font:
                raise ValueError("Selected font does not match the assigned font family")
            byte_hashes[split].add(sha256(verified[selected_font].read_bytes()).hexdigest())
            inventory[split]["renderer"].add(family)
            for axis, key in templates._RENDERER_PROFILES[family].items():
                inventory[split][axis].add(key)
    if any(inventory["train"][axis] & inventory["evaluation"][axis] for axis in AXES):
        raise ValueError("Assigned family keys overlap")
    if byte_hashes["train"] & byte_hashes["evaluation"]:
        raise ValueError("Font bytes overlap across splits")
    return {split: {axis: sorted(values) for axis, values in axes.items()} for split, axes in inventory.items()}


def render(recipe):
    if recipe not in recipes():
        raise ValueError("Recipe is outside the fixed inventory")
    family_inventory()
    verified = fonts.verify()
    font_path = verified[recipe["font"]]
    font_hash = sha256(font_path.read_bytes()).hexdigest()

    class Resolver:
        def __init__(self, *args, **kwargs): pass
        def resolve(self, requested, size_px):
            return ResolvedFont(str(requested), font_path, int(size_px), "project-local-verified", font_hash,
                                fonts.EXPECTED_FAMILIES[recipe["font"]], "Regular")

    with patch.object(templates, "FontResolver", Resolver), patch.object(renderer, "FontResolver", Resolver):
        scene = templates.build_scene(recipe["design"], recipe["seed"], recipe["family"], panel_count=1)
        if any(family["split"] != recipe["scene_split"] for family in scene["families"].values()):
            raise ValueError("Generated scene split does not match the declared recipe")
        image, annotation, mask = contextual_negatives.render_contextual_scene(
            scene, split=recipe["contextual_split"])
    return scene, image, annotation, mask


def png(image):
    out = BytesIO()
    image.save(out, format="PNG", optimize=False)
    return out.getvalue()


def source_hashes():
    files = (Path(__file__), Path(templates.__file__), Path(renderer.__file__), Path(schema.__file__),
             Path(schema.__file__).with_name("scene.schema.json"), Path(font_resolver.__file__),
             Path(contextual_negatives.__file__), Path(fonts.__file__), fonts.PACKAGE / "manifest.json")
    return {str(p.relative_to(ROOT)).replace("\\", "/"): sha256(p.read_bytes()).hexdigest() for p in files}


def generate(output: Path):
    family_inventory()
    output = Path(output)
    artifacts = (ROOT / "artifacts").resolve(strict=True)
    parent = output.parent.resolve(strict=True)
    if not output.is_absolute() or not parent.is_relative_to(artifacts) or output.name in ("", ".", ".."):
        raise ValueError("Output must be a new directory beneath project artifacts")
    output.mkdir(exist_ok=False)
    binding = source_hashes()
    rows, seen = [], set()
    for i, recipe in enumerate(recipes()):
        scene, image, annotation, mask = render(recipe)
        image_bytes = png(image)
        image_hash = sha256(image_bytes).hexdigest()
        if image_hash in seen:
            raise ValueError("Duplicate raster in generated inventory")
        seen.add(image_hash)
        base = f"case-{i:03d}"
        (output / (base + ".png")).write_bytes(image_bytes)
        mask_bytes = png(mask)
        (output / (base + ".mask.png")).write_bytes(mask_bytes)
        payload = {"scene": scene, "annotation": annotation, "status": "review_only", "optimization_allowed": False}
        payload_bytes = json.dumps(payload, sort_keys=True, allow_nan=False).encode("utf-8")
        (output / (base + ".json")).write_bytes(payload_bytes)
        font_path = fonts.verify()[recipe["font"]]
        font_hash = sha256(font_path.read_bytes()).hexdigest()
        rendered_font = annotation["font"]
        if (rendered_font["sha256"] != font_hash or rendered_font["source"] != "project-local-verified"
                or rendered_font["family"] != fonts.EXPECTED_FAMILIES[recipe["font"]]):
            raise ValueError("Rendered font provenance does not match the verified font")
        font_manifest = json.loads((fonts.PACKAGE / "manifest.json").read_text(encoding="utf-8"))
        matching_fonts = [item for item in font_manifest["fonts"] if item["id"] == recipe["font"]]
        if len(matching_fonts) != 1 or matching_fonts[0]["sha256"] != font_hash:
            raise ValueError("Verified font manifest entry differs")
        font_record = matching_fonts[0]
        rows.append({"recipe": recipe, "image": base + ".png", "sha256": image_hash, "scene_id": scene["scene_id"],
                     "font_sha256": font_hash, "font_family": rendered_font["family"],
                     "font_source": rendered_font["source"], "font_license_id": font_record["license_id"],
                     "font_license_file": font_record["license_file"], "mask_sha256": sha256(mask_bytes).hexdigest(),
                     "annotation_sha256": sha256(payload_bytes).hexdigest()})
    if source_hashes() != binding:
        raise ValueError("Generator sources changed during generation")
    manifest = {"revision": REVISION, "status": "review_only", "training_approved": False,
                "evaluation_is_acceptance": False, "family_keys": family_inventory(), "sources": binding,
                "split_mapping": {"train": {"scene": "train", "contextual": "train"},
                                  "evaluation": {"scene": "validation", "contextual": "dev"}},
                "dependencies": {name: version(name) for name in ("Pillow", "numpy", "jsonschema")},
                "limitations": ["shared_renderer_implementation", "historical_near_duplicates_unchecked",
                                "only_configured_family_and_whole_scene_hash_separation_checked",
                                "candidate_and_negative_crop_byte_cross_split_isolation_unchecked",
                                "font_family_labels_are_not_independence_proof", "negative_labels_require_review"],
                "cases": rows}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = generate(args.output)
    print(f"{result['revision']}: {len(result['cases'])} review-only cases")


if __name__ == "__main__":
    main()
