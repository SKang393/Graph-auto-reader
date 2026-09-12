// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Buffers;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace GraphReader.SyntheticRuntimeEvidence;

/// <summary>
/// Reads validated case-level OCR source truth for in-memory sealed evaluation.
/// The returned truth must remain in-process and must not enter sealed evidence.
/// The reader never accepts image crops, plot geometry, paths, or model input.
/// </summary>
internal static class OriginalDbOcrAnnotationReader
{
    public static IReadOnlyList<OriginalDbOcrAggregateTruth> Read(
        byte[] annotationBytes,
        int expectedWidth,
        int expectedHeight)
    {
        ArgumentNullException.ThrowIfNull(annotationBytes);
        if (expectedWidth <= 0 || expectedHeight <= 0)
        {
            throw Failure("EXPECTED_CANVAS_INVALID");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(annotationBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            RejectDuplicatePropertyNames(document.RootElement);
            return ReadAnnotation(document.RootElement, expectedWidth, expectedHeight);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            throw Failure("JSON_INVALID");
        }
    }

    private static ReadOnlyCollection<OriginalDbOcrAggregateTruth> ReadAnnotation(
        JsonElement annotation,
        int expectedWidth,
        int expectedHeight)
    {
        RequireKind(annotation, JsonValueKind.Object, "ROOT_INVALID");
        if (!TryGetString(annotation, "coordinate_space", out string? coordinateSpace) ||
            !string.Equals(coordinateSpace, "original_pixels", StringComparison.Ordinal))
        {
            throw Failure("COORDINATE_SPACE_INVALID");
        }

        if (!annotation.TryGetProperty("canvas", out JsonElement canvas))
        {
            throw Failure("CANVAS_INVALID");
        }
        RequireKind(canvas, JsonValueKind.Object, "CANVAS_INVALID");
        int width = ReadPositiveInteger(canvas, "width", "CANVAS_INVALID");
        int height = ReadPositiveInteger(canvas, "height", "CANVAS_INVALID");
        if (width != expectedWidth || height != expectedHeight)
        {
            throw Failure("CANVAS_MISMATCH");
        }

        var truths = new List<OriginalDbOcrAggregateTruth>();
        var textIds = new HashSet<string>(StringComparer.Ordinal);
        var regionIds = new HashSet<string>(StringComparer.Ordinal);

        AppendTextGroup(annotation, "texts", expectedWidth, expectedHeight, textIds, regionIds, truths);
        if (annotation.TryGetProperty("panels", out JsonElement panels))
        {
            RequireKind(panels, JsonValueKind.Array, "PANELS_INVALID");
            foreach (JsonElement panel in panels.EnumerateArray())
            {
                RequireKind(panel, JsonValueKind.Object, "PANEL_INVALID");
                AppendTextGroup(panel, "texts", expectedWidth, expectedHeight, textIds, regionIds, truths);
            }
        }

        return new ReadOnlyCollection<OriginalDbOcrAggregateTruth>(truths);
    }

    private static void AppendTextGroup(
        JsonElement owner,
        string propertyName,
        int canvasWidth,
        int canvasHeight,
        HashSet<string> textIds,
        HashSet<string> regionIds,
        List<OriginalDbOcrAggregateTruth> truths)
    {
        if (!owner.TryGetProperty(propertyName, out JsonElement records))
        {
            return;
        }
        RequireKind(records, JsonValueKind.Array, "TEXTS_INVALID");

        foreach (JsonElement record in records.EnumerateArray())
        {
            RequireKind(record, JsonValueKind.Object, "TEXT_RECORD_INVALID");
            if (record.TryGetProperty("visible", out JsonElement visible) &&
                visible.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            bool textIsString = TryGetString(record, "text", out string? text);
            if (textIsString && text is not null && IsPythonBlank(text))
            {
                continue;
            }
            if (!record.TryGetProperty("rendered_pixel_box", out JsonElement renderedBox) ||
                renderedBox.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (!textIsString || text is null)
            {
                throw Failure("SELECTED_TEXT_INVALID");
            }
            ValidateUnicode(text);

            string textId = ReadTrimmedIdentity(record, "text_id", "TEXT_ID_INVALID");
            if (!textIds.Add(textId))
            {
                throw Failure("TEXT_ID_DUPLICATE");
            }
            string regionId = ReadTrimmedIdentity(record, "region_id", "REGION_ID_INVALID");
            if (!regionIds.Add(regionId))
            {
                throw Failure("REGION_ID_DUPLICATE");
            }

            OriginalDbOcrGeneratorRole role = ReadRole(record);
            OriginalDbOcrAggregateBox box = ReadBox(renderedBox, canvasWidth, canvasHeight);
            truths.Add(new OriginalDbOcrAggregateTruth(box, text, role));
        }
    }

    private static OriginalDbOcrGeneratorRole ReadRole(JsonElement record)
    {
        if (!TryGetString(record, "role", out string? role))
        {
            throw Failure("ROLE_INVALID");
        }
        return role switch
        {
            "x_tick" => OriginalDbOcrGeneratorRole.XTick,
            "y_tick" => OriginalDbOcrGeneratorRole.YTick,
            "axis_title" => OriginalDbOcrGeneratorRole.AxisTitle,
            "phase_heading" => OriginalDbOcrGeneratorRole.PhaseHeading,
            "legend_text" => OriginalDbOcrGeneratorRole.LegendText,
            "participant" => OriginalDbOcrGeneratorRole.Participant,
            "annotation" => OriginalDbOcrGeneratorRole.Annotation,
            "condition_label" => OriginalDbOcrGeneratorRole.ConditionLabel,
            _ => throw Failure("ROLE_INVALID"),
        };
    }

    private static OriginalDbOcrAggregateBox ReadBox(
        JsonElement value,
        int canvasWidth,
        int canvasHeight)
    {
        RequireKind(value, JsonValueKind.Array, "BOX_INVALID");
        if (value.GetArrayLength() != 4)
        {
            throw Failure("BOX_INVALID");
        }

        double[] coordinates = new double[4];
        int index = 0;
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number ||
                !item.TryGetDouble(out double coordinate) ||
                !double.IsFinite(coordinate))
            {
                throw Failure("BOX_INVALID");
            }
            coordinates[index++] = coordinate;
        }

        double left = coordinates[0];
        double top = coordinates[1];
        double width = coordinates[2];
        double height = coordinates[3];
        double right = left + width;
        double bottom = top + height;
        if (left < 0 || top < 0 || width <= 0 || height <= 0 ||
            !double.IsFinite(right) || !double.IsFinite(bottom) ||
            right > canvasWidth || bottom > canvasHeight)
        {
            throw Failure("BOX_INVALID");
        }
        return new OriginalDbOcrAggregateBox(left, top, right, bottom);
    }

    private static string ReadTrimmedIdentity(JsonElement value, string propertyName, string code)
    {
        if (!TryGetString(value, propertyName, out string? identity) ||
            identity is null || IsPythonBlank(identity))
        {
            throw Failure(code);
        }
        ValidateUnicode(identity);
        return TrimPythonWhitespace(identity);
    }

    private static int ReadPositiveInteger(JsonElement value, string propertyName, string code)
    {
        return value.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out int result) && result > 0
            ? result
            : throw Failure(code);
    }

    private static bool TryGetString(JsonElement value, string propertyName, out string? result)
    {
        if (value.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String)
        {
            result = property.GetString();
            return result is not null;
        }
        result = null;
        return false;
    }

    private static void ValidateUnicode(string value)
    {
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(remaining, out _, out int consumed);
            if (status != OperationStatus.Done)
            {
                throw Failure("UNICODE_INVALID");
            }
            remaining = remaining[consumed..];
        }
    }

    // Python's frozen source selector uses str.strip(). Its whitespace table
    // includes the four ASCII information separators that .NET Trim omits.
    private static bool IsPythonBlank(string value)
    {
        foreach (char character in value)
        {
            if (!IsPythonWhitespace(character))
            {
                return false;
            }
        }
        return true;
    }

    private static string TrimPythonWhitespace(string value)
    {
        int start = 0;
        while (start < value.Length && IsPythonWhitespace(value[start]))
        {
            start++;
        }
        int end = value.Length;
        while (end > start && IsPythonWhitespace(value[end - 1]))
        {
            end--;
        }
        return value[start..end];
    }

    private static bool IsPythonWhitespace(char value) =>
        value is >= '\u0009' and <= '\u000d' or
        >= '\u001c' and <= '\u0020' or
        '\u0085' or '\u00a0' or '\u1680' or
        >= '\u2000' and <= '\u200a' or
        '\u2028' or '\u2029' or '\u202f' or '\u205f' or '\u3000';

    private static void RejectDuplicatePropertyNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Failure("DUPLICATE_JSON_KEY");
                }
                RejectDuplicatePropertyNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicatePropertyNames(item);
            }
        }
    }

    private static void RequireKind(JsonElement value, JsonValueKind expected, string code)
    {
        if (value.ValueKind != expected)
        {
            throw Failure(code);
        }
    }

    private static InvalidDataException Failure(string code) =>
        new("OCR_ANNOTATION_INVALID:" + code);
}
