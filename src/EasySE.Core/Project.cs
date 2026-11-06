using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EasySE.Core;

public sealed record Position(double X, double Y);
public sealed record VideoInfo(string Name = "", double Duration = 0, int Width = 1280, int Height = 720);
public sealed record VisualSettings(double FontSize = Rules.DefaultFontSize);
public sealed record Marker(string Id, string Name, double Start, double End, string Color, Position? Position = null)
{
    [JsonIgnore] public double Duration => End - Start;
    public static Marker Create(double start, double end, int index, double duration) =>
        Rules.Normalize(new(Guid.NewGuid().ToString(), "未命名音效", start, end,
            Rules.Colors[index % Rules.Colors.Count]), duration);
}
public sealed record Project
{
    public string Format { get; init; } = "easy-se-mark";
    public int Version { get; init; } = 2;
    public string Title { get; init; } = "未命名项目";
    public VideoInfo Video { get; init; } = new();
    public IReadOnlyList<Marker> Markers { get; init; } = Array.Empty<Marker>();
    public VisualSettings Visual { get; init; } = new();
}

public static class Rules
{
    public const int MaxMarkers = 5000;
    public const double MinLength = .04;
    public const double DefaultFontSize = 120;
    public const double MaxFontSize = 288;
    public static IReadOnlyList<string> Colors { get; } = Array.AsReadOnly(new[]
        { "#a78bfa", "#5dd8c5", "#f5b96b", "#72aaff", "#f38caf", "#d6db78" });
    public static string Limit(string value, int count) => value.Length > count ? value[..count] : value;
    public static string Name(string? name) => Limit(string.IsNullOrEmpty(name) ? "未命名音效" : name.Replace('\r', ' ').Replace('\n', ' '), 160);
    public static Marker Normalize(Marker marker, double duration)
    {
        var limit = double.IsFinite(duration) && duration > 0 ? duration : double.PositiveInfinity;
        var start = Math.Clamp(double.IsFinite(marker.Start) ? marker.Start : 0, 0, Math.Max(0, limit - MinLength));
        var end = double.IsFinite(marker.End) && marker.End != 0 ? marker.End : start + 1;
        Position? position = marker.Position is { } p && double.IsFinite(p.X) && double.IsFinite(p.Y)
            ? new(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1)) : null;
        return marker with
        {
            Id = string.IsNullOrEmpty(marker.Id) ? Guid.NewGuid().ToString() : marker.Id,
            Name = Name(marker.Name), Start = start, End = Math.Min(Math.Max(end, start + MinLength), limit),
            Color = Colors.Contains(marker.Color) ? marker.Color : Colors[0], Position = position
        };
    }
    public static LaneLayout ArrangeLanes(IEnumerable<Marker> markers, double epsilon = .00001)
    {
        var ends = new List<double>();
        var lanes = new Dictionary<string, int>();
        foreach (var marker in markers.OrderBy(m => m.Start).ThenBy(m => m.End))
        {
            var lane = ends.FindIndex(end => end <= marker.Start + epsilon);
            if (lane < 0) { lane = ends.Count; ends.Add(marker.End); }
            else ends[lane] = marker.End;
            lanes[marker.Id] = lane;
        }
        return new(lanes, Math.Max(2, ends.Count));
    }
}
public sealed record LaneLayout(IReadOnlyDictionary<string, int> Lanes, int Count);

public static partial class Timecode
{
    [GeneratedRegex(@"^\d+(?::[0-5]?\d){0,2}(?:\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();
    public static double Parse(string text)
    {
        text = text.Trim();
        if (!TimePattern().IsMatch(text)) return double.NaN;
        double seconds = 0;
        foreach (var part in text.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return double.NaN;
            seconds = seconds * 60 + value;
        }
        return double.IsFinite(seconds) ? seconds : double.NaN;
    }
    public static string Clock(double seconds, int precision = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(precision);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 6);
        if (!double.IsFinite(seconds)) seconds = 0;
        var factor = (long)Math.Pow(10, precision);
        var value = checked((long)Math.Floor(Math.Max(0, seconds) * factor + .5));
        var whole = value / factor;
        return $"{whole / 60:00}:{whole % 60:00}" + (precision == 0 ? "" : "." + (value % factor).ToString(new string('0', precision), CultureInfo.InvariantCulture));
    }
}

public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static string Serialize(Project project) => JsonSerializer.Serialize(project, Options);
    public static Project Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != "easy-se-mark" ||
                Number(root, "version", 0) is not (1 or 2) || !root.TryGetProperty("markers", out var rawMarkers) ||
                rawMarkers.ValueKind != JsonValueKind.Array || rawMarkers.GetArrayLength() > Rules.MaxMarkers)
                throw new InvalidDataException("项目格式无效");
            if (!root.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("项目视频时长无效");
            var duration = Number(video, "duration", double.NaN, true);
            if (!double.IsFinite(duration) || duration <= 0 || duration > 86400) throw new InvalidDataException("项目视频时长无效");
            var width = Number(video, "width", 1280);
            var height = Number(video, "height", 720);
            if (!ValidDimension(width) || !ValidDimension(height)) throw new InvalidDataException("视频分辨率无效");
            var markers = new List<Marker>(); var ids = new HashSet<string>();
            foreach (var item in rawMarkers.EnumerateArray())
            {
                var start = Number(item, "start", double.NaN);
                var end = Number(item, "end", double.NaN);
                if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
                    throw new InvalidDataException("项目内有无效的音效时间段");
                Position? position = null;
                if (item.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    var x = Number(p, "x", double.NaN); var y = Number(p, "y", double.NaN);
                    if (double.IsFinite(x) && double.IsFinite(y)) position = new(x, y);
                }
                var marker = Rules.Normalize(new(Text(item, "id"), Text(item, "name"), start, end, Text(item, "color"), position), duration);
                if (!ids.Add(marker.Id)) { marker = marker with { Id = Guid.NewGuid().ToString() }; ids.Add(marker.Id); }
                markers.Add(marker);
            }
            var font = root.TryGetProperty("visual", out var visual) ? Number(visual, "fontSize", Rules.DefaultFontSize, true) : Rules.DefaultFontSize;
            if (!double.IsFinite(font) || font == 0) font = Rules.DefaultFontSize;
            return new()
            {
                Title = Rules.Limit(Text(root, "title", "未命名项目"), 100),
                Video = new(Text(video, "name"), duration, (int)width, (int)height),
                Markers = markers.AsReadOnly(), Visual = new(Math.Clamp(font, 12, Rules.MaxFontSize))
            };
        }
        catch (JsonException ex) { throw new InvalidDataException("项目格式无效", ex); }
    }
    private static bool ValidDimension(double value) => double.IsFinite(value) && value > 0 && value <= 16384 && value == Math.Floor(value);
    private static string Text(JsonElement obj, string name, string fallback = "") =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()) ? value.GetString()! : fallback;
    private static double Number(JsonElement obj, string name, double fallback, bool acceptString = false)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (acceptString && value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out number)) return number;
        return double.NaN;
    }
    // The sibling temporary file keeps a failed write from destroying the previous project.
    public static void Save(string path, Project project)
    {
        var full = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(temporary, Serialize(project), new System.Text.UTF8Encoding(false)); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
