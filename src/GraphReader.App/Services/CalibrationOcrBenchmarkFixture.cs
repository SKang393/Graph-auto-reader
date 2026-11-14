// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GraphReader.App.Services;

internal static class CalibrationOcrBenchmarkFixture
{
    // Original project-owned 5 by 7 pixel glyphs, scaled without external fonts.
    private static readonly string[][] Glyphs =
    [
        ["01100", "11100", "01100", "01100", "01100", "01100", "11110"],
        ["11110", "00001", "00001", "01110", "10000", "10000", "11111"],
        ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
    ];

    internal static (CalibrationOcrRequest Request, byte[] Image) Create() => Create(wide: false);

    internal static (CalibrationOcrRequest Request, byte[] Image)[] CreateWorkloads() =>
        [Create(), Create(wide: true)];

    private static (CalibrationOcrRequest Request, byte[] Image) Create(bool wide)
    {
        int width = wide ? 960 : 320, height = wide ? 320 : 200;
        int stride = width * 3;
        const int offset = 54;
        byte[] image = new byte[offset + stride * height];
        image.AsSpan(offset).Fill(255);
        image[0] = (byte)'B'; image[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(2), image.Length);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(10), offset);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(image.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(image.AsSpan(28), 24);
        for (int g = 0; g < Glyphs.Length; g++)
            DrawGlyph(g, wide ? 100 + 260 * g : 75 + 70 * g, wide ? 230 : 160);
        // A modest multi-digit row exercises wider recognition tensors alongside
        // isolated labels. It is timing/parity input, not calibration truth.
        if (wide)
            for (int g = 0; g < 18; g++) DrawGlyph(g % Glyphs.Length, 90 + 18 * g, 275);
        string hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        return (new CalibrationOcrRequest(wide ? "device-benchmark-wide" : "device-benchmark",
            hash, hash, hash, width, height, wide
                ? new CalibrationOcrPlot(50, 20, 900, 220)
                : new CalibrationOcrPlot(50, 20, 280, 150)), image);

        void DrawGlyph(int glyph, int left, int top)
        {
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 5; x++)
                    if (Glyphs[glyph][y][x] == '1')
                        for (int dy = 0; dy < 3; dy++)
                            for (int dx = 0; dx < 3; dx++)
                            {
                                int pixel = offset + (height - 1 - (top + y * 3 + dy)) * stride + (left + x * 3 + dx) * 3;
                                image[pixel] = image[pixel + 1] = image[pixel + 2] = 0;
                            }
        }
    }
}
