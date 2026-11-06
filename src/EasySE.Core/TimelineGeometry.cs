namespace EasySE.Core;

/// <summary>Viewport-independent timeline math, in device-independent pixels and seconds.</summary>
public static class TimelineGeometry
{
    public static double FrameRate(double rate) => double.IsFinite(rate) && rate > 0 ? Math.Clamp(rate, .001, 1000) : 30;

    public static double Scale(double viewport, double duration, double zoom, double frameRate = 30)
    {
        viewport = double.IsFinite(viewport) && viewport > 0 ? viewport : 1;
        duration = double.IsFinite(duration) && duration > 0 ? duration : 30;
        zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, 0, 100) : 0;
        var fit = viewport / duration;
        // Even long recordings can reach millisecond-scale pointing and visible frame divisions.
        var maximum = Math.Max(fit * 16, Math.Max(1000, FrameRate(frameRate) * 16));
        return fit * Math.Pow(maximum / fit, zoom / 100);
    }

    public static double Offset(double anchorTime, double fraction, double scale, double viewport, double duration) =>
        Math.Clamp(anchorTime * scale - Math.Clamp(fraction, 0, 1) * viewport, 0, Math.Max(0, duration * scale - viewport));

    public static double Drag(double value, double deltaPixels, double scale, bool fine, double duration) =>
        Math.Clamp(value + deltaPixels / scale * (fine ? .1 : 1), 0, duration);

    public static double StepFrame(double time, int direction, double rate, double duration)
    {
        rate = FrameRate(rate);
        // Step to a frame boundary, not by a fixed 30 fps increment or a fractional-frame offset.
        var frame = direction < 0 ? Math.Ceiling(time * rate - .00001) - 1 : Math.Floor(time * rate + .00001) + 1;
        return Math.Clamp(frame / rate, 0, duration);
    }
}
