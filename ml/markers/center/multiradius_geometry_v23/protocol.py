# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Frozen declarations for the zero-optimizer V23 candidate."""

TASK = "marker-center"
REVISION = "marker-center-multiradius-geometry-v23"
CANDIDATE_ID = "P1"
EVIDENCE_POLICY_PATH = "ml/policy/evidence-policy.json"
V21_RESULT_PATH = "ml/markers/center/focal_confidence_v21/P1_RESULT.json"
V21_RESULT_SHA256 = "a78710ee13da02ec45c26a216524a90b261b0fbc17017491d525a59fe9ecdacb"
V21_DIAGNOSTIC_PATH = "ml/markers/center/focal_confidence_v21/diagnostics/V21_DIAGNOSTIC.json"
V21_DIAGNOSTIC_SHA256 = "41a7aea0432f11891c5d1c0c641ff637ccea93e073d007ea37044bf89c4b5785"
V21_ONNX_SHA256 = "0b413db48f8e6707ee5ec99afff4cd8ec3d25c6b8a8d9f165bd416deb4578a38"
V13_MANIFEST_SHA256 = "771141bd5d22d2ffdb56e63f193c16c0775a0f47c02313ef67b06bbce9603126"
FIXED_DEV_SPLIT = "marker-center-proposal-geometry-v13-dev"
RING_RADII_PX = tuple(range(3, 13))
CONFIDENCE_THRESHOLD = 0.25
OFFSET_SCALE = 4.0
RADIUS_CLIP_PX = (2.5, 8.0)
ACCEPTANCE_BAR = {
    "precision_minimum": 0.95,
    "recall_minimum": 0.95,
    "prohibited_hits_maximum": 0,
}
OPTIMIZER_STEPS = 0
TRAINING_PERFORMED = False
RUNTIME_CONTRACT = {
    "input": ["candidate_count", 3, 33, 33],
    "output": ["candidate_count", 4],
    "provider": "CPUExecutionProvider",
}
