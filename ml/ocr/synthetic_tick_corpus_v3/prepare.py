# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Bundled-font public synthetic tick corpus; no model or optimizer.

The unchanged generator is adapted only in this process. Its legacy schema
requires system fonts, so a local overlay truthfully records bundled font
provenance. A fresh full-distribution preflight is required for this route.
"""
from collections import Counter
from contextlib import ExitStack, contextmanager
from dataclasses import dataclass
from hashlib import sha256
from importlib.metadata import version
import copy
from io import BytesIO
import json
import os
from pathlib import Path
import stat
import uuid
from tempfile import TemporaryDirectory
from unittest.mock import patch

from jsonschema import Draft202012Validator
from PIL import Image, ImageFont

from ml.markers.center.real_positive_context_v1.positive_archive import _open_read_only_no_writers
from ml.ocr.synthetic_tick_corpus_v2 import prepare as v2
from ml.synthetic import dataset, renderer, schema, templates
from ml.synthetic.io import canonical_json_bytes, png_bytes
from ml.synthetic.public_fonts_v1 import fonts as public_fonts

ROOT = Path(__file__).resolve().parents[3]
SCHEMA = "graphreader.synthetic-tick-corpus.v3"
PREFLIGHT_SCHEMA = "graphreader.synthetic-tick-corpus-v3.full-gate-preflight.v1"
PROFILE = {"vector_clean": ("train", "system_sans", "sans"),
           "print_monochrome": ("train", "system_serif", "serif"),
           "scan_rough": ("validation", "system_mono", "mono")}
SEED_CANDIDATES = tuple(range(20261040, 20261280))
REQUIRED_SEEDS = 24
# The wide validation scene needs genuinely smaller laid-out glyphs to reach
# the unchanged production-resize envelope with the bundled Mono face.
WIDE_VALIDATION_FONT_SIZE_PX = 15
SOURCE_PATHS = tuple(sorted((
    "ml/ocr/synthetic_tick_corpus_v2/prepare.py", "ml/ocr/synthetic_tick_corpus_v3/__init__.py",
    "ml/ocr/synthetic_tick_corpus_v3/prepare.py", "ml/synthetic/__init__.py", "ml/synthetic/dataset.py",
    "ml/synthetic/templates.py", "ml/synthetic/renderer.py", "ml/synthetic/schema.py", "ml/synthetic/scene.schema.json",
    "ml/synthetic/fonts.py", "ml/synthetic/contact_sheet.py", "ml/synthetic/io.py",
    "ml/synthetic/public_fonts_v1/__init__.py", "ml/synthetic/public_fonts_v1/fonts.py",
    "ml/synthetic/public_fonts_v1/manifest.json", "ml/synthetic/public_fonts_v1/assets/LICENSE",
    "ml/synthetic/public_fonts_v1/assets/NotoSans-Regular.ttf", "ml/synthetic/public_fonts_v1/assets/NotoSerif-Regular.ttf",
    "ml/synthetic/public_fonts_v1/assets/NotoSansMono-Regular.ttf",
    "ml/markers/center/__init__.py", "ml/markers/center/model.py", "ml/markers/center/postprocess.py",
    "ml/markers/center/real_positive_context_v1/__init__.py",
    "ml/markers/center/real_positive_context_v1/positive_archive.py",
)))


def _sha(raw):
    return sha256(raw).hexdigest()


def fingerprint():
    return {"schema": "graphreader.synthetic-tick-corpus-v3.sources.v1",
            "files": {path: _sha((ROOT / path).read_bytes()) for path in SOURCE_PATHS},
            "pillow_version": version("Pillow"), "jsonschema_version": version("jsonschema"),
            "font_manifest_sha256": public_fonts.MANIFEST_SHA256}


def _fingerprint_sha():
    return _sha(canonical_json_bytes(fingerprint()))


@dataclass(frozen=True)
class _Resolved:
    requested: str
    path: Path
    size_px: int
    sha256: str
    family: str
    style: str = "Regular"

    def load(self):
        if _sha(self.path.read_bytes()) != self.sha256:
            raise ValueError("Bundled font changed before rendering")
        return ImageFont.truetype(str(self.path), self.size_px)

    def provenance(self):
        return {"requested": self.requested, "resolved_file": self.path.name, "resolved_path": str(self.path),
                "family": self.family, "style": self.style, "size_px": self.size_px, "source": "project_bundled",
                "sha256": self.sha256, "bundled": True, "license_id": "OFL-1.1"}


def _resolver(paths):
    expected = {key: _sha(path.read_bytes()) for key, path in paths.items()}
    class StrictBundledResolver:
        def __init__(self, search_paths=None):
            if search_paths not in (None, []):
                raise ValueError("Host font search paths are forbidden")

        def resolve(self, requested=None, size_px=14):
            if requested not in paths or type(size_px) is not int or size_px <= 0:
                raise ValueError("Only the three fixed bundled font aliases are allowed")
            path = paths[requested]
            if _sha(path.read_bytes()) != expected[requested]:
                raise ValueError("Bundled font bytes changed")
            return _Resolved(requested, path, size_px, expected[requested], public_fonts.EXPECTED_FAMILIES[requested])
    return StrictBundledResolver


def _overlay_schema():
    overlay = copy.deepcopy(schema.load_schema())
    overlay["$defs"]["font"]["properties"]["source"]["const"] = "project_bundled"
    overlay["$defs"]["font"]["properties"]["packaged"]["const"] = True
    overlay["$defs"]["provenance"]["properties"]["license"]["const"] = "not_assessed"
    overlay["$defs"]["provenance"]["properties"]["bundled_fonts"]["const"] = True
    Draft202012Validator.check_schema(overlay)
    return overlay


def _check_preset():
    specs = dataset.PRESETS.get("real_range")
    if specs is None or Counter(spec.renderer_family for spec in specs) != {"vector_clean": 3, "print_monochrome": 1, "scan_rough": 6}:
        raise ValueError("Public train/validation preset differs or contains a reserved family")
    for renderer_family, (split, font_family, alias) in PROFILE.items():
        profile = templates._RENDERER_PROFILES[renderer_family]
        if (profile["font"] != font_family or templates.FAMILY_TO_SPLIT["renderer"][renderer_family] != split or
                templates.FAMILY_TO_SPLIT["font"][font_family] != split or templates._GENERIC_FONT[font_family] != alias or
                any(templates.FAMILY_TO_SPLIT[axis][profile[axis]] != split for axis in ("degradation", "template", "marker"))):
            raise ValueError("Font or source family split differs")


@contextmanager
def isolated_generator(expected_source_sha):
    if not isinstance(expected_source_sha, str) or len(expected_source_sha) != 64:
        raise ValueError("Pinned source SHA-256 is required")
    with ExitStack() as stack:
        for relative in SOURCE_PATHS:
            stack.enter_context(_open_read_only_no_writers(ROOT / relative))
        if _fingerprint_sha() != expected_source_sha:
            raise ValueError("Generator source bundle differs")
        font_paths = public_fonts.verify()
        if len({path.resolve() for path in font_paths.values()}) != 3:
            raise ValueError("Bundled font paths are not distinct")
        strict_resolver = _resolver(font_paths)
        overlay = _overlay_schema()
        validator = Draft202012Validator(overlay)
        original_build_scene = dataset.build_scene

        def adapted_build_scene(*args, **kwargs):
            if kwargs.get("canvas_width") == 6352:
                presentation = kwargs.get("presentation")
                if (len(args) < 3 or args[2] != "scan_rough" or kwargs.get("panel_height") != 520 or
                        not isinstance(presentation, dict) or presentation.get("font_size_px") != 18):
                    raise ValueError("Wide validation preset differs")
                kwargs = {**kwargs, "presentation": {**presentation, "font_size_px": WIDE_VALIDATION_FONT_SIZE_PX}}
            scene = original_build_scene(*args, **kwargs)
            scene["font"]["source"] = "project_bundled"
            scene["font"]["packaged"] = True
            scene["provenance"]["license"] = "not_assessed"
            scene["provenance"]["bundled_fonts"] = True
            return scene

        def validate_overlay(scene):
            validator.validate(scene)
            return scene

        _check_preset()
        stack.enter_context(patch.object(templates, "FontResolver", strict_resolver))
        stack.enter_context(patch.object(renderer, "FontResolver", strict_resolver))
        stack.enter_context(patch.object(dataset, "build_scene", adapted_build_scene))
        stack.enter_context(patch.object(dataset, "validate_scene", validate_overlay))
        yield {"source_sha256": expected_source_sha, "font_paths": font_paths,
               "overlay_schema_sha256": _sha(canonical_json_bytes(overlay))}
        if _fingerprint_sha() != expected_source_sha:
            raise ValueError("Generator source changed during operation")


def _verify_case(case, scene, annotation, image, locked):
    split, font_family, alias = PROFILE[case["families"]["renderer"]]
    expected_path = locked["font_paths"][alias]
    font = annotation.get("font", {})
    if (case["split"] != split or case["families"]["font"] != font_family or
            any(scene.get("families", {}).get(axis) != {"key": case["families"][axis], "split": split} for axis in dataset.FAMILY_AXES) or
            scene["font"].get("source") != "project_bundled" or scene["font"].get("packaged") is not True or
            scene["provenance"].get("bundled_fonts") is not True or scene["provenance"].get("license") != "not_assessed" or
            font.get("source") != "project_bundled" or font.get("bundled") is not True or
            font.get("family") != public_fonts.EXPECTED_FAMILIES[alias] or font.get("resolved_path") != str(expected_path) or
            font.get("sha256") != _sha(expected_path.read_bytes()) or font.get("license_id") != "OFL-1.1"):
        raise ValueError("Rendered font identity or family split differs")
    return v2._x_tick_records(scene, annotation, image)


def _inventory(root):
    result = set()
    for directory, subdirectories, files in os.walk(root, followlinks=False):
        for name in subdirectories + files:
            path = Path(directory) / name
            info = path.lstat()
            if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
                raise ValueError("Corpus contains a link or reparse point")
        result.update((Path(directory) / name).relative_to(root).as_posix() for name in files)
    return result


def preflight(output, source_sha):
    output = Path(output).resolve()
    if not output.is_relative_to((ROOT / "artifacts").resolve()) or output.exists():
        raise ValueError("Use a new project artifacts preflight file")
    passing, failures = [], []
    with isolated_generator(source_sha) as locked, TemporaryDirectory(dir=output.parent, prefix=".v3-gate-") as temporary:
        for seed in SEED_CANDIDATES:
            if len(passing) == REQUIRED_SEEDS:
                break
            try:
                dataset.generate_dataset("real_range", seed, Path(temporary) / str(seed))
            except (AssertionError, RuntimeError, ValueError) as error:
                failures.append({"seed": seed, "error": str(error)})
            else:
                passing.append(seed)
        if len(passing) != REQUIRED_SEEDS:
            raise RuntimeError("Insufficient full-gate-passing bundled-font seeds")
        result = {"schema": PREFLIGHT_SCHEMA, "source_sha256": source_sha, "source_bundle": fingerprint(),
                  "overlay_schema_sha256": locked["overlay_schema_sha256"],
                  "wide_validation_font_size_px": WIDE_VALIDATION_FONT_SIZE_PX,
                  "font_manifest_sha256": public_fonts.MANIFEST_SHA256, "selected_seeds": passing, "failures": failures,
                  "full_generator_distribution_gate_passed": True, "synthetic_only": True, "private_reads": 0,
                  "sealed_reads": 0, "reserved_test_family_opened": False, "optimizer_steps": 0,
                  "training_approved": False, "historical_benchmark_independence": "unproven"}
    with output.open("xb") as stream:
        stream.write(canonical_json_bytes(result))
    return result


def _validate_gate(gate, source_sha):
    required = {"schema": PREFLIGHT_SCHEMA, "source_sha256": source_sha, "font_manifest_sha256": public_fonts.MANIFEST_SHA256,
                "wide_validation_font_size_px": WIDE_VALIDATION_FONT_SIZE_PX,
                "full_generator_distribution_gate_passed": True, "synthetic_only": True, "private_reads": 0,
                "sealed_reads": 0, "reserved_test_family_opened": False, "optimizer_steps": 0, "training_approved": False}
    if any(type(gate.get(key)) is not type(value) or gate.get(key) != value for key, value in required.items()):
        raise ValueError("Bundled-font preflight scope differs")
    if _sha(canonical_json_bytes(gate.get("source_bundle"))) != source_sha:
        raise ValueError("Bundled-font preflight source bundle differs")
    seeds = gate.get("selected_seeds")
    if (not isinstance(seeds, list) or len(seeds) != REQUIRED_SEEDS or len(set(seeds)) != REQUIRED_SEEDS or
            any(type(seed) is not int or seed not in SEED_CANDIDATES for seed in seeds)):
        raise ValueError("Bundled-font preflight seed population differs")
    failures = gate.get("failures")
    if (not isinstance(failures, list) or seeds != sorted(seeds) or
            any(not isinstance(item, dict) or set(item) != {"seed", "error"} or type(item["seed"]) is not int or
                not isinstance(item["error"], str) or not item["error"] for item in failures)):
        raise ValueError("Bundled-font preflight failure inventory differs")
    failed_seeds = [item["seed"] for item in failures]
    examined = list(SEED_CANDIDATES[:SEED_CANDIDATES.index(seeds[-1]) + 1])
    if (len(failed_seeds) != len(set(failed_seeds)) or set(seeds) & set(failed_seeds) or
            sorted(seeds + failed_seeds) != examined):
        raise ValueError("Bundled-font preflight omitted or repeated candidate seeds")
    return seeds


def materialize(output, source_sha, preflight_path, preflight_sha):
    output, preflight_path = Path(output).resolve(), Path(preflight_path).resolve(strict=True)
    artifacts = (ROOT / "artifacts").resolve()
    if not output.is_relative_to(artifacts) or output.exists() or not preflight_path.is_relative_to(artifacts):
        raise ValueError("Paths must be project-local artifacts and output must be new")
    preflight_raw = preflight_path.read_bytes()
    if _sha(preflight_raw) != preflight_sha:
        raise ValueError("Bundled-font preflight hash differs")
    gate = v2._json(preflight_raw)
    seeds = _validate_gate(gate, source_sha)
    rows, cases, label_exclusions, scene_exclusions = [], [], Counter(), Counter()
    split_families = {split: {axis: set() for axis in dataset.FAMILY_AXES} for split in ("train", "validation")}
    split_fonts = {split: set() for split in ("train", "validation")}
    fonts = {}
    with isolated_generator(source_sha) as locked:
        if gate.get("overlay_schema_sha256") != locked["overlay_schema_sha256"]:
            raise ValueError("Overlay schema identity differs")
        output.mkdir(parents=True, exist_ok=False)
        (output / "crops/train").mkdir(parents=True)
        (output / "crops/validation").mkdir(parents=True)
        with TemporaryDirectory(dir=output, prefix=".source-stage-") as temporary:
            for seed in seeds:
                generated = Path(temporary) / str(seed)
                dataset.generate_dataset("real_range", seed, generated)
                manifest = v2._json((generated / "seed-manifest.json").read_bytes())
                if manifest.get("preset") != "real_range" or manifest.get("dataset_seed") != seed or manifest.get("sanity_passed") is not True:
                    raise ValueError("Generated source manifest differs")
                source_cases = manifest.get("cases")
                if not isinstance(source_cases, list) or len(source_cases) != 10 or any(case.get("split") == "test" for case in source_cases):
                    raise ValueError("Reserved or unexpected source scene")
                for case in source_cases:
                    split, scene_id, families = case["split"], case["scene_id"], case["families"]
                    if str(uuid.UUID(scene_id)) != scene_id:
                        raise ValueError("Source scene ID is not canonical")
                    if split not in split_families or set(families) != set(dataset.FAMILY_AXES) or "hand_drawn" in families.values():
                        raise ValueError("Reserved or invalid source family")
                    for axis in dataset.FAMILY_AXES:
                        split_families[split][axis].add(families[axis])
                    payloads = {}
                    for directory, extension in (("images", "png"), ("scenes", "json"), ("annotations", "json")):
                        relative = f"{directory}/{scene_id}.{extension}"
                        payloads[directory] = (generated / relative).read_bytes()
                        if _sha(payloads[directory]) != case["files"][relative]:
                            raise ValueError("Source artifact hash differs")
                    scene, annotation = v2._json(payloads["scenes"]), v2._json(payloads["annotations"])
                    with Image.open(BytesIO(payloads["images"])) as opened:
                        opened.load()
                        image = opened.copy()
                    records = _verify_case(case, scene, annotation, image, locked)
                    font = annotation["font"]
                    font_sha = v2._font_sha(annotation)
                    split_fonts[split].add(font_sha)
                    fonts[font_sha] = {key: font.get(key) for key in ("family", "style", "source", "sha256", "bundled", "resolved_path", "license_id")}
                    source_paths = {directory: f"sources/{seed}/{scene_id}.{extension}" for directory, extension in
                                    (("images", "png"), ("scenes", "scene.json"), ("annotations", "annotation.json"))}
                    for directory, relative in source_paths.items():
                        destination = output / relative
                        destination.parent.mkdir(parents=True, exist_ok=True)
                        with destination.open("xb") as stream:
                            stream.write(payloads[directory])
                    retained, text_records = [], []
                    for record in records:
                        glyph_hash, exclusion = v2._glyph_visibility(scene, annotation, image, record)
                        if exclusion is not None:
                            label_exclusions[exclusion] += 1
                            text_records.append({**record, "glyph_mask_sha256": None, "crop": None,
                                                 "crop_sha256": None, "exclusion_reason": exclusion})
                        else:
                            crop = png_bytes(image.crop(tuple(record["crop_xyxy"])).convert("RGB"))
                            relative = f"crops/{split}/{len(rows):06d}.png"
                            with (output / relative).open("xb") as stream:
                                stream.write(crop)
                            text_record = {**record, "glyph_mask_sha256": glyph_hash, "crop": relative,
                                           "crop_sha256": _sha(crop), "exclusion_reason": None}
                            text_records.append(text_record)
                            retained.append(text_record)
                            rows.append({"sample_id": f"seed-{seed}-{record['text_id']}", "split": split, "seed": seed,
                                         "scene_id": scene_id, "families": families, "source_image_sha256": _sha(payloads["images"]),
                                         "source_scene_sha256": _sha(payloads["scenes"]), "source_annotation_sha256": _sha(payloads["annotations"]),
                                         "font_sha256": font_sha, **{key: text_record[key] for key in
                                             ("text_id", "region_id", "panel_id", "text", "crop_xyxy", "glyph_mask_sha256", "crop", "crop_sha256")}})
                    cases.append({"seed": seed, "scene_id": scene_id, "split": split, "families": families,
                                  "source_image_sha256": _sha(payloads["images"]), "scene_sha256": _sha(payloads["scenes"]),
                                  "annotation_sha256": _sha(payloads["annotations"]), "font_sha256": font_sha,
                                  "source_image": source_paths["images"], "scene": source_paths["scenes"],
                                  "annotation": source_paths["annotations"], "text_records": text_records,
                                  "x_tick_count": len(records), "readable_x_tick_count": len(retained), "included": bool(retained),
                                  "exclusion_reason": None if retained else "source_scene_no_readable_x_tick_glyphs"})
                    if not retained:
                        scene_exclusions["source_scene_no_readable_x_tick_glyphs"] += 1
        v2._assert_isolation(rows, split_families, split_fonts)
        inventory = {row["crop"]: row["crop_sha256"] for row in rows}
        if len(inventory) != len(rows):
            raise ValueError("Duplicate crop output path")
        for case in cases:
            for path_key, digest_key in (("source_image", "source_image_sha256"), ("scene", "scene_sha256"), ("annotation", "annotation_sha256")):
                if case[path_key] in inventory:
                    raise ValueError("Duplicate source output path")
                inventory[case[path_key]] = case[digest_key]
        result = {"schema": SCHEMA, "preset": "real_range", "seeds": seeds, "source_sha256": source_sha,
                  "source_bundle": fingerprint(),
                  "wide_validation_font_size_px": WIDE_VALIDATION_FONT_SIZE_PX,
                  "full_gate_preflight_sha256": preflight_sha,
                  "full_gate_preflight": preflight_path.relative_to(ROOT).as_posix(),
                  "font_manifest_sha256": public_fonts.MANIFEST_SHA256,
                  "overlay_schema_sha256": locked["overlay_schema_sha256"], "font_provenance": fonts,
                  "split_families": {s: {a: sorted(v) for a, v in axes.items()} for s, axes in split_families.items()},
                  "font_bytes_disjoint_across_splits": True, "source_cases": cases, "samples": rows, "artifact_sha256": inventory,
                  "excluded_x_tick_labels": dict(sorted(label_exclusions.items())), "excluded_source_scenes": dict(sorted(scene_exclusions.items())),
                  "synthetic_only": True, "private_reads": 0, "sealed_reads": 0, "reserved_test_family_opened": False,
                  "test_scene_count": 0, "optimizer_steps": 0, "training_approved": False, "production_approved": False,
                  "historical_benchmark_independence": "unproven", "rendered_output_license_assessment": "not_assessed"}
        payload = canonical_json_bytes(result)
        if _inventory(output) != set(inventory):
            raise ValueError("Corpus output inventory differs")
    with (output / "manifest.json").open("xb") as stream:
        stream.write(payload)
    return {"manifest_sha256": _sha(payload), "train_samples": sum(row["split"] == "train" for row in rows),
            "validation_samples": sum(row["split"] == "validation" for row in rows)}


def _checked_artifact(root, relative, digest, inventory):
    if (not isinstance(relative, str) or Path(relative).is_absolute() or ".." in Path(relative).parts or
            inventory.get(relative) != digest):
        raise ValueError("Corpus artifact path or recorded hash differs")
    path = (root / relative).resolve(strict=True)
    if not path.is_relative_to(root):
        raise ValueError("Corpus artifact escapes output directory")
    data = path.read_bytes()
    if _sha(data) != digest:
        raise ValueError("Corpus artifact bytes differ")
    return data


def _verify_text_record(record, saved, scene, annotation, image, sample, root, inventory):
    glyph, exclusion = v2._glyph_visibility(scene, annotation, image, record)
    if exclusion is not None:
        if sample is not None or saved != {**record, "glyph_mask_sha256": None, "crop": None,
                                          "crop_sha256": None, "exclusion_reason": exclusion}:
            raise ValueError("Excluded text record differs")
        return False
    if not isinstance(sample, dict):
        raise ValueError("Included text has no sample")
    crop_path, crop_sha = sample.get("crop"), sample.get("crop_sha256")
    saved_expected = {**record, "glyph_mask_sha256": glyph, "crop": crop_path,
                      "crop_sha256": crop_sha, "exclusion_reason": None}
    if saved != saved_expected:
        raise ValueError("Text-to-glyph or crop mapping differs")
    crop = _checked_artifact(root, crop_path, crop_sha, inventory)
    expected_crop = png_bytes(image.crop(tuple(record["crop_xyxy"])).convert("RGB"))
    if crop != expected_crop:
        raise ValueError("Sample crop differs from authenticated source pixels")
    return True


def verify(output, expected_manifest_sha, source_sha):
    root = Path(output).resolve(strict=True)
    if not root.is_relative_to((ROOT / "artifacts").resolve()):
        raise ValueError("Corpus must be under project artifacts")
    raw = (root / "manifest.json").read_bytes()
    if _sha(raw) != expected_manifest_sha:
        raise ValueError("Corpus manifest hash differs")
    report = v2._json(raw)
    required = {"schema": SCHEMA, "preset": "real_range", "source_sha256": source_sha,
                "wide_validation_font_size_px": WIDE_VALIDATION_FONT_SIZE_PX,
                "font_manifest_sha256": public_fonts.MANIFEST_SHA256, "synthetic_only": True,
                "private_reads": 0, "sealed_reads": 0, "reserved_test_family_opened": False,
                "test_scene_count": 0, "optimizer_steps": 0, "training_approved": False,
                "production_approved": False, "historical_benchmark_independence": "unproven",
                "rendered_output_license_assessment": "not_assessed"}
    if any(type(report.get(k)) is not type(v) or report.get(k) != v for k, v in required.items()):
        raise ValueError("Corpus scope differs")
    if _sha(canonical_json_bytes(report.get("source_bundle"))) != source_sha:
        raise ValueError("Corpus source bundle differs")
    inventory = report.get("artifact_sha256")
    if not isinstance(inventory, dict) or _inventory(root) != set(inventory) | {"manifest.json"}:
        raise ValueError("Corpus output inventory differs")
    gate_rel = report.get("full_gate_preflight")
    if not isinstance(gate_rel, str) or Path(gate_rel).is_absolute() or ".." in Path(gate_rel).parts:
        raise ValueError("Corpus preflight path is unsafe")
    gate_path = (ROOT / gate_rel).resolve(strict=True)
    if not gate_path.is_relative_to((ROOT / "artifacts").resolve()):
        raise ValueError("Corpus preflight escapes project artifacts")
    gate_raw = gate_path.read_bytes()
    if _sha(gate_raw) != report.get("full_gate_preflight_sha256"):
        raise ValueError("Corpus preflight bytes differ")
    gate = v2._json(gate_raw)
    seeds = _validate_gate(gate, source_sha)
    if report.get("seeds") != seeds:
        raise ValueError("Corpus seeds differ from preflight")
    samples, cases = report.get("samples"), report.get("source_cases")
    if not isinstance(samples, list) or not isinstance(cases, list) or len(cases) != 10 * len(seeds):
        raise ValueError("Corpus source or sample inventory differs")
    sample_map = {item["sample_id"]: item for item in samples}
    if len(sample_map) != len(samples):
        raise ValueError("Corpus repeats sample identities")
    split_families = {split: {axis: set() for axis in dataset.FAMILY_AXES} for split in ("train", "validation")}
    split_fonts = {split: set() for split in ("train", "validation")}
    expected_paths, observed_samples, seen_cases = set(), set(), set()
    label_exclusions, scene_exclusions, fonts = Counter(), Counter(), {}
    with isolated_generator(source_sha) as locked:
        if report.get("overlay_schema_sha256") != locked["overlay_schema_sha256"] or gate.get("overlay_schema_sha256") != locked["overlay_schema_sha256"]:
            raise ValueError("Corpus overlay schema differs")
        expected_scenes = {}
        for seed in seeds:
            generated = dataset._build_scenes(dataset.PRESETS["real_range"], seed, require_complete_style_catalog=False)
            if len(generated) != 10:
                raise ValueError("Fixed source scene population differs")
            for scene in generated:
                key = (seed, scene["scene_id"])
                if key in expected_scenes:
                    raise ValueError("Fixed source scene identity repeats")
                expected_scenes[key] = scene
        for case in cases:
            seed, scene_id, split, families = case["seed"], case["scene_id"], case["split"], case["families"]
            if (seed not in seeds or str(uuid.UUID(scene_id)) != scene_id or (seed, scene_id) in seen_cases or
                    split not in split_families or set(families) != set(dataset.FAMILY_AXES)):
                raise ValueError("Corpus source case identity differs")
            seen_cases.add((seed, scene_id))
            for axis in dataset.FAMILY_AXES:
                split_families[split][axis].add(families[axis])
            expected_source = {"source_image": f"sources/{seed}/{scene_id}.png",
                               "scene": f"sources/{seed}/{scene_id}.scene.json",
                               "annotation": f"sources/{seed}/{scene_id}.annotation.json"}
            for key, expected in expected_source.items():
                if case.get(key) != expected or expected in expected_paths:
                    raise ValueError("Corpus source path differs or repeats")
                expected_paths.add(expected)
            image_raw = _checked_artifact(root, case["source_image"], case["source_image_sha256"], inventory)
            scene = v2._json(_checked_artifact(root, case["scene"], case["scene_sha256"], inventory))
            annotation = v2._json(_checked_artifact(root, case["annotation"], case["annotation_sha256"], inventory))
            if scene.get("scene_id") != scene_id or scene != expected_scenes.get((seed, scene_id)):
                raise ValueError("Retained scene differs from fixed generator recipe")
            dataset.validate_scene(scene)
            with Image.open(BytesIO(image_raw)) as opened:
                opened.load()
                image = opened.copy()
            records = _verify_case(case, scene, annotation, image, locked)
            font = annotation["font"]
            if case.get("font_sha256") != font["sha256"]:
                raise ValueError("Source case font binding differs")
            split_fonts[split].add(font["sha256"])
            fonts[font["sha256"]] = {key: font.get(key) for key in ("family", "style", "source", "sha256", "bundled", "resolved_path", "license_id")}
            text_records = case.get("text_records")
            if not isinstance(text_records, list) or len(text_records) != len(records):
                raise ValueError("Source text record inventory differs")
            retained = 0
            for record, saved in zip(records, text_records, strict=True):
                sample_id = f"seed-{seed}-{record['text_id']}"
                sample = sample_map.get(sample_id)
                included = _verify_text_record(record, saved, scene, annotation, image, sample, root, inventory)
                if included:
                    expected_sample = {"sample_id": sample_id, "split": split, "seed": seed, "scene_id": scene_id,
                                       "families": families, "source_image_sha256": case["source_image_sha256"],
                                       "source_scene_sha256": case["scene_sha256"], "source_annotation_sha256": case["annotation_sha256"],
                                       "font_sha256": case["font_sha256"], **{key: saved[key] for key in
                                           ("text_id", "region_id", "panel_id", "text", "crop_xyxy", "glyph_mask_sha256", "crop", "crop_sha256")}}
                    if sample != expected_sample or sample_id in observed_samples or sample["crop"] in expected_paths:
                        raise ValueError("Sample and source text mapping differs or repeats")
                    observed_samples.add(sample_id)
                    expected_paths.add(sample["crop"])
                    retained += 1
                else:
                    label_exclusions[saved["exclusion_reason"]] += 1
            if (case.get("x_tick_count") != len(records) or case.get("readable_x_tick_count") != retained or
                    case.get("included") is not bool(retained) or
                    case.get("exclusion_reason") != (None if retained else "source_scene_no_readable_x_tick_glyphs")):
                raise ValueError("Source text retention summary differs")
            if not retained:
                scene_exclusions["source_scene_no_readable_x_tick_glyphs"] += 1
    if expected_paths != set(inventory) or observed_samples != set(sample_map):
        raise ValueError("Corpus contains unmatched source or sample artifacts")
    if report.get("font_provenance") != fonts or report.get("excluded_x_tick_labels") != dict(sorted(label_exclusions.items())) or report.get("excluded_source_scenes") != dict(sorted(scene_exclusions.items())):
        raise ValueError("Corpus font or exclusion summary differs")
    expected_families = {s: {a: sorted(v) for a, v in axes.items()} for s, axes in split_families.items()}
    if report.get("split_families") != expected_families or report.get("font_bytes_disjoint_across_splits") is not True:
        raise ValueError("Corpus family provenance differs")
    v2._assert_isolation(samples, split_families, split_fonts)
    return {"status": "verified", "source_cases": len(cases), "samples": len(samples), "training_approved": False}


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("fingerprint", "preflight", "materialize", "verify"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--source-sha")
    parser.add_argument("--preflight", type=Path)
    parser.add_argument("--preflight-sha")
    parser.add_argument("--manifest-sha")
    args = parser.parse_args()
    if args.mode == "fingerprint":
        public_fonts.verify()
        print(json.dumps({"source_sha256": _fingerprint_sha(), "source": fingerprint()}, sort_keys=True))
    elif args.mode == "preflight" and args.output and args.source_sha:
        print(json.dumps(preflight(args.output, args.source_sha), sort_keys=True))
    elif args.mode == "materialize" and args.output and args.source_sha and args.preflight and args.preflight_sha:
        print(json.dumps(materialize(args.output, args.source_sha, args.preflight, args.preflight_sha), sort_keys=True))
    elif args.mode == "verify" and args.output and args.source_sha and args.manifest_sha:
        print(json.dumps(verify(args.output, args.manifest_sha, args.source_sha), sort_keys=True))
    else:
        raise SystemExit("Missing required output, pinned source or preflight arguments")
