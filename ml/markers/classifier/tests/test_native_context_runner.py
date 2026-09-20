# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
import json

import numpy as np
import pytest
import torch

from ml.markers.classifier.native_context_v3 import runner


def test_ambiguous_shapes_get_uniform_targets_and_artifacts_do_not_get_shape_labels():
    rows = [dict(artifact=False, shape_target_indices=[0, 4], fill_target_index=2, shape_label_ambiguous=True),
            dict(artifact=True, shape_target_indices=[-1], fill_target_index=-1, shape_label_ambiguous=False)]
    _, shape, fill, artifact, certain = runner.tensors(np.zeros((2, 1, 32, 32), np.float32), rows)
    assert shape[0, 0] == shape[0, 4] == .5
    assert shape[1].sum() == 0
    assert fill.tolist() == [2, -1] and artifact.tolist() == [0, 1]
    assert certain.tolist() == [False, True]


@pytest.mark.parametrize("marker_only", [True, False])
def test_single_class_batches_have_finite_differentiable_loss(marker_only):
    outputs = tuple(torch.zeros((2, width), requires_grad=True) for width in (9, 3, 1, 12))
    shapes = torch.zeros((2, 9)); shapes[:, 0] = 1
    artifacts = torch.full((2,), 0. if marker_only else 1.)
    fills = torch.zeros(2, dtype=torch.long) if marker_only else torch.full((2,), -1, dtype=torch.long)
    loss, _ = runner.loss_terms(outputs, shapes, fills, artifacts, torch.ones(2, dtype=torch.bool))
    assert torch.isfinite(loss)
    loss.backward()
    assert outputs[2].grad is not None


def test_unauthorized_training_cannot_create_output(tmp_path, monkeypatch):
    config = tmp_path / runner.CONFIG_PATH
    config.parent.mkdir(parents=True)
    config.write_text(json.dumps({}))
    def reject(*args, **kwargs):
        raise RuntimeError("not authorized")
    monkeypatch.setattr(runner, "acquire_training_candidate", reject)
    output = tmp_path / "run"
    with pytest.raises(RuntimeError, match="not authorized"):
        runner.run(tmp_path, output)
    assert not output.exists()
