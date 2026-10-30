// SPDX-License-Identifier: Apache-2.0
// Synthetic-only writer harness linking the actual C# archive implementation.
using System.Text.Json;
using GraphReader.Markers.Detection;
using GraphReader.RealAcceptance.Ocr;

if (args.Length != 2) throw new ArgumentException("Expected project root and fresh fixture path");
byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
int patchValues = 3 * 33 * 33;
var first = new EngaugeDevPositiveProposalBatch(
    new string('a', 64), Enumerable.Repeat(0.25f, patchValues).Concat(Enumerable.Repeat(0.75f, patchValues)).ToArray(),
    [new MarkerPoint(350.5, 22.0), new MarkerPoint(400.0, 23.0)]);
var empty = new EngaugeDevPositiveProposalBatch(new string('b', 64), Array.Empty<float>(), []);
using var archive = new EngaugeDevProposalArchive(args[0], args[1], key);
await archive.ConsumeAsync(first, CancellationToken.None);
await archive.ConsumeAsync(empty, CancellationToken.None);
var result = new EngaugeDevProposalPreparationResult(
    new string('1', 64), new string('2', 64), new string('3', 64), new string('4', 64),
    new string('5', 64), new string('6', 64), new string('7', 64), new string('8', 64), 2, 2, 2, true, false, 0, 0);
EngaugeDevProposalArchiveReceipt receipt = archive.Complete(result, CancellationToken.None);
Console.WriteLine(JsonSerializer.Serialize(receipt));

// Minimal type definitions keep this harness independent of real inputs and
// dependencies while compiling and executing the actual archive writer source.
namespace GraphReader.Markers.Detection
{
    internal static class NormalizedMarkerProposalContract { public const int ChannelCount = 3; public const int PatchSize = 33; }
    internal sealed record MarkerPoint(double X, double Y);
}

namespace GraphReader.RealAcceptance.Ocr
{
    internal sealed record EngaugeDevPositiveProposalBatch(string ImageSha256, ReadOnlyMemory<float> Tensor, IReadOnlyList<MarkerPoint> Coordinates);
    internal sealed record EngaugeDevProposalPreparationResult(string BindingSha256, string ProtocolSha256, string AssignmentSha256,
        string SelectedInventorySha256, string OcrDetectionSha256, string OcrRecognitionSha256, string MarkerCenterSha256,
        string MarkerClassifierSha256, int ProcessedTrainGroupCount, int TrainGroupCount, long PositiveProposalCount,
        bool AggregateOnly, bool ProductionApproved, int OptimizerSteps, int SealedReads);
}
