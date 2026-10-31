// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class GraphNumericParserTests
{
    [TestMethod]
    public void DeterministicParserCorpusMeetsAgreedNinetyFivePercentThreshold()
    {
        int exactMatches = NumericCorpus.Cases.Count(testCase =>
        {
            NumericParseResult result = GraphNumericParser.Parse(testCase.Text);
            return result.IsSuccess &&
                result.Value.HasValue &&
                Math.Abs(result.Value.Value - testCase.ExpectedValue) < 1e-9 &&
                result.IsPercent == testCase.IsPercent;
        });
        double exactMatchRate = exactMatches / (double)NumericCorpus.Cases.Count;

        Assert.HasCount(22, NumericCorpus.Cases);
        Assert.IsGreaterThanOrEqualTo(
            NumericCorpus.AgreedExactMatchThreshold,
            exactMatchRate,
            $"Parser-only exact match was {exactMatchRate:P1}; this does not measure a real OCR model.");
    }

    [TestMethod]
    [DataRow("O", 0d)]
    [DataRow("10O", 100d)]
    [DataRow("l", 1d)]
    [DataRow("l0", 10d)]
    public void ContextualZeroAndOneGlyphConfusionsRemainNumericButLowerConfidence(
        string text,
        double expected)
    {
        NumericParseResult result = GraphNumericParser.Parse(text);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(expected, result.Value.Value, 1e-9);
        Assert.IsLessThan(1d, result.Confidence);
        Assert.IsNotEmpty(result.Alternatives);
    }

    [TestMethod]
    [DataRow("-12.5", -12.5d, false)]
    [DataRow("\u22120.25", -0.25d, false)]
    [DataRow(".75", 0.75d, false)]
    [DataRow("50%", 50d, true)]
    public void DecimalPercentAndNegativeFormsPreservePrintedMeaning(
        string text,
        double expected,
        bool expectedPercent)
    {
        NumericParseResult result = GraphNumericParser.Parse(text);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(expected, result.Value.Value, 1e-9);
        Assert.AreEqual(expectedPercent, result.IsPercent);
    }

    [TestMethod]
    [DataRow("1,234.5", 1234.5d)]
    [DataRow("1.234,5", 1234.5d)]
    [DataRow("-12.345,6%", -12345.6d)]
    public void MixedLocaleSeparatorsPreserveMagnitude(string text, double expected)
    {
        NumericParseResult result = GraphNumericParser.Parse(text);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(expected, result.Value.Value, 1e-9);
    }

    [TestMethod]
    [DataRow("12,34.5")]
    [DataRow("1.23,456.7")]
    public void MalformedMixedThousandsGroupingIsRejected(string text)
    {
        NumericParseResult result = GraphNumericParser.Parse(text);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("Generalization")]
    [DataRow("Chandler")]
    [DataRow("Baseline")]
    [DataRow("B")]
    [DataRow("12 sessions")]
    public void SemanticGraphTextIsNotSilentlyParsedAsScientificValue(string text)
    {
        NumericParseResult result = GraphNumericParser.Parse(text);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public void LiteralNumberCheckDoesNotPromoteAmbiguousLetterGlyphs()
    {
        Assert.IsTrue(GraphNumericParser.IsLiteralGraphNumber("-2.5%"));
        Assert.IsTrue(GraphNumericParser.IsLiteralGraphNumber("1,000"));
        Assert.IsFalse(GraphNumericParser.IsLiteralGraphNumber("O"));
        Assert.IsFalse(GraphNumericParser.IsLiteralGraphNumber("I"));
        Assert.IsFalse(GraphNumericParser.IsLiteralGraphNumber("l"));
    }
}
