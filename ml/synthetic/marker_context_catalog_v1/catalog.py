# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Fixed, source-disjoint train/dev scene recipes; does not render or train."""
from copy import deepcopy
from hashlib import sha256
import json
from uuid import UUID, uuid5

from ml.synthetic.schema import validate_scene

REVISION='marker-context-catalog-v1'
DESIGNS=('ab','abab','multiple_baseline','alternating_treatments')
MODES=('connected','missing_edges')
RENDERER='vector_clean'

def recipes(split):
    if split not in ('train','dev'):raise ValueError('Only train and dev recipes exist')
    base,count=(20820000,64) if split=='train' else (20830000,32)
    return tuple({'revision':REVISION,'split':split,'index':i,'seed':base+i,
                  'design':DESIGNS[i%len(DESIGNS)],'connection_mode':MODES[(i//len(DESIGNS))%2],
                  'renderer_family':RENDERER} for i in range(count))

def catalog():
    train,dev=recipes('train'),recipes('dev')
    if {r['seed'] for r in train}&{r['seed'] for r in dev}:raise ValueError('seed overlap')
    return {'schema':'graphreader.synthetic-marker-context-catalog.v1','train':train,'dev':dev,
            'reserved_recipes':None,'reserved_rendered':False,'private_read':False,'sealed_read':False,
            'optimizer_steps':0,'training_approved':False,
            'limits':['Train and dev use disjoint seeds but share generator families; external/historical independence is unproven.',
                      'Recipes are not rendered evidence; actual contextual negative coverage may be zero.',
                      'Font provenance and runtime mask/crop parity require separate admission.']}

def catalog_sha256():
    return sha256(json.dumps(catalog(),sort_keys=True,separators=(',',':')).encode()).hexdigest()

def prepare_scene(scene,recipe):
    """Transform a schema-valid owned scene produced for exactly one recipe.

    The caller must separately bind the generator, fonts and isolated rendering
    process. No random state, render, labels or optimizer are touched here.
    """
    if not isinstance(recipe,dict) or recipe not in recipes(recipe.get('split')):raise ValueError('Recipe is not in fixed catalog')
    validate_scene(scene)
    if scene['seed']!=recipe['seed'] or scene['design']!=recipe['design'] or scene['families']['renderer']['key']!=recipe['renderer_family']:raise ValueError('Scene does not match recipe')
    provenance=scene['provenance']
    if provenance['private_data'] or provenance['external_assets'] or provenance['source']!='procedural' or provenance['license']!='Apache-2.0':raise ValueError('Scene provenance differs')
    prepared=deepcopy(scene)
    prepared['scene_id']=str(uuid5(UUID(scene['scene_id']),f"{REVISION}:{recipe['split']}:{recipe['seed']}:{recipe['connection_mode']}"))
    prepared['presentation']['connecting_line_style']='solid' if recipe['connection_mode']=='connected' else 'missing'
    total=0
    for panel in prepared['panels']:
        for series in panel['series']:series['line_style']='solid' if recipe['connection_mode']=='connected' else 'missing'
        for edge in panel['connections']:
            edge['style']='solid' if recipe['connection_mode']=='connected' else 'missing'
            edge['visible']=recipe['connection_mode']=='connected';total+=1
    if total==0:raise ValueError('Scene has no declared connections to vary')
    validate_scene(prepared)
    return prepared
