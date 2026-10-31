# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Isolated camera-document exploratory synthetic graph family."""

from .templates import CAMERA_DOCUMENT_RENDERER_FAMILIES, build_scene
from .renderer import render_scene

__all__ = ["CAMERA_DOCUMENT_RENDERER_FAMILIES", "build_scene", "render_scene"]
