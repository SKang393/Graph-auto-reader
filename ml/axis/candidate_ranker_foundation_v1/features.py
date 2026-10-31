# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Finite geometry-only candidate features from an ordered C# snapshot.

This is a draft contract: the production exporter and C#/Python parity are not
yet implemented. No labels, optimizer, model, or candidate promotion occur.
"""
from hashlib import sha256
import json,math,re

SCHEMA='graphreader.axis-candidate-features.v1'
FEATURE_NAMES=('pair_score','bottom_left_x_over_width','bottom_left_y_over_height',
 'bottom_right_x_over_width','bottom_right_y_over_height','top_right_x_over_width','top_right_y_over_height',
 'top_left_x_over_width','top_left_y_over_height','x_span_over_width','y_span_over_height',
 'x_coverage','y_coverage','x_rms_over_height','y_rms_over_width','x_support_count','y_support_count')
MAX_CANDIDATES=16384
_HASH=re.compile(r'^[0-9a-f]{64}$')
def _finite(value,name):
 if isinstance(value,bool) or not isinstance(value,(int,float)) or not math.isfinite(value):raise ValueError(name+' must be finite')
 return float(value)
def _source_hash(value,name):
 if not isinstance(value,str) or not _HASH.fullmatch(value):raise ValueError(name+' must be lowercase SHA-256')
 return value
def _ids(value,name):
 if not isinstance(value,list) or not value or any(not isinstance(x,str) or not x for x in value) or len(set(value))!=len(value):raise ValueError(name+' must contain unique nonempty IDs')
 return value
def snapshot_group(source_sha256,panel_sha256,width,height,candidates):
 """Return one ordered source/panel group; reject overflow without truncation."""
 _source_hash(source_sha256,'source');_source_hash(panel_sha256,'panel');w=_finite(width,'width');h=_finite(height,'height')
 if w<=0 or h<=0 or not isinstance(candidates,list) or not 1<=len(candidates)<=MAX_CANDIDATES:raise ValueError('invalid frame or candidate count')
 rows=[];previous=None
 for index,p in enumerate(candidates):
  if not isinstance(p,dict):raise ValueError('candidate must be object')
  score=_finite(p.get('Score'),'score')
  if not 0<=score<=1 or (previous is not None and score>previous):raise ValueError('score range/order differs')
  previous=score;coords=[]
  for k in ('Intersection','BottomRight','TopRight','TopLeft'):
   q=p.get(k)
   if not isinstance(q,dict):raise ValueError('missing corner')
   coords.extend((_finite(q.get('X'),k+'.X')/w,_finite(q.get('Y'),k+'.Y')/h))
  xspan=_finite(p.get('XSpan'),'x span');yspan=_finite(p.get('YSpan'),'y span');xc=_finite(p.get('XCoverageFraction'),'x coverage');yc=_finite(p.get('YCoverageFraction'),'y coverage');xr=_finite(p.get('XRootMeanSquareError'),'x RMS');yr=_finite(p.get('YRootMeanSquareError'),'y RMS')
  if min(xspan,yspan,xr,yr)<0 or not 0<=xc<=1 or not 0<=yc<=1:raise ValueError('invalid span, coverage or RMS')
  xi=_ids(p.get('XSupportingCandidateIds'),'x support');yi=_ids(p.get('YSupportingCandidateIds'),'y support')
  values=[score,*coords,xspan/w,yspan/h,xc,yc,xr/h,yr/w,len(xi),len(yi)]
  identity={'source_sha256':source_sha256,'panel_sha256':panel_sha256,'original_rank':index+1,'snapshot':p}
  try:raw=json.dumps(identity,sort_keys=True,separators=(',',':'),allow_nan=False).encode()
  except (TypeError,ValueError) as exc:raise ValueError('snapshot not canonical JSON') from exc
  rows.append({'rank':index+1,'candidate_identity_sha256':sha256(raw).hexdigest(),'features':values})
 return {'schema':SCHEMA,'source_sha256':source_sha256,'panel_sha256':panel_sha256,'width':w,'height':h,'feature_names':FEATURE_NAMES,'runtime_parity_verified':False,'rows':rows}
