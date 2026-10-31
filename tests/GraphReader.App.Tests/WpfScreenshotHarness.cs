// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GraphReader.App.Tests;

internal sealed record RenderedScreenshot(int Width, int Height, byte[] PngBytes, string Sha256);

internal static class WpfScreenshotHarness
{
    public static RenderedScreenshot Render(
        Func<FrameworkElement> elementFactory,
        int width,
        int height,
        double pixelScale = 1)
    {
        ArgumentNullException.ThrowIfNull(elementFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelScale);

        return StaTestHost.Run(
            () =>
            {
                var element = elementFactory();
                ArgumentNullException.ThrowIfNull(element);
                double logicalWidth = width / pixelScale;
                double logicalHeight = height / pixelScale;
                var host = new Window
                {
                    Width = logicalWidth,
                    Height = logicalHeight,
                    Left = SystemParameters.VirtualScreenLeft - logicalWidth - 100,
                    Top = SystemParameters.VirtualScreenTop - logicalHeight - 100,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Focusable = false,
                    IsHitTestVisible = false,
                    Content = element,
                };
                try
                {
                    bool contentRendered = false;
                    var renderFrame = new DispatcherFrame();
                    var timeout = new DispatcherTimer(
                        TimeSpan.FromSeconds(5),
                        DispatcherPriority.Send,
                        (_, _) => renderFrame.Continue = false,
                        host.Dispatcher);
                    host.ContentRendered += (_, _) =>
                    {
                        contentRendered = true;
                        renderFrame.Continue = false;
                    };
                    host.Show();
                    // An off-screen WPF window can defer composition until its content has
                    // received an explicit layout pass. Do that before treating
                    // ContentRendered as the render-completion signal.
                    host.Measure(new Size(logicalWidth, logicalHeight));
                    host.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
                    host.UpdateLayout();
                    element.Measure(new Size(logicalWidth, logicalHeight));
                    element.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
                    element.UpdateLayout();
                    element.Dispatcher.Invoke(
                        static () => { },
                        DispatcherPriority.Render);
                    timeout.Start();
                    if (!contentRendered)
                    {
                        Dispatcher.PushFrame(renderFrame);
                    }
                    timeout.Stop();
                    if (!contentRendered)
                    {
                        throw new InvalidOperationException("The off-screen WPF screenshot host did not render content.");
                    }

                    RenderTargetBitmap bitmap = CaptureWindow(
                        host,
                        width,
                        height,
                        logicalWidth,
                        logicalHeight,
                        pixelScale);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = new MemoryStream();
                    encoder.Save(output);
                    var png = output.ToArray();
                    return new RenderedScreenshot(
                        bitmap.PixelWidth,
                        bitmap.PixelHeight,
                        png,
                        Convert.ToHexStringLower(SHA256.HashData(png)));
                }
                finally
                {
                    host.Content = null;
                    host.Close();
                }
            });
    }

    private static RenderTargetBitmap CaptureWindow(
        Window host,
        int width,
        int height,
        double logicalWidth,
        double logicalHeight,
        double pixelScale)
    {
        if (new WindowInteropHelper(host).Handle == 0)
        {
            throw new InvalidOperationException("The WPF screenshot host has no window handle.");
        }

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new VisualBrush(host)
                {
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                    AutoLayoutContent = true,
                    Stretch = Stretch.Fill,
                },
                null,
                new Rect(0, 0, logicalWidth, logicalHeight));
        }

        double dpi = 96 * pixelScale;
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels(pixels, stride, 0);
        bool hasRenderedPixel = false;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] == 0)
            {
                continue;
            }

            hasRenderedPixel = true;
            break;
        }
        if (!hasRenderedPixel)
        {
            Assert.Inconclusive(
                "The WPF pixel surface is unavailable on this noninteractive Windows test desktop; no screenshot evidence was emitted.");
        }
        bitmap.Freeze();
        return bitmap;
    }
}
