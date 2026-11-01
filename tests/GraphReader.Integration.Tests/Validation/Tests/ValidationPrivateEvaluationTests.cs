// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Integration.Tests.Validation.Core.Private;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#pragma warning disable CA1861 // Small collection expressions are intentional test fixtures.

namespace GraphReader.Integration.Tests.Validation.Tests;

[TestClass]
public sealed class ValidationPrivateEvaluationTests
{
    [TestMethod]
    public void ValidationPrivateEvaluationRequiresExplicitOptInAndDataDirectory()
    {
        PrivateEvaluationAdapter adapter = CreateAdapter();

        PrivateEvaluationAvailability availability = adapter.CheckAvailability(
            new PrivateEvaluationRequest(ExplicitOptIn: false, ExternalDirectory: null));

        Assert.IsFalse(availability.IsAvailable);
        Assert.IsNull(availability.ExternalDirectory);
        CollectionAssert.AreEquivalent(
            new[]
            {
                PrivateEvaluationUnavailableReason.ExplicitOptInRequired,
                PrivateEvaluationUnavailableReason.ExternalDirectoryRequired,
            },
            availability.Reasons.Select(reason => reason.Code).ToArray());
    }

    [TestMethod]
    public void ValidationPrivateEvaluationCannotRunInContinuousIntegration()
    {
        SyntheticDirectories directories = CreateSyntheticDirectories();
        try
        {
            PrivateEvaluationAdapter adapter = CreateAdapter(
                directories.Repository,
                variable => variable == "CI" ? "true" : null);

            PrivateEvaluationAvailability availability = adapter.CheckAvailability(
                new PrivateEvaluationRequest(ExplicitOptIn: true, directories.External));

            Assert.IsFalse(availability.IsAvailable);
            Assert.HasCount(1, availability.Reasons);
            Assert.AreEqual(
                PrivateEvaluationUnavailableReason.ContinuousIntegrationDetected,
                availability.Reasons[0].Code);
        }
        finally
        {
            directories.Delete();
        }
    }

    [TestMethod]
    public void ValidationPrivateEvaluationRejectsRepositoryData()
    {
        string repositoryRoot = RepositoryRoot.Find();
        PrivateEvaluationAdapter adapter = CreateAdapter(repositoryRoot);

        PrivateEvaluationAvailability availability = adapter.CheckAvailability(
            new PrivateEvaluationRequest(ExplicitOptIn: true, repositoryRoot));

        Assert.IsFalse(availability.IsAvailable);
        Assert.HasCount(1, availability.Reasons);
        Assert.AreEqual(
            PrivateEvaluationUnavailableReason.ExternalDirectoryMustBeOutsideRepository,
            availability.Reasons[0].Code);
    }

    [TestMethod]
    public void ValidationPrivateEvaluationAllowsExplicitExternalLocalData()
    {
        SyntheticDirectories directories = CreateSyntheticDirectories();
        try
        {
            PrivateEvaluationAdapter adapter = CreateAdapter(directories.Repository);

            PrivateEvaluationAvailability availability = adapter.CheckAvailability(
                new PrivateEvaluationRequest(ExplicitOptIn: true, directories.External));

            Assert.IsTrue(availability.IsAvailable);
            Assert.AreEqual(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directories.External)),
                availability.ExternalDirectory);
            Assert.IsEmpty(availability.Reasons);
        }
        finally
        {
            directories.Delete();
        }
    }

    private static PrivateEvaluationAdapter CreateAdapter(
        string? repositoryRoot = null,
        Func<string, string?>? readEnvironmentVariable = null) =>
        new(repositoryRoot ?? RepositoryRoot.Find(), readEnvironmentVariable ?? (_ => null));

    private static SyntheticDirectories CreateSyntheticDirectories()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"GraphReader-ValidationPrivateEvaluation-{Guid.NewGuid():N}");
        string repository = Path.Combine(root, "repository");
        string external = Path.Combine(root, "external");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(external);

        return new SyntheticDirectories(root, repository, external);
    }

    private sealed record SyntheticDirectories(string Root, string Repository, string External)
    {
        public void Delete()
        {
            Directory.Delete(External);
            Directory.Delete(Repository);
            Directory.Delete(Root);
        }
    }
}
