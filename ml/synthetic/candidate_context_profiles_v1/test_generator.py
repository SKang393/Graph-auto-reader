# SPDX-License-Identifier: Apache-2.0
from hashlib import sha256
from collections import Counter
import json
import math
from pathlib import Path
import tempfile

import pytest
from PIL import Image

from . import generator as g


def test_fixed_recipe_and_assigned_family_partition():
    rows = g.recipes()
    assert len(rows) == 12
    assert len({row['seed'] for row in rows}) == len(rows)
    families = g.family_inventory()
    for axis in g.AXES:
        assert set(families['train'][axis]).isdisjoint(families['evaluation'][axis])
    assert {row['split'] for row in rows} == {'train', 'evaluation'}
    for row in rows:
        assert (row['scene_split'], row['contextual_split']) == (
            ('train', 'train') if row['split'] == 'train' else ('validation', 'dev'))


@pytest.mark.parametrize('index', [0, 4, 8])
def test_real_profiles_render_deterministically_with_annotations(index):
    recipe = g.recipes()[index]
    scene, image, annotation, mask = g.render(recipe)
    scene2, image2, annotation2, mask2 = g.render(recipe)
    assert scene == scene2
    assert sha256(g.png(image)).digest() == sha256(g.png(image2)).digest()
    assert sha256(g.png(mask)).digest() == sha256(g.png(mask2)).digest()
    assert annotation == annotation2
    assert image.size == mask.size
    assert scene['families']['renderer']['key'] == recipe['family']
    assert scene['families']['renderer']['split'] == recipe['scene_split']
    assert annotation['font']['family'] == g.fonts.EXPECTED_FAMILIES[recipe['font']]
    assert annotation['font']['source'] == 'project-local-verified'
    assert annotation['font']['sha256'] == sha256(g.fonts.verify()[recipe['font']].read_bytes()).hexdigest()
    assert annotation['contextual_negative_profile']['split'] == recipe['contextual_split']


def test_rejects_unregistered_recipe():
    row = dict(g.recipes()[0], seed=1)
    with pytest.raises(ValueError, match='fixed inventory'):
        g.render(row)


def test_rejects_font_label_and_actual_font_mismatch(monkeypatch):
    monkeypatch.setattr(g, 'PROFILES', {'train': (('vector_clean', 'mono'),),
                                        'evaluation': (('scan_rough', 'sans'),)})
    with pytest.raises(ValueError, match='does not match'):
        g.family_inventory()


def test_rejects_shared_font_bytes_even_if_labels_differ(monkeypatch):
    paths = g.fonts.verify()
    paths['mono'] = paths['sans']
    monkeypatch.setattr(g.fonts, 'verify', lambda: paths)
    with pytest.raises(ValueError, match='Font bytes overlap'):
        g.family_inventory()


def test_output_requires_new_project_artifacts_directory():
    with pytest.raises(ValueError, match='beneath project artifacts'):
        g.generate(g.ROOT / 'not-an-artifact')


def test_generated_manifest_and_no_overwrite():
    temporary_root = g.ROOT / 'artifacts' / 'temp'
    temporary_root.mkdir(parents=True, exist_ok=True)
    # Retain the unique test directory for review; no files are created in the
    # operating system temp directory or removed by test cleanup.
    output = Path(tempfile.mkdtemp(prefix='candidate-context-test-', dir=temporary_root)) / 'generated'
    manifest = g.generate(output)
    assert len(manifest['cases']) == 12
    assert manifest['status'] == 'review_only'
    assert manifest['training_approved'] is False
    assert manifest['evaluation_is_acceptance'] is False
    assert manifest['split_mapping']['evaluation'] == {'scene': 'validation', 'contextual': 'dev'}
    assert all(row['recipe']['scene_split'] == 'validation'
               and row['recipe']['contextual_split'] == 'dev'
               for row in manifest['cases'] if row['recipe']['split'] == 'evaluation')
    assert len({row['sha256'] for row in manifest['cases']}) == 12
    split_fonts = {split: {r['font_sha256'] for r in manifest['cases'] if r['recipe']['split'] == split}
                   for split in ('train', 'evaluation')}
    assert split_fonts['train'].isdisjoint(split_fonts['evaluation'])
    admitted_count = omitted_count = 0
    for row in manifest['cases']:
        payload = json.loads((output / row['image'].replace('.png', '.json')).read_text(encoding='utf-8'))
        annotation, scene = payload['annotation'], payload['scene']
        info = annotation['contextual_negative_profile']
        assert row['font_family'] == annotation['font']['family']
        assert row['font_source'] == annotation['font']['source'] == 'project-local-verified'
        assert row['font_sha256'] == annotation['font']['sha256']
        assert row['font_license_id'] == 'OFL-1.1'
        assert row['font_license_file'] == 'assets/LICENSE'
        assert info['split'] == row['recipe']['contextual_split']
        assert info['replaced_orphan_requests'] == scene['hard_negatives']
        negatives = annotation['hard_negatives']
        omitted = info['omitted_native_negative_candidates']
        admitted_count += len(negatives)
        omitted_count += len(omitted)
        keys = lambda records: Counter((item['kind'], item['source_reference']) for item in records)
        assert keys(g.contextual_negatives._candidates(annotation)) == keys(negatives) + keys(omitted)
        markers = [*annotation.get('markers', []),
                   *(marker for panel in annotation['panels'] for marker in panel.get('markers', []))]
        with Image.open(output / row['image'].replace('.png', '.mask.png')) as mask:
            for candidate in negatives:
                x, y, w, h = candidate['geometry']['box']
                assert mask.crop((math.floor(x), math.floor(y), math.ceil(x+w), math.ceil(y+h))).getbbox() is None
                for marker in markers:
                    a, b, mw, mh = marker['box']
                    margin = info['marker_clearance_pixels']
                    assert x > a+mw+margin or a-margin > x+w or y > b+mh+margin or b-margin > y+h
        assert all(item['reason'] for item in omitted)
    assert admitted_count > 0
    assert omitted_count > 0
    assert (output / 'manifest.json').is_file()
    with pytest.raises(FileExistsError):
        g.generate(output)
