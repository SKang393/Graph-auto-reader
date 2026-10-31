# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from hashlib import sha256
from pathlib import Path
import unittest
from unittest import mock
from tempfile import TemporaryDirectory

from PIL import Image, ImageDraw, ImageFont

from ml.ocr.synthetic_tick_corpus_v3 import prepare
from ml.synthetic import dataset, renderer, templates
from ml.synthetic.public_fonts_v1 import fonts


class BundledCorpusTests(unittest.TestCase):
    def test_resolver_rejects_host_paths_aliases_and_search_directories(self):
        paths = fonts.verify()
        resolver = prepare._resolver(paths)()
        for alias in ("sans", "serif", "mono"):
            font = resolver.resolve(alias, 14)
            self.assertEqual(font.path, paths[alias])
            self.assertTrue(font.provenance()["bundled"])
        for request in ("handwritten", "arial.ttf", str(paths["sans"]), None):
            with self.subTest(request=request), self.assertRaisesRegex(ValueError, "fixed bundled"):
                resolver.resolve(request, 14)
        with self.assertRaisesRegex(ValueError, "search paths"):
            prepare._resolver(paths)(["C:/Windows/Fonts"])

    def test_source_identity_and_scoped_patch_restoration(self):
        before = (templates.FontResolver, renderer.FontResolver, dataset.build_scene, dataset.validate_scene)
        with mock.patch.object(prepare.public_fonts, "verify") as verify:
            with self.assertRaisesRegex(ValueError, "source bundle differs"):
                with prepare.isolated_generator("0" * 64):
                    pass
            verify.assert_not_called()
        actual = prepare._fingerprint_sha()
        with prepare.isolated_generator(actual) as locked:
            self.assertEqual(locked["source_sha256"], actual)
            self.assertIsNot(templates.FontResolver, before[0])
            self.assertIsNot(renderer.FontResolver, before[1])
        self.assertEqual((templates.FontResolver, renderer.FontResolver, dataset.build_scene, dataset.validate_scene), before)

    def test_overlay_records_bundled_provenance_and_preserves_schema(self):
        with prepare.isolated_generator(prepare._fingerprint_sha()):
            scene = dataset.build_scene("ab", 2026104000, "vector_clean")
            dataset.validate_scene(scene)
            self.assertEqual(scene["font"]["source"], "project_bundled")
            self.assertTrue(scene["font"]["packaged"])
            self.assertEqual(scene["provenance"]["license"], "not_assessed")
            self.assertTrue(scene["provenance"]["bundled_fonts"])
            image, annotation, _ = renderer.render_scene(scene)
            self.assertEqual(annotation["font"]["family"], "Noto Sans")
            self.assertEqual(annotation["font"]["source"], "project_bundled")
            self.assertTrue(annotation["font"]["bundled"])
            self.assertEqual(image.size, (scene["canvas"]["width"], scene["canvas"]["height"]))

    def test_wrong_font_and_split_cannot_pass_case_verification(self):
        image = Image.new("RGB", (40, 30), "white")
        paths = fonts.verify()
        families = {"renderer": "vector_clean", "font": "system_sans", "degradation": "none",
                    "template": "classic_single", "marker": "geometric_basic"}
        scene = {"scene_id": "scene", "font": {"source": "project_bundled", "packaged": True},
                 "families": {key: {"key": value, "split": "train"} for key, value in families.items()},
                 "provenance": {"bundled_fonts": True, "license": "not_assessed"},
                 "panels": [{"panel_id": "panel", "ticks": [{"axis": "x", "hidden": False, "label": "1", "value": 1}]}],
                 "annotations": {"text_regions": [{"region_id": "tick", "panel_id": "panel", "text": "1", "role": "x_tick", "visible": True, "box": [10, 16, 5, 8]}]}}
        annotation = {"scene_id": "scene", "coordinate_space": "original_pixels", "canvas": {"width": 40, "height": 30},
                      "panels": [{"panel_id": "panel", "texts": [{"text_id": "tick", "region_id": "tick", "panel_id": "panel",
                        "text": "1", "role": "x_tick", "visible": True, "coordinate_space": "original_pixels", "position": [10, 16],
                        "rendered_pixel_box": [10, 16, 5, 8], "partial": False}]}],
                      "font": {"source": "project_bundled", "bundled": True, "family": "Noto Sans",
                               "resolved_path": str(paths["sans"]), "sha256": sha256(paths["sans"].read_bytes()).hexdigest(), "license_id": "OFL-1.1"}}
        case = {"split": "train", "families": families}
        self.assertEqual(len(prepare._verify_case(case, scene, annotation, image, {"font_paths": paths})), 1)
        annotation["font"]["resolved_path"] = r"C:\Windows\Fonts\arial.ttf"
        with self.assertRaisesRegex(ValueError, "font identity"):
            prepare._verify_case(case, scene, annotation, image, {"font_paths": paths})
        annotation["font"]["resolved_path"] = str(paths["sans"])
        case["split"] = "validation"
        with self.assertRaisesRegex(ValueError, "font identity"):
            prepare._verify_case(case, scene, annotation, image, {"font_paths": paths})

    def test_noto_alpha_validation_rejects_contrasting_block(self):
        path = fonts.verify()["sans"]
        font = ImageFont.truetype(str(path), 16)
        image = Image.new("RGB", (80, 60), "white")
        source = {"annotations": {"text_regions": [{"region_id": "tick", "panel_id": "panel", "text": "12",
                   "role": "x_tick", "visible": True, "box": [20, 20, 20, 20]}]}}
        generated = renderer._draw_text(image, ImageDraw.Draw(image), {"text": "12", "position": [20, 20], "anchor": "lt",
                         "role": "x_tick", "visible": True, "region_id": "tick", "text_id": "tick"},
                         font=font, default_position=(20, 20), default_role="x_tick", default_id="tick")
        annotation = {"font": {"resolved_path": str(path), "size_px": 16, "sha256": sha256(path.read_bytes()).hexdigest()}}
        x, y, w, h = map(int, generated["rendered_pixel_box"])
        record = {"region_id": "tick", "rendered_pixel_box": generated["rendered_pixel_box"], "crop_xyxy": [x, y, x + w, y + h]}
        good, reason = prepare.v2._glyph_visibility(source, annotation, image, record)
        self.assertIsNotNone(good)
        self.assertIsNone(reason)
        ImageDraw.Draw(image).rectangle((x, y, x + w, y + h), fill="black")
        good, reason = prepare.v2._glyph_visibility(source, annotation, image, record)
        self.assertIsNone(good)
        self.assertEqual(reason, "renderer_glyph_alpha_not_visible_in_final_crop")

    def test_source_to_glyph_and_crop_mapping_is_independently_checked(self):
        path = fonts.verify()["sans"]
        font = ImageFont.truetype(str(path), 16)
        image = Image.new("RGB", (80, 60), "white")
        scene = {"annotations": {"text_regions": [{"region_id": "tick", "panel_id": "panel", "text": "12",
                 "role": "x_tick", "visible": True, "box": [20, 20, 20, 20]}]}}
        drawn = renderer._draw_text(image, ImageDraw.Draw(image), {"text": "12", "position": [20, 20], "anchor": "lt",
                   "role": "x_tick", "visible": True, "region_id": "tick", "text_id": "tick"}, font=font,
                   default_position=(20, 20), default_role="x_tick", default_id="tick")
        annotation = {"font": {"resolved_path": str(path), "size_px": 16, "sha256": sha256(path.read_bytes()).hexdigest()}}
        x, y, w, h = map(int, drawn["rendered_pixel_box"])
        record = {"text_id": "tick", "region_id": "tick", "panel_id": "panel", "text": "12",
                  "rendered_pixel_box": drawn["rendered_pixel_box"], "crop_xyxy": [x, y, x + w, y + h]}
        glyph, error = prepare.v2._glyph_visibility(scene, annotation, image, record)
        self.assertIsNone(error)
        crop = prepare.png_bytes(image.crop(tuple(record["crop_xyxy"])).convert("RGB"))
        crop_sha = sha256(crop).hexdigest()
        saved = {**record, "glyph_mask_sha256": glyph, "crop": "crop.png", "crop_sha256": crop_sha, "exclusion_reason": None}
        sample = {"crop": "crop.png", "crop_sha256": crop_sha}
        temp_root = prepare.ROOT / "artifacts" / "test-temp"
        temp_root.mkdir(parents=True, exist_ok=True)
        with TemporaryDirectory(dir=temp_root) as temporary:
            root = Path(temporary).resolve()
            (root / "crop.png").write_bytes(crop)
            inventory = {"crop.png": crop_sha}
            self.assertTrue(prepare._verify_text_record(record, saved, scene, annotation, image, sample, root, inventory))
            changed = {**saved, "glyph_mask_sha256": "0" * 64}
            with self.assertRaisesRegex(ValueError, "glyph or crop mapping"):
                prepare._verify_text_record(record, changed, scene, annotation, image, sample, root, inventory)
            (root / "crop.png").write_bytes(crop + b"tamper")
            with self.assertRaisesRegex(ValueError, "artifact bytes"):
                prepare._verify_text_record(record, saved, scene, annotation, image, sample, root, inventory)

    def test_preflight_binds_source_and_every_examined_seed(self):
        bundle = {"test": "synthetic"}
        source_sha = sha256(prepare.canonical_json_bytes(bundle)).hexdigest()
        gate = {"schema": prepare.PREFLIGHT_SCHEMA, "source_sha256": source_sha, "source_bundle": bundle,
                "font_manifest_sha256": fonts.MANIFEST_SHA256, "full_generator_distribution_gate_passed": True,
                "wide_validation_font_size_px": prepare.WIDE_VALIDATION_FONT_SIZE_PX,
                "synthetic_only": True, "private_reads": 0, "sealed_reads": 0,
                "reserved_test_family_opened": False, "optimizer_steps": 0, "training_approved": False,
                "selected_seeds": list(prepare.SEED_CANDIDATES[:prepare.REQUIRED_SEEDS]), "failures": []}
        self.assertEqual(prepare._validate_gate(gate, source_sha), gate["selected_seeds"])
        with self.assertRaisesRegex(ValueError, "source bundle"):
            prepare._validate_gate({**gate, "source_bundle": {"test": "changed"}}, source_sha)
        skipped = list(gate["selected_seeds"])
        skipped[-1] += 1
        with self.assertRaisesRegex(ValueError, "omitted or repeated"):
            prepare._validate_gate({**gate, "selected_seeds": skipped}, source_sha)

    def test_wide_font_size_is_applied_before_layout_and_preset_stays_unchanged(self):
        spec = next(item for item in prepare.dataset.PRESETS["real_range"] if item.canvas_width == 6352)
        self.assertEqual(spec.presentation["font_size_px"], 18)
        with prepare.isolated_generator(prepare._fingerprint_sha()):
            scenes = prepare.dataset._build_scenes(prepare.dataset.PRESETS["real_range"], 20261040,
                                                   require_complete_style_catalog=False)
            wide = next(scene for scene in scenes if scene["canvas"]["width"] == 6352)
            self.assertEqual(wide["presentation"]["font_size_px"], prepare.WIDE_VALIDATION_FONT_SIZE_PX)
            self.assertEqual(wide["families"]["renderer"]["key"], "scan_rough")
            prepare.dataset.validate_scene(wide)
        self.assertEqual(spec.presentation["font_size_px"], 18)

    def test_generated_source_annotation_glyph_and_crop_chain(self):
        with prepare.isolated_generator(prepare._fingerprint_sha()) as locked:
            scenes = prepare.dataset._build_scenes(prepare.dataset.PRESETS["real_range"], 20261040,
                                                   require_complete_style_catalog=False)
            scene = next(item for item in scenes if item["canvas"]["width"] == 361)
            image, annotation, _ = prepare.dataset.render_scene(scene)
            families = {axis: scene["families"][axis]["key"] for axis in prepare.dataset.FAMILY_AXES}
            records = prepare._verify_case({"split": "train", "families": families}, scene, annotation, image, locked)
            found = False
            for record in records:
                glyph, excluded = prepare.v2._glyph_visibility(scene, annotation, image, record)
                if excluded:
                    continue
                raw = prepare.png_bytes(image.crop(tuple(record["crop_xyxy"])).convert("RGB"))
                digest = sha256(raw).hexdigest()
                saved = {**record, "glyph_mask_sha256": glyph, "crop": "crop.png", "crop_sha256": digest, "exclusion_reason": None}
                temp_root = prepare.ROOT / "artifacts" / "test-temp"
                temp_root.mkdir(parents=True, exist_ok=True)
                with TemporaryDirectory(dir=temp_root) as temporary:
                    root = Path(temporary).resolve()
                    (root / "crop.png").write_bytes(raw)
                    self.assertTrue(prepare._verify_text_record(record, saved, scene, annotation, image,
                                    {"crop": "crop.png", "crop_sha256": digest}, root, {"crop.png": digest}))
                found = True
                break
            self.assertTrue(found, "synthetic source must contain a visible numeric glyph")
