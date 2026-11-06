using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EasySE.Core;

namespace EasySE.App;

internal static class Draw
{
    public static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    public static Pen Pen(string color, double width = 1) => new(Brush(color), width);
    public static FormattedText Text(string text, double size, string color = "#e6e6ef", bool mono = false, bool bold = false) =>
        new(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily(mono ? "Consolas" : "Microsoft YaHei, Segoe UI"), FontStyles.Normal,
                bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, Brush(color), 1);
    public static double Measure(string text, double size) => Text(text, size, bold: true).WidthIncludingTrailingWhitespace;
    public static void TextAt(DrawingContext dc, string text, double x, double y, double size, string color = "#e6e6ef", bool mono = false) => dc.DrawText(Text(text, size, color, mono), new(x, y));
    public static void Label(DrawingContext dc, LabelBox box, bool selected)
    {
        var rect = new Rect(box.X - box.Width / 2, box.Y - box.Height / 2, box.Width, box.Height);
        dc.DrawRoundedRectangle(Brush("#bf080b12"), null, rect, box.FontSize * .12, box.FontSize * .12);
        for (var i = 0; i < box.Lines.Count; i++)
        {
            var text = Text(box.Lines[i], box.FontSize, box.Color, bold: true);
            var centerY = rect.Top + box.Padding + box.FontSize * .7 + i * box.FontSize * 1.4;
            dc.DrawText(text, new(box.X - text.WidthIncludingTrailingWhitespace / 2, centerY - text.Height / 2));
        }
        if (selected)
        {
            var pen = Pen(box.Color, Math.Max(1, box.FontSize * .035)); pen.DashStyle = DashStyles.Dash;
            dc.DrawRectangle(null, pen, rect);
        }
    }
}
