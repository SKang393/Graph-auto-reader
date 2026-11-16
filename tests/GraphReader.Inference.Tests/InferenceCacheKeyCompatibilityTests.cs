// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GraphReader.Inference.Tests;

[TestClass]
public sealed class InferenceCacheKeyCompatibilityTests
{
    private static readonly int[] SpecialBits = [0, unchecked((int)0x80000000), 0x3f800000, unchecked((int)0xff7fffff),
        0x7f7fffff, 0x7f800000, unchecked((int)0xff800000), 0x7fc00001, 0x7fc12345, unchecked((int)0xffc01234)];

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(17)]
    [DataRow(16383)]
    [DataRow(16384)]
    [DataRow(16385)]
    [DataRow(32771)]
    public void ChunkBoundariesPreserveLegacyKeysAndInputBits(int length)
    {
        float[] values = Enumerable.Range(0, length).Select(i => BitConverter.Int32BitsToSingle(SpecialBits[i % SpecialBits.Length])).ToArray();
        int[] before = values.Select(BitConverter.SingleToInt32Bits).ToArray();
        InferenceRequest request = Request(values);
        Assert.AreEqual(LegacyKey(request), InferenceCacheKeyDeriver.Derive(request).Value);
        CollectionAssert.AreEqual(before, values.Select(BitConverter.SingleToInt32Bits).ToArray());
    }

    [TestMethod]
    public void LegacyIdentityAndOptionsRemainBoundToDerivedKey()
    {
        InferenceRequest baseline = Request([0f, -0f, 1.25f, BitConverter.Int32BitsToSingle(0x7fc12345)]);
        InferenceRequest[] variants = [
            baseline with { Input = baseline.Input with { InputName = "other-input" } },
            baseline with { Input = baseline.Input with { OutputName = "other-output" } },
            baseline with { Input = baseline.Input with { Shape = [1L, 4L] } },
            baseline with { Model = baseline.Model with { Sha256 = new string('b', 64) } },
            baseline with { CacheMaterial = baseline.CacheMaterial with { Parameters = new Dictionary<string, object?> { ["width"] = 321, ["mode"] = "dynamic" } } },
            baseline with { AllowedProviders = [InferenceProvider.DirectMl] },
        ];
        string original = InferenceCacheKeyDeriver.Derive(baseline).Value;
        Assert.AreEqual(LegacyKey(baseline), original);
        foreach (InferenceRequest request in variants)
        {
            string actual = InferenceCacheKeyDeriver.Derive(request).Value;
            Assert.AreEqual(LegacyKey(request), actual);
            Assert.AreNotEqual(original, actual);
        }
    }

    private static InferenceRequest Request(float[] values) => new(
        new ModelIdentity("owned", "1", new string('a', 64), "unused.onnx"),
        new InferenceInput(values, [values.Length], "input-한", "output"),
        new StageCacheMaterial(new string('c', 64), "crop", "transform", "recognition", "v1",
            new Dictionary<string, object?> { ["mode"] = "dynamic", ["width"] = 320 }, 1),
        TimeSpan.FromSeconds(1), [InferenceProvider.Cpu]);

    // The prior implementation wrote each float separately in little-endian order.
    private static string LegacyKey(InferenceRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, request.Input.InputName); AppendString(hash, request.Input.OutputName);
        Span<byte> integer = stackalloc byte[sizeof(long)];
        foreach (long dimension in request.Input.Shape)
        {
            BinaryPrimitives.WriteInt64LittleEndian(integer, dimension);
            hash.AppendData(integer);
        }
        Span<byte> number = stackalloc byte[sizeof(float)];
        foreach (float value in request.Input.Values.Span)
        {
            BinaryPrimitives.WriteSingleLittleEndian(number, value);
            hash.AppendData(number);
        }
        string tensor = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        string providers = request.AllowedProviders is null ? "policy-default" :
            string.Join(',', request.AllowedProviders.Distinct().OrderBy(provider => provider).Select(provider => provider.ToString()));
        StageCacheMaterial material = request.CacheMaterial;
        return StageCacheKey.Create(material.InputSha256 + ":" + tensor, material.PanelCrop, material.TransformChain,
            material.StageName, material.StageVersion, request.Model.Sha256 + ":" + providers, material.Parameters, material.ContractVersion).Value;
    }

    private static void AppendString(IncrementalHash hash, string? value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length); hash.AppendData(bytes);
    }
}
