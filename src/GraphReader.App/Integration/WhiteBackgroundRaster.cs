// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GraphReader.App.Integration;

internal static class WhiteBackgroundRaster
{
    internal static BitmapSource Normalize(BitmapSource source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(source.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * source.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);
        bool changed = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
            int alpha = pixels[i + 3];
            if (alpha == 255) continue;
            changed = true;
            for (int channel = 0; channel < 3; channel++)
                pixels[i + channel] = (byte)((pixels[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
            pixels[i + 3] = 255;
        }
        token.ThrowIfCancellationRequested();
        if (!changed) return source;
        return BitmapSource.Create(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
    }
}
