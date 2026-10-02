# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import copy

import numpy as np
import onnx
import onnxruntime as ort
import pytest
import torch

from ml.markers.center.confidence_bce_v33.objective import confidence_loss
from ml.markers.center.confidence_spatial_v35.model import SpatialCenterNet, frozen_spatial_features
from ml.markers.center.scale_classifier_v16.model import ScaleClassifierNet


def patches(count=4):
    return torch.rand(count, 3, 33, 33, generator=torch.Generator().manual_seed(20261002))


def test_initial_projection_preserves_predictions_and_has_independent_storage():
    base = ScaleClassifierNet().eval()
    inputs = patches()
    with torch.no_grad():
        expected = base(inputs)
    model = SpatialCenterNet(base)
    with torch.no_grad():
        actual = model(inputs)
        spatial = frozen_spatial_features(base, inputs)
    assert spatial.shape == (4, 768)
    assert torch.equal(expected[:, 1:], actual[:, 1:])
    torch.testing.assert_close(expected[:, 0], actual[:, 0], atol=1e-5, rtol=0)
    torch.testing.assert_close(model.confidence(spatial), base.forward_raw(inputs)[:, 0], atol=1e-5, rtol=0)
    assert sum(p.numel() for p in model.parameters() if p.requires_grad) == 49281
    assert sum(p.numel() for p in base.parameters()) == 63452
    assert torch.equal(model.confidence.projection.weight, base.head[1].weight)
    assert model.confidence.projection.weight.data_ptr() != base.head[1].weight.data_ptr()
    assert model.confidence.output.weight.data_ptr() != base.head[3].weight.data_ptr()


def test_training_changes_both_confidence_layers_without_moving_geometry_or_buffers():
    model = SpatialCenterNet(ScaleClassifierNet())
    original = copy.deepcopy(model.base.state_dict())
    before_head = copy.deepcopy(model.confidence.state_dict())
    inputs = patches()
    with torch.no_grad():
        before = model(inputs)
        spatial = frozen_spatial_features(model.base, inputs)
    model.train()
    assert not any(module.training for module in model.base.modules())
    optimizer = torch.optim.AdamW(model.confidence.parameters(), lr=.0003, weight_decay=.0001)
    for _ in range(3):
        loss = confidence_loss(model.confidence(spatial), torch.tensor([0., 1., 0., 1.]))
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        assert all(p.grad is None for p in model.base.parameters())
        optimizer.step()
    model.verify_frozen_state(original)
    assert not torch.equal(before_head['projection.weight'], model.confidence.projection.weight)
    assert not torch.equal(before_head['output.weight'], model.confidence.output.weight)
    with torch.no_grad():
        after = model(inputs)
    assert torch.equal(before[:, 1:], after[:, 1:])
    assert not torch.equal(before[:, 0], after[:, 0])
    assert torch.equal(frozen_spatial_features(model.base, inputs), spatial)


def test_deployed_branches_share_one_tower_pass():
    model = SpatialCenterNet(ScaleClassifierNet())
    calls = {'ink': 0, 'mask': 0}
    def hook(name):
        def record(_module, _args, _output):
            calls[name] += 1
        return record
    handles = [model.base.ink_tower.register_forward_hook(hook('ink')),
               model.base.mask_tower.register_forward_hook(hook('mask'))]
    try:
        model(patches())
    finally:
        for handle in handles:
            handle.remove()
    assert calls == {'ink': 1, 'mask': 1}


@pytest.mark.parametrize('name', ['head.3.weight', 'head.1.bias', 'ink_tower.1.running_mean'])
def test_audit_rejects_retained_parameter_or_buffer_mutation(name):
    model = SpatialCenterNet(ScaleClassifierNet())
    original = copy.deepcopy(model.base.state_dict())
    with torch.no_grad():
        model.base.state_dict()[name].flatten()[0].add_(1)
    with pytest.raises(ValueError, match='Retained state changed'):
        model.verify_frozen_state(original)


@pytest.mark.parametrize('defect', ['nonfinite', 'unfrozen_base', 'normalization', 'frozen_projection'])
def test_audit_rejects_changed_training_contract(defect):
    model = SpatialCenterNet(ScaleClassifierNet())
    original = copy.deepcopy(model.base.state_dict())
    if defect == 'nonfinite':
        with torch.no_grad():
            model.confidence.projection.weight[0, 0] = float('nan')
    elif defect == 'unfrozen_base':
        model.base.head[1].weight.requires_grad_(True)
    elif defect == 'normalization':
        model.base.ink_tower[1].train()
    else:
        model.confidence.projection.requires_grad_(False)
    with pytest.raises(ValueError):
        model.verify_frozen_state(original)


def test_cpu_export_preserves_dynamic_batches_and_adapted_confidence(tmp_path):
    model = SpatialCenterNet(ScaleClassifierNet()).eval()
    with torch.no_grad():
        model.confidence.projection.bias.add_(.03)
        model.confidence.output.weight.add_(.007)
    output = tmp_path/'spatial.onnx'
    torch.onnx.export(model, patches(1), output, input_names=['candidate_patches'], output_names=['candidate_predictions'],
        dynamic_axes={'candidate_patches': {0: 'candidate_count'}, 'candidate_predictions': {0: 'candidate_count'}},
        opset_version=18, dynamo=False)
    onnx.checker.check_model(onnx.load(output))
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    options.add_session_config_entry('session.intra_op.allow_spinning', '0')
    options.add_session_config_entry('session.inter_op.allow_spinning', '0')
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
    session = ort.InferenceSession(str(output), sess_options=options, providers=['CPUExecutionProvider'])
    for count in (1, 5):
        value = patches(count)
        with torch.no_grad():
            expected = model(value).numpy()
            retained = model.base(value).numpy()
        actual = session.run(None, {'candidate_patches': value.numpy()})[0]
        assert actual.shape == (count, 4) and np.isfinite(actual).all()
        np.testing.assert_allclose(actual, expected, atol=1e-5, rtol=0)
        np.testing.assert_allclose(actual[:, 1:], retained[:, 1:], atol=1e-5, rtol=0)
    assert model.export_contract()['input_shape'] == ['candidate_count', 3, 33, 33]


@pytest.mark.parametrize('shape', [(2, 3, 32, 33), (2, 2, 33, 33), (3, 33, 33)])
def test_malformed_patch_contract_is_rejected(shape):
    with pytest.raises(ValueError, match='requires NCHW'):
        SpatialCenterNet(ScaleClassifierNet())(torch.zeros(shape))
