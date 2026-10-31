# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Truth-free status, rank and Python/C# feature-parity audit of frozen capture."""
from hashlib import sha256
import json, math, sys
from pathlib import Path
from ml.axis.candidate_ranker_foundation_v1.features import FEATURE_NAMES, snapshot_group

ROOT=Path(__file__).resolve().parents[3]
CAPTURE=ROOT/'artifacts/goal22-runs/axis-ranker-full-extraction-run-v1'
PANEL=ROOT/'artifacts/goal22-runs/axis-ranker-panelization-run-v2/receipt.json'
OUTPUT=ROOT/'artifacts/goal22-runs/axis-ranker-full-extraction-audit-v1'
PANEL_SHA='2cb1df8370e9da28e762a16d05e0098ca461d2013bbd61e02bb402d3c630e571'
BINDING_SHA='f167f51771483f7bc5b6cc7d7e69cdf463624769cc850276aeb2efd1c0e7b5b5'
def read(path,expected):
    data=path.read_bytes()
    if sha256(data).hexdigest()!=expected:raise ValueError('receipt or report hash differs')
    return json.loads(data)
def audit(expected):
    receipt=read(CAPTURE/'receipt.json',expected);panel_receipt=read(PANEL,PANEL_SHA)
    if receipt['binding_sha256']!=BINDING_SHA or receipt['truth_read'] or receipt['dev_read'] or receipt['reserved_read'] or receipt['optimizer_steps'] or receipt['training_admitted']:raise ValueError('capture scope differs')
    source_status={r['id']:r['source_status'] for r in panel_receipt['sources']}
    if len(source_status)!=64 or len(receipt['sources'])!=64:raise ValueError('source inventory differs')
    result={'source_count':64,'expected_panel_count':96,'detected_sources':sum(s=='detected' for s in source_status.values()),'fallback_sources':sum(s=='no_panel_fallback' for s in source_status.values()),'failed_sources':sum(s=='failed' for s in source_status.values()),'report_failures':0,'exit_status_mismatches':0,'panel_failures':0,'empty_candidates':0,'audited_panels':0,'audited_candidates':0,'max_absolute_feature_difference':0.0}
    seen=set();panel_count=0
    for index,row in enumerate(receipt['sources']):
        if row['source_id'] not in source_status or row['source_id'] in seen:raise ValueError('source identity differs')
        seen.add(row['source_id']);panel_count+=len(row['expected_panels'])
        status=row['report_status'];code=row['exit_code'];process=row['process_status']
        if (process=='completed' and status=='success' and code!=0) or (process=='completed' and status!='success' and code==0) or (process!='completed' and code is not None):
            result['exit_status_mismatches']+=1;continue
        if status!='success' or process!='completed' or code!=0 or not row['report_sha256']:
            result['report_failures']+=1;continue
        doc=read(CAPTURE/f'{index:03}.json',row['report_sha256'])
        if doc['status']!='success':raise ValueError('receipt report status differs')
        expected_ids={(x['source_id'],x['panel_id']) for x in row['expected_panels']}
        actual_ids={(x['source_id'],x['panel_id']) for x in doc['panel_results']}
        if actual_ids!=expected_ids or len(doc['panel_results'])!=len(actual_ids):raise ValueError('report panel coverage differs')
        for panel in doc['panel_results']:
            if panel['status']!='success':result['panel_failures']+=1;continue
            snapshots=panel['candidate_snapshots'];matrix=panel['normalized_features']
            if not snapshots:result['empty_candidates']+=1;continue
            if matrix['schema']!='graphreader.axis-candidate-features.v1' or matrix['feature_names']!=list(FEATURE_NAMES) or len(matrix['rows'])!=len(snapshots):raise ValueError('feature matrix differs')
            derived=snapshot_group(panel['source_sha256'],panel['panel_sha256'],panel['panel_width'],panel['panel_height'],snapshots)
            for i,(snapshot,actual,python_row) in enumerate(zip(snapshots,matrix['rows'],derived['rows'])):
                if snapshot['Rank']!=i or actual['rank']!=i+1 or python_row['rank']!=i+1:raise ValueError('zero-/one-based rank mapping differs')
                if len(actual['features'])!=len(FEATURE_NAMES):raise ValueError('feature width differs')
                for c,p in zip(actual['features'],python_row['features']):
                    if not math.isclose(c,p,rel_tol=1e-12,abs_tol=1e-12):raise ValueError('C#/Python feature parity differs')
                    result['max_absolute_feature_difference']=max(result['max_absolute_feature_difference'],abs(c-p))
            result['audited_panels']+=1;result['audited_candidates']+=len(snapshots)
    if panel_count!=96:raise ValueError('panel denominator differs')
    result.update({'schema':'graphreader.axis-ranker-extraction-audit.v1','capture_receipt_sha256':expected,'panelization_receipt_sha256':PANEL_SHA,'script_sha256':sha256(Path(__file__).read_bytes()).hexdigest(),'feature_source_sha256':sha256((ROOT/'ml/axis/candidate_ranker_foundation_v1/features.py').read_bytes()).hexdigest(),'rank_mapping':'snapshot.Rank=i; normalized row.rank=i+1; join by list order','truth_read':False,'training_admitted':False})
    if OUTPUT.exists():raise ValueError('audit exists')
    OUTPUT.mkdir();raw=(json.dumps(result,sort_keys=True,indent=2)+'\n').encode()
    with (OUTPUT/'report.json').open('xb') as f:f.write(raw)
    return sha256(raw).hexdigest()
if __name__=='__main__':
    if len(sys.argv)!=2:raise SystemExit('usage: audit_extraction_v1 <capture-receipt-sha>')
    print('audit_sha256',audit(sys.argv[1]))
