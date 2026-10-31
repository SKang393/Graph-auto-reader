"""Blocked GPU-only execution contract for a future V5-compatible run.

No function in this module creates an optimizer, runs a training step, writes a
checkpoint, or admits an unregistered corpus.  It exists to make the eventual
GPU execution boundary explicit and testable before training is authorized.
"""
from __future__ import annotations

from hashlib import sha256
from pathlib import Path
from typing import Mapping

import torch

from ml.ocr.component_ensemble_v5.model import ComponentEnsembleGlyphNet
from ml.ocr.component_ensemble_v5.protocol import CLASS_COUNT, ENCODED_GLYPH_WIDTH, GLYPH_HEIGHT
from . import foundation

MODEL_SOURCE = foundation.ROOT / "ml/ocr/component_ensemble_v5/model.py"
PROTOCOL_SOURCE = foundation.ROOT / "ml/ocr/component_ensemble_v5/protocol.py"
EXPECTED_GLYPH_SHAPE = (1, GLYPH_HEIGHT, ENCODED_GLYPH_WIDTH)
EXPECTED_CLASS_COUNT = CLASS_COUNT
REJECT_CLASS_INDEX = 13
PERCENT_CLASS_INDEX = 12


def v5_contract() -> dict[str, object]:
    """Return the fixed runtime shape/class contract without constructing a model."""
    return {
        "model": ComponentEnsembleGlyphNet.__name__,
        "glyph_shape": EXPECTED_GLYPH_SHAPE,
        "class_count": EXPECTED_CLASS_COUNT,
        "reject_class_index": REJECT_CLASS_INDEX,
        "model_source_sha256": sha256(MODEL_SOURCE.read_bytes()).hexdigest(),
        "protocol_source_sha256": sha256(PROTOCOL_SOURCE.read_bytes()).hexdigest(),
    }


def verify_recipe_v5_contract(recipe: Mapping[str, object]) -> None:
    """Bind recipe values to the live, imported V5 source bytes before data access."""
    actual = v5_contract()
    expected = dict(recipe.get("v5_contract", {})) if isinstance(recipe.get("v5_contract"), Mapping) else None
    if expected is None or expected.get("glyph_shape") != list(actual["glyph_shape"]):
        raise foundation.TrainingAdmissionBlocked("V5 glyph contract differs from live source")
    for key in ("model", "class_count", "reject_class_index", "model_source_sha256", "protocol_source_sha256"):
        if expected.get(key) != actual[key]:
            raise foundation.TrainingAdmissionBlocked("V5 source contract differs from live source")


def numeric_tick_class_policy() -> dict[str, object]:
    """Keep V5's 14 classes while excluding percent labels from numeric X ticks."""
    return {
        "runtime_class_count": EXPECTED_CLASS_COUNT,
        "reject_class_index": REJECT_CLASS_INDEX,
        "percent_class_index": PERCENT_CLASS_INDEX,
        "percent_policy": "percent is retained for V5 compatibility but forbidden in numeric X-tick positive labels",
        "negative_supervision": "not admitted until an authenticated corpus supplies reject-class negatives",
    }


def _state_tensors(value: object) -> list[torch.Tensor]:
    """Collect every tensor from nested optimizer state without constructing it."""
    if isinstance(value, torch.Tensor):
        return [value]
    if isinstance(value, Mapping):
        return [tensor for nested in value.values() for tensor in _state_tensors(nested)]
    if isinstance(value, (list, tuple, set)):
        return [tensor for nested in value for tensor in _state_tensors(nested)]
    return []


def require_v5_cuda_state(
    model: ComponentEnsembleGlyphNet,
    glyphs: torch.Tensor,
    loss: torch.Tensor,
    optimizer_state: Mapping[object, object],
) -> None:
    """Guard a future caller's model, input, loss, gradients and state on cuda:0.

    This accepts an externally supplied state mapping only. It never creates an
    optimizer and therefore cannot silently create CPU-resident optimizer state.
    """
    if not isinstance(model, ComponentEnsembleGlyphNet):
        raise foundation.TrainingAdmissionBlocked("actual ComponentEnsembleGlyphNet is required")
    if tuple(glyphs.shape[1:]) != EXPECTED_GLYPH_SHAPE:
        raise foundation.TrainingAdmissionBlocked("V5 glyph input must be [glyph_count,1,24,26]")
    if loss.ndim != 0:
        raise foundation.TrainingAdmissionBlocked("V5 loss must be scalar")
    tensors = [glyphs, loss, *list(model.parameters()), *list(model.buffers())]
    for parameter in model.parameters():
        if parameter.grad is not None:
            tensors.append(parameter.grad)
    tensors.extend(_state_tensors(optimizer_state))
    foundation.require_rx6600xt_gpu(*tensors)


def preflight_execution(corpus_root: Path, manifest_sha256: str, recipe_path: Path) -> dict[str, object]:
    """Run GPU and immutable-input admission then intentionally remain blocked."""
    result = foundation.preflight(corpus_root, manifest_sha256, recipe_path)
    raise foundation.TrainingAdmissionBlocked(
        "execution remains blocked: registered corpus and reject-class negative supervision are required"
    )


__all__ = ["EXPECTED_CLASS_COUNT", "EXPECTED_GLYPH_SHAPE", "PERCENT_CLASS_INDEX", "REJECT_CLASS_INDEX", "numeric_tick_class_policy", "preflight_execution", "require_v5_cuda_state", "v5_contract", "verify_recipe_v5_contract"]
