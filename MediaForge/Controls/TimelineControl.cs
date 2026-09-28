using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MediaForge.Core;
using MediaForge.Models;

namespace MediaForge.Controls;

public sealed class TimelineControl : FrameworkElement
{
    private const double RulerHeight = 20;
    private const double MarkerLaneHeight = 26;

    // Muss vor den Pinseln stehen: statische Feldinitialisierer laufen in Quelltextreihenfolge.
    private static readonly Dictionary<string, Brush> ColorCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Brush Background = Brush("#0E1218");
    private static readonly Brush Ruler = Brush("#181E27");
    private static readonly Brush Lane = Brush("#141A23");
    private static readonly Brush Empty = Brush("#1B222D");
    private static readonly Brush Muted = Brush("#939CAA");
    private static readonly Brush Green = Brush("#4CAF7D");
    private static readonly Brush Accent = Brush("#E2696C");
    private static readonly Brush Playhead = Brush("#EEF2F7");
    private static readonly Brush[] ZoneBrushes = [Brush("#45A9CE"), Brush("#D9A441"), Brush("#3FB8A2"), Brush("#5B8CFF"), Brush("#E2696C"), Brush("#8FA0B8")];

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty InPointProperty = DependencyProperty.Register(nameof(InPoint), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutPointProperty = DependencyProperty.Register(nameof(OutPoint), typeof(double), typeof(TimelineControl), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThumbnailsProperty = DependencyProperty.Register(nameof(Thumbnails), typeof(IEnumerable), typeof(TimelineControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, CollectionChanged));
    public static readonly DependencyProperty ZonesProperty = DependencyProperty.Register(nameof(Zones), typeof(IEnumerable), typeof(TimelineControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, CollectionChanged));
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IEnumerable), typeof(TimelineControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, CollectionChanged));
    public static readonly DependencyProperty ClipsProperty = DependencyProperty.Register(nameof(Clips), typeof(IEnumerable), typeof(TimelineControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, CollectionChanged));
    public static readonly DependencyProperty SelectedMarkerProperty = DependencyProperty.Register(nameof(SelectedMarker), typeof(MediaMarker), typeof(TimelineControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double InPoint { get => (double)GetValue(InPointProperty); set => SetValue(InPointProperty, value); }
    public double OutPoint { get => (double)GetValue(OutPointProperty); set => SetValue(OutPointProperty, value); }
    public IEnumerable? Thumbnails { get => (IEnumerable?)GetValue(ThumbnailsProperty); set => SetValue(ThumbnailsProperty, value); }
    public IEnumerable? Zones { get => (IEnumerable?)GetValue(ZonesProperty); set => SetValue(ZonesProperty, value); }

    /// <summary>Marker aus der Sidecar-Datei; ist die Liste gesetzt, bekommt die Zeitleiste eine eigene Markerspur.</summary>
    public IEnumerable? Markers { get => (IEnumerable?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    /// <summary>Abschnitte der Schnittliste; alles außerhalb wird abgedunkelt.</summary>
    public IEnumerable? Clips { get => (IEnumerable?)GetValue(ClipsProperty); set => SetValue(ClipsProperty, value); }

    public MediaMarker? SelectedMarker { get => (MediaMarker?)GetValue(SelectedMarkerProperty); set => SetValue(SelectedMarkerProperty, value); }

    public event EventHandler<double>? SeekRequested;

    /// <summary>Klick auf einen Marker in der Markerspur.</summary>
    public event EventHandler<MediaMarker>? MarkerClicked;

    public TimelineControl()
    {
        Height = 84;
        Cursor = Cursors.Hand;
        Focusable = true;
    }

    private double LaneHeight => Markers is null ? 0 : MarkerLaneHeight;
    private double StripHeight => Math.Max(24, ActualHeight - RulerHeight - LaneHeight);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = e.GetPosition(this);
        if (MarkerAt(point) is { } marker)
        {
            MarkerClicked?.Invoke(this, marker);
            SetCurrentValue(PositionProperty, marker.StartSeconds);
            SeekRequested?.Invoke(this, marker.StartSeconds);
            return;
        }
        CaptureMouse();
        Seek(point.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured) { Seek(e.GetPosition(this).X); return; }
        var marker = MarkerAt(e.GetPosition(this));
        var tip = marker is null ? null : $"{marker.TimeText}  ·  {marker.DisplayTitle}";
        if (!Equals(ToolTip, tip)) ToolTip = tip;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); ReleaseMouseCapture(); }

    private void Seek(double x)
    {
        if (Duration <= 0 || ActualWidth <= 0) return;
        var value = Math.Clamp(x / ActualWidth * Duration, 0, Duration);
        SetCurrentValue(PositionProperty, value);
        SeekRequested?.Invoke(this, value);
    }

    /// <summary>Findet den Marker unter dem Mauszeiger — Bereiche über ihre Breite, Punkte mit etwas Toleranz.</summary>
    private MediaMarker? MarkerAt(Point point)
    {
        var markers = Items<MediaMarker>(Markers);
        if (markers.Count == 0 || Duration <= 0) return null;
        var top = StripHeight;
        if (point.Y < top || point.Y > top + MarkerLaneHeight) return null;
        foreach (var marker in markers.Where(m => m.IsRange))
        {
            if (point.X >= X(marker.StartSeconds) - 2 && point.X <= X(marker.EndSeconds) + 2) return marker;
        }
        return markers.Where(m => !m.IsRange).OrderBy(m => Math.Abs(X(m.StartSeconds) - point.X))
                      .FirstOrDefault(m => Math.Abs(X(m.StartSeconds) - point.X) <= 7);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var stripHeight = StripHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var images = Items<ImageSource>(Thumbnails);
        if (images.Count > 0)
        {
            var width = ActualWidth / images.Count;
            for (var i = 0; i < images.Count; i++) dc.DrawImage(images[i], new Rect(i * width, 0, width + .5, stripHeight));
        }
        else
        {
            dc.DrawRectangle(Empty, null, new Rect(0, 0, ActualWidth, stripHeight));
            DrawText(dc, "(keine Vorschau geladen)", ActualWidth / 2, stripHeight / 2, 11, Muted, TextAlignment.Center);
        }
        dc.DrawRectangle(Ruler, null, new Rect(0, ActualHeight - RulerHeight, ActualWidth, RulerHeight));

        var zones = Items<SpeedZone>(Zones);
        for (var i = 0; i < zones.Count; i++)
        {
            var zone = zones[i]; var x1 = X(zone.Start); var x2 = X(zone.End);
            dc.PushOpacity(.64); dc.DrawRectangle(ZoneBrushes[i % ZoneBrushes.Length], new Pen(Brushes.White, 1), new Rect(x1, 0, Math.Max(1, x2 - x1), stripHeight)); dc.Pop();
            DrawText(dc, $"{zone.Factor:g}x", (x1 + x2) / 2, stripHeight - 13, 11, Brushes.White, TextAlignment.Center);
        }

        if (Clips is not null) DrawClips(dc, stripHeight);

        if (!double.IsNaN(InPoint) || !double.IsNaN(OutPoint))
        {
            var start = double.IsNaN(InPoint) ? 0 : InPoint; var end = double.IsNaN(OutPoint) ? Duration : OutPoint;
            var x1 = X(start); var x2 = X(end);
            dc.PushOpacity(.58); dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, x1, stripHeight)); dc.DrawRectangle(Brushes.Black, null, new Rect(x2, 0, Math.Max(0, ActualWidth - x2), stripHeight)); dc.Pop();
            DrawMarker(dc, x1, Green, "I", stripHeight); DrawMarker(dc, x2, Accent, "O", stripHeight);
        }

        if (Markers is not null) DrawMarkers(dc, stripHeight);

        if (Duration > 0)
        for (var i = 0; i <= 4; i++)
        {
            var fraction = i / 4d; var alignment = i == 0 ? TextAlignment.Left : i == 4 ? TextAlignment.Right : TextAlignment.Center;
            DrawText(dc, Formatters.Time(Duration * fraction)[..8], fraction * ActualWidth, ActualHeight - RulerHeight / 2, 9, Muted, alignment);
        }
        var px = X(Position); dc.DrawLine(new Pen(Playhead, 2), new Point(px, 0), new Point(px, ActualHeight));
        var marker = new StreamGeometry();
        using (var context = marker.Open())
        {
            context.BeginFigure(new Point(px - 5, 0), true, true);
            context.LineTo(new Point(px + 5, 0), true, false);
            context.LineTo(new Point(px, 7), true, false);
        }
        marker.Freeze();
        dc.DrawGeometry(Playhead, null, marker);
    }

    /// <summary>Dunkelt alles ab, was in keinem Clip liegt, und nummeriert die Abschnitte in Listenreihenfolge.</summary>
    private void DrawClips(DrawingContext dc, double stripHeight)
    {
        var clips = Items<EditorClip>(Clips);
        var covered = new List<(double From, double To)>();
        foreach (var clip in clips) covered.Add((X(clip.StartSeconds), X(clip.EndSeconds)));
        covered.Sort((a, b) => a.From.CompareTo(b.From));
        var cursor = 0d;
        dc.PushOpacity(.62);
        foreach (var (from, to) in covered)
        {
            if (from > cursor) dc.DrawRectangle(Brushes.Black, null, new Rect(cursor, 0, from - cursor, stripHeight));
            cursor = Math.Max(cursor, to);
        }
        if (cursor < ActualWidth) dc.DrawRectangle(Brushes.Black, null, new Rect(cursor, 0, ActualWidth - cursor, stripHeight));
        dc.Pop();

        var pen = new Pen(Brush("#7C9CFF"), 1.4);
        foreach (var clip in clips)
        {
            var x1 = X(clip.StartSeconds);
            var x2 = X(clip.EndSeconds);
            if (clip.IsSelected)
            {
                dc.PushOpacity(.18);
                dc.DrawRectangle(Brush("#7C9CFF"), null, new Rect(x1, 0, Math.Max(1, x2 - x1), stripHeight));
                dc.Pop();
            }
            dc.DrawLine(pen, new Point(x1, 0), new Point(x1, stripHeight));
            dc.DrawLine(pen, new Point(x2, 0), new Point(x2, stripHeight));
            if (x2 - x1 < 22) continue;
            dc.DrawRectangle(Brush("#7C9CFF"), null, new Rect(x1 + 2, 2, 17, 13));
            DrawText(dc, clip.Number.ToString(CultureInfo.CurrentCulture), x1 + 10.5, 8.5, 9, Brushes.White, TextAlignment.Center);
        }
    }

    /// <summary>Zeichnet Bereich-Marker als Balken und Punkt-Marker als Nadel in der Markerspur.</summary>
    private void DrawMarkers(DrawingContext dc, double stripHeight)
    {
        var markers = Items<MediaMarker>(Markers);
        var top = stripHeight;
        dc.DrawRectangle(Lane, null, new Rect(0, top, ActualWidth, MarkerLaneHeight));
        var selected = SelectedMarker;

        foreach (var marker in markers.Where(m => m.IsRange))
        {
            var x1 = X(marker.StartSeconds);
            var x2 = Math.Max(x1 + 2, X(marker.EndSeconds));
            var brush = Brush(marker.CategoryColor);
            dc.PushOpacity(.2);
            dc.DrawRectangle(brush, null, new Rect(x1, 0, x2 - x1, stripHeight));
            dc.Pop();
            dc.DrawRoundedRectangle(brush, ReferenceEquals(marker, selected) ? new Pen(Playhead, 1.5) : null,
                new Rect(x1, top + 4, x2 - x1, MarkerLaneHeight - 8), 3, 3);
            if (x2 - x1 > 46) DrawText(dc, marker.DisplayTitle, (x1 + x2) / 2, top + MarkerLaneHeight / 2, 9.5, Brushes.White, TextAlignment.Center);
        }

        foreach (var marker in markers.Where(m => !m.IsRange))
        {
            var x = X(marker.StartSeconds);
            var brush = Brush(marker.CategoryColor);
            dc.PushOpacity(.7);
            dc.DrawLine(new Pen(brush, 1), new Point(x, 0), new Point(x, stripHeight));
            dc.Pop();
            var pin = new StreamGeometry();
            using (var context = pin.Open())
            {
                context.BeginFigure(new Point(x - 5.5, top + 5), true, true);
                context.LineTo(new Point(x + 5.5, top + 5), true, false);
                context.LineTo(new Point(x, top + MarkerLaneHeight - 5), true, false);
            }
            pin.Freeze();
            dc.DrawGeometry(brush, ReferenceEquals(marker, selected) ? new Pen(Playhead, 1.5) : null, pin);
        }
    }

    private void DrawMarker(DrawingContext dc, double x, Brush brush, string label, double stripHeight)
    {
        dc.DrawLine(new Pen(brush, 2), new Point(x, 0), new Point(x, stripHeight));
        dc.DrawRectangle(brush, null, new Rect(x - 8, stripHeight - 14, 16, 14));
        DrawText(dc, label, x, stripHeight - 7, 9, Brushes.White, TextAlignment.Center);
    }

    private double X(double time) => Duration <= 0 ? 0 : Math.Clamp(time / Duration * ActualWidth, 0, ActualWidth);

    private static List<T> Items<T>(IEnumerable? source) => source?.Cast<object>().OfType<T>().ToList() ?? [];

    private static Brush Brush(string color)
    {
        if (ColorCache.TryGetValue(color, out var cached)) return cached;
        var value = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        value.Freeze();
        ColorCache[color] = value;
        return value;
    }

    private void DrawText(DrawingContext dc, string value, double x, double y, double size, Brush color, TextAlignment alignment)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip) { TextAlignment = alignment };
        dc.DrawText(text, new Point(x, y - text.Height / 2));
    }

    private static void CollectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= ((TimelineControl)d).OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection) newCollection.CollectionChanged += ((TimelineControl)d).OnCollectionChanged;
        ((TimelineControl)d).InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}
