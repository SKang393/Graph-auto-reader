"""In-memory public-font tests of the existing schema and renderer."""
import hashlib,unittest
from pathlib import Path
from unittest import mock

from ml.synthetic import renderer,templates
from ml.synthetic.contextual_negatives import render_contextual_scene
from ml.synthetic.fonts import ResolvedFont
from ml.synthetic.marker_context_catalog_v1 import catalog,labels
from ml.synthetic.public_fonts_v1.fonts import verify
from ml.synthetic.schema import validate_scene

class RealSchemaTests(unittest.TestCase):
    def test_connected_and_missing_edges_render_with_matching_scene_metadata(self):
        font=verify()['sans'];font_sha=hashlib.sha256(font.read_bytes()).hexdigest()
        class ProjectFontResolver:
            def __init__(self,*args,**kwargs):pass
            def resolve(self,requested,size_px):
                return ResolvedFont(str(requested),Path(font),int(size_px),'project-test-font',font_sha,'Noto Sans','Regular')
        with mock.patch.object(templates,'FontResolver',ProjectFontResolver),mock.patch.object(renderer,'FontResolver',ProjectFontResolver):
            for mode in ('connected','missing_edges'):
                recipe=next(r for r in catalog.recipes('train') if r['design']=='ab' and r['connection_mode']==mode)
                scene=templates.build_scene(recipe['design'],recipe['seed'],recipe['renderer_family'],panel_count=1)
                prepared=catalog.prepare_scene(scene,recipe);validate_scene(prepared)
                self.assertEqual(prepared['presentation']['connecting_line_style'],'solid' if mode=='connected' else 'missing')
                image,annotation,marker_mask=render_contextual_scene(prepared,split='train')
                result=labels.classify(annotation,expected_split='train')
                self.assertGreater(len(result['records']),0)
                for p in annotation['panels']:
                    self.assertTrue(all(edge['drawn']==(mode=='connected') for edge in p['edges']))
                wanted='connected_positive' if mode=='connected' else 'isolated_positive'
                self.assertGreater(result['counts'].get(wanted,0),0)
                self.assertFalse(result['optimization_allowed']);self.assertEqual(image.size,marker_mask.size)
                with self.assertRaises(ValueError):labels.classify(annotation,expected_split='dev')

if __name__=='__main__':unittest.main()
