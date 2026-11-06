using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EasySE.Core;

namespace EasySE.App;

public sealed class TimelineSurface : FrameworkElement
{
    private readonly DispatcherTimer edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    public TimelineSurface()
    {
        Focusable = true;
        edgeTimer.Tick += (_, _) =>
        {
            if (mode is null || !IsMouseCaptured) { edgeTimer.Stop(); return; }
            var x = Mouse.GetPosition(this).X - ViewportLeft;
            var delta = x < 24 ? -Math.Min(18, (24 - x) * .6) : x > ViewportWidth - 24 ? Math.Min(18, (x - ViewportWidth + 24) * .6) : 0;
            if (delta != 0) ScrollBy?.Invoke(delta);
            UpdateDrag(Mouse.GetPosition(this));
        };
    }
    public EditorSession? Session { get; set; }
    public double Time { get; set; }
    public double? RangeStart { get; set; }
    public double PixelsPerSecond { get; set; } = 50;
    public Action<double>? Seek { get; set; }
    public Action<string>? Selected { get; set; }
    public Action<double, double>? AddRange { get; set; }
    public Action? Pause { get; set; }
    public Action<double>? ScrollBy { get; set; }
    public double FrameRate { get; set; } = 30;
    private string? mode;
    private double origin;
    private double dragEnd;
    private Marker? original;
    private bool moved;
    private double lastPointerX, pointerTravel;
    public double ViewportLeft { get; set; }
    public double ViewportWidth { get; set; }
    public IReadOnlyList<ImageSource> Thumbnails { get; set; } = [];
    private double Duration => Session?.Project.Video.Duration ?? 0;
    public void Resize(double viewport, double zoom)
    {
        if (!double.IsFinite(viewport) || viewport <= 0) return;
        PixelsPerSecond = TimelineGeometry.Scale(viewport, Duration, zoom, FrameRate);
        Width = Math.Max(viewport, (Duration > 0 ? Duration : 30) * PixelsPerSecond);
        Height = Math.Max(186, 82 + (Session is null ? 2 : Rules.ArrangeLanes(Session.Project.Markers).Count) * 44 + 12);
        InvalidateVisual();
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Draw.Brush("#17181f"), null, new(RenderSize));
        dc.DrawRectangle(Draw.Brush("#1d1c25"), null, new(0, 0, ActualWidth, 28));
        dc.DrawRectangle(Draw.Brush("#141419"), null, new(0, 28, ActualWidth, 54));
        var dur = Duration > 0 ? Duration : 30;
        var steps = new[] { .001, .002, .005, .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600, 7200, 14400 };
        var step = steps.FirstOrDefault(s => s * PixelsPerSecond >= 70, 14400);
        var left = ViewportLeft;
        var right = left + (ViewportWidth > 0 ? ViewportWidth : ActualWidth) + 100;
        // Draw only visible ruler ticks on heavily zoomed long projects.
        for (var t = Math.Max(0, Math.Floor(left / PixelsPerSecond / step) * step); t < dur && t * PixelsPerSecond <= right; t += step)
        {
            var x = t * PixelsPerSecond;
            dc.DrawLine(Draw.Pen("#55666071"), new(x, 0), new(x, 28));
            Draw.TextAt(dc, Timecode.Clock(t, step < .1 ? 3 : step < 1 ? 2 : 0), x + 6, 6, 9, "#777180", true);
            for (var i = 1; i < 5 && t + step * i / 5 < dur; i++)
                dc.DrawLine(Draw.Pen("#49424f"), new((t + step * i / 5) * PixelsPerSecond, 22), new((t + step * i / 5) * PixelsPerSecond, 28));
        }
        var frameStep = 1 / TimelineGeometry.FrameRate(FrameRate);
        if (frameStep * PixelsPerSecond >= 8)
        {
            for (var frame = Math.Max(0, Math.Ceiling(left / PixelsPerSecond / frameStep)); frame * frameStep <= dur && frame * frameStep * PixelsPerSecond <= right; frame++)
            {
                var x = frame * frameStep * PixelsPerSecond;
                dc.DrawLine(Draw.Pen("#665b74"), new(x, 28), new(x, 33));
            }
        }
        var clipW = Math.Max(1, dur * PixelsPerSecond);
        dc.DrawRoundedRectangle(Draw.Brush(Duration > 0 ? "#272031" : "#18202731"), Draw.Pen("#59486c"), new(0, 34, clipW, 42), 4, 4);
        if (Thumbnails.Count > 0)
        {
            dc.PushOpacity(.3); dc.PushClip(new RectangleGeometry(new Rect(0, 35, clipW, 40)));
            var tile = clipW / Thumbnails.Count;
            for (var i = 0; i < Thumbnails.Count; i++)
            {
                if ((i + 1) * tile < left || i * tile > right) continue;
                dc.DrawImage(Thumbnails[i], new Rect(i * tile, 35, tile, 40));
            }
            dc.Pop(); dc.Pop();
        }
        Draw.TextAt(dc, Session?.Project.Video.Name ?? "", 12, 47, 10, "#ac95c7");
        var lanes = Session is null ? new LaneLayout(new Dictionary<string, int>(), 2) : Rules.ArrangeLanes(Session.Project.Markers);
        for (var i = 0; i <= lanes.Count; i++) dc.DrawLine(Draw.Pen("#282430"), new(0, 82 + i * 44), new(ActualWidth, 82 + i * 44));
        if (Session is not null)
        {
            foreach (var marker in Session.Project.Markers)
            {
                var rect = RectFor(marker, lanes.Lanes);
                if (rect.Right < left || rect.Left > right) continue;
                var color = (Color)ColorConverter.ConvertFromString(marker.Color);
                var selected = marker.Id == Session.SelectedId;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(selected ? (byte)65 : (byte)35, color.R, color.G, color.B)), Draw.Pen(marker.Color), rect, 4, 4);
                dc.DrawLine(Draw.Pen(marker.Color, 3), new(rect.Left + 1.5, rect.Top + 2), new(rect.Left + 1.5, rect.Bottom - 2));
                if (rect.Width > 20)
                {
                    dc.PushClip(new RectangleGeometry(new(rect.Left + 5, rect.Top, Math.Max(0, rect.Width - 10), rect.Height)));
                    Draw.TextAt(dc, marker.Name, rect.Left + 8, rect.Top + 9, 10, marker.Color); dc.Pop();
                }
            }
        }
        if (RangeStart is { } range) dc.DrawRectangle(Draw.Brush("#12a78bfa"), Draw.Pen("#88bd9ce9"), new(Math.Min(Time, range) * PixelsPerSecond, 0, Math.Max(1, Math.Abs(Time - range) * PixelsPerSecond), ActualHeight));
        if (mode == "range") dc.DrawRectangle(Draw.Brush("#22a78bfa"), Draw.Pen("#ad8af1"), new(Math.Min(origin, dragEnd) * PixelsPerSecond, 82, Math.Max(1, Math.Abs(origin - dragEnd) * PixelsPerSecond), ActualHeight - 82));
        var playX = Time * PixelsPerSecond;
        dc.DrawLine(Draw.Pen("#b697e2"), new(playX, 12), new(playX, ActualHeight));
        var head = new StreamGeometry(); using (var c = head.Open()) { c.BeginFigure(new(playX - 5, 0), true, true); c.PolyLineTo(new Point[] { new(playX + 5, 0), new(playX + 5, 8), new(playX, 13), new(playX - 5, 8) }, true, false); }
        dc.DrawGeometry(Draw.Brush("#ccacf7"), null, head);
    }
    private Rect RectFor(Marker m, IReadOnlyDictionary<string, int> lanes) => new(m.Start * PixelsPerSecond, 90 + lanes[m.Id] * 44, Math.Max(3, m.Duration * PixelsPerSecond), 33);
    private double At(double x) => Math.Clamp(x / PixelsPerSecond, 0, Duration);
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Session is null || Duration <= 0) return;
        Focus(); Pause?.Invoke(); var p = e.GetPosition(this); origin = At(p.X); dragEnd = origin; moved = false;
        lastPointerX = p.X; pointerTravel = 0;
        var lanes = Rules.ArrangeLanes(Session.Project.Markers).Lanes;
        original = Session.Project.Markers.Reverse().FirstOrDefault(m => RectFor(m, lanes).Contains(p));
        if (p.Y < 82 || (original is null && Math.Abs(p.X - Time * PixelsPerSecond) < 5)) { mode = "seek"; original = null; Seek?.Invoke(origin); }
        else if (original is not null)
        {
            var rect = RectFor(original, lanes); mode = p.X - rect.Left < 7 ? "start" : rect.Right - p.X < 7 ? "end" : "move";
            Selected?.Invoke(original.Id); Session.Begin();
        }
        else { mode = "range"; }
        CaptureMouse(); edgeTimer.Start(); e.Handled = true; InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        UpdateDrag(e.GetPosition(this));
        if (mode is not null) e.Handled = true;
    }
    private void UpdateDrag(Point p)
    {
        if (mode is null || Session is null) return;
        var fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var delta = p.X - lastPointerX; lastPointerX = p.X; pointerTravel += Math.Abs(delta);
        if (Math.Abs(delta) < .000001) return;
        var value = TimelineGeometry.Drag(dragEnd, delta, PixelsPerSecond, fine, Duration); dragEnd = value;
        if (pointerTravel > 3) moved = true;
        if (mode == "seek") Seek?.Invoke(value);
        else if (mode == "range") InvalidateVisual();
        else if (moved && original is { } m)
        {
            var shift = fine || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            double Snap(double t) => Session.Snap(t, PixelsPerSecond, m.Id, Time, shift);
            if (mode == "start") Session.Replace(m with { Start = Math.Clamp(Snap(m.Start + value - origin), 0, m.End - Rules.MinLength) });
            else if (mode == "end") Session.Replace(m with { End = Math.Clamp(Snap(m.End + value - origin), m.Start + Rules.MinLength, Duration) });
            else
            {
                var start = Math.Clamp(m.Start + value - origin, 0, Duration - m.Duration);
                var snapStart = Snap(start); var snapEnd = Snap(start + m.Duration) - m.Duration;
                var snapped = Math.Abs(snapStart - start) <= Math.Abs(snapEnd - start) ? snapStart : snapEnd;
                start = Math.Clamp(snapped, 0, Duration - m.Duration);
                Session.Replace(m with { Start = start, End = start + m.Duration });
            }
        }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { OnMouseMove(e); Finish(false); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { Finish(true); base.OnLostMouseCapture(e); }
    public void CancelDrag() => Finish(true);
    private void Finish(bool cancel)
    {
        var previousMode = mode; if (previousMode is null) return;
        var clickedMarker = original;
        mode = null; original = null; edgeTimer.Stop();
        if (previousMode is "move" or "start" or "end") { if (cancel) Session?.Cancel(); else Session?.Commit(); }
        ReleaseMouseCapture();
        if (!cancel && clickedMarker is not null && !moved) Seek?.Invoke(clickedMarker.Start);
        if (!cancel && previousMode == "range" && moved && Math.Abs(dragEnd - origin) >= Rules.MinLength) AddRange?.Invoke(Math.Min(origin, dragEnd), Math.Max(origin, dragEnd));
        else if (!cancel && previousMode == "range") Seek?.Invoke(origin);
        InvalidateVisual();
    }
}
