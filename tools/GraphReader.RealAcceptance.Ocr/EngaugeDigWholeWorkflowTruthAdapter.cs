// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using GraphReader.Export;

namespace GraphReader.RealAcceptance.Ocr;

internal sealed record EngaugeDigAxisAnchor(
    double ScreenX,
    double ScreenY,
    double GraphX,
    double GraphY);

internal sealed record EngaugeDigCurvePoint(
    string PointKey,
    double ScreenX,
    double ScreenY,
    double GraphX,
    double GraphY);

internal sealed record EngaugeDigCurve(
    string CurveKey,
    IReadOnlyList<EngaugeDigCurvePoint> Points);

internal sealed class EngaugeDigTruthException : InvalidOperationException
{
    internal EngaugeDigTruthException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        int detailSeparator = message.IndexOf(':', StringComparison.Ordinal);
        Code = detailSeparator < 0 ? message : message[..detailSeparator];
    }

    internal string Code { get; }
}

internal sealed class EngaugeDigWholeWorkflowTruth
{
    private readonly byte[] imageBytes;

    internal EngaugeDigWholeWorkflowTruth(
        string projectSha256,
        string sourceImageSha256,
        int sourceWidth,
        int sourceHeight,
        byte[] imageBytes,
        IReadOnlyList<EngaugeDigAxisAnchor> anchors,
        IReadOnlyList<EngaugeDigCurve> curves,
        WholeWorkflowTruthCase truthCase)
    {
        ProjectSha256 = projectSha256;
        SourceImageSha256 = sourceImageSha256;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        this.imageBytes = (byte[])imageBytes.Clone();
        Anchors = Array.AsReadOnly(anchors.ToArray());
        Curves = Array.AsReadOnly(curves.ToArray());
        TruthCase = truthCase;
    }

    internal string ProjectSha256 { get; }

    internal string SourceImageSha256 { get; }

    internal int SourceWidth { get; }

    internal int SourceHeight { get; }

    internal IReadOnlyList<EngaugeDigAxisAnchor> Anchors { get; }

    internal IReadOnlyList<EngaugeDigCurve> Curves { get; }

    internal WholeWorkflowTruthCase TruthCase { get; }

    internal byte[] CopyImageBytes() => (byte[])imageBytes.Clone();
}

/// <summary>
/// Reads evaluator-only truth from an Engauge project. No curve role, relation,
/// or phase is inferred from names. Those metrics remain explicitly unavailable.
/// </summary>
internal static class EngaugeDigWholeWorkflowTruthAdapter
{
    internal const long MaximumProjectBytes = 64L * 1024 * 1024;
    internal const int MaximumEmbeddedImageBytes = 64 * 1024 * 1024;
    private const long MaximumSourcePixels = 40_000_000;
    private const int MaximumPngChunkBytes = 64 * 1024 * 1024;
    private const int MaximumPngChunkCount = 100_000;

    private static readonly HashSet<string> PointElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Point", "DataPoint", "CurvePoint", "Coordinate",
    };

    internal static EngaugeDigWholeWorkflowTruth Read(
        string path,
        string caseKey,
        CancellationToken cancellationToken) =>
        ReadCore(path, caseKey, beforeFirstPayloadRead: null, cancellationToken);

    internal static EngaugeDigWholeWorkflowTruth Read(
        string path,
        string caseKey,
        Action beforeFirstPayloadRead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforeFirstPayloadRead);
        return ReadCore(path, caseKey, beforeFirstPayloadRead, cancellationToken);
    }

    private static EngaugeDigWholeWorkflowTruth ReadCore(
        string path,
        string caseKey,
        Action? beforeFirstPayloadRead,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(caseKey);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumProjectBytes)
        {
            throw new EngaugeDigTruthException("DIG_PROJECT_SIZE_UNSUPPORTED");
        }

        string projectSha256 = Hash(stream, beforeFirstPayloadRead, cancellationToken);
        stream.Position = 0;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            IgnoreWhitespace = false,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaximumProjectBytes,
            CloseInput = false,
        };
        XDocument document;
        try
        {
            using XmlReader reader = XmlReader.Create(stream, settings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException exception)
        {
            throw new EngaugeDigTruthException("DIG_XML_INVALID", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        XElement root = document.Root ?? throw new EngaugeDigTruthException("DIG_ROOT_MISSING");
        RejectDetectedNonlinearScale(root);
        (byte[] imageBytes, int width, int height) = ReadEmbeddedPng(root, cancellationToken);
        string imageSha256 = Convert.ToHexStringLower(SHA256.HashData(imageBytes));

        XElement[] curves = root.DescendantsAndSelf()
            .Where(static element => LocalName(element).Equals("Curve", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (curves.Length == 0)
        {
            throw new EngaugeDigTruthException("DIG_CURVES_MISSING");
        }
        if (curves.Any(curve => curve.Ancestors().Any(ancestor =>
                LocalName(ancestor).Equals("Curve", StringComparison.OrdinalIgnoreCase))))
        {
            throw new EngaugeDigTruthException("DIG_NESTED_CURVE_UNSUPPORTED");
        }

        var curveNames = new HashSet<string>(StringComparer.Ordinal);
        var curveElements = new List<(string Key, XElement Element, XElement[] Points)>(curves.Length);
        foreach (XElement curve in curves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string curveKey = CurveIdentity(curve);
            if (!curveNames.Add(curveKey))
            {
                throw new EngaugeDigTruthException("DIG_CURVE_IDENTITY_DUPLICATE");
            }
            XElement[] points = curve.Descendants()
                .Where(static element => PointElementNames.Contains(LocalName(element)))
                .ToArray();
            if (points.Length == 0)
            {
                throw new EngaugeDigTruthException("DIG_CURVE_POINTS_MISSING");
            }
            if (points.Any(point => point.Ancestors().TakeWhile(ancestor => ancestor != curve)
                    .Any(ancestor => PointElementNames.Contains(LocalName(ancestor)))))
            {
                throw new EngaugeDigTruthException("DIG_NESTED_POINT_UNSUPPORTED");
            }
            curveElements.Add((curveKey, curve, points));
        }

        var pointOwners = curveElements
            .SelectMany(curve => curve.Points.Select(point => (Point: point, Curve: curve.Element)))
            .ToDictionary(static item => item.Point, static item => item.Curve);
        XElement[] allPointElements = root.DescendantsAndSelf()
            .Where(static element => PointElementNames.Contains(LocalName(element)))
            .ToArray();
        var anchors = new List<EngaugeDigAxisAnchor>(3);
        foreach (XElement point in allPointElements.Where(point => !pointOwners.ContainsKey(point)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            (double X, double Y) screen = RequiredUniquePair(point, "PositionScreen", "DIG_AXIS_SCREEN_POSITION");
            (double X, double Y) graph = RequiredUniquePair(point, "PositionGraph", "DIG_AXIS_GRAPH_POSITION");
            anchors.Add(new EngaugeDigAxisAnchor(screen.X, screen.Y, graph.X, graph.Y));
        }
        if (anchors.Count != 3)
        {
            throw new EngaugeDigTruthException($"DIG_AXIS_ANCHOR_COUNT:{anchors.Count}");
        }
        ValidateAnchorGeometry(anchors);
        foreach (EngaugeDigAxisAnchor anchor in anchors)
        {
            ValidateSourcePoint(anchor.ScreenX, anchor.ScreenY, width, height, "DIG_AXIS_SCREEN_OUTSIDE_IMAGE");
        }

        var truthCurves = new List<EngaugeDigCurve>(curveElements.Count);
        var truthSeries = new List<WholeWorkflowTruthSeries>(curveElements.Count);
        var truthPoints = new List<WholeWorkflowTruthPoint>();
        int curveIndex = 0;
        foreach ((string curveKey, _, XElement[] pointElements) in curveElements)
        {
            var points = new List<EngaugeDigCurvePoint>(pointElements.Length);
            for (var pointIndex = 0; pointIndex < pointElements.Length; pointIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                XElement point = pointElements[pointIndex];
                (double X, double Y) screen = RequiredUniquePair(
                    point, "PositionScreen", "DIG_CURVE_SCREEN_POSITION");
                if (Descendants(point, "PositionGraph").Length != 0)
                {
                    throw new EngaugeDigTruthException("DIG_CURVE_GRAPH_POSITION_AMBIGUOUS");
                }
                ValidateSourcePoint(screen.X, screen.Y, width, height, "DIG_CURVE_POINT_OUTSIDE_IMAGE");
                (double graphX, double graphY) = MapToGraph(anchors, screen.X, screen.Y);
                string pointKey = FormattableString.Invariant(
                    $"curve-{curveIndex:D4}-point-{pointIndex:D6}");
                points.Add(new EngaugeDigCurvePoint(
                    pointKey, screen.X, screen.Y, graphX, graphY));
                truthPoints.Add(new WholeWorkflowTruthPoint(
                    pointKey,
                    curveKey,
                    screen.X,
                    screen.Y,
                    graphX,
                    graphY,
                    Math.Round(graphX, MidpointRounding.ToEven),
                    ExportMode.PrintedSession,
                    AuthoritativePhaseCode: null));
            }
            truthCurves.Add(new EngaugeDigCurve(curveKey, Array.AsReadOnly(points.ToArray())));
            truthSeries.Add(new WholeWorkflowTruthSeries(curveKey));
            curveIndex++;
        }
        if (anchors.Count + truthPoints.Count != allPointElements.Length)
        {
            throw new EngaugeDigTruthException("DIG_POINT_INVENTORY_MISMATCH");
        }

        var truthCase = new WholeWorkflowTruthCase(
            caseKey,
            imageSha256,
            width,
            height,
            Array.AsReadOnly(truthSeries.ToArray()),
            Array.AsReadOnly(truthPoints.ToArray()),
            Relations: null);
        return new EngaugeDigWholeWorkflowTruth(
            projectSha256,
            imageSha256,
            width,
            height,
            imageBytes,
            Array.AsReadOnly(anchors.ToArray()),
            Array.AsReadOnly(truthCurves.ToArray()),
            truthCase);
    }

    private static string Hash(
        Stream stream,
        Action? beforeFirstPayloadRead,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        cancellationToken.ThrowIfCancellationRequested();
        beforeFirstPayloadRead?.Invoke();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string CurveIdentity(XElement curve)
    {
        XAttribute[] names = curve.Attributes()
            .Where(static attribute => LocalName(attribute).Equals("Name", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (names.Length != 1 || string.IsNullOrWhiteSpace(names[0].Value) ||
            !string.Equals(names[0].Value, names[0].Value.Trim(), StringComparison.Ordinal))
        {
            throw new EngaugeDigTruthException("DIG_CURVE_IDENTITY_INVALID");
        }
        return names[0].Value;
    }

    private static (double X, double Y) RequiredUniquePair(
        XElement parent,
        string childName,
        string failureCode)
    {
        XElement[] matches = Descendants(parent, childName);
        if (matches.Length != 1)
        {
            throw new EngaugeDigTruthException(failureCode);
        }
        XElement value = matches[0];
        double x = RequiredFiniteAttribute(value, "X", failureCode);
        double y = RequiredFiniteAttribute(value, "Y", failureCode);
        return (x, y);
    }

    private static XElement[] Descendants(XElement parent, string name) =>
        parent.Descendants().Where(element =>
            LocalName(element).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();

    private static double RequiredFiniteAttribute(XElement element, string name, string failureCode)
    {
        XAttribute[] attributes = element.Attributes().Where(attribute =>
            LocalName(attribute).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (attributes.Length != 1 ||
            !double.TryParse(attributes[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value))
        {
            throw new EngaugeDigTruthException(failureCode);
        }
        return value;
    }

    private static void ValidateAnchorGeometry(List<EngaugeDigAxisAnchor> anchors)
    {
        double screenDeterminant = Determinant(
            anchors[0].ScreenX, anchors[0].ScreenY,
            anchors[1].ScreenX, anchors[1].ScreenY,
            anchors[2].ScreenX, anchors[2].ScreenY);
        double graphDeterminant = Determinant(
            anchors[0].GraphX, anchors[0].GraphY,
            anchors[1].GraphX, anchors[1].GraphY,
            anchors[2].GraphX, anchors[2].GraphY);
        if (NearlyDegenerate(
                screenDeterminant,
                anchors[0].ScreenX, anchors[0].ScreenY,
                anchors[1].ScreenX, anchors[1].ScreenY,
                anchors[2].ScreenX, anchors[2].ScreenY) ||
            NearlyDegenerate(
                graphDeterminant,
                anchors[0].GraphX, anchors[0].GraphY,
                anchors[1].GraphX, anchors[1].GraphY,
                anchors[2].GraphX, anchors[2].GraphY))
        {
            throw new EngaugeDigTruthException("DIG_AXIS_ANCHORS_DEGENERATE");
        }
    }

    private static bool NearlyDegenerate(
        double determinant,
        double x1,
        double y1,
        double x2,
        double y2,
        double x3,
        double y3)
    {
        double edge12 = SquaredDistance(x1, y1, x2, y2);
        double edge13 = SquaredDistance(x1, y1, x3, y3);
        double edge23 = SquaredDistance(x2, y2, x3, y3);
        double squaredScale = Math.Max(1, Math.Max(edge12, Math.Max(edge13, edge23)));
        return !double.IsFinite(determinant) || !double.IsFinite(squaredScale) ||
            Math.Abs(determinant) <= 1e-12 * squaredScale;
    }

    private static double SquaredDistance(double x1, double y1, double x2, double y2) =>
        ((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1));

    private static double Determinant(
        double x1, double y1, double x2, double y2, double x3, double y3) =>
        ((x2 - x1) * (y3 - y1)) - ((x3 - x1) * (y2 - y1));

    private static (double X, double Y) MapToGraph(
        List<EngaugeDigAxisAnchor> anchors,
        double screenX,
        double screenY)
    {
        EngaugeDigAxisAnchor first = anchors[0];
        EngaugeDigAxisAnchor second = anchors[1];
        EngaugeDigAxisAnchor third = anchors[2];
        double determinant = Determinant(
            first.ScreenX, first.ScreenY,
            second.ScreenX, second.ScreenY,
            third.ScreenX, third.ScreenY);
        double offsetX = screenX - first.ScreenX;
        double offsetY = screenY - first.ScreenY;
        double secondWeight = ((offsetX * (third.ScreenY - first.ScreenY)) -
            (offsetY * (third.ScreenX - first.ScreenX))) / determinant;
        double thirdWeight = (((second.ScreenX - first.ScreenX) * offsetY) -
            ((second.ScreenY - first.ScreenY) * offsetX)) / determinant;
        double graphX = first.GraphX +
            (secondWeight * (second.GraphX - first.GraphX)) +
            (thirdWeight * (third.GraphX - first.GraphX));
        double graphY = first.GraphY +
            (secondWeight * (second.GraphY - first.GraphY)) +
            (thirdWeight * (third.GraphY - first.GraphY));
        if (!double.IsFinite(graphX) || !double.IsFinite(graphY))
        {
            throw new EngaugeDigTruthException("DIG_GRAPH_COORDINATE_INVALID");
        }
        return (graphX, graphY);
    }

    private static void ValidateSourcePoint(
        double x,
        double y,
        int width,
        int height,
        string failureCode)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > width || y > height)
        {
            throw new EngaugeDigTruthException(failureCode);
        }
    }

    private static void RejectDetectedNonlinearScale(XElement root)
    {
        bool logarithmic = root.DescendantsAndSelf().Any(element =>
            element.Attributes().Any(attribute =>
                LocalName(attribute).Contains("Scale", StringComparison.OrdinalIgnoreCase) &&
                attribute.Value.Contains("Log", StringComparison.OrdinalIgnoreCase)) ||
            (LocalName(element).Contains("Scale", StringComparison.OrdinalIgnoreCase) &&
             element.Value.Trim().Contains("Log", StringComparison.OrdinalIgnoreCase)));
        if (logarithmic)
        {
            throw new EngaugeDigTruthException("DIG_NONLINEAR_SCALE_UNSUPPORTED");
        }
    }

    private static (byte[] Bytes, int Width, int Height) ReadEmbeddedPng(
        XElement root,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        XElement[] candidates = root.DescendantsAndSelf().Where(element =>
        {
            string name = LocalName(element);
            if (!name.Contains("Image", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("Raster", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return element.Nodes().OfType<XText>().Any(static node => !string.IsNullOrWhiteSpace(node.Value));
        }).ToArray();
        if (candidates.Length != 1)
        {
            throw new EngaugeDigTruthException("DIG_IMAGE_COUNT_UNSUPPORTED");
        }
        string encoded = string.Concat(candidates[0].Nodes().OfType<XText>().Select(static node => node.Value));
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new EngaugeDigTruthException("DIG_IMAGE_BASE64_INVALID", exception);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > 4 && !bytes.AsSpan().StartsWith(PngSignature) &&
            bytes.AsSpan(4).StartsWith(PngSignature))
        {
            bytes = bytes[4..];
        }
        if (bytes.Length is <= 0 or > MaximumEmbeddedImageBytes ||
            bytes.Length < 24 || !bytes.AsSpan().StartsWith(PngSignature) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
            ReadBigEndianInt32(bytes.AsSpan(8, 4)) != 13)
        {
            throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
        }
        int width = ReadBigEndianInt32(bytes.AsSpan(16, 4));
        int height = ReadBigEndianInt32(bytes.AsSpan(20, 4));
        if (width <= 0 || height <= 0)
        {
            throw new EngaugeDigTruthException("DIG_IMAGE_DIMENSIONS_INVALID");
        }
        if ((long)width * height > MaximumSourcePixels)
        {
            throw new EngaugeDigTruthException("DIG_IMAGE_PIXEL_COUNT_UNSUPPORTED");
        }
        ValidateDeclaredImageDimension(candidates[0], "Width", width);
        ValidateDeclaredImageDimension(candidates[0], "Height", height);
        ValidateDeclaredPngFormat(candidates[0]);
        ValidatePngContainer(bytes, width, height, cancellationToken);
        return (bytes, width, height);
    }

    private static void ValidateDeclaredPngFormat(XElement image)
    {
        XAttribute[] declarations = image.Attributes().Where(attribute =>
            LocalName(attribute).Equals("Format", StringComparison.OrdinalIgnoreCase) ||
            LocalName(attribute).Equals("Mime", StringComparison.OrdinalIgnoreCase) ||
            LocalName(attribute).Equals("MimeType", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (declarations.Length == 0)
        {
            return;
        }
        if (declarations.Length != 1 ||
            !(declarations[0].Value.Equals("PNG", StringComparison.OrdinalIgnoreCase) ||
              declarations[0].Value.Equals("image/png", StringComparison.OrdinalIgnoreCase)))
        {
            throw new EngaugeDigTruthException("DIG_IMAGE_FORMAT_MISMATCH");
        }
    }

    private static void ValidatePngContainer(
        byte[] bytes,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        // This validates bounded PNG framing, chunk order, dimensions, and CRCs.
        // The workflow runner must still decode the image under its bounded raster
        // policy before treating these bytes as an inference input.
        int offset = PngSignature.Length;
        int chunkCount = 0;
        bool sawHeader = false;
        bool sawImageData = false;
        bool imageDataEnded = false;
        long imageDataBytes = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunkCount++;
            if (chunkCount > MaximumPngChunkCount || bytes.Length - offset < 12)
            {
                throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
            }
            uint unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (unsignedLength > int.MaxValue || unsignedLength > MaximumPngChunkBytes)
            {
                throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
            }
            int chunkLength = (int)unsignedLength;
            long chunkEnd = (long)offset + 12 + chunkLength;
            if (chunkEnd > bytes.Length)
            {
                throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
            }

            int typeOffset = offset + 4;
            int dataOffset = typeOffset + 4;
            int crcOffset = dataOffset + chunkLength;
            ReadOnlySpan<byte> chunkType = bytes.AsSpan(typeOffset, 4);
            uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(crcOffset, 4));
            uint computedCrc = ComputePngCrc(
                bytes.AsSpan(typeOffset, chunkLength + 4), cancellationToken);
            if (storedCrc != computedCrc)
            {
                throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
            }

            if (chunkType.SequenceEqual("IHDR"u8))
            {
                if (sawHeader || chunkCount != 1 || chunkLength != 13 ||
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset, 4)) != (uint)width ||
                    BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataOffset + 4, 4)) != (uint)height)
                {
                    throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
                }
                sawHeader = true;
            }
            else if (chunkType.SequenceEqual("IDAT"u8))
            {
                if (!sawHeader || imageDataEnded)
                {
                    throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
                }
                sawImageData = true;
                imageDataBytes += chunkLength;
            }
            else if (chunkType.SequenceEqual("IEND"u8))
            {
                if (!sawHeader || !sawImageData || imageDataBytes == 0 || chunkLength != 0 ||
                    chunkEnd != bytes.Length)
                {
                    throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
                }
                return;
            }
            else
            {
                if (!sawHeader)
                {
                    throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
                }
                imageDataEnded = sawImageData;
            }
            offset = (int)chunkEnd;
        }
        throw new EngaugeDigTruthException("DIG_IMAGE_PNG_INVALID");
    }

    private static uint ComputePngCrc(
        ReadOnlySpan<byte> data,
        CancellationToken cancellationToken)
    {
        uint crc = uint.MaxValue;
        for (var index = 0; index < data.Length; index++)
        {
            if ((index & 0xffff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            crc ^= data[index];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320u & unchecked((uint)-(int)(crc & 1)));
            }
        }
        return crc ^ uint.MaxValue;
    }

    private static void ValidateDeclaredImageDimension(
        XElement image,
        string name,
        int detectedValue)
    {
        XAttribute[] declarations = image.Attributes().Where(attribute =>
            LocalName(attribute).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (declarations.Length == 0)
        {
            return;
        }
        if (declarations.Length != 1 ||
            !int.TryParse(declarations[0].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out int declaredValue) ||
            declaredValue != detectedValue)
        {
            throw new EngaugeDigTruthException("DIG_IMAGE_DIMENSION_MISMATCH");
        }
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static string LocalName(XObject value) => value switch
    {
        XElement element => element.Name.LocalName,
        XAttribute attribute => attribute.Name.LocalName,
        _ => string.Empty,
    };

    private static ReadOnlySpan<byte> PngSignature =>
        [137, 80, 78, 71, 13, 10, 26, 10];
}
