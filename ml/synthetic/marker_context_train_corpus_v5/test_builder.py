from collections import Counter
from copy import deepcopy
from hashlib import sha256
from unittest.mock import patch

import pytest
from ml.synthetic import templates
from ml.synthetic.fonts import ResolvedFont
from ml.synthetic.public_fonts_v1.fonts import verify
from ml.synthetic.marker_context_catalog_v1 import catalog
from ml.synthetic.marker_context_train_corpus_v5 import builder, plan


def test_all_64_template_configurations_validate_without_rendering():
    font = verify()['sans']; font_sha = sha256(font.read_bytes()).hexdigest(); original_kinds = Counter()
    class Resolver:
        def __init__(self, *args, **kwargs): pass
        def resolve(self, requested, size_px):
            return ResolvedFont(str(requested), font, int(size_px), 'project-local-verified', font_sha, 'Noto Sans', 'Regular')
    with patch.object(templates, 'FontResolver', Resolver):
        for entry in plan.fixed_plan():
            recipe = entry['recipe']
            raw = templates.build_scene(recipe['design'], recipe['seed'], recipe['renderer_family'], panel_count=1)
            baseline = catalog.prepare_scene(raw, recipe); expected = deepcopy(baseline)
            original_kinds.update(stage['kind'] for stage in baseline['degradations'])
            for stage in expected['degradations']: stage['kind'] = 'none'
            actual = builder.prepare_scene(raw, recipe)
            assert actual == expected and all(stage['kind'] == 'none' for stage in actual['degradations'])
    assert original_kinds['grayscale'] > 0


def test_failed_generation_creates_nonreusable_terminal_failure(tmp_path, monkeypatch):
    output = tmp_path / 'artifacts/goal22-runs/marker-context-train-corpus-v5-full'; output.parent.mkdir(parents=True)
    monkeypatch.setattr(builder, 'ROOT', tmp_path)
    def fail(mode, binding, dev, smoke, completed):
        completed.append({'seed': 20820000, 'case_id': 'fixture', 'image_sha256': 'a' * 64})
        raise builder.CaseFailure(20820001, 'SyntheticFailure')
    monkeypatch.setattr(builder, 'build', fail)
    with pytest.raises(builder.CaseFailure): builder.write_new(output, 'full', 'a'*64, {}, {})
    assert (output / 'failure-receipt.json').is_file() and not (output / 'COMPLETE.json').exists()
    import json
    receipt = json.loads((output / 'failure-receipt.json').read_bytes()); accounting = receipt['accounting']
    assert (accounting['planned_count'], accounting['attempted_count'], accounting['completed_count'],
            accounting['failed_count'], accounting['unattempted_count']) == (64, 2, 1, 1, 62)
    assert accounting['completed_recipe_ids'] == [plan.recipe_id(20820000)]
    assert accounting['failed_recipe_ids'] == [plan.recipe_id(20820001)]
    assert len(accounting['unattempted_recipe_ids']) == 62
    assert 'annotation' not in json.dumps(receipt) and 'overlay' not in json.dumps(receipt)
    with pytest.raises(ValueError, match='Exact new output'): builder.write_new(output, 'full', 'a'*64, {}, {})


def test_smoke_mode_rejected_before_source_binding_or_output_creation(tmp_path, monkeypatch):
    monkeypatch.setattr(builder, 'ROOT', tmp_path)
    def unexpected(): raise AssertionError('source binding must not be read')
    monkeypatch.setattr(builder, 'source_binding', unexpected)
    with pytest.raises(ValueError, match='only the fixed full'):
        builder.build('smoke', 'a'*64, {}, {})
    output = tmp_path / 'artifacts/goal22-runs/marker-context-train-corpus-v5-smoke'
    with pytest.raises(ValueError, match='only the fixed full'):
        builder.write_new(output, 'smoke', 'a'*64, {}, {})
    assert not output.exists()


def test_smoke_metadata_binding_requires_exact_scene_image_inventory(monkeypatch):
    identity = {'mode': 'smoke', 'sources': [{'seed': 20820000, 'image_sha256': 'a' * 64},
                                              {'seed': 20820012, 'image_sha256': 'b' * 64}]}
    manifest = {'mode': 'smoke', 'sources': [{'sha256': 'a' * 64}, {'sha256': 'b' * 64}]}
    raw_identity, raw_manifest = plan.canonical(identity), plan.canonical(manifest)
    receipt = {'schema': 'marker-context-train-corpus-complete-v3', 'mode': 'smoke', 'source_count': 2,
               'training_approved': False, 'raster_manifest_sha256': plan.digest(raw_manifest),
               'identity_sha256': plan.digest(raw_identity)}
    raw_receipt = plan.canonical(receipt)
    raw = {'receipt': raw_receipt, 'manifest': raw_manifest, 'identity': raw_identity}
    monkeypatch.setattr(builder, 'SMOKE_HASHES', {k: plan.digest(v) for k, v in raw.items()})
    assert set(builder.smoke_metadata(raw)) == {20820000, 20820012}
    altered = dict(raw, identity=b'{}')
    with pytest.raises(ValueError, match='binding differs'): builder.smoke_metadata(altered)


def test_full_requires_its_own_binding_before_data_or_render(monkeypatch):
    monkeypatch.setattr(builder, 'source_binding', lambda: {'revision': 'v4'})
    monkeypatch.setattr(builder, 'dev_metadata', lambda _: (_ for _ in ()).throw(AssertionError('must not read data')))
    with pytest.raises(ValueError, match='Source binding differs'):
        builder.build('full', '0'*64, {}, {})
