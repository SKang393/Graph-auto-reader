# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from PIL import Image, ImageDraw
from ml.synthetic.marker_context_catalog_v1 import preflight as p

class PreflightTests(unittest.TestCase):
    def test_recipe_duplicates_cross_split_and_nontrain_rejected(self):
        train=[{'seed':1,'split':'train'},{'seed':2,'split':'train'}]
        dev=[{'seed':3,'split':'dev'}]
        identity=lambda row: str(row['seed'])
        self.assertEqual(tuple(map(len,p.check_identities(train,dev,train,identity))),(2,1))
        for tr,dv,selected,fn in ((train+[train[0]],dev,train,identity),
                                  (train,[{'seed':2,'split':'dev'}],train,identity),
                                  (train,dev,dev,identity),
                                  (train,dev,train,lambda row:'same')):
            with self.assertRaises(ValueError):p.check_identities(tr,dv,selected,fn)

    def test_missing_required_coverage_rejected(self):
        p.require_coverage({name:1 for name in p.REQUIRED})
        for name in p.REQUIRED:
            with self.assertRaises(ValueError):p.require_coverage({n:1 for n in p.REQUIRED if n!=name})

    def test_binding_rejects_digest_and_source_drift(self):
        with tempfile.TemporaryDirectory(dir=Path('E:/Graph-auto-reader/artifacts/test-temp')) as d:
            path=Path(d)/'binding.json';obj={'source':'fixed'}
            raw=p.canonical(obj)+b'\n';path.write_bytes(raw)
            with patch.object(p,'PROTOCOL',path),patch.object(p,'snapshot',return_value=obj):
                self.assertEqual(p.verify_binding(p.digest(raw)),obj)
                with self.assertRaises(ValueError):p.verify_binding('0'*64)
            with patch.object(p,'PROTOCOL',path),patch.object(p,'snapshot',return_value={'source':'changed'}):
                with self.assertRaises(ValueError):p.verify_binding(p.digest(raw))

    def test_ablation_rejects_absent_or_nonunique_glyph(self):
        class Renderer:
            @staticmethod
            def _draw_marker(draw,mask,xy,*args):draw.point(xy,fill='black')
        def render(scene,split):
            image=Image.new('RGB',(8,8),'white');draw=ImageDraw.Draw(image)
            for xy in scene:Renderer._draw_marker(draw,None,xy)
            return image,{},None
        image,_,_=render([(3,3)],'train')
        self.assertEqual(p.glyph_visible([(3,3)],image,[3,3],[2,2,3,3],Renderer,render),1)
        self.assertEqual(p.glyph_visible([(3,3)],image,[3,3],[1,1,2,2],Renderer,render),1)
        with self.assertRaises(ValueError):p.glyph_visible([],image,[3,3],[2,2,3,3],Renderer,render)
        with self.assertRaises(ValueError):p.glyph_visible([(3,3),(3,3)],image,[3,3],[2,2,3,3],Renderer,render)
        with self.assertRaises(ValueError):p.glyph_visible([(3,3)],image,[3,3],[0,0,1,1],Renderer,render)

if __name__=='__main__':unittest.main()
