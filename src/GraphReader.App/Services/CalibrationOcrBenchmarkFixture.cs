// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GraphReader.App.Services;

internal static class CalibrationOcrBenchmarkFixture
{
    internal static (CalibrationOcrRequest Request, byte[] Image) Create()
    {
        const int width = 320, height = 200, stride = width * 3, offset = 54;
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
        // Original project-owned 5 by 7 pixel glyphs, scaled without external fonts.
        string[][] glyphs = [["01100", "11100", "01100", "01100", "01100", "01100", "11110"],
            ["11110", "00001", "00001", "01110", "10000", "10000", "11111"],
            ["11110", "00001", "00001", "01110", "00001", "00001", "11110"]];
        for (int g = 0; g < glyphs.Length; g++)
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 5; x++)
                    if (glyphs[g][y][x] == '1')
                        for (int dy = 0; dy < 3; dy++)
                            for (int dx = 0; dx < 3; dx++)
                            {
                                int pixel = offset + (height - 1 - (160 + y * 3 + dy)) * stride + (75 + 70 * g + x * 3 + dx) * 3;
                                image[pixel] = image[pixel + 1] = image[pixel + 2] = 0;
                            }
        string hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        return (new CalibrationOcrRequest("device-benchmark", hash, hash, hash, width, height,
            new CalibrationOcrPlot(50, 20, 280, 150)), image);
    }
}
