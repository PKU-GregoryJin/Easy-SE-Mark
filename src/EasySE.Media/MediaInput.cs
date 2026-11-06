using EasySE.Core;
using FFmpeg.AutoGen;

namespace EasySE.Media;

public sealed record MediaInfo(string Path, double Duration, int Width, int Height, string VideoCodec,
    bool HasAudio, double FrameRate, int Rotation, double Origin)
{
    public VideoInfo ToVideoInfo() => new(System.IO.Path.GetFileName(Path), Duration, Width, Height);
}
public sealed record VideoFrame(byte[] Pixels, int Width, int Height, double Time)
{
    public int Stride => Width * 4;
}

internal sealed unsafe class MediaInput : IDisposable
{
    public AVFormatContext* Format;
    public int VideoIndex { get; }
    public int AudioIndex { get; }
    public MediaInfo Info { get; }
    public MediaInput(string path)
    {
        NativeRuntime.Initialize();
        if (!System.IO.Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("仅支持 MP4 文件");
        path = System.IO.Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("视频文件不存在", path);
        try
        {
            AVFormatContext* format = null;
            AVDictionary* options = null;
            try
            {
                ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
                NativeRuntime.Check(ffmpeg.avformat_open_input(&format, path, null, &options), "打开视频"); Format = format;
            }
            finally { ffmpeg.av_dict_free(&options); }
            NativeRuntime.Check(ffmpeg.avformat_find_stream_info(Format, null), "读取视频信息");
            if (!NativeRuntime.Utf8(Format->iformat->name).Split(',').Contains("mp4")) throw new InvalidOperationException("文件内容不是 MP4");
            VideoIndex = NativeRuntime.Check(ffmpeg.av_find_best_stream(Format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0), "读取视频流");
            AudioIndex = ffmpeg.av_find_best_stream(Format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            var stream = Format->streams[VideoIndex]; var par = stream->codecpar;
            if (par->codec_id is not (AVCodecID.AV_CODEC_ID_H264 or AVCodecID.AV_CODEC_ID_HEVC or AVCodecID.AV_CODEC_ID_AV1))
                throw new InvalidOperationException("仅支持 H.264、H.265、AV1 编码的 MP4");
            var origin = Format->start_time == ffmpeg.AV_NOPTS_VALUE ? stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time * ffmpeg.av_q2d(stream->time_base) : Format->start_time / (double)ffmpeg.AV_TIME_BASE;
            var duration = Format->duration > 0 ? Format->duration / (double)ffmpeg.AV_TIME_BASE : stream->duration * ffmpeg.av_q2d(stream->time_base);
            if (!double.IsFinite(duration) || duration < .04 || duration > 86400 || par->width <= 0 || par->height <= 0 || par->width > 16384 || par->height > 16384)
                throw new InvalidOperationException("视频时长或分辨率无效");
            var rotation = 0;
            var matrix = ffmpeg.av_packet_side_data_get(par->coded_side_data, par->nb_coded_side_data, AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
            if (matrix != null && matrix->size >= 36)
            {
                var angle = -ffmpeg.av_display_rotation_get(*(int_array9*)matrix->data);
                if (double.IsFinite(angle)) rotation = ((int)Math.Round(angle / 90) * 90 % 360 + 360) % 360;
            }
            var rate = ffmpeg.av_q2d(ffmpeg.av_guess_frame_rate(Format, stream, null));
            Info = new(path, duration, rotation % 180 == 0 ? par->width : par->height, rotation % 180 == 0 ? par->height : par->width,
                ffmpeg.avcodec_get_name(par->codec_id), AudioIndex >= 0, double.IsFinite(rate) && rate > 0 ? rate : 30, rotation, origin);
        }
        catch { Dispose(); throw; }
    }
    public AVCodecContext* OpenCodec(int index)
    {
        var par = Format->streams[index]->codecpar;
        var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (codec == null) throw new InvalidOperationException("缺少解码器");
        var context = ffmpeg.avcodec_alloc_context3(codec);
        if (context == null) throw new OutOfMemoryException();
        try
        {
            NativeRuntime.Check(ffmpeg.avcodec_parameters_to_context(context, par), "初始化解码器");
            context->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 8);
            context->pkt_timebase = Format->streams[index]->time_base;
            NativeRuntime.Check(ffmpeg.avcodec_open2(context, codec, null), "启动解码器");
            return context;
        }
        catch { ffmpeg.avcodec_free_context(&context); throw; }
    }
    public static MediaInfo Probe(string path) { using var input = new MediaInput(path); return input.Info; }
    public void Dispose() { var format = Format; if (format != null) ffmpeg.avformat_close_input(&format); Format = null; }
}
public static class MediaProbe { public static MediaInfo Read(string path) => MediaInput.Probe(path); }
