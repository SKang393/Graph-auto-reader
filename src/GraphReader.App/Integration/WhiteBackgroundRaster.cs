// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GraphReader.App.Integration;

internal static class WhiteBackgroundRaster
{
    internal static BitmapSource Normalize(BitmapSource source, CancellationToken token) => Normalize(source, token, out _);

    internal static BitmapSource Normalize(BitmapSource source, CancellationToken token, out byte[] compositedBgra)
    {
        token.ThrowIfCancellationRequested();
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(source.PixelWidth * 4);
        compositedBgra = new byte[checked(stride * source.PixelHeight)];
        bgra.CopyPixels(compositedBgra, stride, 0);
        bool changed = false;
        for (int i = 0; i < compositedBgra.Length; i += 4)
        {
            if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
            int alpha = compositedBgra[i + 3];
            if (alpha == 255) continue;
            changed = true;
            for (int channel = 0; channel < 3; channel++)
                compositedBgra[i + channel] = (byte)((compositedBgra[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
            compositedBgra[i + 3] = 255;
        }
        token.ThrowIfCancellationRequested();
        if (!changed) return source;
        return BitmapSource.Create(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, compositedBgra, stride);
    }
}
