// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Ocr.Tests;

[TestClass]
public sealed class CtcRecognitionDecoderTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(8)]
    [DataRow(32)]
    public void CertainSurroundingCharactersCannotInflateAnUnsupportedReplacement(int length)
    {
        var probabilities = new float[length * 2 * 3];
        for (var index = 0; index < length; index++)
        {
            probabilities[index * 6 + 1] = 0.999999f;
            probabilities[index * 6 + 2] = 0.000001f;
            probabilities[index * 6 + 3] = 1f;
        }

        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            probabilities, length * 2, "AB", maximumAlternatives: 2,
            outputActivation: OcrRecognitionOutputActivation.Probabilities);

        Assert.HasCount(2, alternatives);
        Assert.AreEqual(new string('A', length), alternatives[0].Text);
        Assert.IsGreaterThan(0.99, alternatives[0].Confidence);
        Assert.AreEqual('B' + new string('A', length - 1), alternatives[1].Text);
        Assert.IsTrue(alternatives[1].Confidence is > 0 and < 0.000002);
    }

    [TestMethod]
    public void UnsupportedBlankInsertionCannotBorrowTheRecognizedCharactersConfidence()
    {
        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            [0f, 1f, 0f, 0.999999f, 0f, 0.000001f], 2, "AB",
            outputActivation: OcrRecognitionOutputActivation.Probabilities);

        Assert.AreEqual("A", alternatives[0].Text);
        CtcDecodedAlternative insertion = alternatives.Single(candidate => candidate.Text == "AB");
        Assert.IsTrue(insertion.Confidence is > 0 and < 0.000002);
    }

    [TestMethod]
    public void RemovingASeparatingBlankCannotBorrowTheRemainingCharactersConfidence()
    {
        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            [0f, 1f, 0.999999f, 0.000001f, 0f, 1f], 3, "A",
            outputActivation: OcrRecognitionOutputActivation.Probabilities);

        Assert.AreEqual("AA", alternatives[0].Text);
        CtcDecodedAlternative deletion = alternatives.Single(candidate => candidate.Text == "A");
        Assert.IsTrue(deletion.Confidence is > 0 and < 0.000002);
    }

    [TestMethod]
    public void ZeroProbabilityClassesCannotManufactureAlternativeText()
    {
        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            [0f, 1f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], 3, "AB",
            outputActivation: OcrRecognitionOutputActivation.Probabilities);

        Assert.HasCount(1, alternatives);
        Assert.AreEqual("AA", alternatives[0].Text);
        Assert.AreEqual(1d, alternatives[0].Confidence);
    }

    [TestMethod]
    public void EqualSupportDeletionRemainsBelowThePrimaryReading()
    {
        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            [0f, 1f, 0.5f, 0.5f, 0f, 1f], 3, "A",
            outputActivation: OcrRecognitionOutputActivation.Probabilities);

        Assert.AreEqual("AA", alternatives[0].Text);
        CtcDecodedAlternative deletion = alternatives.Single(candidate => candidate.Text == "A");
        Assert.AreEqual(0.99d, deletion.Confidence);
        Assert.IsTrue(deletion.Confidence < alternatives[0].Confidence);
    }

    [TestMethod]
    public void DecoderCollapsesRepeatsAndBlankClassesDeterministically()
    {
        float[] logits = Logits(
            classCount: 3,
            1,
            1,
            0,
            2,
            0);

        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            logits,
            timeSteps: 5,
            alphabet: "01",
            blankClassIndex: 0);

        Assert.IsNotEmpty(alternatives);
        Assert.AreEqual("01", alternatives[0].Text);
        Assert.IsGreaterThan(0.95d, alternatives[0].Confidence);
    }

    [TestMethod]
    public void DecoderRetainsAUniqueLowerConfidenceAlternative()
    {
        float[] logits =
        [
            0f, 4f, 3.8f,
            4f, 0f, 0f,
        ];

        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            logits,
            timeSteps: 2,
            alphabet: "01",
            blankClassIndex: 0,
            maximumAlternatives: 2);

        Assert.HasCount(2, alternatives);
        Assert.AreEqual("0", alternatives[0].Text);
        Assert.AreEqual("1", alternatives[1].Text);
        Assert.IsGreaterThan(alternatives[1].Confidence, alternatives[0].Confidence);
    }

    [TestMethod]
    public void DecoderRejectsTensorShapeThatDoesNotMatchAlphabet()
    {
        Assert.Throws<ArgumentException>(() => CtcRecognitionDecoder.Decode(
            [1f, 2f, 3f, 4f],
            timeSteps: 2,
            alphabet: "01",
            blankClassIndex: 0));
    }

    [TestMethod]
    public void ProbabilityOutputKeepsModelConfidenceWithoutApplyingSoftmaxAgain()
    {
        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            [0.1f, 0.8f, 0.1f],
            timeSteps: 1,
            alphabet: "01",
            blankClassIndex: 0,
            outputActivation: OcrRecognitionOutputActivation.Probabilities);

        Assert.HasCount(1, alternatives);
        Assert.AreEqual("0", alternatives[0].Text);
        Assert.AreEqual(0.8d, alternatives[0].Confidence, 1e-6);
    }

    [TestMethod]
    public void AutoActivationRecognizesSoftmaxProbabilityRows()
    {
        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            [0.05f, 0.15f, 0.8f],
            timeSteps: 1,
            alphabet: "01",
            blankClassIndex: 0);

        Assert.AreEqual("1", alternatives[0].Text);
        Assert.AreEqual(0.8d, alternatives[0].Confidence, 1e-6);
    }

    [TestMethod]
    public void UnicodeScalarAlphabetMapsAstralCharacterToOneCtcClass()
    {
        IReadOnlyList<CtcDecodedAlternative> alternatives = CtcRecognitionDecoder.Decode(
            [0.05f, 0.05f, 0.9f],
            timeSteps: 1,
            alphabet: "0𝑢",
            blankClassIndex: 0,
            outputActivation: OcrRecognitionOutputActivation.Probabilities);

        Assert.AreEqual("𝑢", alternatives[0].Text);
        Assert.AreEqual(0.9d, alternatives[0].Confidence, 1e-6);
    }

    [TestMethod]
    public void DeclaredProbabilityOutputRejectsNonProbabilityRows()
    {
        Assert.Throws<InvalidDataException>(() => CtcRecognitionDecoder.Decode(
            [0f, 4f, 3f],
            timeSteps: 1,
            alphabet: "01",
            blankClassIndex: 0,
            outputActivation: OcrRecognitionOutputActivation.Probabilities));
    }

    private static float[] Logits(int classCount, params int[] winningClasses)
    {
        var values = Enumerable.Repeat(-6f, classCount * winningClasses.Length).ToArray();
        for (var time = 0; time < winningClasses.Length; time++)
        {
            values[(time * classCount) + winningClasses[time]] = 6f;
        }

        return values;
    }
}
