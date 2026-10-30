// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using GraphReader.Axis;
using GraphReader.App.ViewModels;

namespace GraphReader.App.Models;

public sealed record LocalizedChoice<T>(T Value, string Label)
    where T : struct, Enum;

public sealed class SeriesRelationChoiceViewModel : ObservableObject
{
    private bool _isSelected;

    public SeriesRelationChoiceViewModel(string? seriesId, string label, bool isSelected = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        SeriesId = seriesId;
        Label = label;
        _isSelected = isSelected;
    }

    public string? SeriesId { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed record ManualCalibrationState(
    PixelPoint Session1Y0,
    PixelPoint Session1YMaximum,
    PixelPoint SessionMaximumY0,
    double YMaximum,
    double XMaximum,
    LinearAxisTransform XTransform,
    LinearAxisTransform YTransform,
    double Confidence,
    double FirstVisibleSession = 1);

public sealed class EditablePhaseDivider : ObservableObject
{
    private double _originalX;
    private string _code;
    private string _label;

    public EditablePhaseDivider(string dividerId, double originalX, string code, string label)
    {
        DividerId = dividerId;
        _originalX = originalX;
        _code = code;
        _label = label;
    }

    public string DividerId { get; }

    public double OriginalX
    {
        get => _originalX;
        internal set => SetProperty(ref _originalX, value);
    }

    public string Code
    {
        get => _code;
        internal set => SetProperty(ref _code, value);
    }

    public string Label
    {
        get => _label;
        internal set => SetProperty(ref _label, value);
    }
}
