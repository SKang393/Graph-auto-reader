# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Unregistered GPU-only experiment seam; no automatic launch or promotion.

This is not a candidate admission or accuracy claim. Four synthetic scenes,
their historical benchmark independence, target ambiguities, and the
localized mask policy are unapproved. Any execution is a single-use experiment
for an explicitly supplied bounded recipe; a crash cannot be silently resumed.
"""
from hashlib import sha256

import numpy as np
import torch

from ml.ocr.gpu_db_head_probe_v1.probe import require_gpu
from ml.ocr.official_bakeoff import frozen_trunk_head as frozen
from .journal import Recipe, SingleUseJournal, recipe_id, schedule
from . import execution_bundle
from .source import FEATURE_SHA, MASK_SHA, MODEL, MODEL_SHA, TARGET_SHA, load_bound_source

TRAINABLE = frozenset({"conv_weight", "deconv0_weight", "deconv0_bias", "deconv1_weight", "deconv1_bias"})
FROZEN_BN = frozenset({"bn0_weight", "bn0_bias", "bn1_weight", "bn1_bias"})
DEVICE = torch.device("cuda:0")
MAX_ALLOCATION_FRACTION = 0.60
MIN_FREE_BYTES = 1024**3


def _schedule(recipe):
    return schedule(recipe)


def _new_head():
    require_gpu()
    # Lock and authenticate the exact parent while the topology reader reopens it.
    from ml.markers.center.real_positive_context_v1.positive_archive import _open_read_only_no_writers
    with _open_read_only_no_writers(MODEL) as lock:
        if sha256(lock.read()).hexdigest() != MODEL_SHA:
            raise ValueError("Frozen V46 detector changed")
        head = frozen.extract_frozen_trunk_head(MODEL, MODEL_SHA).head
    if type(head) is not frozen.FrozenDbHead:
        raise ValueError("V46 head architecture differs")
    names = dict(head.named_parameters())
    if set(names) != TRAINABLE | FROZEN_BN or len(tuple(head.buffers())) != 4:
        raise ValueError("V46 head parameter or frozen-buffer inventory differs")
    for name, parameter in names.items():
        parameter.requires_grad_(name in TRAINABLE)
    head.eval().to(DEVICE)
    require_gpu(*head.parameters(), *head.buffers())
    frozen_values = {name: value.detach().clone() for name, value in head.named_parameters() if name in FROZEN_BN}
    frozen_values.update({"buffer:" + name: value.detach().clone() for name, value in head.named_buffers()})
    return head, frozen_values


def _check_head(head, frozen_values):
    if type(head) is not frozen.FrozenDbHead or head.training:
        raise ValueError("Head must remain the exact inference-mode DB head")
    parameters, buffers = dict(head.named_parameters()), dict(head.named_buffers())
    if set(parameters) != TRAINABLE | FROZEN_BN or set(frozen_values) != FROZEN_BN | {"buffer:" + n for n in buffers}:
        raise ValueError("Head parameter inventory differs")
    require_gpu(*parameters.values(), *buffers.values(), *frozen_values.values())
    for name, value in parameters.items():
        if value.dtype != torch.float32 or value.requires_grad != (name in TRAINABLE):
            raise ValueError("Head parameter training state differs")
        if name in FROZEN_BN and (value.grad is not None or not torch.equal(value.view(torch.int32), frozen_values[name].view(torch.int32))):
            raise ValueError("Frozen BN affine values changed")
    for name, value in buffers.items():
        if (value.dtype != torch.float32 or value.requires_grad or
                not torch.equal(value.view(torch.int32), frozen_values["buffer:" + name].view(torch.int32))):
            raise ValueError("Frozen BN running values changed")


def _check_optimizer(optimizer, head, recipe):
    if type(optimizer) is not torch.optim.AdamW or len(optimizer.param_groups) != 1:
        raise ValueError("Exact AdamW optimizer is required")
    group = optimizer.param_groups[0]
    expected = {id(value) for name, value in head.named_parameters() if name in TRAINABLE}
    actual = [id(value) for value in group["params"]]
    if (len(actual) != len(expected) or set(actual) != expected or group["lr"] != recipe.learning_rate or
            group["weight_decay"] != recipe.weight_decay or group.get("capturable") is not True or
            group.get("foreach") is not False or group.get("fused") is not False):
        raise ValueError("Optimizer membership or configuration differs")
    state_tensors = [v for state in optimizer.state.values() for v in state.values() if isinstance(v, torch.Tensor)]
    if state_tensors:
        require_gpu(*state_tensors)


def _verify_sample(sample, index):
    if (index < 0 or index >= 4 or sample.mask_sha256 != MASK_SHA[index] or
            sample.feature_sha256 != FEATURE_SHA[index] or sample.target_sha256 != TARGET_SHA[index]):
        raise ValueError("Sample feature, target or mask identity differs")
    for data, digest in ((sample.feature, sample.feature_sha256), (sample.target, sample.target_sha256), (sample.mask, sample.mask_sha256)):
        if data.dtype != np.dtype("float32") or sha256(data.tobytes()).hexdigest() != digest:
            raise ValueError("Sample contents changed")
    if (sample.feature.ndim != 4 or sample.feature.shape[:2] != (1, 96) or
            sample.target.shape != sample.mask.shape or sample.target.shape != (1, sample.feature.shape[2] * 4, sample.feature.shape[3] * 4) or
            not np.any((sample.target == 1) & (sample.mask == 1)) or
            not np.any((sample.target == 0) & (sample.mask == 1))):
        raise ValueError("Sample has invalid geometry or supervision")


def _step(head, frozen_values, optimizer, sample, sample_index, recipe, journal, step_index):
    if type(journal) is not SingleUseJournal or journal.run_id != recipe_id(recipe):
        raise ValueError("Step must use the exact single-use recipe journal")
    _verify_sample(sample, sample_index)
    _check_head(head, frozen_values)
    _check_optimizer(optimizer, head, recipe)
    feature = torch.from_numpy(np.array(sample.feature, copy=True)).to(DEVICE)
    target = torch.from_numpy(np.array(sample.target, copy=True)).unsqueeze(0).to(DEVICE)
    mask = torch.from_numpy(np.array(sample.mask, copy=True)).unsqueeze(0).to(DEVICE)
    require_gpu(feature, target, mask)
    if any(value.dtype != torch.float32 for value in (feature, target, mask)):
        raise ValueError("GPU inputs must be float32")
    journal.reserve(step_index, sample_index)
    optimizer.zero_grad(set_to_none=True)
    logits = head.forward_logits(feature)
    require_gpu(logits)
    if logits.shape != target.shape or not bool(torch.isfinite(logits).all().item()):
        raise ValueError("GPU head output differs")
    probabilities = torch.sigmoid(logits)
    positive = target * mask
    loss = 1 - (2 * (probabilities * positive).sum() + 1e-6) / ((probabilities * mask).sum() + positive.sum() + 1e-6)
    require_gpu(loss)
    if not bool(torch.isfinite(loss).item()):
        raise ValueError("GPU loss is not finite")
    loss.backward()
    gradients = [value.grad for name, value in head.named_parameters() if name in TRAINABLE]
    if any(gradient is None for gradient in gradients):
        raise ValueError("A trainable head parameter has no gradient")
    require_gpu(*gradients)
    if any(not bool(torch.isfinite(gradient).all().item()) for gradient in gradients):
        raise ValueError("GPU gradient is not finite")
    _check_head(head, frozen_values)
    _check_optimizer(optimizer, head, recipe)
    optimizer.step()
    require_gpu(*head.parameters(), *head.buffers())
    _check_optimizer(optimizer, head, recipe)
    _check_head(head, frozen_values)
    if any(not bool(torch.isfinite(value).all().item()) for value in head.parameters()):
        raise ValueError("GPU parameter is not finite after step")
    torch.cuda.synchronize(DEVICE)
    journal.complete_step(step_index)


def run_unregistered_experiment(recipe: Recipe) -> dict:
    """Run one bounded explicit recipe; no checkpoint, promotion or CLI.

    The caller must separately decide whether this unapproved four-scene
    experiment is appropriate. This task does not invoke this function.
    """
    if type(recipe) is not Recipe:
        raise ValueError("An explicit bounded recipe is required")
    schedule = _schedule(recipe)
    execution_bundle.require_runtime()
    startup_receipt = execution_bundle.verify_startup(recipe.bundle_sha256)
    source = load_bound_source()
    if len(source.samples) != 4:
        raise ValueError("The source must contain exactly four synthetic scenes")
    require_gpu()
    free, _total = torch.cuda.mem_get_info(DEVICE)
    if free < MIN_FREE_BYTES:
        raise RuntimeError("RX 6600 XT has insufficient free memory")
    torch.cuda.set_per_process_memory_fraction(MAX_ALLOCATION_FRACTION, DEVICE)
    torch.use_deterministic_algorithms(True)
    torch.manual_seed(recipe.seed)
    torch.cuda.manual_seed(recipe.seed)
    head, frozen_values = _new_head()
    optimizer = torch.optim.AdamW([value for name, value in head.named_parameters() if name in TRAINABLE],
                                  lr=recipe.learning_rate, weight_decay=recipe.weight_decay,
                                  capturable=True, foreach=False, fused=False)
    if optimizer.state:
        raise ValueError("Optimizer must start fresh")
    _check_head(head, frozen_values)
    _check_optimizer(optimizer, head, recipe)
    journal = SingleUseJournal(recipe, startup_receipt)
    for step_index, sample_index in enumerate(schedule):
        _step(head, frozen_values, optimizer, source.samples[sample_index], sample_index, recipe, journal, step_index)
    journal.finish()
    return {"status": "completed_unregistered_experiment", "steps": len(schedule), "training_approved": False,
            "production_approved": False, "checkpoint_written": False}
