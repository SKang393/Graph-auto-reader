// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Text.Json;
using System.Security.Cryptography;
using GraphReader.AxisScoringVariants;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.AxisScoringVariants.Tests;

[TestClass]
public sealed class VariantEvaluationTests
{
    [TestMethod]
    public void SourceBindingRequiresCorrectManifestAndVerifiesCurrentSources()
    {
        const string root = @"E:\Graph-auto-reader";
        string path = Path.Combine(root, "tools", "GraphReader.AxisScoringVariants", "source-bindings.json");
        string digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        Assert.ThrowsExactly<InvalidDataException>(() => SourceBindingLease.Open(root, new string('0', 64)));
        using SourceBindingLease lease = SourceBindingLease.Open(root, digest);
        lease.Verify();
        Assert.AreEqual(digest, lease.ManifestSha256);
        Assert.IsTrue(lease.SourceFiles.ContainsKey("tools/GraphReader.AxisScoringVariants/Program.cs"));
        Assert.IsTrue(lease.SourceFiles.ContainsKey("tools/GraphReader.AxisScoringVariants/VariantEvaluation.cs"));
        Assert.IsTrue(lease.SourceFiles.ContainsKey("src/GraphReader.Axis/AxisGeometryDetector.cs"));
    }

    [TestMethod]
    public async Task GeneratedVariantsAreDeterministicAndObserverMatchesProductionSelection()
    {
        IReadOnlyList<SyntheticVariant> first = VariantEvaluation.Generate();
        IReadOnlyList<SyntheticVariant> second = VariantEvaluation.Generate();
        Assert.HasCount(27, first);
        Assert.AreEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.AreEqual(27, first.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (string category in new[] { "clean", "short", "frame", "divider", "scatter", "disconnected", "fragmented", "competing-short", "competing-long" })
            Assert.AreEqual(3, first.Count(item => item.Name.StartsWith(category + "-", StringComparison.Ordinal)));

        VariantResult[] measured = await VariantEvaluation.EvaluateAsync();
        VariantResult[] repeated = await VariantEvaluation.EvaluateAsync();
        Assert.AreEqual(JsonSerializer.Serialize(measured), JsonSerializer.Serialize(repeated));
        Assert.HasCount(27, measured);
        Assert.IsTrue(measured.Any(static item => item.Status == "completed"));
        Assert.IsTrue(measured.Where(static item => item.Status == "completed").All(static item =>
            item.ObserverMatchesSelection && item.Candidates.Count > 0 && item.Candidates[0].Rank == 0));
        Assert.IsTrue(measured.All(static item => item.Status is "completed" or "rejected"));
        VariantResult[] competing = measured.Where(static item => item.Name.StartsWith("competing-", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(6, competing);
        Assert.IsTrue(competing.All(static item => item.Status == "completed" &&
            item.Candidates.Any(static candidate => candidate.MaximumCornerErrorPixels < 1)));
        Assert.IsTrue(competing.All(static item => item.MaximumCornerErrorPixels < 1 || item.NeedsReview),
            "A wrong selected competitor must remain reviewable; a future correct selection is also acceptable.");
    }
}
