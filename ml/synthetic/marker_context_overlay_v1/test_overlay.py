# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import copy
import hashlib
from unittest import TestCase, main
from unittest.mock import patch

from PIL import Image, ImageChops, ImageDraw
from ml.synthetic import renderer, templates
from ml.synthetic.contextual_negatives import render_contextual_scene
from ml.synthetic.fonts import ResolvedFont
from ml.synthetic.marker_context_catalog_v1 import catalog
from ml.synthetic.marker_context_overlay_v1.overlay import overlay
from ml.synthetic.public_fonts_v1.fonts import verify


class OverlayTests(TestCase):
    @classmethod
    def setUpClass(cls):
        font = verify()['sans']; digest = hashlib.sha256(font.read_bytes()).hexdigest()
        class FontResolver:
            def __init__(self, *args, **kwargs): pass
            def resolve(self, requested, size_px):
                return ResolvedFont(str(requested), font, int(size_px), 'project-local-verified', digest, 'Noto Sans', 'Regular')
        cls.recipe = catalog.recipes('train')[4]
        with patch.object(templates, 'FontResolver', FontResolver), patch.object(renderer, 'FontResolver', FontResolver):
            cls.scene = catalog.prepare_scene(templates.build_scene(cls.recipe['design'], cls.recipe['seed'], cls.recipe['renderer_family'], panel_count=1), cls.recipe)
            cls.image, cls.annotation, cls.mask = render_contextual_scene(cls.scene, split='train')
        cls.panel = cls.scene['panels'][0]['panel_id']
        # Find a blank in-plot location using only this one generated train scene.
        x, y, w, h = cls.scene['panels'][0]['plot_box']
        cls.center = None
        for yy in range(int(y)+20, int(y+h)-20, 12):
            for xx in range(int(x)+20, int(x+w)-40, 12):
                _, record = overlay(cls.scene, cls.annotation, cls.image, cls.mask, recipe=cls.recipe, panel_id=cls.panel, center=(xx, yy), cue=True)
                if record['materialized']:
                    cls.center = (xx, yy); break
            if cls.center: break
        if cls.center is None: raise AssertionError('Train fixture has no blank overlay location')

    def invoke(self, center=None, cue=False, scene=None, annotation=None, recipe=None):
        return overlay(scene or self.scene, annotation or self.annotation, self.image, self.mask,
                       recipe=recipe or self.recipe, panel_id=self.panel, center=center or self.center, cue=cue)

    def test_cued_and_uncued_are_visible_but_review_only(self):
        for cue in (False, True):
            image, result = self.invoke(cue=cue)
            self.assertNotEqual(image.tobytes(), self.image.tobytes())
            self.assertEqual(result['cue_visible'], cue)
            self.assertEqual(result['status'], 'review_only')
            self.assertFalse(result['optimization_allowed'])
            self.assertTrue(result['isolated_positive_control_ids'])
            self.assertEqual(result['marker_mask_sha256'], hashlib.sha256(self.mask.tobytes()).hexdigest())

    def test_shared_renderer_monkeypatch_does_not_change_overlay(self):
        baseline, baseline_record = self.invoke(cue=True)
        with patch.object(renderer, '_draw_marker', side_effect=AssertionError('shared helper was invoked')):
            result, record = self.invoke(cue=True)
        self.assertEqual(result.tobytes(), baseline.tobytes())
        self.assertEqual(record, baseline_record)

    def test_positive_overlap_is_not_materialized(self):
        control = self.annotation['panels'][0]['markers'][0]['center']
        image, record = self.invoke(center=tuple(round(v) for v in control))
        self.assertFalse(record['materialized'])
        self.assertEqual(record['status'], 'review_only')
        self.assertEqual(record['reason'], 'positive_marker_overlap')
        self.assertEqual(record['scene_id'], self.scene['scene_id'])
        self.assertEqual(image.tobytes(), self.image.tobytes())

    def test_existing_ink_is_not_modified(self):
        x, y, w, h = self.scene['panels'][0]['plot_box']
        dark = ImageChops.invert(self.image.convert('L'))
        center = None
        for yy in range(int(y)+8, int(y+h)-8, 4):
            for xx in range(int(x)+8, int(x+w)-8, 4):
                footprint = Image.new('L', self.image.size, 0)
                renderer._draw_marker(ImageDraw.Draw(Image.new('RGB', self.image.size)), ImageDraw.Draw(footprint), (xx, yy), 5, 'circle', 'open', (0, 0, 0), 1)
                if ImageChops.multiply(footprint, dark).getbbox() and not ImageChops.multiply(footprint, self.mask).getbbox():
                    center = (xx, yy); break
            if center: break
        self.assertIsNotNone(center)
        result, record = self.invoke(center=center)
        self.assertEqual(record['reason'], 'existing_ink_overlap')
        self.assertFalse(record['materialized'])
        self.assertEqual(result.tobytes(), self.image.tobytes())

    def test_rerender_rejects_tampered_rasters_and_annotation(self):
        image = self.image.copy(); image.putpixel((0, 0), (0, 0, 0))
        with self.assertRaisesRegex(ValueError, 'Input image differs'):
            overlay(self.scene, self.annotation, image, self.mask, recipe=self.recipe, panel_id=self.panel, center=self.center, cue=False)
        mask = self.mask.copy(); mask.putpixel((0, 0), 255)
        with self.assertRaisesRegex(ValueError, 'Input marker mask differs'):
            overlay(self.scene, self.annotation, self.image, mask, recipe=self.recipe, panel_id=self.panel, center=self.center, cue=False)
        annotation = copy.deepcopy(self.annotation); annotation['renderer']['version'] = 'tampered'
        with self.assertRaisesRegex(ValueError, 'Input annotation differs'):
            self.invoke(annotation=annotation)

    def test_recipe_connection_mode_checked_in_all_three_scene_layers(self):
        for mutate in (lambda s: s['presentation'].__setitem__('connecting_line_style', 'solid'),
                       lambda s: s['panels'][0]['series'][0].__setitem__('line_style', 'solid'),
                       lambda s: s['panels'][0]['connections'][0].__setitem__('style', 'solid'),
                       lambda s: s['panels'][0]['connections'][0].__setitem__('visible', True)):
            scene = copy.deepcopy(self.scene); mutate(scene)
            with self.assertRaisesRegex(ValueError, '(?i)connection mode differs'):
                self.invoke(scene=scene)

    def test_invalid_plot_provenance_and_transform_fail_closed(self):
        with self.assertRaises(ValueError): self.invoke(center=(1, 1))
        altered = copy.deepcopy(self.annotation); altered['transforms'] = [{'kind': 'skew'}]
        with self.assertRaises(ValueError): self.invoke(annotation=altered)
        altered = copy.deepcopy(self.annotation); altered['contextual_negative_profile']['source_scene_canonical_sha256'] = '0'*64
        with self.assertRaises(ValueError): self.invoke(annotation=altered)
        altered = copy.deepcopy(self.annotation); altered['font']['sha256'] = '0'*64
        with self.assertRaises(ValueError): self.invoke(annotation=altered)
        altered_scene = copy.deepcopy(self.scene); altered_scene['degradations'][0]['kind'] = 'downsample'
        with self.assertRaises(ValueError): self.invoke(scene=altered_scene)
        with self.assertRaises(ValueError): self.invoke(cue='yes')
        with self.assertRaises(ValueError): self.invoke(recipe=catalog.recipes('dev')[4])

if __name__ == '__main__': main()
