// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GraphReader.Ocr;

public sealed record NumericParseAlternative(string NormalizedText, double Value, double Confidence);

public sealed record NumericParseResult(
    bool IsSuccess,
    double? Value,
    string? NormalizedText,
    double Confidence,
    IReadOnlyList<NumericParseAlternative> Alternatives,
    bool IsPercent = false,
    string? FailureReason = null);

public static partial class GraphNumericParser
{
    /// <summary>
    /// Returns true only when the recognized text is already a graph-number
    /// literal after punctuation and separator normalization. Unlike Parse,
    /// this does not reinterpret letter glyphs such as O, I, or l as digits.
    /// Role classification uses this stricter check so semantic text is not
    /// relabeled as numeric without an explicit numeric geometry context.
    /// </summary>
    public static bool IsLiteralGraphNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string normalized = text.Trim()
            .Replace('\u2212', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2044', '/');
        string compact = RemoveSpacing(normalized);
        return ParseCandidate((NormalizeSeparators(compact), 1d)) is not null;
    }

    public static NumericParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Failure("empty_numeric_text");
        }

        var compact = RemoveSpacing(text.Trim());
        var alternatives = BuildCandidates(compact)
            .Select(ParseCandidate)
            .Where(static candidate => candidate is not null)
            .Cast<NumericParseAlternative>()
            .GroupBy(static candidate => candidate.NormalizedText, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(candidate => candidate.Confidence).First())
            .OrderByDescending(static candidate => candidate.Confidence)
            .ThenBy(static candidate => candidate.NormalizedText, StringComparer.Ordinal)
            .ToArray();

        if (alternatives.Length == 0)
        {
            return Failure("not_a_graph_number");
        }

        var best = alternatives[0];
        return new NumericParseResult(
            true,
            best.Value,
            best.NormalizedText,
            best.Confidence,
            OcrCollections.Freeze(alternatives),
            best.NormalizedText.EndsWith('%'));
    }

    private static List<(string Text, double Confidence)> BuildCandidates(string input)
    {
        var candidates = new List<(string Text, double Confidence)>();
        var normalizedPunctuation = input
            .Replace('\u2212', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2044', '/');

        AddCandidate(candidates, NormalizeSeparators(normalizedPunctuation), 0.99);

        var substituted = new StringBuilder(normalizedPunctuation.Length);
        var substitutions = 0;
        foreach (var character in normalizedPunctuation)
        {
            var replacement = character switch
            {
                'O' or 'o' => '0',
                'I' or 'l' or '|' => '1',
                _ => character,
            };
            substitutions += replacement == character ? 0 : 1;
            substituted.Append(replacement);
        }

        if (substitutions > 0)
        {
            AddCandidate(
                candidates,
                NormalizeSeparators(substituted.ToString()),
                Math.Max(0.55, 0.93 - (substitutions * 0.08)));
        }

        if (normalizedPunctuation.Length >= 3 &&
            normalizedPunctuation[0] == '(' &&
            normalizedPunctuation[^1] == ')')
        {
            AddCandidate(
                candidates,
                "-" + NormalizeSeparators(normalizedPunctuation[1..^1]),
                0.90);
        }

        return candidates;
    }

    private static NumericParseAlternative? ParseCandidate((string Text, double Confidence) candidate)
    {
        if (!GraphNumberPattern().IsMatch(candidate.Text))
        {
            return null;
        }

        var isPercent = candidate.Text.EndsWith('%');
        var numericText = isPercent ? candidate.Text[..^1] : candidate.Text;
        if (!double.TryParse(
                numericText,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var value) ||
            !double.IsFinite(value))
        {
            return null;
        }

        var normalizedNumber = value.ToString("0.################", CultureInfo.InvariantCulture);
        var normalized = normalizedNumber + (isPercent ? "%" : string.Empty);
        return new NumericParseAlternative(normalized, value, candidate.Confidence);
    }

    private static string NormalizeSeparators(string text)
    {
        bool containsDecimalPoint = text.Contains('.');
        bool containsComma = text.Contains(',');
        if (containsDecimalPoint && containsComma)
        {
            return NormalizeMixedSeparators(text);
        }

        if (containsDecimalPoint || !containsComma)
        {
            return text.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        var commaIndex = text.LastIndexOf(',');
        var suffixLength = text.Length - commaIndex - 1 - (text.EndsWith('%') ? 1 : 0);
        return suffixLength == 3 && commaIndex > 0
            ? text.Replace(",", string.Empty, StringComparison.Ordinal)
            : text.Replace(',', '.');
    }

    private static string NormalizeMixedSeparators(string text)
    {
        bool isPercent = text.EndsWith('%');
        string number = isPercent ? text[..^1] : text;
        int signLength = number.Length > 0 && number[0] is '+' or '-' ? 1 : 0;
        string unsigned = number[signLength..];
        int lastPoint = unsigned.LastIndexOf('.');
        int lastComma = unsigned.LastIndexOf(',');
        char decimalSeparator = lastPoint > lastComma ? '.' : ',';
        int decimalIndex = Math.Max(lastPoint, lastComma);
        string integerPart = unsigned[..decimalIndex];
        string fractionalPart = unsigned[(decimalIndex + 1)..];
        char groupSeparator = decimalSeparator == '.' ? ',' : '.';

        if (integerPart.Length == 0 || !fractionalPart.All(char.IsAsciiDigit))
        {
            return text;
        }

        string[] groups = integerPart.Split(groupSeparator);
        if (groups.Length == 1)
        {
            if (!groups[0].All(char.IsAsciiDigit))
            {
                return text;
            }
        }
        else if (groups[0].Length is < 1 or > 3 ||
                 !groups[0].All(char.IsAsciiDigit) ||
                 groups.Skip(1).Any(group => group.Length != 3 || !group.All(char.IsAsciiDigit)))
        {
            return text;
        }

        string normalizedInteger = string.Concat(groups);
        string sign = signLength == 0 ? string.Empty : number[..signLength];
        return string.Concat(sign, normalizedInteger, ".", fractionalPart, isPercent ? "%" : string.Empty);
    }

    private static string RemoveSpacing(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static void AddCandidate(
        List<(string Text, double Confidence)> candidates,
        string text,
        double confidence)
    {
        if (!string.IsNullOrEmpty(text))
        {
            candidates.Add((text, confidence));
        }
    }

    private static NumericParseResult Failure(string reason) =>
        new(false, null, null, 0, Array.Empty<NumericParseAlternative>(), FailureReason: reason);

    [GeneratedRegex(@"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)%?$", RegexOptions.CultureInvariant)]
    private static partial Regex GraphNumberPattern();
}
