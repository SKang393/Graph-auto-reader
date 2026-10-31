# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Annotation classification only; training and runtime inputs stay unapproved."""
from collections import Counter
from hashlib import sha256
import math,struct

PATCH_BYTES=3*33*33*4

def _overlaps(a,b):
    if not isinstance(a,list) or len(a)!=4 or not isinstance(b,list) or len(b)!=4:raise ValueError('Invalid context box')
    x,y,w,h=a;u,v,p,q=b
    if any(isinstance(n,bool) or not isinstance(n,(int,float)) or not math.isfinite(n) for n in (x,y,w,h,u,v,p,q)) or min(w,h,p,q)<=0:raise ValueError('Invalid context box')
    return x<=u+p and u<=x+w and y<=v+q and v<=y+h

def classify(annotation,*,expected_split):
    """Classify annotations after checking the caller's expected split.

    The split value is not authenticated by this function; a future bound
    producer must verify recipe, scene, output and source identities.
    """
    if expected_split not in ('train','dev'):raise ValueError('Expected train or dev split')
    if not isinstance(annotation,dict) or not isinstance(annotation.get('scene_id'),str) or annotation.get('coordinate_space')!='original_pixels' or not isinstance(annotation.get('panels'),list):raise ValueError('Invalid annotation')
    profile=annotation.get('contextual_negative_profile')
    if not isinstance(profile,dict) or profile.get('id')!='synthetic-contextual-negatives-v1' or profile.get('split')!=expected_split or profile.get('source_scene_id')!=annotation['scene_id']:raise ValueError('Contextual renderer provenance or split differs')
    canvas=annotation.get('canvas')
    if not isinstance(canvas,dict) or type(canvas.get('width')) is not int or type(canvas.get('height')) is not int or min(canvas['width'],canvas['height'])<=0:raise ValueError('Invalid annotation canvas')
    def center(point):
        if not isinstance(point,list) or len(point)!=2 or any(isinstance(n,bool) or not isinstance(n,(int,float)) or not math.isfinite(n) for n in point) or not (0<=point[0]<canvas['width'] and 0<=point[1]<canvas['height']):raise ValueError('Invalid source center')
        return point
    records=[];seen=set();panels={}
    def add(row):
        if not isinstance(row['id'],str) or not row['id'] or row['id'] in seen:raise ValueError('Duplicate or missing object ID')
        seen.add(row['id']);records.append(row)
    for panel in annotation['panels']:
        pid=panel['panel_id']
        if pid in panels:raise ValueError('Duplicate panel')
        panels[pid]=panel
        markers=panel['markers'];edges=panel['edges']
        ids={m['point_id'] for m in markers}
        if len(ids)!=len(markers):raise ValueError('Duplicate marker point IDs')
        connected=set()
        for edge in edges:
            if edge['from_marker_id'] not in ids or edge['to_marker_id'] not in ids or type(edge['drawn']) is not bool:raise ValueError('Invalid edge references')
            if edge['drawn']:connected.update((edge['from_marker_id'],edge['to_marker_id']))
        for marker in markers:
            if marker.get('panel_id')!=pid or marker.get('coordinate_space')!='original_pixels':raise ValueError('Marker panel or coordinate space differs')
            add({'id':'marker:'+marker['marker_id'],'panel_id':pid,'source_id':marker['marker_id'],
                 'center':center(marker['center']),'bin':'connected_positive' if marker['point_id'] in connected else 'isolated_positive','label':1})
    for neg in annotation.get('hard_negatives',[]):
        if neg.get('kind')!='legend_symbol':continue
        context=neg.get('context');pid=neg.get('panel_id');panel=panels.get(pid)
        if panel is None or not isinstance(context,dict) or context.get('category')!='legends' or neg.get('coordinate_space')!='original_pixels' or neg.get('excluded_from_marker_mask') is not True:raise ValueError('Legend context is missing')
        legends=[x for x in panel['legends'] if x['legend_id']==context.get('legend_id') and x.get('visible',True)]
        text_ids=context.get('text_ids')
        if len(legends)!=1 or not isinstance(text_ids,list) or not text_ids or len(set(text_ids))!=len(text_ids):raise ValueError('Visible legend text is missing')
        texts={t['text_id']:t for t in panel['texts']}
        if len(texts)!=len(panel['texts']):raise ValueError('Duplicate text IDs')
        if any(i not in texts or texts[i].get('role')!='legend_text' or not texts[i].get('visible',True) or not str(texts[i].get('text','')).strip() or not texts[i].get('rendered_pixel_box') or not _overlaps(texts[i]['rendered_pixel_box'],legends[0]['box']) for i in text_ids):raise ValueError('Legend text provenance differs')
        ref=neg.get('source_reference');entries=legends[0]['entries']
        matches=[entry for i,entry in enumerate(entries) if ref==f"{legends[0]['legend_id']}:{i}" and any(entry.get('text')==texts[tid]['text'] for tid in text_ids)]
        if len(matches)!=1 or neg['geometry']['center']!=matches[0]['glyph_center'] or neg['geometry']['box']!=matches[0]['glyph_box']:raise ValueError('Legend glyph geometry differs')
        add({'id':'negative:'+neg['hard_negative_id'],'panel_id':pid,'source_id':neg['hard_negative_id'],'center':center(neg['geometry']['center']),'bin':'contextual_negative','label':0})
    counts=Counter(r['bin'] for r in records)
    return {'scene_id':annotation['scene_id'],'records':records,'counts':dict(counts),'missing_bins':[b for b in ('connected_positive','isolated_positive','contextual_negative') if not counts[b]],'optimization_allowed':False,'runtime_parity_verified':False,'limits':['Legend text provenance and geometric context are checked; independent glyph visibility still requires validation.','Empty bins do not establish readiness; production masks and patch parity are unverified.']}

def mark_local_collisions(records,patches):
    """Mark exact caller-supplied centered 33px float32 cross-label collisions.

    This does not authenticate patches or establish production preprocessing.
    Ambiguous rows remain review-only; isolated positives are not relabeled.
    """
    ids=[r['id'] for r in records]
    if len(set(ids))!=len(ids) or set(ids)!=set(patches):raise ValueError('Patch inventory differs')
    hashes={};classes={}
    for r in records:
        raw=patches[r['id']]
        if type(raw) is not bytes or len(raw)!=PATCH_BYTES:raise ValueError('Expected CHW float32 33px bytes')
        if any(not math.isfinite(x[0]) or not 0<=x[0]<=1 for x in struct.iter_unpack('<f',raw)):raise ValueError('Invalid patch values')
        if r['label'] not in (0,1) or type(r['label']) is not int:raise ValueError('Invalid label')
        h=sha256(raw).hexdigest();hashes[r['id']]=h;classes.setdefault(h,set()).add(r['label'])
    return [{**r,'local_patch_sha256':hashes[r['id']],'review_only_local_identity_collision':len(classes[hashes[r['id']]])>1,'supervision':'review_only' if len(classes[hashes[r['id']]])>1 else 'pending_admission'} for r in records]
