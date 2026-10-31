# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""Loading and validation helpers for synthetic graph scene documents."""

from __future__ import annotations

import json
from functools import lru_cache
from pathlib import Path
from typing import Any, Mapping

from jsonschema import Draft202012Validator, FormatChecker
from jsonschema.exceptions import SchemaError, ValidationError


SCHEMA_VERSION = 1
SCHEMA_PATH = Path(__file__).with_name("scene.schema.json")


class SceneValidationError(ValueError):
    """Raised when a scene does not satisfy the frozen synthetic schema."""


@lru_cache(maxsize=1)
def _loaded_schema() -> dict[str, Any]:
    with SCHEMA_PATH.open("r", encoding="utf-8") as stream:
        schema = json.load(stream)
    Draft202012Validator.check_schema(schema)
    return schema


def load_schema() -> dict[str, Any]:
    """Return an independent copy of the declarative scene schema."""

    return json.loads(json.dumps(_loaded_schema()))


@lru_cache(maxsize=1)
def _validator() -> Draft202012Validator:
    return Draft202012Validator(_loaded_schema(), format_checker=FormatChecker())


def validation_errors(scene: Mapping[str, Any]) -> tuple[str, ...]:
    """Return stable, path-qualified validation diagnostics."""

    errors = sorted(
        _validator().iter_errors(scene),
        key=lambda error: tuple(str(part) for part in error.absolute_path),
    )
    return tuple(_format_error(error) for error in errors)


def is_valid_scene(scene: Mapping[str, Any]) -> bool:
    """Return whether *scene* satisfies the schema."""

    return not validation_errors(scene)


def validate_scene(scene: Mapping[str, Any]) -> Mapping[str, Any]:
    """Validate and return *scene*, raising one concise aggregate error."""

    errors = validation_errors(scene)
    if errors:
        raise SceneValidationError("Invalid synthetic scene: " + "; ".join(errors))
    return scene


def _format_error(error: ValidationError | SchemaError) -> str:
    path = ".".join(str(part) for part in error.absolute_path) or "$"
    return f"{path}: {error.message}"


__all__ = [
    "SCHEMA_PATH",
    "SCHEMA_VERSION",
    "SceneValidationError",
    "is_valid_scene",
    "load_schema",
    "validate_scene",
    "validation_errors",
]
