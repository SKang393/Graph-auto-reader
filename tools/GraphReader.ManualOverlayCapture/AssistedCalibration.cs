// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.IO;
using System.Text.Json;
using GraphReader.App.Integration.Workflow;
using Domain = GraphReader.Domain;

namespace GraphReader.RealAcceptance.Ocr;

internal static partial class Program
{
    private const string AssistedBindingSha = "e092e6c352b3bd25ccf59debd72c6df60a5bfe2daf0b6a9b2384f0a8ad19cb9c";

    private sealed record AssistedCalibrationInput(
        string Id, string SourceSha, Guid PanelId, string PanelSha,
        Domain.PixelPoint Origin, Domain.PixelPoint YAnchor, Domain.PixelPoint XAnchor,
        double XMaximum, double YMaximum)
    {
        internal WorkflowManualCalibrationBinding Bind(Guid projectId, Guid sourceId, WorkflowImportedPanel panel, string sourceSha)
        {
            Require(panel.PanelId == PanelId && panel.SourceId == sourceId && sourceSha == SourceSha &&
                string.Equals(panel.Original.Sha256, PanelSha, StringComparison.OrdinalIgnoreCase),
                "Assisted source or panel identity differs.");
            Domain.PixelPoint[] points = [Origin, YAnchor, XAnchor];
            Require(points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
                    point.X >= 0 && point.X <= panel.Original.Width && point.Y >= 0 && point.Y <= panel.Original.Height) &&
                double.IsFinite(XMaximum) && XMaximum > 1 && double.IsFinite(YMaximum) && YMaximum > 0 &&
                YAnchor.Y < Origin.Y && XAnchor.X > Origin.X &&
                Math.Abs(YAnchor.X - Origin.X) <= 2 && Math.Abs(XAnchor.Y - Origin.Y) <= 2,
                "Assisted anchors do not satisfy the existing manual calibration contract.");
            double pitch = (XAnchor.X - Origin.X) / (XMaximum - 1);
            Require(double.IsFinite(pitch) && pitch > 0, "Assisted session pitch is invalid.");
            var calibration = new Domain.CalibrationRecord(Domain.CalibrationId.New(), Domain.CalibrationStatus.Valid,
            [
                new(Domain.CalibrationAnchorKind.Session1Y0, Origin, new Domain.GraphPoint(1, 0), 1, null),
                new(Domain.CalibrationAnchorKind.Session1Ymax, YAnchor, new Domain.GraphPoint(1, YMaximum), 1, null),
                new(Domain.CalibrationAnchorKind.SessionmaxY0, XAnchor, new Domain.GraphPoint(XMaximum, 0), 1, null),
            ], new Domain.SessionLatticeRecord(Origin.X, pitch, null, null, 1, "manual_three_anchor"),
                UserConfirmed: true, Confidence: 1, Reasons: ["training_only_simulated_confirmation"]);
            return new WorkflowManualCalibrationBinding(projectId, sourceId, panel.PanelId, PanelSha, calibration);
        }
    }

    private static async Task<Dictionary<string, AssistedCalibrationInput>> ReadAssistedCalibrations(string path, string expectedSha)
    {
        Require(path == OutputRoot + "/assisted-calibration-bindings-v1.json" && expectedSha == AssistedBindingSha,
            "Assisted calibration input is not the fixed training binding.");
        using JsonDocument document = JsonDocument.Parse(await ReadPinned(path, expectedSha, 100_000));
        JsonElement root = document.RootElement;
        Exact(root, "schema", "scope", "anchors_sha256", "plan_sha256", "capture_sha256", "source_sha256",
            "eligible_images", "excluded_images", "recorded_anchors_are_not_user_confirmation", "production_approval",
            "sealed_payload_reads", "rows");
        Require(root.GetProperty("schema").GetString() == "graphreader.assisted-calibration-bindings.v1" &&
            root.GetProperty("scope").GetString() == "train-only-unapproved" &&
            root.GetProperty("anchors_sha256").GetString() == "899cbd19b7baa39ed00b9882ec3166d83cb80c2d359b4410af39290b601fac12" &&
            root.GetProperty("plan_sha256").GetString() == "34431350ed5e94dce0b2debd481b855ed673fdb7839d669527db397a48b64428" &&
            root.GetProperty("capture_sha256").GetString() == "f6d7e6e0dcfa88e3f77c398d4ce6071da59f2d885cba785bb834d985c02f0016" &&
            root.GetProperty("eligible_images").GetInt32() == 7 && root.GetProperty("excluded_images").GetInt32() == 1 &&
            root.GetProperty("recorded_anchors_are_not_user_confirmation").GetBoolean() &&
            !root.GetProperty("production_approval").GetBoolean() && root.GetProperty("sealed_payload_reads").GetInt32() == 0,
            "Assisted calibration scope differs.");
        var result = new Dictionary<string, AssistedCalibrationInput>(StringComparer.Ordinal);
        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            Exact(row, "id", "source_sha256", "panel_id", "panel_sha256", "origin", "y_anchor", "x_anchor", "x_max", "y_max");
            string id = row.GetProperty("id").GetString() ?? "";
            string sourceSha = row.GetProperty("source_sha256").GetString() ?? "";
            string panelSha = row.GetProperty("panel_sha256").GetString() ?? "";
            bool hasPanelId = Guid.TryParse(row.GetProperty("panel_id").GetString(), out Guid panelId);
            Require(id.Length == 10 && id.StartsWith("source-", StringComparison.Ordinal) && id[7..].All(char.IsAsciiDigit) &&
                sourceSha.Length == 64 && sourceSha.All(char.IsAsciiHexDigit) &&
                panelSha.Length == 64 && panelSha.All(char.IsAsciiHexDigit) &&
                hasPanelId, "Assisted input identity is invalid.");
            var value = new AssistedCalibrationInput(id, sourceSha, panelId, panelSha,
                Point(row.GetProperty("origin")), Point(row.GetProperty("y_anchor")), Point(row.GetProperty("x_anchor")),
                row.GetProperty("x_max").GetDouble(), row.GetProperty("y_max").GetDouble());
            Require(result.TryAdd(id, value), "Duplicate assisted source identity.");
        }
        Require(result.Count == 7, "Assisted source count differs.");
        return result;
    }

    private static Domain.PixelPoint Point(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2, "Assisted anchor must contain two coordinates.");
        return new Domain.PixelPoint(value[0].GetDouble(), value[1].GetDouble());
    }

    private static int AssistedBindingSelfTest()
    {
        string hash = new('a', 64);
        Guid panelId = Guid.NewGuid(), sourceId = Guid.NewGuid(), projectId = Guid.NewGuid();
        var image = new WorkflowImageEvidence("memory:assisted-test.png", hash, 100, 100, WorkflowImageVariant.Original);
        var panel = new WorkflowImportedPanel(panelId, sourceId, "assisted-test.png", image);
        var input = new AssistedCalibrationInput("source-000", hash, panelId, hash,
            new Domain.PixelPoint(10, 90), new Domain.PixelPoint(10, 10), new Domain.PixelPoint(90, 90), 9, 100);
        WorkflowManualCalibrationBinding binding = input.Bind(projectId, sourceId, panel, hash);
        Require(binding.Calibration.UserConfirmed && binding.Calibration.SessionLattice?.PitchPixels == 10 &&
            binding.Calibration.Reasons.Contains("training_only_simulated_confirmation", StringComparer.Ordinal),
            "Synthetic assisted binding did not preserve its diagnostic identity.");
        bool rejected = false;
        try { _ = (input with { XAnchor = new Domain.PixelPoint(101, 90) }).Bind(projectId, sourceId, panel, hash); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Out-of-panel assisted anchor was not rejected.");
        Console.WriteLine("assisted_binding_self_test=passed; production_approval=false");
        return 0;
    }
}
