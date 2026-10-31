# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Offline plot matching and continuous targets; never runtime features.

These pure helpers do not authenticate receipts. A future extraction producer
must first freeze source-only panel/export outputs, verify all hashes, and only
then load matched training annotations. They perform no model optimization.
"""
import math
from .features import MAX_CANDIDATES

def _number(v):
 if isinstance(v,bool) or not isinstance(v,(int,float)) or not math.isfinite(v):raise ValueError('expected finite number')
 return float(v)
def _plot(p):
 if not isinstance(p,dict) or not isinstance(p.get('id'),str) or not p['id']:raise ValueError('invalid plot ID')
 points=p.get('polygon')
 if not isinstance(points,list) or len(points)!=4 or any(not isinstance(q,list) or len(q)!=2 for q in points):raise ValueError('plot must contain four corners')
 xy=[(_number(q[0]),_number(q[1])) for q in points];w=max(x for x,_ in xy)-min(x for x,_ in xy);h=max(y for _,y in xy)-min(y for _,y in xy)
 turns=[(xy[(i+1)%4][0]-xy[i][0])*(xy[(i+2)%4][1]-xy[(i+1)%4][1])-(xy[(i+1)%4][1]-xy[i][1])*(xy[(i+2)%4][0]-xy[(i+1)%4][0]) for i in range(4)]
 if w<=0 or h<=0 or not (all(z>0 for z in turns) or all(z<0 for z in turns)):raise ValueError('plot must be a nondegenerate convex quadrilateral')
 return xy,w,h
def associate(source_status,plots,panels):
 """One-to-one plot-center/crop match, with explicit rejection counters."""
 if source_status not in ('detected','no_panel_fallback','failed') or not isinstance(plots,list) or not isinstance(panels,list):raise ValueError('invalid source shape')
 parsed=[_plot(p) for p in plots];pids=[p['id'] for p in plots]
 if len(set(pids))!=len(pids):raise ValueError('duplicate plot IDs')
 if any(not isinstance(p,dict) or not isinstance(p.get('id'),str) or not p['id'] for p in panels):raise ValueError('invalid panel ID')
 ids=[p['id'] for p in panels]
 if len(set(ids))!=len(ids):raise ValueError('duplicate panel IDs')
 summary={'plots':len(plots),'detected_panels':0,'fallback_panels':0,'matched_geometry':0,'missed_plots':0,'ambiguous_plots':0,'unknown_plots':0,'unmatched_panels':0,'ambiguous_panels':0,'exporter_failures':0,'overflow_groups':0,'labelable_groups':0}
 if source_status=='failed':
  if panels:raise ValueError('failed source cannot contain panels')
  summary['unknown_plots']=len(plots);return {'summary':summary,'matches':[]}
 if source_status=='no_panel_fallback':
  if len(panels)!=1:raise ValueError('fallback must contain one panel')
  summary['fallback_panels']=1;summary['missed_plots']=len(plots);return {'summary':summary,'matches':[]}
 if not panels:raise ValueError('detected source must contain panels')
 crops=[]
 for p in panels:
  crop=p.get('crop')
  if not isinstance(crop,list) or len(crop)!=4:raise ValueError('invalid crop')
  x,y,w,h=map(_number,crop)
  if min(x,y)<0 or min(w,h)<=0:raise ValueError('invalid crop dimensions')
  if not isinstance(p.get('exporter_success'),bool):raise ValueError('missing exporter status')
  count=p.get('candidate_count')
  if p['exporter_success'] and (isinstance(count,bool) or not isinstance(count,int) or count<=0):raise ValueError('invalid candidate count')
  if not p['exporter_success'] and count is not None:raise ValueError('failed exporter cannot claim count')
  crops.append((x,y,w,h))
 summary['detected_panels']=len(panels);centers=[(sum(x for x,_ in xy)/4,sum(y for _,y in xy)/4) for xy,_,_ in parsed]
 hit=[[j for j,(cx,cy) in enumerate(centers) if x<=cx<x+w and y<=cy<y+h] for x,y,w,h in crops];inverse=[[i for i,items in enumerate(hit) if j in items] for j in range(len(plots))]
 summary['missed_plots']=sum(not a for a in inverse);summary['ambiguous_plots']=sum(len(a)>1 or (len(a)==1 and len(hit[a[0]])!=1) for a in inverse);matches=[]
 for i,items in enumerate(hit):
  if not items:summary['unmatched_panels']+=1;continue
  if len(items)!=1 or len(inverse[items[0]])!=1:summary['ambiguous_panels']+=1;continue
  summary['matched_geometry']+=1;panel=panels[i]
  if not panel['exporter_success']:summary['exporter_failures']+=1;continue
  if panel['candidate_count']>MAX_CANDIDATES:summary['overflow_groups']+=1;continue
  matches.append({'panel_id':panel['id'],'plot_id':plots[items[0]]['id']});summary['labelable_groups']+=1
 return {'summary':summary,'matches':matches}
def continuous_targets(plot,crop,candidates):
 """One target per native-order candidate; never truncate or reorder."""
 points,w,h=_plot(plot)
 if not isinstance(crop,list) or len(crop)!=4:raise ValueError('invalid crop')
 ox,oy,cw,ch=map(_number,crop)
 if min(ox,oy)<0 or min(cw,ch)<=0 or not isinstance(candidates,list) or not 1<=len(candidates)<=MAX_CANDIDATES:raise ValueError('invalid candidate group')
 targets=[]
 for pair in candidates:
  if not isinstance(pair,dict):raise ValueError('invalid candidate')
  distance=[]
  for key,(x,y) in zip(('TopLeft','TopRight','BottomRight','Intersection'),points,strict=True):
   q=pair.get(key)
   if not isinstance(q,dict):raise ValueError('candidate corner missing')
   distance.append(math.hypot((_number(q.get('X'))+ox-x)/w,(_number(q.get('Y'))+oy-y)/h))
  targets.append(sum(distance)/4)
 return targets
