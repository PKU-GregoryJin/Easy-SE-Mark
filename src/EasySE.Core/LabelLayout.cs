using System.Text;

namespace EasySE.Core;

public sealed record LabelBox(string Id, double X, double Y, double Width, double Height, double FontSize,
    double Padding, IReadOnlyList<string> Lines, string Color, double Start, double End)
{
    public bool Active(double time) => Start <= time + .000001 && End > time + .000001;
    public bool Contains(double x, double y) => Math.Abs(x - X) <= Width / 2 && Math.Abs(y - Y) <= Height / 2;
}
public static class LabelLayout
{
    public static IReadOnlyList<string> Wrap(string name, double maxWidth, Func<string, double> measure)
    {
        var lines = new List<string>(); var line = "";
        foreach (var rune in name.EnumerateRunes())
        {
            var c = rune.ToString();
            if (line.Length > 0 && measure(line + c) > maxWidth) { lines.Add(line); line = c; }
            else line += c;
        }
        if (line.Length > 0) lines.Add(line);
        if (lines.Count == 0) lines.Add("未命名音效");
        return lines;
    }
    public static LabelBox Calculate(Marker marker, double width, double height, double fontSize,
        Func<string, double, double> measure, double defaultY = .88)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var font = fontSize * Math.Min(width / 1280, height / 720);
        IReadOnlyList<string> lines = []; double boxW = 0, boxH = 0, pad = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            pad = font * .35;
            var currentFont = font;
            lines = Wrap(marker.Name, width * .92 - pad * 2, text => measure(text, currentFont));
            boxW = Math.Min(width * .96, lines.Max(line => measure(line, currentFont)) + pad * 2);
            boxH = lines.Count * font * 1.4 + pad * 2;
            if (boxH <= height * .92 || attempt == 11) break;
            font *= Math.Min(.9, height * .88 / boxH);
        }
        return new(marker.Id,
            Math.Clamp((marker.Position?.X ?? .5) * width, boxW / 2, Math.Max(boxW / 2, width - boxW / 2)),
            Math.Clamp((marker.Position?.Y ?? defaultY) * height, boxH / 2, Math.Max(boxH / 2, height - boxH / 2)),
            boxW, boxH, font, pad, lines, marker.Color, marker.Start, marker.End);
    }
    public static IReadOnlyList<LabelBox> CalculateAll(Project project, double width, double height, Func<string, double, double> measure)
    {
        var lanes = Rules.ArrangeLanes(project.Markers, 0).Lanes;
        return project.Markers.OrderBy(m => m.Start).ThenBy(m => m.End).Select(m => Calculate(m, width, height,
            project.Visual.FontSize, measure, Math.Max(.12, .88 - lanes[m.Id] * (project.Visual.FontSize / 720 * 2.1 + .025)))).ToArray();
    }
}
