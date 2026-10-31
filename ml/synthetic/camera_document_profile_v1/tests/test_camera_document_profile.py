from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from ml.synthetic.camera_document_profile_v1.fonts import (
    CAMERA_DOCUMENT_FONT_LICENSE_PATH,
    CAMERA_DOCUMENT_FONT_LICENSE_URL,
    CAMERA_DOCUMENT_FONT_LICENSE_SHA256,
    CAMERA_DOCUMENT_FONT_PATH,
    CAMERA_DOCUMENT_FONT_REPOSITORY,
    CAMERA_DOCUMENT_FONT_SHA256,
    CAMERA_DOCUMENT_FONT_SOURCE_URL,
    FontResolutionError,
    FontResolver,
)
from ml.synthetic.camera_document_profile_v1.renderer import render_scene
from ml.synthetic.camera_document_profile_v1.templates import (
    CAMERA_DOCUMENT_RENDERER_FAMILIES,
    FAMILY_TO_SPLIT,
    RENDERER_FAMILIES,
    build_scene,
)

PACKAGE_ROOT = Path(__file__).resolve().parents[1]
PROJECT_ROOT = Path(__file__).resolve().parents[4]
EXPECTED_SNAPSHOT_BINDINGS_SHA256 = "ada8e17530846e03cfe471c8cb92d7def1ebdb3aa24be1caaaaf164870da586a"


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _scene_json_sha256(scene: dict[str, object]) -> str:
    payload = json.dumps(
        scene,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
    ).encode("utf-8")
    return _sha256(payload)


def test_legacy_family_order_and_vector_clean_fixture_are_unchanged() -> None:
    assert RENDERER_FAMILIES[:4] == (
        "vector_clean",
        "print_monochrome",
        "scan_rough",
        "hand_drawn",
    )

    scene = build_scene("ab", 12345, "vector_clean")
    image, _, mask = render_scene(scene)

    assert scene["scene_id"] == "09fdcfdb-f6a1-5892-a940-ee6f895eed4f"
    assert _scene_json_sha256(scene) == "d055144a41bf467d3f73f8d206d73765727eb52f82b63480088798754d45c5dc"
    assert _sha256(image.tobytes()) == "4077be29f0b9f3ad1f5579f8b5a7b9e38f9e8e8d2216dba7487a42a76c9418c7"
    assert _sha256(mask.tobytes()) == "f9e92ad0aae7e31391c52f6885afe9b027487ff6377392d17dacf13895615a93"


def test_single_camera_document_development_family_is_deterministic() -> None:
    family = "camera_document_dev"
    first = build_scene("ab", 7621, family)
    second = build_scene("ab", 7621, family)

    assert first == second
    assert CAMERA_DOCUMENT_RENDERER_FAMILIES == (family,)
    assert first["families"]["renderer"] == {"key": family, "split": "validation"}
    assert all(item["split"] == "validation" for item in first["families"].values())
    assert all(
        FAMILY_TO_SPLIT[category][first["families"][category]["key"]] == "validation"
        for category in ("renderer", "font", "degradation", "template", "marker")
    )
    assert len(first["degradations"]) == 1
    assert first["degradations"][0]["family_key"] == family
    assert first["degradations"][0]["kind"] == "camera_document"
    assert "hand_drawn" not in json.dumps(first, sort_keys=True)

    image_a, annotations_a, mask_a = render_scene(first)
    image_b, annotations_b, mask_b = render_scene(second)
    assert image_a.tobytes() == image_b.tobytes()
    assert annotations_a == annotations_b
    assert mask_a.tobytes() == mask_b.tobytes()
    assert mask_a.getextrema() == (0, 255)
    assert annotations_a["degradations"][0]["geometry_transformed"] is True
    assert annotations_a["font"]["source"] == "package_asset"
    assert annotations_a["font"]["bundled"] is True
    assert annotations_a["font"]["asset_id"] == "noto-sans-regular"
    assert annotations_a["fonts"][0]["source"] == "package_asset"
    assert annotations_a["fonts"][0]["bundled"] is True
    assert annotations_a["fonts"][0]["asset_id"] == "noto-sans-regular"


def test_camera_document_font_is_checksum_bound_and_license_evidence_is_pinned() -> None:
    scene = build_scene("ab", 7621, "camera_document_dev")
    font = scene["font"]

    assert _sha256(CAMERA_DOCUMENT_FONT_PATH.read_bytes()) == CAMERA_DOCUMENT_FONT_SHA256
    assert _sha256(CAMERA_DOCUMENT_FONT_LICENSE_PATH.read_bytes()) == CAMERA_DOCUMENT_FONT_LICENSE_SHA256
    assert font["source"] == "package_asset"
    assert font["packaged"] is True
    assert font["asset_id"] == "noto-sans-regular"
    assert font["path"] == str(CAMERA_DOCUMENT_FONT_PATH)
    assert font["sha256"] == CAMERA_DOCUMENT_FONT_SHA256
    assert font["license_evidence_path"] == str(CAMERA_DOCUMENT_FONT_LICENSE_PATH)
    assert font["license_evidence_sha256"] == CAMERA_DOCUMENT_FONT_LICENSE_SHA256
    provenance = scene["provenance"]
    assert provenance["license"] == "not_assessed"
    assert provenance["code_license"] == "Apache-2.0"
    assert provenance["output_license_assessment"] == "not_assessed"
    assert provenance["external_assets"] == []
    assert provenance["bundled_fonts"] is True
    assert len(provenance["font_assets"]) == 1
    asset = provenance["font_assets"][0]
    assert asset["packaged"] is True
    assert asset["package_path"] == "ml/synthetic/camera_document_profile_v1/assets/NotoSans-Regular.ttf"
    assert asset["sha256"] == CAMERA_DOCUMENT_FONT_SHA256
    assert asset["license_evidence_sha256"] == CAMERA_DOCUMENT_FONT_LICENSE_SHA256
    assert asset["repository"] == CAMERA_DOCUMENT_FONT_REPOSITORY
    assert asset["source_url"] == CAMERA_DOCUMENT_FONT_SOURCE_URL
    assert asset["license_evidence_url"] == CAMERA_DOCUMENT_FONT_LICENSE_URL
    assert provenance["renderer_environment"]["pillow_version"]
    assert provenance["renderer_environment"]["cross_host_bitwise_reproducibility"] == "not_verified"

    with pytest.raises(FontResolutionError):
        FontResolver().resolve(
            CAMERA_DOCUMENT_FONT_PATH,
            14,
            expected_sha256="0" * 64,
        )

    tampered_scene = dict(scene)
    tampered_scene["font"] = {**font, "sha256": "0" * 64}
    with pytest.raises(FontResolutionError):
        render_scene(tampered_scene)


def test_snapshot_manifest_authenticates_copied_sources_and_scope() -> None:
    manifest_path = PACKAGE_ROOT / "snapshot_bindings.json"
    assert _sha256(manifest_path.read_bytes()) == EXPECTED_SNAPSHOT_BINDINGS_SHA256
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for relative_path, expected_sha256 in manifest["profile_files"].items():
        assert _sha256((PACKAGE_ROOT / relative_path).read_bytes()) == expected_sha256
    for filename, binding in manifest["upstream_paths"].items():
        upstream = PROJECT_ROOT / binding["source_path"]
        local_copy = PACKAGE_ROOT / filename
        assert _sha256(upstream.read_bytes()) == binding["source_sha256"]
        assert _sha256(local_copy.read_bytes()) == binding["local_copy_sha256"]

    scope = manifest["scope"]
    font_asset = manifest["font_asset"]
    assert _sha256((PROJECT_ROOT / font_asset["path"]).read_bytes()) == font_asset["sha256"]
    assert _sha256((PROJECT_ROOT / font_asset["license_evidence_path"]).read_bytes()) == font_asset["license_evidence_sha256"]
    assert scope["sealed"] is False
    assert scope["historical_independence_proven"] is False
    assert scope["internal_split_independence_claimed"] is False
    assert scope["development_only"] is True
    assert scope["training_eligible"] is False
    assert scope["distribution_approved"] is False
    assert scope["independent_signed_approval"] is False
