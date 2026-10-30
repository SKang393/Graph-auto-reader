# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

from copy import deepcopy
from hashlib import sha256
import json
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from PIL import Image

from ml.markers.center.artifact_mask_public_gate import PROHIBITED_STRUCTURE_KINDS
from ml.markers.center.v3_artifact_corpus_v1.audit import audit_train_scene
from ml.synthetic.runtime_graph_visible_content_v3 import render_visible_content_scene
from ml.synthetic.templates import build_scene


def _scene():
    scene = build_scene(
        "ab", 8101, "vector_clean", panel_count=1, session_count=8,
        canvas_width=480, panel_height=270,
    )
    scene["degradations"] = [{
        "stage": 1, "family_key": "print_light", "kind": "none",
        "parameters": {}, "deterministic": True,
    }]
    return scene


class TrainSceneAuditTests(unittest.TestCase):
    def test_all_category_mappings_aggregate_top_level_and_multiple_panels(self):
        annotation_keys = ("texts", "axes", "ticks", "dividers", "brackets", "arrows", "legends")
        annotation = {
            "scene_id": "fake-for-aggregation-only",
            "coordinate_space": "original_pixels",
            "hard_negatives": [{"kind": "tick", "geometry": {"point": [9, 8]}}],
            "panels": [],
        }
        for key in annotation_keys:
            annotation[key] = [{"source": "top", "field": key}]
        for index in range(2):
            panel = {key: [{"source": f"panel-{index}", "field": key}] for key in annotation_keys}
            annotation["panels"].append(panel)
        fake = SimpleNamespace(
            annotation=annotation,
            image=Image.new("RGB", (2, 2), "white"),
            marker_mask=Image.new("L", (2, 2), 0),
            audit=SimpleNamespace(texts=[]),
        )
        mappings = {
            "text": "texts", "axis": "axes", "tick": "ticks", "divider": "dividers",
            "bracket": "brackets", "arrow_shaft": "arrows", "arrowhead": "arrows", "legend": "legends",
        }
        with patch("ml.markers.center.v3_artifact_corpus_v1.audit.render_visible_content_scene", return_value=fake):
            audit = audit_train_scene({}, split="train")
        for kind, key in mappings.items():
            with self.subTest(kind=kind):
                expected = annotation[key] + [panel[key][0] for panel in annotation["panels"]]
                category = audit["categories"][kind]
                self.assertEqual(category["annotation_key"], key)
                self.assertEqual(category["semantic_records"], expected)
                self.assertEqual(category["semantic_record_count"], 3)
                self.assertFalse(category["full_final_pixel_mask_available"])
        self.assertEqual(audit["categories"]["line_intersection"]["semantic_records"], [])
        self.assertEqual(audit["hard_negatives"], annotation["hard_negatives"])

    def test_real_v3_two_panels_and_nonempty_hard_negative(self):
        scene = build_scene(
            "ab", 8101, "vector_clean", panel_count=2, session_count=8,
            canvas_width=480, panel_height=270,
        )
        scene["degradations"] = [{
            "stage": 1, "family_key": "print_light", "kind": "none",
            "parameters": {}, "deterministic": True,
        }]
        rendered = render_visible_content_scene(scene)
        self.assertEqual(len(rendered.annotation["panels"]), 2)
        self.assertTrue(rendered.annotation["hard_negatives"])
        with patch("ml.markers.center.v3_artifact_corpus_v1.audit.render_visible_content_scene", return_value=rendered):
            audit = audit_train_scene(scene, split="train")
        self.assertEqual(audit["hard_negatives"], rendered.annotation["hard_negatives"])
        self.assertIn("geometry", audit["hard_negatives"][0])
        self.assertIn("rendered_pixel_box", audit["hard_negatives"][0])
        for kind, key in {
            "text": "texts", "axis": "axes", "tick": "ticks", "divider": "dividers",
            "bracket": "brackets", "arrow_shaft": "arrows", "arrowhead": "arrows", "legend": "legends",
        }.items():
            with self.subTest(kind=kind):
                expected = list(rendered.annotation[key])
                for panel in rendered.annotation["panels"]:
                    expected.extend(panel[key])
                self.assertEqual(audit["categories"][kind]["semantic_records"], expected)
                self.assertEqual(audit["categories"][kind]["semantic_record_count"], len(expected))

    def test_real_v3_raster_and_geometry_are_preserved_without_claiming_masks(self):
        scene = _scene()
        original = deepcopy(scene)
        rendered = render_visible_content_scene(scene)
        audit = audit_train_scene(scene, split="train")
        panel = rendered.annotation["panels"][0]

        self.assertEqual(original, scene)
        self.assertEqual(audit["raster_sha256"], sha256(rendered.image.tobytes()).hexdigest())
        self.assertEqual(audit["marker_mask_sha256"], sha256(rendered.marker_mask.tobytes()).hexdigest())
        self.assertEqual(set(audit["categories"]), set(PROHIBITED_STRUCTURE_KINDS))
        self.assertEqual(audit["categories"]["axis"]["semantic_records"], panel["axes"])
        self.assertEqual(audit["categories"]["text"]["semantic_records"], panel["texts"])
        self.assertEqual(audit["hard_negatives"], rendered.annotation["hard_negatives"])
        self.assertIsNone(audit["categories"]["line_intersection"]["annotation_key"])
        self.assertTrue(all(not item["full_final_pixel_mask_available"] for item in audit["categories"].values()))
        self.assertFalse(audit["complete_artifact_mask"])
        self.assertFalse(audit["training_input_ready"])
        self.assertFalse(audit["production_approved"])
        self.assertFalse(audit["split_provenance_verified"])
        json.dumps(audit, allow_nan=False)

    def test_forbidden_split_is_rejected_before_rendering(self):
        with patch("ml.markers.center.v3_artifact_corpus_v1.audit.render_visible_content_scene") as render:
            for split in ("validation", "dev", "sealed_public", "private", "TRAIN", ""):
                with self.subTest(split=split):
                    with self.assertRaises(ValueError):
                        audit_train_scene({}, split=split)
            render.assert_not_called()

    def test_unbounded_noise_reports_text_support_ungradable(self):
        scene = _scene()
        scene["degradations"] = [{
            "stage": 1, "family_key": "print_light", "kind": "gaussian_noise",
            "parameters": {"sigma": 3}, "deterministic": True,
        }]
        audit = audit_train_scene(scene, split="train")
        self.assertTrue(audit["text_support_audits"])
        self.assertTrue(all(
            item["support_assessment"] == "ungradable_full_canvas_stochastic_stage"
            for item in audit["text_support_audits"]
        ))
        self.assertFalse(audit["complete_artifact_mask"])


if __name__ == "__main__":
    unittest.main()
