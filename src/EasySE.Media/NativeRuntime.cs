using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FFmpeg.AutoGen;

namespace EasySE.Media;

public static unsafe class NativeRuntime
{
    private static readonly object Gate = new();
    private static bool initialized;
    public static string Version { get; private set; } = "";
    public static string LibraryDirectory { get; private set; } = "";
    public static void Initialize()
    {
        lock (Gate)
        {
            if (initialized) return;
            var assembly = typeof(NativeRuntime).Assembly;
            using var stream = assembly.GetManifestResourceStream("EasySE.Media.Native.manifest.json") ?? throw new InvalidOperationException("视频组件清单缺失");
            using var manifest = JsonDocument.Parse(stream);
            var files = manifest.RootElement.GetProperty("files");
            var version = manifest.RootElement.GetProperty("version").GetString()!;
            var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest.RootElement.GetRawText())))[..16].ToLowerInvariant();
            var root = Environment.GetEnvironmentVariable("EASYSE_CACHE_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasySEMark", "cache");
            var directory = Path.Combine(root, "native", version + "-" + fingerprint);
            Directory.CreateDirectory(directory);
            foreach (var file in files.EnumerateArray())
            {
                var name = file.GetProperty("name").GetString()!;
                if (Path.GetFileName(name) != name) throw new InvalidDataException("视频组件文件名无效");
                var expected = file.GetProperty("sha256").GetString()!;
                var path = Path.Combine(directory, name);
                if (!Valid(path, expected))
                {
                    var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var input = assembly.GetManifestResourceStream("EasySE.Media.Native." + name) ?? throw new InvalidDataException("视频组件缺失: " + name))
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                        if (!Valid(temporary, expected)) throw new InvalidDataException("视频组件校验失败: " + name);
                        try { File.Move(temporary, path, true); }
                        catch (IOException) when (Valid(path, expected)) { }
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                // Load dependencies in manifest order, always by absolute path (never the CWD).
                NativeLibrary.Load(path);
            }
            ffmpeg.RootPath = directory;
            if ((ffmpeg.avcodec_version() >> 16) != ffmpeg.LIBAVCODEC_VERSION_MAJOR ||
                (ffmpeg.avformat_version() >> 16) != ffmpeg.LIBAVFORMAT_VERSION_MAJOR ||
                (ffmpeg.avutil_version() >> 16) != ffmpeg.LIBAVUTIL_VERSION_MAJOR)
                throw new InvalidOperationException("视频组件与绑定版本不匹配");
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            foreach (var id in new[] { AVCodecID.AV_CODEC_ID_H264, AVCodecID.AV_CODEC_ID_HEVC, AVCodecID.AV_CODEC_ID_AV1 })
                if (ffmpeg.avcodec_find_decoder(id) == null) throw new InvalidOperationException("缺少视频解码器: " + id);
            if (ffmpeg.avcodec_find_encoder_by_name("libx264") == null || ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC) == null)
                throw new InvalidOperationException("缺少 H.264/AAC 编码器");
            LibraryDirectory = directory;
            Version = ffmpeg.av_version_info() ?? version;
            initialized = true;
        }
    }
    private static bool Valid(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
    public static int Check(int result, string operation)
    {
        if (result >= 0) return result;
        var buffer = stackalloc byte[1024]; ffmpeg.av_strerror(result, buffer, 1024);
        throw new InvalidOperationException(operation + "：" + Marshal.PtrToStringUTF8((nint)buffer));
    }
    internal static string Utf8(byte* value) => Marshal.PtrToStringUTF8((nint)value) ?? "";
}
