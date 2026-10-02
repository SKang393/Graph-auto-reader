# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import copy

import numpy as np
import onnx
import onnxruntime as ort
import pytest
import torch

from ml.markers.center.confidence_bce_v33.objective import confidence_loss
from ml.markers.center.confidence_head_v32.adaptation import frozen_features
from ml.markers.center.confidence_residual_v34.model import ResidualCenterNet
from ml.markers.center.scale_classifier_v16.model import ScaleClassifierNet


def patches(count=4):
    return torch.rand(count, 3, 33, 33, generator=torch.Generator().manual_seed(20261002))


def test_zero_residual_preserves_retained_predictions_bit_for_bit():
    base = ScaleClassifierNet().eval()
    inputs = patches()
    with torch.no_grad():
        expected = base(inputs)
    model = ResidualCenterNet(base)
    with torch.no_grad():
        actual = model(inputs)
        features = frozen_features(base, inputs)
    assert torch.equal(expected, actual)
    assert torch.count_nonzero(model.confidence.residual(features)) == 0
    assert sum(p.numel() for p in model.parameters() if p.requires_grad) == 4225
    assert sum(p.numel() for p in base.parameters()) == 63452
    assert not any(p.requires_grad for p in base.parameters())
    torch.testing.assert_close(model.confidence(features), base.forward_raw(inputs)[:, 0], rtol=1e-5, atol=1e-6)


def test_training_updates_only_the_residual_and_preserves_geometry_and_buffers():
    base = ScaleClassifierNet().eval()
    original = copy.deepcopy(base.state_dict())
    model = ResidualCenterNet(base)
    inputs = patches()
    with torch.no_grad():
        before = model(inputs)
        features = frozen_features(base, inputs)
    model.train()
    assert not any(module.training for module in base.modules())
    optimizer = torch.optim.AdamW(model.confidence.parameters(), lr=.0003, weight_decay=.0001)
    for _ in range(3):
        loss = confidence_loss(model.confidence(features), torch.tensor([0., 1., 0., 1.]))
        optimizer.zero_grad(set_to_none=True)
        loss.backward()
        assert all(p.grad is None for p in base.parameters())
        optimizer.step()
    model.verify_frozen_state(original)
    model.eval()
    with torch.no_grad():
        after = model(inputs)
    assert torch.equal(before[:, 1:], after[:, 1:])
    assert not torch.equal(before[:, 0], after[:, 0])
    assert torch.equal(frozen_features(base, inputs), features)


@pytest.mark.parametrize("name", ['head.3.weight', 'head.1.bias', 'ink_tower.1.running_mean'])
def test_state_audit_rejects_any_retained_weight_or_buffer_mutation(name):
    model = ResidualCenterNet(ScaleClassifierNet())
    original = copy.deepcopy(model.base.state_dict())
    with torch.no_grad():
        model.base.state_dict()[name].flatten()[0].add_(1)
    with pytest.raises(ValueError, match='Retained state changed'):
        model.verify_frozen_state(original)


@pytest.mark.parametrize("defect", ['cached_weight', 'cached_bias', 'nonfinite_residual', 'unfrozen_parameter', 'unfrozen_normalization'])
def test_state_audit_rejects_changed_baseline_or_nonfinite_residual(defect):
    model = ResidualCenterNet(ScaleClassifierNet())
    original = copy.deepcopy(model.base.state_dict())
    with torch.no_grad():
        if defect == 'cached_weight':
            model.confidence.base_weight[0, 0].add_(1)
        elif defect == 'cached_bias':
            model.confidence.base_bias[0].add_(1)
        elif defect == 'nonfinite_residual':
            model.confidence.residual[2].bias.fill_(float('nan'))
        elif defect == 'unfrozen_parameter':
            model.base.head[1].weight.requires_grad_(True)
        else:
            model.base.ink_tower[1].train()
    with pytest.raises(ValueError):
        model.verify_frozen_state(original)


def test_exported_cpu_contract_preserves_dynamic_batch_and_nonzero_residual(tmp_path):
    model = ResidualCenterNet(ScaleClassifierNet()).eval()
    # Exercise export after a nonzero residual, not only its initialization.
    with torch.no_grad():
        model.confidence.residual[2].weight.fill_(.007)
        model.confidence.residual[2].bias.fill_(-.03)
    output = tmp_path/'residual.onnx'
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
    contract = model.export_contract()
    assert contract['input_shape'] == ['candidate_count', 3, 33, 33]
    assert contract['output_columns'] == ['marker_probability', 'offset_x_grid', 'offset_y_grid', 'radius_pixels']
