from collections import Counter
from uuid import uuid4
import pytest
from ml.synthetic.marker_context_train_corpus_v5 import plan


def test_fixed_plan_balances_all_16_cells_and_disjoint_seed_ranges():
    rows = plan.fixed_plan(); cells = Counter((r['recipe']['design'], r['recipe']['connection_mode'], r['cue']) for r in rows)
    assert len(rows) == 64 and len(cells) == 16 and set(cells.values()) == {4}
    assert len({r['recipe']['seed'] for r in rows}) == 64
    assert plan.SMOKE_INDICES == (0, 12) and rows[0]['cue'] is True and rows[12]['cue'] is False


def test_dev_seed_overlap_is_explicitly_rejected(monkeypatch):
    original = plan.catalog.recipes
    def overlapping(split):
        rows = original(split)
        if split != 'dev': return rows
        changed = [dict(row) for row in rows]; changed[0]['seed'] = 20820000
        return tuple(changed)
    monkeypatch.setattr(plan.catalog, 'recipes', overlapping)
    with pytest.raises(ValueError, match='Train/dev seed identity overlap'): plan.fixed_plan()


def test_preflight_and_postbuild_failure_accounting_keep_full_denominator():
    early = plan.failure_account([], None)
    assert (early['attempted_count'], early['completed_count'], early['failed_count'], early['unattempted_count']) == (0, 0, 0, 64)
    complete = plan.failure_account([{'seed': 20820000 + i} for i in range(64)], None)
    assert (complete['attempted_count'], complete['completed_count'], complete['failed_count'], complete['unattempted_count']) == (64, 64, 0, 0)


def test_identity_checks_reject_cross_split_and_duplicates():
    dev = {'images': {'dev-image'}, 'scene_ids': {'dev-scene'}, 'source_ids': {'dev-source'}}
    rows = [{'image_sha256': 'image', 'scene_id': 'scene', 'source_id': 'source', 'case_id': 'case'}]
    plan.check_train_identities(rows, dev, 1)
    for field, value in [('image_sha256', 'dev-image'), ('scene_id', 'dev-scene'), ('source_id', 'dev-source')]:
        bad = [dict(rows[0], **{field: value})]
        with pytest.raises(ValueError, match='collision'): plan.check_train_identities(bad, dev, 1)
    with pytest.raises(ValueError, match='collision'): plan.check_train_identities(rows * 2, dev, 2)


def test_revision_case_and_source_ids_are_stable_and_distinct():
    scene = str(uuid4()); project = str(uuid4())
    assert plan.case_id(scene, 0) == plan.case_id(scene, 0) != plan.case_id(scene, 1)
    assert plan.source_id(project, 'a') == plan.source_id(project, 'a') != plan.source_id(project, 'b')
    previous_project = plan.stable_id('center-only-train-project-v1',
        '51c7b65a076579764da6b58c51ea4ff0c10bcd48232e1ac1b625b74cf100be73',
        'b885933f468a040a0e42b65159fa76f3aee30f143d3ab53fdad70135fa0989a7')
    assert plan.stable_id('center-only-train-source-v1', previous_project,
        '8d483f799bc23f795213c01f8be35b9426c10809decfa222edde545c431ace9c') == 'f8f90c5e-128c-c053-a90d-bd87aa5d09a0'


def test_capture_plan_requires_full_bound_source_case_mapping():
    builder_binding = 'a' * 64
    project = plan.stable_id('marker-context-train-corpus-v5-project', builder_binding); rows = []
    for i in range(64):
        image, scene = f'{i:064x}', str(uuid4())
        rows.append({'image_sha256': image, 'scene_id': scene, 'source_id': plan.source_id(project, image),
                     'case_id': plan.case_id(scene, i), 'seed': 20820000+i, 'source_id_status': 'predeclared_not_yet_imported'})
    manifest = {'schema': 'marker-context-train-raster-v5', 'mode': 'full', 'project_id': project,
                'sources': [{'sha256': r['image_sha256'], 'source_id': r['source_id']} for r in rows]}
    identity = {'schema': 'marker-context-train-identity-v5', 'mode': 'full', 'project_id': project, 'sources': rows}
    receipt = {'schema': 'marker-context-train-corpus-complete-v5', 'mode': 'full', 'source_count': 64, 'training_approved': False,
               'builder_binding_sha256': builder_binding,
               'raster_manifest_sha256': plan.digest(plan.canonical(manifest)), 'identity_sha256': plan.digest(plan.canonical(identity))}
    dev = {'images': set(), 'scene_ids': set(), 'source_ids': set()}
    result = plan.capture_plan(manifest, identity, receipt, plan.digest(plan.canonical(receipt)), dev)
    assert len(result['sources']) == 64 and result['training_approved'] is False
    bad = dict(receipt, mode='smoke')
    with pytest.raises(ValueError, match='full producer receipt'): plan.capture_plan(manifest, identity, bad, plan.digest(plan.canonical(bad)), dev)
    rows[0]['case_id'] = str(uuid4()); receipt['identity_sha256'] = plan.digest(plan.canonical(identity))
    with pytest.raises(ValueError, match='mapping differs'): plan.capture_plan(manifest, identity, receipt, plan.digest(plan.canonical(receipt)), dev)
