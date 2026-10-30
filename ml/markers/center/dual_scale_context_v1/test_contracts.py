# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Tests run inference and preprocessing only; there are no optimizer steps."""
import unittest
from io import BytesIO

import numpy as np
import onnx
import onnxruntime as ort
import torch

from .crops import crop_context
from .model import DualScaleContextNet
from ..confidence_spatial_v35.model import SpatialCenterNet
from ..scale_classifier_v16.model import ModelConfig, ScaleClassifierNet


def test_padded_context_center_is_original_local_crop():
    scene = torch.arange(3 * 5 * 7, dtype=torch.float32).reshape(3, 5, 7)
    coordinates = torch.tensor([[0., 0.], [6., 4.], [2., 3.]])
    contexts = crop_context(scene, coordinates)
    local_pad = torch.nn.functional.pad(scene, (16, 16, 16, 16))
    expected = torch.stack([local_pad[:, int(y):int(y)+33, int(x):int(x)+33]
                            for x, y in coordinates])
    assert torch.equal(contexts[:, :, 16:49, 16:49], expected)
    assert torch.equal(contexts[0, :, 32:37, 32:39], scene)


def test_rejects_nonintegral_and_out_of_bounds_proposals():
    scene = torch.zeros(3, 4, 4)
    case = unittest.TestCase()
    with case.assertRaises(ValueError):
        crop_context(scene, torch.tensor([[1.5, 2.]]))
    with case.assertRaises(ValueError):
        crop_context(scene, torch.tensor([[4., 0.]]))


def test_zero_residual_is_exact_local_prediction_and_local_stays_frozen():
    torch.manual_seed(19)
    local = SpatialCenterNet(ScaleClassifierNet(ModelConfig())).eval()
    model = DualScaleContextNet(local)
    context = torch.rand(4, 3, 65, 65)
    with torch.no_grad():
        expected = local(context[:, :, 16:49, 16:49])
        actual = model(context)
    assert torch.equal(actual, expected)
    model.train()
    assert not local.training
    assert all(not p.requires_grad for p in local.parameters())
    assert all(p.requires_grad for p in model.context.parameters())
    with torch.no_grad():
        model.context[-1].bias.fill_(1.)
        altered = model(context)
    assert torch.equal(altered[:, 1:], expected[:, 1:])
    assert not torch.equal(altered[:, 0], expected[:, 0])


def test_gpu_and_onnx_inference_parity():
    from .runner import require_gpu

    require_gpu()
    model = DualScaleContextNet(SpatialCenterNet(ScaleClassifierNet(ModelConfig()))).eval()
    batch = torch.rand(2, 3, 65, 65)
    with torch.inference_mode():
        cpu = model(batch)
    model.to("cuda:0")
    require_gpu(model)
    with torch.inference_mode():
        gpu = model(batch.to("cuda:0")).cpu()
    model.cpu()
    assert torch.allclose(cpu, gpu, rtol=1e-5, atol=1e-6)
    stream = BytesIO()
    torch.onnx.export(model, batch[:1], stream, input_names=["candidate_patches"],
                      output_names=["candidate_predictions"],
                      dynamic_axes={"candidate_patches": {0: "candidate_count"},
                                    "candidate_predictions": {0: "candidate_count"}},
                      opset_version=18, dynamo=False)
    blob = stream.getvalue()
    onnx.checker.check_model(onnx.load_model_from_string(blob))
    actual = ort.InferenceSession(blob, providers=["CPUExecutionProvider"]).run(
        ["candidate_predictions"], {"candidate_patches": batch.numpy()})[0]
    assert np.allclose(actual, cpu.numpy(), rtol=1e-5, atol=1e-6)


if __name__ == "__main__":
    test_padded_context_center_is_original_local_crop()
    test_rejects_nonintegral_and_out_of_bounds_proposals()
    test_zero_residual_is_exact_local_prediction_and_local_stays_frozen()
    test_gpu_and_onnx_inference_parity()
    print("4 dual-scale inference and crop checks passed; optimizer steps: 0")
