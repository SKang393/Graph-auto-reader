// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Sungwoo Kang

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using GraphReader.App.Models;
using GraphReader.App.Services;
using GraphReader.App.ViewModels;
using GraphReader.Domain;
using AppGraphPoint = GraphReader.App.Models.GraphPoint;

namespace GraphReader.App.Controls;

public sealed class ManualReviewOverlay : FrameworkElement
{
    private readonly ObservableCollection<AppGraphPoint> _points;
    private readonly ObservableCollection<SeriesCardViewModel> _series;
    private readonly ObservableCollection<EditablePhaseDivider> _dividers;
    private Point[] _axisProposal = [];
    private MarkerExemplarRectangle? _markerExemplar;
    private Point[] _markerCandidates = [];

    public void SetAxisProposal(IEnumerable<Point> anchors)
    {
        _axisProposal = anchors.ToArray();
        InvalidateVisual();
    }

    public void SetMarkerExemplar(MarkerExemplarRectangle? exemplar, IEnumerable<MarkerExemplarPoint> candidates)
    {
        _markerExemplar = exemplar;
        _markerCandidates = candidates.Select(candidate => new Point(candidate.X, candidate.Y)).ToArray();
        InvalidateVisual();
    }

    public ManualReviewOverlay(
        ObservableCollection<AppGraphPoint> points,
        ObservableCollection<SeriesCardViewModel> series,
        ObservableCollection<EditablePhaseDivider> dividers,
        double width,
        double height)
    {
        _points = points;
        _series = series;
        _dividers = dividers;
        Width = width;
        Height = height;
        IsHitTestVisible = false;
        _points.CollectionChanged += OnPointsChanged;
        _series.CollectionChanged += OnSeriesChanged;
        _dividers.CollectionChanged += OnDividersChanged;
        foreach (AppGraphPoint point in _points)
        {
            point.PropertyChanged += OnItemChanged;
        }

        foreach (EditablePhaseDivider divider in _dividers)
        {
            divider.PropertyChanged += OnItemChanged;
        }

        foreach (SeriesCardViewModel item in _series)
        {
            item.PropertyChanged += OnItemChanged;
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_axisProposal.Length == 3)
        {
            var axisPen = new Pen(Brushes.DeepSkyBlue, 2) { DashStyle = DashStyles.Dash };
            drawingContext.DrawLine(axisPen, _axisProposal[0], _axisProposal[1]);
            drawingContext.DrawLine(axisPen, _axisProposal[0], _axisProposal[2]);
            foreach (Point anchor in _axisProposal)
                drawingContext.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 2), anchor, 5, 5);
        }
        if (_markerExemplar is { } exemplar)
        {
            var samplePen = new Pen(Brushes.Gold, 1.5) { DashStyle = DashStyles.Dash };
            drawingContext.DrawRectangle(Brushes.Transparent, samplePen,
                new Rect(exemplar.X, exemplar.Y, exemplar.Width, exemplar.Height));
        }
        var candidatePen = new Pen(Brushes.OrangeRed, 2);
        foreach (Point candidate in _markerCandidates)
        {
            drawingContext.DrawEllipse(Brushes.Transparent, candidatePen, candidate, 6, 6);
            drawingContext.DrawLine(candidatePen, new Point(candidate.X - 3, candidate.Y),
                new Point(candidate.X + 3, candidate.Y));
            drawingContext.DrawLine(candidatePen, new Point(candidate.X, candidate.Y - 3),
                new Point(candidate.X, candidate.Y + 3));
        }
        var dividerPen = new Pen(Brushes.DarkSlateGray, 1) { DashStyle = DashStyles.Dash };
        foreach (EditablePhaseDivider divider in _dividers)
        {
            drawingContext.DrawLine(dividerPen, new Point(divider.OriginalX, 0), new Point(divider.OriginalX, Height));
        }

        foreach (AppGraphPoint point in _points)
        {
            SeriesCardViewModel? series = _series.FirstOrDefault(item => item.SeriesId == point.SeriesId);
            if (series is null || !series.IsVisible)
            {
                continue;
            }

            Brush fill = series.Fill switch
            {
                MarkerFill.Filled => Brushes.DodgerBlue,
                MarkerFill.Open => Brushes.White,
                _ => Brushes.Transparent,
            };
            var pen = new Pen(Brushes.Black, 1.5)
            {
                DashStyle = series.Fill == MarkerFill.Unknown ? DashStyles.Dot : DashStyles.Solid,
            };
            Point center = new(point.PixelX, point.PixelY);
            switch (series.Shape)
            {
                case MarkerShape.Square:
                    drawingContext.DrawRectangle(fill, pen, new Rect(point.PixelX - 4, point.PixelY - 4, 8, 8));
                    break;
                case MarkerShape.Diamond:
                    drawingContext.PushTransform(new RotateTransform(45, point.PixelX, point.PixelY));
                    drawingContext.DrawRectangle(fill, pen, new Rect(point.PixelX - 3.5, point.PixelY - 3.5, 7, 7));
                    drawingContext.Pop();
                    break;
                case MarkerShape.TriangleUp:
                    DrawPolygon(
                        drawingContext,
                        fill,
                        pen,
                        new Point(point.PixelX, point.PixelY - 5),
                        new Point(point.PixelX - 5, point.PixelY + 4),
                        new Point(point.PixelX + 5, point.PixelY + 4));
                    break;
                case MarkerShape.TriangleDown:
                    DrawPolygon(
                        drawingContext,
                        fill,
                        pen,
                        new Point(point.PixelX - 5, point.PixelY - 4),
                        new Point(point.PixelX + 5, point.PixelY - 4),
                        new Point(point.PixelX, point.PixelY + 5));
                    break;
                case MarkerShape.Star:
                    DrawStar(drawingContext, fill, pen, center);
                    break;
                case MarkerShape.Asterisk:
                    DrawAsterisk(drawingContext, pen, center);
                    break;
                case MarkerShape.Cross:
                    drawingContext.DrawLine(
                        pen,
                        new Point(point.PixelX - 4, point.PixelY - 4),
                        new Point(point.PixelX + 4, point.PixelY + 4));
                    drawingContext.DrawLine(
                        pen,
                        new Point(point.PixelX - 4, point.PixelY + 4),
                        new Point(point.PixelX + 4, point.PixelY - 4));
                    break;
                default:
                    drawingContext.DrawEllipse(fill, pen, center, 4, 4);
                    break;
            }
        }
    }

    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (AppGraphPoint point in e.OldItems)
            {
                point.PropertyChanged -= OnItemChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (AppGraphPoint point in e.NewItems)
            {
                point.PropertyChanged += OnItemChanged;
            }
        }

        InvalidateVisual();
    }

    private void OnDividersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (EditablePhaseDivider divider in e.OldItems)
            {
                divider.PropertyChanged -= OnItemChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (EditablePhaseDivider divider in e.NewItems)
            {
                divider.PropertyChanged += OnItemChanged;
            }
        }

        InvalidateVisual();
    }

    private void OnSeriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        if (e.OldItems is not null)
        {
            foreach (SeriesCardViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnItemChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (SeriesCardViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnItemChanged;
            }
        }

        InvalidateVisual();
    }

    private static void DrawPolygon(
        DrawingContext drawingContext,
        Brush fill,
        Pen pen,
        params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: true, isClosed: true);
            context.PolyLineTo(
                new ArraySegment<Point>(points, 1, points.Length - 1),
                isStroked: true,
                isSmoothJoin: true);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(fill, pen, geometry);
    }

    private static void DrawStar(DrawingContext drawingContext, Brush fill, Pen pen, Point center)
    {
        var points = new Point[10];
        for (int index = 0; index < points.Length; index++)
        {
            double radius = index % 2 == 0 ? 5 : 2.25;
            double angle = (-Math.PI / 2) + (index * Math.PI / 5);
            points[index] = new Point(
                center.X + (Math.Cos(angle) * radius),
                center.Y + (Math.Sin(angle) * radius));
        }

        DrawPolygon(drawingContext, fill, pen, points);
    }

    private static void DrawAsterisk(DrawingContext drawingContext, Pen pen, Point center)
    {
        for (int index = 0; index < 3; index++)
        {
            double angle = index * Math.PI / 3;
            double x = Math.Cos(angle) * 5;
            double y = Math.Sin(angle) * 5;
            drawingContext.DrawLine(
                pen,
                new Point(center.X - x, center.Y - y),
                new Point(center.X + x, center.Y + y));
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        InvalidateVisual();
    }
}
