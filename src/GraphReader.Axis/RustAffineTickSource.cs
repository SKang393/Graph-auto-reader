// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
namespace GraphReader.Axis;

/// <summary>Original Gray8 pixel centers and their reversible source-to-derived affine map.
/// Both original and derived rasters are snapshotted by the tick client before scheduling.</summary>
public sealed record RustAffineTickSource(int Width, int Height, ReadOnlyMemory<byte> Pixels,
    double M11, double M12, double M13, double M21, double M22, double M23);
