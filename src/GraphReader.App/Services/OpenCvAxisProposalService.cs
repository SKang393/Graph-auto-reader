// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GraphReader.App.Integration.NativeAxis;
using GraphReader.Axis;

namespace GraphReader.App.Services;

/// <summary>Runs a bounded, review-only native geometry proposal in an isolated process.</summary>
public sealed class OpenCvAxisProposalService : IAxisProposalService, IAxisCandidateReviewService
{
    private static readonly string[] CandidateResultFields = ["Candidates", "ErrorCode"];
    private static readonly string[] CandidateResultFieldsWithCompletion = ["Candidates", "ErrorCode", "EnumerationCompleted"];
    private static readonly string[] CandidateFields = ["Index", "Geometry"];
    private static readonly string[] AffineCandidateFields = ["Index", "Geometry", "AffineGeometry"];
    private static readonly string[] AffineGeometryFields = ["Origin", "XEnd", "YEnd", "Confidence"];
    private static readonly string[] AffinePointFields = ["X", "Y"];
    private static readonly string[] CandidateGeometryFields = ["Left", "Bottom", "Right", "Top", "Confidence", "FirstTickX", "LastTickX", "XTickPositions", "YTickPositions"];
    private readonly string workerPath;
    public OpenCvAxisProposalService(string workerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        if (!Path.IsPathFullyQualified(workerPath)) throw new ArgumentException("An absolute worker path is required.", nameof(workerPath));
        this.workerPath = Path.GetFullPath(workerPath);
    }

    public Task<RustAxisProposalResult> SuggestAsync(byte[] originalImage, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<RustAxisProposalResult>(cancellationToken);
        if (originalImage is null || originalImage.Length == 0 || originalImage.Length > NativeAxisWorker.MaximumImageBytes)
            return Task.FromResult(Failure("INVALID_INPUT"));
        byte[] snapshot = (byte[])originalImage.Clone();
        return Task.Run(() => RunAsync(snapshot, cancellationToken), CancellationToken.None);
    }

    public Task<AxisCandidateReviewResult> ReviewCandidatesAsync(byte[] originalImage, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<AxisCandidateReviewResult>(cancellationToken);
        if (originalImage is null || originalImage.Length == 0 || originalImage.Length > NativeAxisWorker.MaximumImageBytes)
            return Task.FromResult(CandidateFailure("INVALID_INPUT"));
        byte[] snapshot = (byte[])originalImage.Clone();
        return Task.Run(() => RunCandidatesAsync(snapshot, cancellationToken), CancellationToken.None);
    }

    private async Task<AxisCandidateReviewResult> RunCandidatesAsync(byte[] image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = new ProcessStartInfo(workerPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--native-axis-candidates-worker");
        try
        {
            using var input = new MemoryStream(image, writable: false);
            BitmapFrame frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216) return CandidateFailure("INVALID_INPUT");
            if (!process.Start()) return CandidateFailure("OPENCV_AXIS_UNAVAILABLE");
            using var stop = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            Task drain = DrainAsync(process.StandardError.BaseStream);
            Task<AxisCandidateReviewResult?> read = ReadCandidateResponseAsync(process.StandardOutput.BaseStream, deadline.Token);
            byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, image.Length);
            await process.StandardInput.BaseStream.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(image, deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            AxisCandidateReviewResult? result = await read.ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            byte[] extra = new byte[1];
            if (await process.StandardOutput.BaseStream.ReadAsync(extra, deadline.Token).ConfigureAwait(false) != 0 ||
                process.ExitCode != 0 || result?.Provider != "opencv-review" || !ValidCandidates(result, width, height)) return CandidateFailure("OPENCV_AXIS_UNAVAILABLE");
            return new AxisCandidateReviewResult(Array.AsReadOnly(result!.Candidates.ToArray()), result.ErrorCode)
                { EnumerationCompleted = result.EnumerationCompleted };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return CandidateFailure("OPENCV_AXIS_TIMEOUT"); }
        catch (Exception) { cancellationToken.ThrowIfCancellationRequested(); return CandidateFailure("OPENCV_AXIS_UNAVAILABLE"); }
        finally
        {
            try { if (!process.HasExited) process.Kill(true); _ = process.WaitForExit(1000); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    internal static async Task<AxisCandidateReviewResult?> ReadCandidateResponseAsync(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > NativeAxisCandidateReview.MaximumResponseBytes) throw new InvalidDataException();
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (!(HasExactProperties(root, CandidateResultFields) || HasExactProperties(root, CandidateResultFieldsWithCompletion)) ||
            (root.TryGetProperty("EnumerationCompleted", out JsonElement completed) && completed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) ||
            root.GetProperty("Candidates").ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        foreach (JsonElement candidate in root.GetProperty("Candidates").EnumerateArray())
        {
            bool rectangle = HasExactProperties(candidate, CandidateFields) &&
                HasExactProperties(candidate.GetProperty("Geometry"), CandidateGeometryFields);
            bool affine = HasExactProperties(candidate, AffineCandidateFields) &&
                candidate.GetProperty("Geometry").ValueKind == JsonValueKind.Null &&
                HasExactProperties(candidate.GetProperty("AffineGeometry"), AffineGeometryFields) &&
                HasExactProperties(candidate.GetProperty("AffineGeometry").GetProperty("Origin"), AffinePointFields) &&
                HasExactProperties(candidate.GetProperty("AffineGeometry").GetProperty("XEnd"), AffinePointFields) &&
                HasExactProperties(candidate.GetProperty("AffineGeometry").GetProperty("YEnd"), AffinePointFields);
            if (!rectangle && !affine)
                throw new InvalidDataException();
        }
        return JsonSerializer.Deserialize<AxisCandidateReviewResult>(bytes);
    }

    private static bool HasExactProperties(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        string[] properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        return properties.Length == expected.Length && properties.Distinct(StringComparer.Ordinal).Count() == expected.Length &&
            expected.All(name => properties.Contains(name, StringComparer.Ordinal));
    }

    internal static bool ValidCandidates(AxisCandidateReviewResult? result, int width, int height)
    {
        if (result?.Candidates is null || result.Provider is not ("opencv-review" or "rust-review") || result.Candidates.Count > NativeAxisCandidateReview.MaximumCandidates) return false;
        if (!result.EnumerationCompleted && (result.Provider != "opencv-review" || result.ErrorCode is not null || result.Candidates.Count == 0)) return false;
        if (result.ErrorCode is not null)
            return result.Candidates.Count == 0 && result.ErrorCode is "OPENCV_AXIS_UNAVAILABLE" or "OPENCV_AXIS_AMBIGUOUS" or "INVALID_INPUT";
        if (result.Candidates.Count == 0) return false;
        var distinct = new HashSet<(int Left, int Bottom, int Right, int Top)>();
        var distinctAffine = new HashSet<RustAffineAxisGeometry>();
        for (int i = 0; i < result.Candidates.Count; i++)
        {
            AxisReviewCandidate? candidate = result.Candidates[i];
            if (candidate is null || candidate.Index != i) return false;
            if (candidate.AffineGeometry is { } affine)
            {
                if (candidate.Geometry is not null || result.Provider != "opencv-review" || affine.Confidence != 0 ||
                    !ValidAffineGeometry(affine, width, height) || !distinctAffine.Add(affine)) return false;
                continue;
            }
            if (candidate.Geometry is not { } geometry || geometry.Confidence != 0 ||
                !distinct.Add((geometry.Left, geometry.Bottom, geometry.Right, geometry.Top)) ||
                geometry.Left < 0 || geometry.Top < 0 || geometry.Right >= width || geometry.Bottom >= height ||
                geometry.Left >= geometry.Right || geometry.Top >= geometry.Bottom) return false;
            if (result.Provider == "opencv-review")
            {
                if (geometry.FirstTickX is not null || geometry.LastTickX is not null ||
                    geometry.XTickPositions is not { Count: 0 } || geometry.YTickPositions is not { Count: 0 }) return false;
            }
            else if (!ValidReviewTicks(geometry.XTickPositions, geometry.Left, geometry.Right) ||
                !ValidReviewTicks(geometry.YTickPositions, geometry.Top, geometry.Bottom) ||
                geometry.FirstTickX != geometry.XTickPositions[0] || geometry.LastTickX != geometry.XTickPositions[^1]) return false;
        }
        return true;
    }

    private static bool ValidReviewTicks(IReadOnlyList<int>? ticks, int minimum, int maximum)
    {
        if (ticks is null || ticks.Count is < 3 or > 128) return false;
        for (int index = 0; index < ticks.Count; index++)
            if (ticks[index] < minimum || ticks[index] > maximum || (index > 0 && ticks[index] <= ticks[index - 1])) return false;
        return true;
    }

    private static AxisCandidateReviewResult CandidateFailure(string code) => new(Array.Empty<AxisReviewCandidate>(), code);

    private async Task<RustAxisProposalResult> RunAsync(byte[] originalImage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (originalImage is null || originalImage.Length == 0 || originalImage.Length > NativeAxisWorker.MaximumImageBytes) return Failure("INVALID_INPUT");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process { StartInfo = new ProcessStartInfo(workerPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--native-axis-worker");
        try
        {
            using var input = new MemoryStream(originalImage, writable: false);
            BitmapFrame frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            if (width is < 32 or > 4096 || height is < 32 or > 4096 || (long)width * height > 16_777_216) return Failure("INVALID_INPUT");
            if (!process.Start()) return Failure("OPENCV_AXIS_UNAVAILABLE");
            using var stop = deadline.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            Task drain = DrainAsync(process.StandardError.BaseStream);
            Task<RustAxisProposalResult?> read = ReadResponseAsync(process.StandardOutput.BaseStream, deadline.Token);
            byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, originalImage.Length);
            await process.StandardInput.BaseStream.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(originalImage, deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            RustAxisProposalResult? result = await read.ConfigureAwait(false);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await drain.ConfigureAwait(false);
            byte[] extra = new byte[1];
            if (await process.StandardOutput.BaseStream.ReadAsync(extra, deadline.Token).ConfigureAwait(false) != 0 ||
                process.ExitCode != 0 || !Valid(result, width, height)) return Failure("OPENCV_AXIS_UNAVAILABLE");
            return result!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("OPENCV_AXIS_TIMEOUT"); }
        catch (Exception)
        { cancellationToken.ThrowIfCancellationRequested(); return Failure("OPENCV_AXIS_UNAVAILABLE"); }
        finally
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
                _ = process.WaitForExit(1000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
    private static async Task DrainAsync(Stream stream) { byte[] buffer = new byte[1024]; try { while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0) { } } catch (IOException) { } }
    private static async Task<RustAxisProposalResult?> ReadResponseAsync(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > NativeAxisWorker.MaximumResponseBytes) throw new InvalidDataException();
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<RustAxisProposalResult>(bytes);
    }
    internal static bool Valid(RustAxisProposalResult? result, int width, int height)
    {
        if (result is null || result.Provider != "opencv-review" || result.ReviewReasons is null || result.ReviewReasons.Count > 32 ||
            result.ReviewReasons.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128)) return false;
        if (result.AffineGeometry is { } affine)
            return result.Geometry is null && result.ErrorCode is null &&
                result.ReviewReasons.Contains("native_affine_geometry", StringComparer.Ordinal) && ValidAffineGeometry(affine, width, height);
        if (result.Geometry is null) return result.ErrorCode is "OPENCV_AXIS_UNAVAILABLE" or "OPENCV_AXIS_AMBIGUOUS" or "INVALID_INPUT";
        RustAxisGeometry g = result.Geometry;
        return result.ErrorCode is null && g.Left >= 0 && g.Top >= 0 && g.Right < width && g.Bottom < height && g.Left < g.Right && g.Top < g.Bottom &&
            double.IsFinite(g.Confidence) && g.Confidence is >= 0 and <= 1 && g.FirstTickX is null && g.LastTickX is null &&
            g.XTickPositions is { Count: 0 } && g.YTickPositions is { Count: 0 };
    }
    internal static bool ValidAffineGeometry(RustAffineAxisGeometry? frame, int width, int height)
    {
        if (frame?.Origin is not { } origin || frame.XEnd is not { } xEnd || frame.YEnd is not { } yEnd ||
            width <= 0 || height <= 0 || !double.IsFinite(frame.Confidence) || frame.Confidence is < 0 or > 1) return false;
        bool InFrame(double x, double y) => double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0 && x < width && y < height;
        if (!InFrame(origin.X, origin.Y) || !InFrame(xEnd.X, xEnd.Y) || !InFrame(yEnd.X, yEnd.Y)) return false;
        double fourthX = xEnd.X + yEnd.X - origin.X, fourthY = xEnd.Y + yEnd.Y - origin.Y;
        double determinant = (xEnd.X - origin.X) * (yEnd.Y - origin.Y) - (xEnd.Y - origin.Y) * (yEnd.X - origin.X);
        return InFrame(fourthX, fourthY) && xEnd.X > origin.X && yEnd.Y < origin.Y && double.IsFinite(determinant) && determinant < 0;
    }

    private static RustAxisProposalResult Failure(string code) => new(null, code) { Provider = "opencv-review" };
}
