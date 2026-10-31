# SPDX-License-Identifier: Apache-2.0
"""Metadata-only fixed recipe, identity and split checks."""
from collections import Counter
import hashlib
import json
from uuid import UUID, uuid5

from ml.synthetic.marker_context_catalog_v1 import catalog

REVISION = 'marker-context-train-corpus-v5'
SMOKE_INDICES = (0, 12)


def canonical(value): return json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()
def digest(raw): return hashlib.sha256(raw).hexdigest()


def fixed_plan():
    recipes, dev = catalog.recipes('train'), catalog.recipes('dev')
    if len(recipes) != 64 or [r['seed'] for r in recipes] != list(range(20820000, 20820064)):
        raise ValueError('Fixed train recipe inventory differs')
    dev_seeds = [r['seed'] for r in dev]
    if len(dev_seeds) != 32 or len(set(dev_seeds)) != 32 or {r['seed'] for r in recipes} & set(dev_seeds):
        raise ValueError('Train/dev seed identity overlap or denominator differs')
    rows = []
    for i, recipe in enumerate(recipes):
        if recipe['split'] != 'train' or recipe['index'] != i: raise ValueError('Train recipe identity differs')
        rows.append({'recipe': recipe, 'cue': (i // 8) % 2 == 0})
    cells = Counter((r['recipe']['design'], r['recipe']['connection_mode'], r['cue']) for r in rows)
    if len(cells) != 16 or set(cells.values()) != {4}: raise ValueError('Required 16-cell balance differs')
    return rows


def recipe_id(seed):
    if type(seed) is not int or seed not in range(20820000, 20820064): raise ValueError('Unknown train seed')
    return f'{REVISION}:seed:{seed}'


def failure_account(completed, failed_seed=None):
    """Account for every planned recipe without recording any truth rows.

    Completed means rendered through per-case checks, not admitted or durable.
    """
    # Fixed preregistered range remains available even if catalog preflight fails.
    planned = list(range(20820000, 20820064))
    done = [row['seed'] for row in completed]
    if done != planned[:len(done)] or len(done) > len(planned): raise ValueError('Completed recipe order differs')
    if failed_seed is not None and (len(done) >= len(planned) or failed_seed != planned[len(done)]):
        raise ValueError('Failed recipe identity differs')
    attempted = done + ([] if failed_seed is None else [failed_seed])
    remaining = planned[len(attempted):]
    return {'planned_count': len(planned), 'attempted_count': len(attempted), 'completed_count': len(done),
            'failed_count': 0 if failed_seed is None else 1, 'unattempted_count': len(remaining),
            'attempted_recipe_ids': [recipe_id(x) for x in attempted],
            'completed_recipe_ids': [recipe_id(x) for x in done],
            'failed_recipe_ids': [] if failed_seed is None else [recipe_id(failed_seed)],
            'unattempted_recipe_ids': [recipe_id(x) for x in remaining],
            'completed_scope': 'per_case_rendered_not_admitted_or_durable'}


def case_id(scene_id, index):
    return str(uuid5(UUID(scene_id), f'{REVISION}:case:{index}'))


def stable_id(*values):
    # Mirrors ProductionWorkflowPanelStore.CreateStableId and Guid(byte[]) byte order.
    raw = bytearray(hashlib.sha256('\x1f'.join(values).encode()).digest()[:16])
    raw[6] = (raw[6] & 0x0f) | 0x50; raw[8] = (raw[8] & 0x3f) | 0x80
    return str(UUID(bytes_le=bytes(raw)))


def source_id(project_id, image_sha256):
    return stable_id('marker-context-train-corpus-v5-source', project_id, image_sha256)


def check_dev_metadata(manifest, identities, report):
    if (manifest.get('schema') != 'graphreader.marker-context-dev-raster-input.v2' or manifest.get('split') != 'dev' or
        identities.get('schema') != 'graphreader.marker-context-dev-identity-metadata.v2' or
        report.get('schema') != 'graphreader.marker-dev-source-import-report.v1' or report.get('model_inference') is not False or
        report.get('truth_read') is not False or report.get('source_count') != 32):
        raise ValueError('Dev metadata scope differs')
    image_rows = manifest.get('sources', []); identity_rows = identities.get('sources', []); sources = report.get('sources', [])
    if len(image_rows) != 32 or len(identity_rows) != 32 or len(sources) != 32: raise ValueError('Dev denominator differs')
    images = [x['sha256'] for x in image_rows]; scenes = [x['scene_id'] for x in identity_rows]
    ids = [x['source_id'] for x in sources]
    if (len(set(images)) != 32 or len(set(scenes)) != 32 or len(set(ids)) != 32 or
        {x['sha256'] for x in identity_rows} != set(images) or {x['image_sha256'] for x in sources} != set(images) or
        any(x.get('status') != 'completed' for x in sources)):
        raise ValueError('Dev identity inventory differs')
    return {'images': set(images), 'scene_ids': set(scenes), 'source_ids': set(ids)}


def check_train_identities(rows, dev, expected_count):
    if len(rows) != expected_count: raise ValueError('Train denominator differs')
    for field, dev_key in [('image_sha256', 'images'), ('scene_id', 'scene_ids'), ('source_id', 'source_ids')]:
        values = [r[field] for r in rows]
        if len(set(values)) != expected_count or set(values) & dev[dev_key]: raise ValueError('Train/dev identity collision')
    cases = [r['case_id'] for r in rows]
    if len(set(cases)) != expected_count: raise ValueError('Train case collision')


def capture_plan(manifest, identity, receipt, expected_receipt_sha256, dev):
    """Bind a caller-trusted receipt; production import/capture is separate."""
    if (digest(canonical(receipt)) != expected_receipt_sha256 or
        receipt.get('schema') != 'marker-context-train-corpus-complete-v5' or receipt.get('mode') != 'full' or
        receipt.get('training_approved') is not False or receipt.get('source_count') != 64 or
        receipt.get('raster_manifest_sha256') != digest(canonical(manifest)) or
        receipt.get('identity_sha256') != digest(canonical(identity))):
        raise ValueError('Completed full producer receipt required')
    rows = identity['sources']; check_train_identities(rows, dev, 64)
    project = identity.get('project_id')
    if (identity.get('schema') != 'marker-context-train-identity-v5' or identity.get('mode') != 'full' or
        manifest.get('schema') != 'marker-context-train-raster-v5' or manifest.get('mode') != 'full' or
        manifest.get('project_id') != project or not isinstance(project, str) or
        project != stable_id('marker-context-train-corpus-v5-project', receipt.get('builder_binding_sha256', ''))):
        raise ValueError('Train source project identity differs')
    if {(x['sha256'], x['source_id']) for x in manifest['sources']} != {(x['image_sha256'], x['source_id']) for x in rows}:
        raise ValueError('Capture image inventory differs')
    if {r['seed'] for r in rows} != set(range(20820000, 20820064)) or any(
        r['source_id'] != source_id(project, r['image_sha256']) or
        r['case_id'] != case_id(r['scene_id'], r['seed'] - 20820000) or
        r.get('source_id_status') != 'predeclared_not_yet_imported' for r in rows):
        raise ValueError('Predeclared source or case mapping differs')
    return {'schema': 'marker-context-train-capture-plan-v5', 'producer_receipt_sha256': digest(canonical(receipt)),
            'raster_manifest_sha256': receipt['raster_manifest_sha256'], 'sources': [
                {'source_id': x['source_id'], 'image_sha256': x['image_sha256'], 'case_id': x['case_id']} for x in rows],
            'scope': 'fixed-train-only-no-truth-no-classifier', 'training_approved': False}
