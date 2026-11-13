// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GraphReader.Axis;

namespace GraphReader.App.Services;

public interface IAxisProposalService
{
    Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken);
}

public sealed class RustAxisProposalService(string workerPath) : IAxisProposalService
{
    public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream(originalImage, writable: false);
            BitmapFrame frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth is < 32 or > 4096 || frame.PixelHeight is < 32 or > 4096 ||
                (long)frame.PixelWidth * frame.PixelHeight > 16_777_216)
                return new RustAxisProposalResult(null, "INVALID_INPUT");
            var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);
            byte[] pixels = new byte[checked(gray.PixelWidth * gray.PixelHeight)];
            gray.CopyPixels(pixels, gray.PixelWidth, 0);
            return await RustAxisProposal.RunAsync(workerPath, gray.PixelWidth, gray.PixelHeight,
                pixels, cancellationToken: cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}
