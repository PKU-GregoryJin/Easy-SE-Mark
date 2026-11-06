using System.Text.Json;

namespace EasySE.Media;

internal sealed class ExportLog : IDisposable
{
    private readonly StreamWriter writer;
    private bool failed;
    public string Path { get; }
    public ExportLog(string cache, string jobId)
    {
        var directory = System.IO.Path.Combine(cache, "logs", "exports"); Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + jobId + ".jsonl");
        writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    }
    public void Write(string stage, object? details = null)
    {
        if (failed) return;
        try { writer.WriteLine(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, stage, details })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { failed = true; }
    }
    public void Dispose() { try { writer.Dispose(); } catch (IOException) { } }
}
