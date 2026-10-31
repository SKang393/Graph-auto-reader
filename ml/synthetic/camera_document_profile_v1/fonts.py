# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
"""License-clean font discovery for the synthetic renderer.

This resolver does not copy fonts at runtime. It resolves either an explicit
user-supplied font file or an installed font. The isolated camera-document
profile packages its checksum-pinned Noto asset separately and records that
asset's provenance in each generated scene.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache
import hashlib
import os
from pathlib import Path
import re
from typing import Iterable, Sequence

from PIL import ImageFont


SUPPORTED_FONT_SUFFIXES = frozenset({".ttf", ".otf", ".ttc"})

PACKAGE_ROOT = Path(__file__).resolve().parent
CAMERA_DOCUMENT_FONT_PATH = (
    PACKAGE_ROOT / "assets" / "NotoSans-Regular.ttf"
)
CAMERA_DOCUMENT_FONT_SHA256 = "b85c38ecea8a7cfb39c24e395a4007474fa5a4fc864f6ee33309eb4948d232d5"
CAMERA_DOCUMENT_FONT_SOURCE_COMMIT = "ffebf8c1ee449e544955a7e813c54f9b73848eac"
CAMERA_DOCUMENT_FONT_REPOSITORY = "https://github.com/notofonts/noto-fonts"
CAMERA_DOCUMENT_FONT_SOURCE_URL = (
    "https://github.com/notofonts/noto-fonts/blob/ffebf8c1ee449e544955a7e813c54f9b73848eac/"
    "hinted/ttf/NotoSans/NotoSans-Regular.ttf"
)
CAMERA_DOCUMENT_FONT_LICENSE_PATH = (
    PACKAGE_ROOT / "assets" / "LICENSE"
)
CAMERA_DOCUMENT_FONT_LICENSE_SHA256 = "0dab92d0544f7b233403f14b84a663bdbfa746982eda629e7f4f9ffe1b036feb"
CAMERA_DOCUMENT_FONT_LICENSE_URL = (
    "https://github.com/notofonts/noto-fonts/blob/ffebf8c1ee449e544955a7e813c54f9b73848eac/LICENSE"
)


class FontResolutionError(RuntimeError):
    """Raised when a requested system or user-supplied font cannot be used."""


@dataclass(frozen=True, slots=True)
class ResolvedFont:
    """A resolved host font and its reproducibility provenance."""

    requested: str
    path: Path
    size_px: int
    source: str
    sha256: str
    family: str
    style: str

    def load(self) -> ImageFont.FreeTypeFont:
        """Load the resolved font at the recorded pixel size."""

        try:
            return ImageFont.truetype(str(self.path), self.size_px)
        except (OSError, ValueError) as exc:
            raise FontResolutionError(
                f"Font '{self.path}' was resolved but Pillow could not load it: {exc}"
            ) from exc

    def provenance(self) -> dict[str, object]:
        """Return JSON-safe font provenance without copying the font itself."""

        return {
            "requested": self.requested,
            "resolved_file": self.path.name,
            "resolved_path": str(self.path),
            "family": self.family,
            "style": self.style,
            "size_px": self.size_px,
            "source": self.source,
            "sha256": self.sha256,
            "bundled": False,
        }


def system_font_directories() -> tuple[Path, ...]:
    """Return existing host font directories in deterministic search order."""

    candidates: list[Path] = []
    windir = os.environ.get("WINDIR")
    if windir:
        candidates.append(Path(windir) / "Fonts")
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        candidates.append(Path(local_app_data) / "Microsoft" / "Windows" / "Fonts")

    # These paths make the training utility usable on non-Windows build hosts;
    # production remains Windows-first and no discovered file is redistributed.
    candidates.extend(
        (
            Path("/usr/share/fonts"),
            Path("/usr/local/share/fonts"),
            Path.home() / ".local" / "share" / "fonts",
            Path.home() / ".fonts",
        )
    )

    unique: list[Path] = []
    seen: set[str] = set()
    for candidate in candidates:
        try:
            resolved = candidate.expanduser().resolve()
        except OSError:
            continue
        key = os.path.normcase(str(resolved))
        if resolved.is_dir() and key not in seen:
            seen.add(key)
            unique.append(resolved)
    return tuple(unique)


def _iter_font_files(roots: Iterable[Path]) -> Iterable[Path]:
    for root in roots:
        try:
            paths = sorted(
                (
                    path
                    for path in root.rglob("*")
                    if path.is_file() and path.suffix.lower() in SUPPORTED_FONT_SUFFIXES
                ),
                key=lambda path: (path.name.casefold(), str(path).casefold()),
            )
        except OSError:
            continue
        yield from paths


@lru_cache(maxsize=8)
def _indexed_fonts(root_names: tuple[str, ...]) -> tuple[Path, ...]:
    return tuple(_iter_font_files(Path(name) for name in root_names))


@lru_cache(maxsize=128)
def _sha256(path_text: str) -> str:
    digest = hashlib.sha256()
    with Path(path_text).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _sha256_uncached(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _font_name(path: Path, size_px: int) -> tuple[str, str]:
    try:
        family, style = ImageFont.truetype(str(path), size_px).getname()
    except (OSError, ValueError) as exc:
        raise FontResolutionError(f"Pillow could not load font '{path}': {exc}") from exc
    return str(family), str(style)


def _requested_candidates(requested: str) -> tuple[str, ...]:
    aliases = {
        "default": ("arial.ttf", "segoeui.ttf", "dejavusans.ttf"),
        "sans": ("arial.ttf", "segoeui.ttf", "dejavusans.ttf"),
        "sans-serif": ("arial.ttf", "segoeui.ttf", "dejavusans.ttf"),
        "serif": ("times.ttf", "timesnewroman.ttf", "dejavuserif.ttf"),
        "monospace": ("consola.ttf", "cour.ttf", "dejavusansmono.ttf"),
        "mono": ("consola.ttf", "cour.ttf", "dejavusansmono.ttf"),
        "handwritten": ("segoepr.ttf", "segoesc.ttf", "comic.ttf", "arial.ttf", "dejavusans.ttf"),
    }
    normalized = requested.strip().casefold()
    return aliases.get(normalized, (normalized,))


class FontResolver:
    """Resolve fonts from explicit user paths and installed system locations."""

    def __init__(self, search_paths: Sequence[str | os.PathLike[str]] | None = None) -> None:
        roots = list(system_font_directories())
        if search_paths:
            roots = [Path(path).expanduser().resolve() for path in search_paths] + roots
        self._roots = tuple(dict.fromkeys(root for root in roots if root.is_dir()))

    def resolve(
        self,
        requested: str | os.PathLike[str] | None = None,
        size_px: int = 14,
        *,
        expected_sha256: str | None = None,
    ) -> ResolvedFont:
        """Resolve a font name or path and return immutable provenance.

        ``requested`` may be an absolute/relative user-supplied font path, an
        installed font file or family name, or the generic aliases ``sans``,
        ``serif``, and ``monospace``.  The default is ``sans``.  Missing fonts
        fail explicitly rather than silently falling back to Pillow's bitmap
        font, whose metrics would make labels non-reproducible.
        """

        if isinstance(size_px, bool) or not isinstance(size_px, int) or size_px <= 0:
            raise ValueError("size_px must be a positive integer")
        if expected_sha256 is not None and not re.fullmatch(
            r"[0-9a-fA-F]{64}", expected_sha256
        ):
            raise ValueError("expected_sha256 must contain exactly 64 hexadecimal characters")

        request_text = str(requested or "sans").strip()
        if not request_text:
            request_text = "sans"
        explicit = Path(request_text).expanduser()
        looks_like_path = (
            explicit.is_absolute()
            or explicit.suffix.lower() in SUPPORTED_FONT_SUFFIXES
            or any(separator in request_text for separator in ("/", "\\"))
        )
        source = "system"
        resolved_path: Path | None = None

        if looks_like_path:
            if not explicit.is_file():
                raise FontResolutionError(
                    f"User-supplied font file does not exist: '{explicit}'. "
                    "Provide an existing .ttf, .otf, or .ttc file."
                )
            if explicit.suffix.lower() not in SUPPORTED_FONT_SUFFIXES:
                raise FontResolutionError(
                    f"Unsupported font format '{explicit.suffix}' for '{explicit}'. "
                    "Supported formats are .ttf, .otf, and .ttc."
                )
            resolved_path = explicit.resolve()
            source = "user_supplied"
        else:
            fonts = _indexed_fonts(tuple(str(root) for root in self._roots))
            candidates = _requested_candidates(request_text)
            by_filename = {path.name.casefold(): path for path in fonts}
            by_stem = {path.stem.casefold(): path for path in fonts}
            for candidate in candidates:
                resolved_path = by_filename.get(candidate) or by_stem.get(Path(candidate).stem)
                if resolved_path is not None:
                    break

            if resolved_path is None:
                # A family-name pass is slower, so perform it only after exact
                # deterministic filename matching fails.
                wanted = {Path(candidate).stem.replace(" ", "").casefold() for candidate in candidates}
                wanted.add(request_text.replace(" ", "").casefold())
                for path in fonts:
                    try:
                        family, _ = _font_name(path, size_px)
                    except FontResolutionError:
                        continue
                    if family.replace(" ", "").casefold() in wanted:
                        resolved_path = path
                        break

            if resolved_path is None:
                roots = ", ".join(str(root) for root in self._roots) or "<none>"
                raise FontResolutionError(
                    f"Installed font '{request_text}' was not found. Searched: {roots}. "
                    "Install the font or provide an explicit user-supplied font path."
                )

        family, style = _font_name(resolved_path, size_px)
        resolved_sha256 = (
            _sha256_uncached(resolved_path)
            if expected_sha256 is not None
            else _sha256(str(resolved_path))
        )
        if expected_sha256 is not None and resolved_sha256.casefold() != expected_sha256.casefold():
            raise FontResolutionError(
                f"Font '{resolved_path}' does not match its pinned SHA-256. "
                f"Expected {expected_sha256.casefold()}, got {resolved_sha256}."
            )
        return ResolvedFont(
            requested=request_text,
            path=resolved_path,
            size_px=size_px,
            source=source,
            sha256=resolved_sha256,
            family=family,
            style=style,
        )


def resolve_font(
    requested: str | os.PathLike[str] | None = None,
    size_px: int = 14,
    search_paths: Sequence[str | os.PathLike[str]] | None = None,
    *,
    expected_sha256: str | None = None,
) -> ResolvedFont:
    """Convenience API for resolving a host or user-supplied font."""

    return FontResolver(search_paths).resolve(
        requested,
        size_px,
        expected_sha256=expected_sha256,
    )


def load_font(
    requested: str | os.PathLike[str] | None = None,
    size_px: int = 14,
    search_paths: Sequence[str | os.PathLike[str]] | None = None,
    *,
    expected_sha256: str | None = None,
) -> tuple[ImageFont.FreeTypeFont, dict[str, object]]:
    """Resolve and load a font, returning the Pillow font and provenance."""

    resolved = resolve_font(
        requested,
        size_px,
        search_paths,
        expected_sha256=expected_sha256,
    )
    return resolved.load(), resolved.provenance()
