// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class OcrV8ProductionCompositionFactoryTests
{
    [TestMethod]
    public void UnreviewedPayloadMetadataFailsBeforeModelExposure()
    {
        var wrong = new ModelIdentity("wrong", "1", new string('a', 64), "missing.onnx");
        var payloads = new OcrV8ProductionPayloadSet(wrong, wrong, wrong, wrong, "not-reviewed");

        InvalidDataException exception = Assert.ThrowsExactly<InvalidDataException>(() =>
            OcrV8ProductionCompositionFactory.ValidatePayloads(payloads));

        StringAssert.Contains(exception.Message, "unreviewed identity");
    }

    [TestMethod]
    public void V5NumericRecognizerUsesCpuWhenCallerAllowsDirectMlAndCpu()
    {
        IReadOnlyList<InferenceProvider> providers =
            OcrV8ProductionCompositionFactory.V5NumericRecognizerProviderPolicy(
                [InferenceProvider.DirectMl, InferenceProvider.Cpu]);

        CollectionAssert.AreEqual(new[] { InferenceProvider.Cpu }, providers.ToArray());
        CollectionAssert.AreEqual(
            new[] { InferenceProvider.DirectMl, InferenceProvider.Cpu },
            OcrV8ProductionCompositionFactory.ValidateProviderPolicy(
                [InferenceProvider.DirectMl, InferenceProvider.Cpu]).ToArray());
    }

    [TestMethod]
    public void V5NumericRecognizerRejectsCallerPolicyWithoutCpu()
    {
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() =>
            OcrV8ProductionCompositionFactory.V5NumericRecognizerProviderPolicy(
                [InferenceProvider.DirectMl]));

        StringAssert.Contains(exception.Message, "retain CPU fallback");
    }
}
