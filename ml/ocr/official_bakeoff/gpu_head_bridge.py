# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Authenticate nine synthetic captures and construct train-only DB-head inputs.

ONNX Runtime extracts the frozen trunk on CPU, as in captured_head_features;
only the trainable head and its optimizer run on the required GPU. The trunk
file is temporary, contains original source weights, and is never a candidate.
"""
from __future__ import annotations

from copy import deepcopy
from hashlib import sha256
from pathlib import Path
import tempfile

import numpy as np
import torch
import onnxruntime as ort

from . import frozen_head_training as training
from . import frozen_trunk_head as trunk
from . import gpu_head_training as gpu
from . import marker_style_head_inputs as marker
from . import captured_head_features as parity


class BridgeError(ValueError):
    """A source identity, feature or initializer parity check failed."""


def _immutable(values):
    array = np.ascontiguousarray(values, dtype=np.float32)
    return np.frombuffer(array.tobytes(), dtype=np.float32).reshape(array.shape)


def _assemble(panels, model_path: Path, source_sha256: str, temporary_root: Path):
    """Assemble already authenticated train panels; public callers use pinned source."""
    gpu.require_device()
    bundle = trunk.extract_frozen_trunk_head(model_path, source_sha256)
    expected_source = trunk.sha256_file(model_path)
    if expected_source != source_sha256:
        raise BridgeError('Detector source hash changed')
    head = deepcopy(bundle.head).to(gpu.DEVICE).eval()
    result = []
    identities = []
    seen = set()
    with tempfile.TemporaryDirectory(prefix='ocr-frozen-trunk-', dir=temporary_root) as temporary:
        feature_path = Path(temporary) / 'frozen-trunk.onnx'
        feature_hash = trunk.write_frozen_trunk_model(model_path, feature_path, source_sha256)
        options = ort.SessionOptions()
        options.intra_op_num_threads = options.inter_op_num_threads = 1
        options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
        full = ort.InferenceSession(str(model_path), options, providers=['CPUExecutionProvider'])
        features_session = ort.InferenceSession(str(feature_path), options, providers=['CPUExecutionProvider'])
        if full.get_providers() != ['CPUExecutionProvider'] or features_session.get_providers() != ['CPUExecutionProvider']:
            raise BridgeError('Frozen feature extraction requires CPUExecutionProvider')
        with torch.inference_mode():
            for panel in panels:
                if panel.split != 'train' or not panel.panel_id or panel.panel_id in seen:
                    raise BridgeError('Training panel split or identity changed')
                seen.add(panel.panel_id)
                values = panel.input_values
                if (values.dtype != np.float32 or values.ndim != 4 or values.shape[:2] != (1, 3)
                    or not values.flags.c_contiguous or not np.isfinite(values).all()
                    or sha256(values.astype('<f4', copy=False).tobytes()).hexdigest() != panel.tensor_sha256):
                    raise BridgeError('Captured input tensor identity changed')
                expected = full.run([trunk.MODEL_OUTPUT_NAME], {trunk.MODEL_INPUT_NAME: values})[0]
                features = features_session.run([trunk.FEATURE_OUTPUT_NAME], {trunk.MODEL_INPUT_NAME: values})[0]
                shape = (1, trunk.FEATURE_CHANNELS, values.shape[2] // 4, values.shape[3] // 4)
                if features.shape != shape or features.dtype != np.float32 or not np.isfinite(features).all():
                    raise BridgeError('Frozen feature shape or values changed')
                actual = head(torch.tensor(features, device=gpu.DEVICE)).cpu().numpy()
                if (expected.shape != (1, 1, values.shape[2], values.shape[3]) or actual.shape != expected.shape
                    or not np.isfinite(expected).all() or not np.isfinite(actual).all()):
                    raise BridgeError('Detector/head output shape or values changed')
                error = float(np.max(np.abs(expected - actual)))
                if error > parity.TOLERANCE:
                    raise BridgeError(f'GPU initializer parity failed: {error} > {parity.TOLERANCE}')
                target, mask = _immutable(panel.shrink_target), _immutable(panel.supervision_mask)
                required = (1, values.shape[2], values.shape[3])
                if (target.shape != required or mask.shape != required or not np.isfinite(target).all()
                    or not np.isfinite(mask).all() or not np.isin(target, (0, 1)).all()
                    or not np.isin(mask, (0, 1)).all() or not np.any(mask)):
                    raise BridgeError('Target or supervision mask is invalid')
                result.append(training.TrainingPanel(panel.panel_id, 'train', _immutable(features), target, mask))
                identities.append((panel.panel_id, panel.tensor_sha256, sha256(features.tobytes()).hexdigest(),
                    sha256(target.tobytes()).hexdigest(), sha256(mask.tobytes()).hexdigest(), error))
    if trunk.sha256_file(model_path) != expected_source:
        raise BridgeError('Detector source changed during extraction')
    return bundle.head, tuple(result), {'source_sha256': source_sha256, 'trunk_sha256': feature_hash,
        'panels': tuple(identities), 'maximum_absolute_error': max(row[5] for row in identities),
        'optimizer_steps': 0, 'private_reads': 0, 'sealed_reads': 0}


def assemble_nine_profile(*, repository_root: Path, model_path: Path, request_path: Path,
                          request_sha256: str, report_path: Path, report_sha256: str):
    """Authenticate the pinned nine-train-source capture and validate GPU parity."""
    root = repository_root.resolve()
    model = model_path.resolve()
    split = marker.load_model_grid_inputs(request_path, request_sha256, report_path, report_sha256,
                                          repository_root=root)
    if split.name != 'train' or split.source_count != marker.SOURCE_COUNT or split.panel_count != marker.SOURCE_COUNT:
        raise BridgeError('Nine-source train inventory changed')
    result = _assemble(split.panels, model, trunk.REVIEWED_DETECTOR_SHA256, Path(__file__).resolve().parents[3] / 'artifacts')
    return result


def prepare_historical_train(historical_root: Path, *, old_prefix: str, relocated_root: Path):
    """Reuse V45 evidence checks while resolving only the known checkout move.

    V45 authenticates open synthetic dev for parity; returned panels are train
    only. Historical candidate JSON and the reconstructed request are unchanged.
    Call in a single-threaded preparation process, before any optimizer work.
    """
    from . import text_extent_head_inputs as text_inputs
    from ml.ocr.legend_coverage_db_head_v45 import train_p1 as v45

    root = historical_root.resolve()
    if not isinstance(old_prefix, str) or not old_prefix.endswith('\\'):
        raise BridgeError('Relocation prefix must be an explicit absolute Windows directory')
    from pathlib import PureWindowsPath
    if not PureWindowsPath(old_prefix).is_absolute():
        raise BridgeError('Relocation prefix must be an explicit absolute Windows directory')
    new_root = relocated_root.resolve()
    original_authenticate = text_inputs._authenticate_candidate

    def map_path(value):
        if not isinstance(value, str) or not value.startswith(old_prefix):
            raise BridgeError('Historical candidate path is outside the exact relocation prefix')
        resolved = (new_root / value[len(old_prefix):]).resolve()
        if not resolved.is_relative_to(new_root):
            raise BridgeError('Relocated candidate path escapes its supplied root')
        return str(resolved)

    def authenticate(candidate, checked_root):
        mapped = deepcopy(candidate)
        mapped['detector']['model_path'] = map_path(candidate['detector']['model_path'])
        mapped['detector']['manifest_path'] = map_path(candidate['detector']['manifest_path'])
        mapped['native_path'] = map_path(candidate['native_path'])
        for old, replacement in zip(candidate['license_inputs'], mapped['license_inputs'], strict=True):
            replacement['path'] = map_path(old['path'])
        _, manifest_path, manifest_hash = original_authenticate(mapped, checked_root)
        return candidate['detector'], manifest_path, manifest_hash

    try:
        text_inputs._authenticate_candidate = authenticate
        prepared = v45.prepare_training(root)
    finally:
        text_inputs._authenticate_candidate = original_authenticate
    if len(prepared.panels) != 34 or any(panel.split != 'train' for panel in prepared.panels):
        raise BridgeError('Historical train panel inventory changed')
    if len({panel.panel_id for panel in prepared.panels}) != 34:
        raise BridgeError('Historical train panel identities repeat')
    return prepared


def prepare_combined(*, historical_root: Path, old_prefix: str, relocated_root: Path,
                     repository_root: Path, model_path: Path, request_path: Path,
                     request_sha256: str, report_path: Path, report_sha256: str):
    """Authenticate 34 historical plus nine new train panels; perform no training."""
    old = prepare_historical_train(historical_root, old_prefix=old_prefix, relocated_root=relocated_root)
    head, new, evidence = assemble_nine_profile(repository_root=repository_root, model_path=model_path,
        request_path=request_path, request_sha256=request_sha256, report_path=report_path,
        report_sha256=report_sha256)
    combined = old.panels + new
    ids = [panel.panel_id for panel in combined]
    if len(old.panels) != 34 or len(new) != 9 or len(combined) != 43 or len(set(ids)) != 43:
        raise BridgeError('Expected 34 historical and nine disjoint new train panels')
    if any(panel.split != 'train' for panel in combined):
        raise BridgeError('Non-train input reached the combined optimizer list')
    return head, combined, {
        'historical_config_sha256': old.config_sha256,
        'historical_feature_inventory_sha256': old.feature_inventory_sha256,
        'historical_target_inventory_sha256': old.target_inventory_sha256,
        'historical_parity_maximum_absolute_error': old.parity_maximum_absolute_error,
        'historical_open_dev_authenticated': True,
        'new_request_sha256': request_sha256, 'new_report_sha256': report_sha256,
        'new_profile': evidence, 'historical_train_count': 34, 'new_train_count': 9,
        'optimizer_steps': 0, 'private_reads': 0, 'sealed_reads': 0}
