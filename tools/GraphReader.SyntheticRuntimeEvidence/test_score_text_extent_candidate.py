# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
from copy import deepcopy
from types import SimpleNamespace
import pytest
import score_text_extent_candidate as scorer


def _fixture():
    rows=[]
    truths=[]
    for index in range(709):
        identity=f'{index:064x}'
        rows.append({'truth_id':identity,'source_sha256':'a'*64,'source_box_ltrb':[1,2,11,12],
                     'role':'condition_label','text':'Review'})
        truths.append(SimpleNamespace(truth_id=identity,source_sha256='a'*64,
                                      source_box=(1,2,11,12),role='condition_label'))
    return {'truths':rows},SimpleNamespace(source_truths=truths)


def test_saved_text_keeps_full_inventory_and_frozen_condition_role():
    document,authenticated=_fixture()
    truths=scorer._saved_train_truth(document,authenticated)
    assert len(truths)==709
    assert all(t.text=='Review' and t.expected_runtime_role=='phaseheading' for t in truths)
    metric=scorer.metric._score_split(truths,{})
    assert metric['truth_region_count']==709
    assert metric['recognition_exact_count']==0
    assert metric['unmatched_truth_deletion_edit_count']==709*6


@pytest.mark.parametrize('defect',['missing','duplicate','geometry','source','role','empty'])
def test_saved_truth_must_match_authenticated_geometry(defect):
    document,authenticated=_fixture()
    if defect=='missing': document['truths'].pop()
    elif defect=='duplicate': document['truths'][1]=deepcopy(document['truths'][0])
    elif defect=='geometry': document['truths'][0]['source_box_ltrb'][0]=3
    elif defect=='source': document['truths'][0]['source_sha256']='b'*64
    elif defect=='role': document['truths'][0]['role']='annotation'
    else: document['truths'][0]['text']=' '
    with pytest.raises(scorer.metric.EvidenceError):
        scorer._saved_train_truth(document,authenticated)
