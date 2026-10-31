# SPDX-License-Identifier: Apache-2.0
"""Fixed full train producer; run only after independent binding review."""
from copy import deepcopy
from collections import Counter
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
from unittest.mock import patch

from ml.synthetic import renderer, templates
from ml.synthetic.contextual_negatives import render_contextual_scene
from ml.synthetic.fonts import ResolvedFont
from ml.synthetic.marker_context_catalog_v1 import catalog, labels
from ml.synthetic.marker_context_overlay_v1.overlay import overlay
from ml.synthetic.marker_context_train_capture_v1.builder import SOURCE_PATHS as ORIGINAL_SOURCE_PATHS
from ml.synthetic.marker_context_train_corpus_v5 import plan
from ml.synthetic.public_fonts_v1.fonts import verify
from ml.synthetic.schema import validate_scene

ROOT = Path(__file__).resolve().parents[3]
DEV_HASHES = {
    'manifest': '7e32372e3d27561dc0aa167c1f8af4bcf4ffbd293d69eeb4ae22c8bbd0848c82',
    'identity': 'fbf1dff193a2da5d284cb1ad392b277863a42263ac2333c845b718b905f46215',
    'report': '0c70301865aff8754e3621278b7286c2b8974079998df3973a2df7d02c77d326',
}
SMOKE_HASHES = {
    'receipt': 'd3ec47441f2bd19504c6390a16ed2a94725751c84d5d7c43f6855328e7207dc6',
    'manifest': '72975d61930bdafe9817f9c221d1699192c0f82e85c3c4c686edd5db0d7a99f3',
    'identity': 'e0ea1b6894d119e4f3063dce4fd373af90585c91626ce4d84621dde15d125150',
}
SOURCE_PATHS = tuple(dict.fromkeys((*ORIGINAL_SOURCE_PATHS,
    'ml/synthetic/marker_context_train_corpus_v5/__init__.py',
    'ml/synthetic/marker_context_train_corpus_v5/plan.py',
    'ml/synthetic/marker_context_train_corpus_v5/builder.py',
    'ml/synthetic/marker_context_train_corpus_v5/launcher.py')))


def source_binding():
    source_hashes = {}
    for relative in SOURCE_PATHS:
        path = ROOT / relative
        if not path.is_file() or not path.resolve(strict=True).is_relative_to(ROOT): raise ValueError('Source outside project or missing')
        source_hashes[relative] = plan.digest(path.read_bytes())
    return {'schema': 'marker-context-train-corpus-binding-v5', 'revision': plan.REVISION,
            'train_seeds': [r['recipe']['seed'] for r in plan.fixed_plan()], 'smoke_indices': list(plan.SMOKE_INDICES),
            'cue_rule': 'even groups of eight', 'geometry_policy': 'none-family grayscale converted to no-op; others rejected',
            'max_overlay_positions_per_source': 256,
            'dev_metadata_sha256': DEV_HASHES, 'smoke_metadata_sha256': SMOKE_HASHES,
            'execution_mode': 'full_only', 'sources': source_hashes, 'training_approved': False}


def dev_metadata(raw):
    if set(raw) != set(DEV_HASHES) or any(plan.digest(raw[k]) != value for k, value in DEV_HASHES.items()):
        raise ValueError('Dev metadata binding differs')
    return plan.check_dev_metadata(*(json.loads(raw[k]) for k in ('manifest', 'identity', 'report')))


def smoke_metadata(raw):
    if set(raw) != set(SMOKE_HASHES) or any(plan.digest(raw[k]) != value for k, value in SMOKE_HASHES.items()):
        raise ValueError('Smoke metadata binding differs')
    receipt, manifest, identity = (json.loads(raw[k]) for k in ('receipt', 'manifest', 'identity'))
    if (receipt.get('schema') != 'marker-context-train-corpus-complete-v3' or receipt.get('mode') != 'smoke' or
        receipt.get('training_approved') is not False or receipt.get('source_count') != 2 or
        receipt.get('raster_manifest_sha256') != SMOKE_HASHES['manifest'] or receipt.get('identity_sha256') != SMOKE_HASHES['identity'] or
        manifest.get('mode') != 'smoke' or identity.get('mode') != 'smoke'):
        raise ValueError('Smoke receipt differs')
    rows = identity.get('sources', [])
    if len(rows) != 2 or {r['seed'] for r in rows} != {20820000,20820012} or \
       {r['image_sha256'] for r in rows} != {r['sha256'] for r in manifest['sources']}:
        raise ValueError('Smoke identity mapping differs')
    return {r['seed']: r for r in rows}


def prepare_scene(scene, recipe):
    scene = catalog.prepare_scene(scene, recipe)
    if scene['families']['degradation']['key'] != 'none' or not scene['degradations']:
        raise ValueError('Only no-degradation family supported')
    result = deepcopy(scene)
    for stage in result['degradations']:
        if (stage.get('family_key') != 'none' or stage.get('kind') not in ('none', 'grayscale') or
            stage.get('parameters') != {} or stage.get('deterministic') is not True):
            raise ValueError('Unsupported degradation')
        stage['kind'] = 'none'
    validate_scene(result)
    return result


class CaseFailure(ValueError):
    def __init__(self, seed, error_type):
        self.seed, self.error_type = seed, error_type
        super().__init__(f'Case {seed} failed: {error_type}')


def build(mode, expected_binding_sha256, dev_bytes, smoke_bytes, completed=None):
    if mode != 'full': raise ValueError('This revision permits only the fixed full train corpus')
    if plan.digest(plan.canonical(source_binding())) != expected_binding_sha256: raise ValueError('Source binding differs')
    dev, smoke = dev_metadata(dev_bytes), smoke_metadata(smoke_bytes)
    selected = plan.fixed_plan()
    project_id = plan.stable_id('marker-context-train-corpus-v5-project', expected_binding_sha256)
    font = verify()['sans']; font_hash = sha256(font.read_bytes()).hexdigest()
    class Resolver:
        def __init__(self, *args, **kwargs): pass
        def resolve(self, requested, size_px):
            return ResolvedFont(str(requested), font, int(size_px), 'project-local-verified', font_hash, 'Noto Sans', 'Regular')
    raster_rows, identity_rows, truth_rows, images = [], [], [], {}
    with patch.object(templates, 'FontResolver', Resolver), patch.object(renderer, 'FontResolver', Resolver):
        for selected_row in selected:
            recipe, cue = selected_row['recipe'], selected_row['cue']
            try:
                scene = prepare_scene(templates.build_scene(recipe['design'], recipe['seed'], recipe['renderer_family'], panel_count=1), recipe)
                image, annotation, marker_mask = render_contextual_scene(scene, split='train')
                classified = labels.classify(annotation, expected_split='train')
                wanted = 'connected_positive' if recipe['connection_mode'] == 'connected' else 'isolated_positive'
                if not classified['counts'].get(wanted): raise ValueError('Required positive control is absent')
                panel_id = scene['panels'][0]['panel_id']; x, y, width, height = scene['panels'][0]['plot_box']
                candidate = None; attempts = 0
                for yy in range(int(y)+20, int(y+height)-20, 12):
                    for xx in range(int(x)+20, int(x+width)-40, 12):
                        if attempts >= 256: break
                        attempts += 1
                        altered, sidecar = overlay(scene, annotation, image, marker_mask, recipe=recipe,
                            panel_id=panel_id, center=(xx, yy), cue=cue)
                        if sidecar['materialized']: candidate = altered, sidecar; break
                    if candidate is not None or attempts >= 256: break
                if candidate is None: raise ValueError('No safe review-only overlay')
                image, sidecar = candidate
                if sidecar.get('status') != 'review_only' or sidecar.get('cue_visible') is not cue or sidecar.get('optimization_allowed') is not False:
                    raise ValueError('Overlay scope differs')
                buffer = BytesIO(); image.save(buffer, format='PNG', optimize=False)
                raw = buffer.getvalue(); sha = plan.digest(raw); name = sha + '.png'
                if name in images: raise ValueError('Duplicate raster')
                case = plan.case_id(scene['scene_id'], recipe['index']); source = plan.source_id(project_id, sha)
                if recipe['seed'] in smoke:
                    previous = smoke[recipe['seed']]
                    if (previous['scene_id'] != scene['scene_id'] or previous['scene_sha256'] != plan.digest(plan.canonical(scene)) or
                        previous['image_sha256'] != sha):
                        raise ValueError('Full case differs from the bound smoke scene or raster')
                raster_rows.append({'file': name, 'sha256': sha, 'width': image.width, 'height': image.height, 'source_id': source})
                identity_rows.append({'source_id': source, 'image_sha256': sha, 'scene_id': scene['scene_id'],
                    'scene_sha256': plan.digest(plan.canonical(scene)), 'case_id': case, 'seed': recipe['seed'],
                    'source_id_status': 'predeclared_not_yet_imported'})
                truth_rows.append({'image_sha256': sha, 'case_id': case, 'scene': scene, 'annotation': annotation, 'overlay': sidecar})
                images[name] = raw
                if completed is not None: completed.append({'seed': recipe['seed'], 'case_id': case, 'image_sha256': sha, 'source_id': source})
            except Exception as error:
                raise CaseFailure(recipe['seed'], type(error).__name__) from error
    plan.check_train_identities(identity_rows, dev, len(selected))
    if plan.digest(plan.canonical(source_binding())) != expected_binding_sha256: raise ValueError('Source changed during rendering')
    manifest = {'schema': 'marker-context-train-raster-v5', 'mode': mode, 'builder_binding_sha256': expected_binding_sha256,
                'project_id': project_id, 'sources': raster_rows, 'training_approved': False}
    identity = {'schema': 'marker-context-train-identity-v5', 'revision': plan.REVISION, 'mode': mode,
                'project_id': project_id, 'sources': identity_rows, 'training_approved': False}
    truth = {'schema': 'marker-context-train-truth-v5', 'mode': mode, 'sources': truth_rows, 'training_approved': False}
    return manifest, identity, images, truth


def write_new(output, mode, expected_binding_sha256, dev_bytes, smoke_bytes):
    if mode != 'full': raise ValueError('This revision permits only the fixed full train corpus')
    expected = ROOT / ('artifacts/goal22-runs/marker-context-train-corpus-v5-' + mode)
    out = Path(output)
    if not out.is_absolute() or out != expected or out.exists() or out.parent.resolve(strict=True) != expected.parent:
        raise ValueError('Exact new output required')
    completed = []; out.mkdir()
    try:
        manifest, identity, images, truth = build(mode, expected_binding_sha256, dev_bytes, smoke_bytes, completed)
        raster = out / 'raster-only'; raster.mkdir(); sidecars = out / 'post-capture-truth'; sidecars.mkdir()
        for name, payload in images.items():
            with (raster / name).open('xb') as stream: stream.write(payload)
        for path, obj in [(raster / 'manifest.json', manifest), (out / 'identity-metadata.json', identity),
                          (sidecars / 'truth.json', truth)]:
            with path.open('xb') as stream: stream.write(plan.canonical(obj))
        receipt = {'schema': 'marker-context-train-corpus-complete-v5', 'mode': mode, 'builder_binding_sha256': expected_binding_sha256,
                   'source_count': len(images), 'raster_manifest_sha256': plan.digest(plan.canonical(manifest)),
                   'identity_sha256': plan.digest(plan.canonical(identity)), 'truth_sha256': plan.digest(plan.canonical(truth)),
                   'review_only_overlays': len(images), 'training_approved': False}
        selected = plan.fixed_plan()
        cells = Counter((r['recipe']['design'], r['recipe']['connection_mode'], r['cue']) for r in selected)
        receipt['recipe_cells'] = [{'design': d, 'connection_mode': m, 'cue_visible': cue, 'count': count}
                                   for (d, m, cue), count in sorted(cells.items())]
        return receipt
    except Exception as error:
        accounting = plan.failure_account(completed, getattr(error, 'seed', None))
        failure = {'schema': 'marker-context-train-corpus-failure-v5', 'mode': mode, 'status': 'incomplete',
                   'error_type': type(error).__name__, 'accounting_status': 'available', 'accounting': accounting,
                   'training_approved': False}
        with (out / 'failure-receipt.json').open('xb') as stream: stream.write(plan.canonical(failure))
        raise
