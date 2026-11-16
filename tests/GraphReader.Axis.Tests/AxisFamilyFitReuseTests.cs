// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace GraphReader.Axis.Tests;

[TestClass]
public sealed class AxisFamilyFitReuseTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type ObservationType = typeof(AxisGeometryDetector).GetNestedType("Observation", BindingFlags.NonPublic)!;
    private static readonly Type OrientationType = typeof(AxisGeometryDetector).GetNestedType("Orientation", BindingFlags.NonPublic)!;
    private static readonly object Horizontal = Enum.Parse(OrientationType, "Horizontal");
    private static readonly MethodInfo Build = typeof(AxisGeometryDetector).GetMethod("BuildFamilies", PrivateStatic)!;
    private static readonly MethodInfo Fit = typeof(AxisGeometryDetector).GetMethod("FitFamily", PrivateStatic)!;
    private static readonly MethodInfo CreateObservation = typeof(AxisGeometryDetector).GetMethod("TryCreateObservation", PrivateStatic)!;
    private static readonly MethodInfo SegmentFits = typeof(AxisGeometryDetector).GetMethod("SegmentFitsLine", PrivateStatic)!;
    private static readonly MethodInfo AnglePair = typeof(AxisGeometryDetector).GetMethod("AngleDifferenceDegrees", PrivateStatic,
        [ObservationType, ObservationType])!;
    private static readonly MethodInfo AngleFit = typeof(AxisGeometryDetector).GetMethod("AngleDifferenceDegrees", PrivateStatic,
        [ObservationType, typeof(double)])!;
    private static readonly string[] ScalarProperties = ["Slope", "Intercept", "Span", "CoverageFraction", "RootMeanSquareError"];
    private static readonly string[] ExpectedCollisionIds = ["a", "b\u001fc"];

    [TestMethod]
    [DataRow("duplicate", false)]
    [DataRow("duplicate", true)]
    [DataRow("distinct", false)]
    [DataRow("distinct", true)]
    [DataRow("converging", false)]
    [DataRow("converging", true)]
    [DataRow("key-collision", false)]
    [DataRow("key-collision", true)]
    [DataRow("empty-refinement", false)]
    [DataRow("empty-refinement", true)]
    public void RefinedFamilyFitsMatchUnchangedOracleExactly(string kind, bool reverse)
    {
        AxisGeometryRequest request = Fixture(kind, reverse);
        var options = request.Options ?? new AxisGeometryOptions();
        Array observations = Observations(request, options);
        object[] expected = OriginalBuild(observations, options);
        object[] actual = ((IEnumerable)Build.Invoke(null, [observations, Horizontal, options, CancellationToken.None])!).Cast<object>().ToArray();
        Assert.HasCount(expected.Length, actual);
        for (int index = 0; index < expected.Length; index++)
        {
            foreach (string property in ScalarProperties)
                Assert.AreEqual(BitConverter.DoubleToInt64Bits((double)Get(expected[index], property)),
                    BitConverter.DoubleToInt64Bits((double)Get(actual[index], property)), property);
            Assert.AreEqual((GeometryLineSegment)Get(expected[index], "Line"), (GeometryLineSegment)Get(actual[index], "Line"));
            Assert.AreEqual(JsonSerializer.Serialize(Get(expected[index], "Direction")), JsonSerializer.Serialize(Get(actual[index], "Direction")));
            object[] before = Members(expected[index]), after = Members(actual[index]);
            Assert.HasCount(before.Length, after);
            for (int member = 0; member < before.Length; member++) Assert.AreSame(before[member], after[member]);
        }
        if (kind == "converging") { Assert.HasCount(1, actual); Assert.HasCount(3, Members(actual[0])); }
        if (kind == "key-collision")
        {
            Assert.HasCount(1, actual);
            CollectionAssert.AreEquivalent(ExpectedCollisionIds, Members(actual[0]).Select(member => Candidate(member).CandidateId).ToArray());
        }
        if (kind == "empty-refinement") Assert.IsEmpty(actual);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FamilyConstructionRetainsCancellation(bool beforeStart)
    {
        var options = new AxisGeometryOptions();
        Array observations = Observations(Fixture("duplicate", false), options);
        using var cancellation = new CancellationTokenSource();
        object input = observations;
        if (beforeStart) cancellation.Cancel();
        else input = Activator.CreateInstance(typeof(CancellingObservations<>).MakeGenericType(ObservationType), observations, cancellation)!;
        TargetInvocationException error = Assert.ThrowsExactly<TargetInvocationException>(() =>
            Build.Invoke(null, [input, Horizontal, options, cancellation.Token]));
        Assert.IsInstanceOfType<OperationCanceledException>(error.InnerException);
        Assert.AreEqual(cancellation.Token, ((OperationCanceledException)error.InnerException).CancellationToken);
    }

    // Literal pre-change orchestration; fitting/predicates are shared unchanged
    // math, while this oracle always performs the second fit before key lookup.
    private static object[] OriginalBuild(Array observations, AxisGeometryOptions options)
    {
        var families = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (object hypothesis in observations)
        {
            Array initial = Filter(observations, candidate =>
                (double)AnglePair.Invoke(null, [candidate, hypothesis])! <= options.MergeAngleToleranceDegrees &&
                (bool)SegmentFits.Invoke(null, [Candidate(candidate).Segment, Candidate(hypothesis).Segment,
                    options.MergeDistancePixels + (Candidate(candidate).StrokeWidthPixels + Candidate(hypothesis).StrokeWidthPixels) / 2])!);
            object fitted = Fit.Invoke(null, [initial, Horizontal])!;
            Array refined = Filter(observations, candidate =>
                (double)AngleFit.Invoke(null, [candidate, (double)Get(fitted, "AngleDegrees")])! <= options.MergeAngleToleranceDegrees &&
                (bool)fitted.GetType().GetMethod("SegmentFits")!.Invoke(fitted,
                    [Candidate(candidate).Segment, options.MergeDistancePixels + Candidate(candidate).StrokeWidthPixels])!);
            if (refined.Length == 0) continue;
            fitted = Fit.Invoke(null, [refined, Horizontal])!;
            string key = string.Join('\u001f', Members(fitted).Select(member => Candidate(member).CandidateId).Order(StringComparer.Ordinal));
            if (!families.TryGetValue(key, out object? existing) ||
                (double)Get(fitted, "RootMeanSquareError") < (double)Get(existing, "RootMeanSquareError")) families[key] = fitted;
        }
        return families.Values.ToArray();
    }

    private static Array Observations(AxisGeometryRequest request, AxisGeometryOptions options)
    {
        Array result = Array.CreateInstance(ObservationType, request.LineCandidates.Count);
        for (int index = 0; index < result.Length; index++)
        {
            object?[] arguments = [request.LineCandidates[index], request, options, null];
            Assert.IsTrue((bool)CreateObservation.Invoke(null, arguments)!);
            result.SetValue(arguments[3], index);
        }
        return result;
    }
    private static Array Filter(Array input, Func<object, bool> predicate)
    {
        object[] selected = input.Cast<object>().Where(predicate).ToArray();
        Array result = Array.CreateInstance(ObservationType, selected.Length);
        Array.Copy(selected, result, selected.Length);
        return result;
    }
    private static object Get(object value, string property) => value.GetType().GetProperty(property)!.GetValue(value)!;
    private static object[] Members(object family) => ((IEnumerable)Get(family, "Members")).Cast<object>().ToArray();
    private static GeometryLineCandidate Candidate(object observation) => (GeometryLineCandidate)Get(observation, "Candidate");

    private static AxisGeometryRequest Fixture(string kind, bool reverse)
    {
        var fixture = new AxisFixtureBuilder(4096, 4096);
        if (kind is "duplicate" or "distinct")
            for (int index = 0; index < 128; index++)
                fixture.Line(100 + (kind == "duplicate" ? .01 * index : 0), kind == "duplicate" ? 300 : 120 + 20 * index,
                    700 - (kind == "duplicate" ? .01 * index : 0), kind == "duplicate" ? 300 : 120 + 20 * index, id: $"h-{index:D3}");
        else if (kind == "converging")
            fixture.Line(100, 300, 700, 300).Line(100, 303, 700, 303).Line(100, 305, 700, 305);
        else if (kind == "key-collision")
            fixture.Line(100, 300, 700, 300, id: "a\u001fb").Line(100, 301, 700, 301, id: "c")
                .Line(100, 500, 700, 500, id: "a").Line(100, 500.1, 700, 500.1, id: "b\u001fc");
        else fixture.Line(100, 300, 110, 300).Line(115, 304, 125, 304);
        AxisGeometryRequest request = fixture.Build();
        return reverse ? request with { LineCandidates = request.LineCandidates.Reverse().ToArray() } : request;
    }

    private sealed class CancellingObservations<T>(Array items, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        private readonly T[] values = (T[])items;
        private int enumerations;
        public int Count => values.Length;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator()
        {
            if (++enumerations == 4) cancellation.Cancel();
            return ((IEnumerable<T>)values).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
