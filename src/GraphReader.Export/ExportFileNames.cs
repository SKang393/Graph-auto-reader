// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;

namespace GraphReader.Export;

internal sealed record ExportFileNames(
    string MinimalCsv,
    string ExtendedAuditCsv,
    string AuditJson);

internal static class ExportFileNamePlanner
{
    private const int MaximumFileNameLength = 240;
    private const string MinimalCsvSuffix = ".csv";
    private const string ExtendedAuditCsvSuffix = ".audit.csv";
    private const string AuditJsonSuffix = ".audit.json";
    private static readonly int MaximumStemLength =
        MaximumFileNameLength - AuditJsonSuffix.Length;

    private static readonly HashSet<char> InvalidFileNameCharacters =
    [
        '<',
        '>',
        ':',
        '"',
        '/',
        '\\',
        '|',
        '?',
        '*',
    ];

    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CLOCK$",
            "CONIN$",
            "CONOUT$",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "COM¹",
            "COM²",
            "COM³",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
            "LPT¹",
            "LPT²",
            "LPT³",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<Guid, ExportFileNames> Plan(
        string? participant,
        IEnumerable<ExportSeries> interventionSeries,
        string? fileNamePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(interventionSeries);

        ExportSeries[] orderedSeries = interventionSeries
            .OrderBy(static series => series.SeriesId)
            .ToArray();

        string participantName = SanitizeComponent(participant, "participant");
        string prefix = string.IsNullOrWhiteSpace(fileNamePrefix)
            ? string.Empty
            : SanitizeComponent(fileNamePrefix, "panel") + "_";
        var results = new Dictionary<Guid, ExportFileNames>();
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nextSuffixByStem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (ExportSeries series in orderedSeries)
        {
            if (results.ContainsKey(series.SeriesId))
            {
                throw new ArgumentException(
                    $"Series identifier '{series.SeriesId}' appears more than once.",
                    nameof(interventionSeries));
            }

            string displayNameWithoutSymbol = RemoveSymbol(series.DisplayName, series.Symbol);
            string seriesName = SanitizeComponent(
                displayNameWithoutSymbol,
                $"series-{series.SeriesId:N}");
            string stem = LimitStem($"{prefix}{participantName}_{seriesName}", suffixLength: 0);

            int suffixNumber = nextSuffixByStem.TryGetValue(stem, out int previousSuffix)
                ? checked(previousSuffix + 1)
                : 1;

            while (true)
            {
                string duplicateSuffix = suffixNumber == 1 ? string.Empty : $"-{suffixNumber}";
                string uniqueStem = LimitStem(stem, duplicateSuffix.Length) + duplicateSuffix;
                var names = new ExportFileNames(
                    uniqueStem + MinimalCsvSuffix,
                    uniqueStem + ExtendedAuditCsvSuffix,
                    uniqueStem + AuditJsonSuffix);

                if (AreAvailable(names, usedFileNames))
                {
                    Reserve(names, usedFileNames);
                    results.Add(series.SeriesId, names);
                    nextSuffixByStem[stem] = suffixNumber;
                    break;
                }

                suffixNumber = checked(suffixNumber + 1);
            }
        }

        return new ReadOnlyDictionary<Guid, ExportFileNames>(results);
    }

    private static string RemoveSymbol(string displayName, string symbol)
    {
        if (string.IsNullOrEmpty(symbol))
        {
            return displayName;
        }

        return displayName.Replace(symbol, string.Empty, StringComparison.Ordinal);
    }

    private static string SanitizeComponent(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var sanitized = new char[value.Length];
        int written = 0;
        bool lastWasReplacement = false;

        foreach (char character in value)
        {
            bool mustReplace = char.IsControl(character) ||
                InvalidFileNameCharacters.Contains(character);
            if (mustReplace)
            {
                if (!lastWasReplacement)
                {
                    sanitized[written++] = '-';
                    lastWasReplacement = true;
                }

                continue;
            }

            sanitized[written++] = character;
            lastWasReplacement = false;
        }

        string result = new string(sanitized, 0, written).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(result))
        {
            return fallback;
        }

        return IsReservedDeviceName(result) ? $"_{result}" : result;
    }

    private static bool IsReservedDeviceName(string value)
    {
        int periodIndex = value.IndexOf('.');
        string deviceCandidate = periodIndex >= 0 ? value[..periodIndex] : value;
        return ReservedDeviceNames.Contains(deviceCandidate.TrimEnd('.', ' '));
    }

    private static string LimitStem(string stem, int suffixLength)
    {
        int maximumLength = MaximumStemLength - suffixLength;
        if (stem.Length <= maximumLength)
        {
            return stem;
        }

        int length = maximumLength;
        if (char.IsHighSurrogate(stem[length - 1]) &&
            length < stem.Length &&
            char.IsLowSurrogate(stem[length]))
        {
            length--;
        }

        return stem[..length].TrimEnd('.', ' ');
    }

    private static bool AreAvailable(
        ExportFileNames names,
        HashSet<string> usedFileNames) =>
        !usedFileNames.Contains(names.MinimalCsv) &&
        !usedFileNames.Contains(names.ExtendedAuditCsv) &&
        !usedFileNames.Contains(names.AuditJson);

    private static void Reserve(
        ExportFileNames names,
        HashSet<string> usedFileNames)
    {
        usedFileNames.Add(names.MinimalCsv);
        usedFileNames.Add(names.ExtendedAuditCsv);
        usedFileNames.Add(names.AuditJson);
    }
}
