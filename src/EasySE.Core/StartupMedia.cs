namespace EasySE.Core;

public static class StartupMedia
{
    // Windows passes drag-to-EXE/shortcut paths as separate, already unquoted arguments.
    // The editor has one video slot: load the first MP4 without shell parsing or file associations.
    public static string? Select(IEnumerable<string> arguments) => arguments.FirstOrDefault(path =>
        !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase));
}
