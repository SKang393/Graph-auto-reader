# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import hashlib
import json

import numpy as np
import pytest

from ml.ocr.scale_coverage_db_head_v46 import inputs

def example():
    capture={'panels':[{'panel_id':'train-panel','source_sha256':'train-source','split':'train',
        'crop':[20,30,100,50],'tensor':{'shape':[1,3,64,128]}},
        {'panel_id':'dev-panel','source_sha256':'dev-source','split':'validation'}]}
    truth={'split':'train','synthetic_only':True,'private_data':False,'sealed_data':False,
        'truths':[{'truth_id':'word','source_sha256':'train-source','source_box_ltrb':[30,40,60,50]}]}
    return capture,truth

def test_source_crop_offset_is_removed_before_tensor_scaling():
    capture,truth=example()
    result=inputs.project_training_boxes(capture,truth)
    np.testing.assert_allclose(result['train-panel'][0]['box'],[12.8,12.8,51.2,25.6])

def test_development_source_is_rejected_before_projection():
    capture,truth=example()
    capture['panels'][1]['source_sha256']='train-source'
    with pytest.raises(ValueError,match='overlap'):
        inputs.project_training_boxes(capture,truth)

@pytest.mark.parametrize('field,value',[('split','validation'),('synthetic_only',False),
    ('private_data',True),('sealed_data',True)])
def test_non_training_scope_is_rejected(field,value):
    capture,truth=example()
    truth[field]=value
    with pytest.raises(ValueError,match='Only owned training'):
        inputs.project_training_boxes(capture,truth)

def test_missing_original_projection_cannot_be_silently_dropped():
    capture,truth=example()
    truth['truths'][0]['source_box_ltrb']=[0,0,10,10]
    with pytest.raises(ValueError,match='exactly one full'):
        inputs.project_training_boxes(capture,truth)

def test_duplicate_original_truth_is_rejected():
    capture,truth=example()
    truth['truths']*=2
    with pytest.raises(ValueError,match='Repeated training truth'):
        inputs.project_training_boxes(capture,truth)

def test_window_partition_records_full_cut_and_outside_words():
    boxes=[{'truth_id':'full','box':[400,10,450,20]},
        {'truth_id':'cut','box':[375,10,400,20]},
        {'truth_id':'outside','box':[0,10,100,20]}]
    full,partial=inputs.augmented_geometry(boxes,768,128)
    assert full==[{'truth_id':'full','box':[32,20,132,40]}]
    assert partial==[{'truth_id':'cut','box':[0,20,32,40]}]

def test_cut_word_is_ignored_without_erasing_visible_full_targets():
    full=[{'truth_id':'full','box':[10,10,30,30]}]
    partial=[{'truth_id':'cut','box':[0,0,20,40]}]
    target,mask,restored=inputs.targets(64,64,full,partial)
    assert mask[0,5,5]==0 and target[0,5,5]==0
    assert mask[0,50,50]==1
    assert target.sum()>0 and np.all(mask[target>0]==1) and restored>0
    assert not target.flags.writeable and not mask.flags.writeable

def test_enlargement_preserves_channels_and_original_pixels():
    source=np.array([[[[1,2],[3,4]],[[10,20],[30,40]],[[100,200],[300,400]]]],dtype=np.float32)
    original=source.tobytes()
    enlarged=inputs.enlarge_tensor(source)
    assert enlarged.shape==(1,3,4,4) and source.tobytes()==original
    np.testing.assert_array_equal(enlarged[0,0,0],[1,1.25,1.75,2])
    np.testing.assert_array_equal(enlarged[0,1],enlarged[0,0]*10)
    np.testing.assert_array_equal(enlarged[0,2],enlarged[0,0]*100)
    assert not enlarged.flags.writeable

def test_authenticated_array_detects_changed_payload(tmp_path):
    p=tmp_path/'pixels.f32'
    descriptor=inputs.array_ref(tmp_path,p,np.ones((1,3,4,4),dtype=np.float32))
    assert not inputs.load_array(tmp_path,descriptor).flags.writeable
    data=bytearray(p.read_bytes()); data[0]^=1;p.write_bytes(data)
    with pytest.raises(ValueError,match='changed'):
        inputs.load_array(tmp_path,descriptor)

def test_training_loader_rejects_dev_before_any_feature_is_opened(tmp_path):
    row={'split':'validation','source_sha256':'dev-source'}
    document={'schema':'graphreader.ocr-scale-coverage-inputs.v1','recipe':inputs.RECIPE,
        'private_reads':0,'sealed_reads':0,'training':[row]*112,
        'development':[{'source_sha256':'dev-source'}]}
    path=tmp_path/'manifest.json';path.write_text(json.dumps(document))
    with pytest.raises(ValueError,match='Development data'):
        inputs.load_training(tmp_path,path,hashlib.sha256(path.read_bytes()).hexdigest())
