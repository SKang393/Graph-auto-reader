# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Frozen identities and unchanged operating point for V25 training."""

from pathlib import Path


TASK = "marker-center"
TRAINING_REVISION = "marker-center-plot-domain-v25"
TRAINING_CANDIDATE_ID = "P1"
PROVIDER = "CPUExecutionProvider"
CONFIDENCE_THRESHOLD = 0.25

EVIDENCE_POLICY_PATH = Path("ml/policy/evidence-policy.json")
ACCEPTANCE_BARS_PATH = Path("ml/policy/acceptance-bars.json")
DEV_PROTOCOL_PATH = Path("ml/markers/center/plot_domain_v25/dev_protocol.json")
DEV_PROTOCOL_SHA256 = "26c790e19e574a71669d940c48497425308d6e53f57e9876d9fb14ac314e24ae"
COMPONENT_CONFIG_PATH = Path(
    "ml/markers/center/mask_preserving_v24/training/p1_retry13.json"
)
COMPONENT_CONFIG_SHA256 = "adb5a6ff5215757f06e1a35ab08f86a981061c5f242ac87653743ef6847f6ce3"
FAMILY_BINDING_PATH = Path(
    "artifacts/goal22-runs/marker-v25-plot-domain/visible-content-v3-binding/binding-v3.json"
)
FAMILY_BINDING_SHA256 = "bcf821a2aaadc5ee8e18bd97dbe0488d120d2ac974a249c00d76b6cfccf0d98a"

COMPONENT_TRAIN_SCENES = 167
COMPONENT_DEV_SCENES = 167
COMPONENT_TRAIN_TRUTHS = 2004
COMPONENT_DEV_TRUTHS = 2004
FAMILY_TRAIN_SOURCES = 20
FAMILY_TRAIN_PANELS = 28
FAMILY_TRAIN_TRUTHS = 500
FAMILY_DEV_SOURCES = 3
FAMILY_DEV_PANELS = 9
FAMILY_DEV_TRUTHS = 206
FAMILY_TRAIN_TENSOR_SHA256 = "e56c8f641f9e0d19e9439aeb39d8c10c3edda5657ad4a554ab45ea9ef1261599"
FAMILY_DEV_TENSOR_SHA256 = "7a8d688123e76c1b144ea5a19a00cb3a76a004c25c02194cb75345ddedae8120"
FAMILY_PANEL_INVENTORY_SHA256 = "05b7358d933e06469de35bec72ebcdc00daa731c57695bbebe1e926091aad8ea"
FAMILY_MAPPING_AUDIT_SHA256 = "aab24d1be5eabdb2205a735b73e7853fe73a405a541027271ce524bdea0e1895"
FAMILY_TRUTH_MAPPING_SHA256 = "3e01264e2b7f4fa0483e674f2b938c1ad60eb590df231950133b55efee75cb1d"


__all__ = [name for name in globals() if name.isupper()]
