using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EasySE.Core;
using EasySE.Media;
using System.Windows.Media.Imaging;

namespace EasySE.App;

public sealed class PreviewSurface : FrameworkElement
{
    public PreviewSurface() { Focusable = true; }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
    public EditorSession? Session { get; set; }
    private WriteableBitmap? image;
    private double frameTime;
    public bool HasFrame => image is not null;
    public double DisplayTime => frameTime;
    public void Present(VideoFrame frame)
    {
        if (image is null || image.PixelWidth != frame.Width || image.PixelHeight != frame.Height)
            image = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
        image.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
        frameTime = frame.Time; InvalidateVisual();
    }
    public void ClearFrame() { image = null; frameTime = 0; InvalidateVisual(); }
    public Action<string>? Selected { get; set; }
    private Point? origin;
    private LabelBox? dragBox;
    private Marker? dragMarker;
    private Project? layoutProject;
    private IReadOnlyList<LabelBox> layouts = [];
    private (double Scale, double Left, double Top) View
    {
        get
        {
            var video = Session?.Project.Video ?? new();
            var scale = Math.Min(ActualWidth / video.Width, ActualHeight / video.Height);
            return (scale, (ActualWidth - video.Width * scale) / 2, (ActualHeight - video.Height * scale) / 2);
        }
    }
    private Point Local(Point p) { var v = View; return new((p.X - v.Left) / Math.Max(.00001, v.Scale), (p.Y - v.Top) / Math.Max(.00001, v.Scale)); }
    private IReadOnlyList<LabelBox> Boxes
    {
        get
        {
            if (Session is null) return [];
            if (!ReferenceEquals(layoutProject, Session.Project))
            {
                layoutProject = Session.Project;
                layouts = LabelLayout.CalculateAll(layoutProject, layoutProject.Video.Width, layoutProject.Video.Height, Draw.Measure);
            }
            return layouts;
        }
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Draw.Brush("#090a0e"), null, new(RenderSize));
        if (Session is null || image is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var v = View;
        var video = Session.Project.Video;
        dc.PushClip(new RectangleGeometry(new Rect(v.Left, v.Top, video.Width * v.Scale, video.Height * v.Scale)));
        dc.PushTransform(new TranslateTransform(v.Left, v.Top)); dc.PushTransform(new ScaleTransform(v.Scale, v.Scale));
        dc.DrawImage(image, new Rect(0, 0, video.Width, video.Height));
        foreach (var box in Boxes.Where(b => b.Active(DisplayTime))) Draw.Label(dc, box, box.Id == Session.SelectedId);
        dc.Pop(); dc.Pop(); dc.Pop();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Session is null || image is null) return;
        Focus(); var point = Local(e.GetPosition(this));
        var box = Boxes.Reverse().FirstOrDefault(b => b.Active(DisplayTime) && b.Contains(point.X, point.Y));
        if (box is null) return;
        Selected?.Invoke(box.Id);
        dragMarker = Session.Selected; dragBox = box; origin = point;
        Session.Begin(); CaptureMouse(); Cursor = Cursors.SizeAll; e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = Local(e.GetPosition(this));
        if (Session is not null && origin is { } start && dragBox is { } box && dragMarker is { } marker)
        {
            var video = Session.Project.Video;
            Session.Replace(marker with { Position = new(
                Math.Clamp(box.X + point.X - start.X, box.Width / 2, video.Width - box.Width / 2) / video.Width,
                Math.Clamp(box.Y + point.Y - start.Y, box.Height / 2, video.Height - box.Height / 2) / video.Height) });
            InvalidateVisual();
        }
        else Cursor = Boxes.Any(b => b.Active(DisplayTime) && b.Contains(point.X, point.Y)) ? Cursors.Hand : Cursors.Arrow;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { OnMouseMove(e); Finish(false); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { Finish(true); base.OnLostMouseCapture(e); }
    public void CancelDrag() => Finish(true);
    private void Finish(bool cancel)
    {
        if (origin is null) return;
        origin = null; dragBox = null; dragMarker = null;
        if (cancel) Session?.Cancel(); else Session?.Commit();
        ReleaseMouseCapture(); Cursor = Cursors.Arrow; InvalidateVisual();
    }
}
