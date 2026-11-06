using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace EasySE.Media;

/// <summary>Pitch-preserving speed adjustment using FFmpeg's atempo, not device pitch changes.</summary>
public sealed unsafe class AudioTempo : IDisposable
{
    private AVFilterGraph* graph;
    private AVFilterContext* source;
    private AVFilterContext* sink;
    private AVFrame* frame;
    private long position;
    public AudioTempo(double speed)
    {
        NativeRuntime.Initialize();
        if (speed is < .5 or > 2) throw new ArgumentOutOfRangeException(nameof(speed));
        try
        {
            graph = ffmpeg.avfilter_graph_alloc(); frame = ffmpeg.av_frame_alloc();
            if (graph == null || frame == null) throw new OutOfMemoryException();
            graph->nb_threads = 2;
            AVFilterContext* start = null; AVFilterContext* tempo = null; AVFilterContext* format = null; AVFilterContext* end = null;
            NativeRuntime.Check(ffmpeg.avfilter_graph_create_filter(&start, ffmpeg.avfilter_get_by_name("abuffer"), "in",
                "time_base=1/48000:sample_rate=48000:sample_fmt=s16:channel_layout=stereo", null, graph), "初始化音频输入"); source = start;
            NativeRuntime.Check(ffmpeg.avfilter_graph_create_filter(&tempo, ffmpeg.avfilter_get_by_name("atempo"), "speed", speed.ToString(System.Globalization.CultureInfo.InvariantCulture), null, graph), "初始化倍速音频");
            NativeRuntime.Check(ffmpeg.avfilter_graph_create_filter(&format, ffmpeg.avfilter_get_by_name("aformat"), "format", "sample_fmts=s16:sample_rates=48000:channel_layouts=stereo", null, graph), "初始化音频格式");
            NativeRuntime.Check(ffmpeg.avfilter_graph_create_filter(&end, ffmpeg.avfilter_get_by_name("abuffersink"), "out", null, null, graph), "初始化音频输出"); sink = end;
            NativeRuntime.Check(ffmpeg.avfilter_link(source, 0, tempo, 0), "连接音频输入");
            NativeRuntime.Check(ffmpeg.avfilter_link(tempo, 0, format, 0), "连接倍速音频");
            NativeRuntime.Check(ffmpeg.avfilter_link(format, 0, sink, 0), "连接音频输出");
            NativeRuntime.Check(ffmpeg.avfilter_graph_config(graph, null), "启动音频过滤器");
        }
        catch { Dispose(); throw; }
    }
    public void Push(byte[]? bytes)
    {
        if (bytes is null) { NativeRuntime.Check(ffmpeg.av_buffersrc_add_frame_flags(source, null, 0), "完成倍速音频"); return; }
        ffmpeg.av_frame_unref(frame); frame->nb_samples = bytes.Length / 4;
        frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16; frame->sample_rate = 48000;
        ffmpeg.av_channel_layout_default(&frame->ch_layout, 2); frame->pts = position; position += frame->nb_samples;
        NativeRuntime.Check(ffmpeg.av_frame_get_buffer(frame, 0), "分配音频缓冲");
        Marshal.Copy(bytes, 0, (nint)frame->data[0], bytes.Length);
        NativeRuntime.Check(ffmpeg.av_buffersrc_add_frame_flags(source, frame, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF), "处理倍速音频");
        ffmpeg.av_frame_unref(frame);
    }
    public byte[]? Receive()
    {
        var result = ffmpeg.av_buffersink_get_frame(sink, frame);
        if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return null;
        NativeRuntime.Check(result, "读取倍速音频");
        var bytes = new byte[frame->nb_samples * 4]; Marshal.Copy((nint)frame->data[0], bytes, 0, bytes.Length);
        ffmpeg.av_frame_unref(frame); return bytes;
    }
    public void Dispose()
    {
        var f = frame; if (f != null) ffmpeg.av_frame_free(&f); frame = null;
        var g = graph; if (g != null) ffmpeg.avfilter_graph_free(&g); graph = null; source = sink = null;
    }
}
