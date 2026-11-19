// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.Ocr;

namespace GraphReader.App.Integration.Workflow;

internal static class ProductionAttachedTickStroke
{
    internal static List<AttachedTickStroke> Find(AxisGeometryResult axis, OcrImage image, bool horizontal,
        int threshold, AxisGeometryOptions options, CancellationToken token)
    {
        var line = horizontal ? axis.XAxis.Line : axis.YAxis.Line;
        var crossAxis = horizontal ? axis.YAxis.Line : axis.XAxis.Line;
        return AttachedTickStroke.Find(image, horizontal, threshold,
            horizontal ? line.Start.X : line.Start.Y, horizontal ? line.End.X : line.End.Y,
            horizontal ? line.Start.Y : line.Start.X, horizontal ? line.End.Y : line.End.X,
            (int)Math.Ceiling(crossAxis.Length * options.TickMaximumLengthFraction), token);
    }
}
