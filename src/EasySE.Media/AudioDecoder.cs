using FFmpeg.AutoGen;

namespace EasySE.Media;

public sealed record PcmChunk(byte[] Data, double Time)
{
    public int Samples => Data.Length / 4;
}
/// <summary>First audio stream, resampled to interleaved stereo signed-16 at 48 kHz.</summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    private readonly MediaInput input;
    private AVCodecContext* codec;
    private AVPacket* packet;
    private AVFrame* frame;
    private SwrContext* resampler;
    private bool draining, eof;
    private double endTime;
    public const int SampleRate = 48000;
    public AudioDecoder(string path, double start = 0)
    {
        input = new(path);
        try
        {
            if (input.AudioIndex < 0) throw new InvalidOperationException("视频没有音频流");
            codec = input.OpenCodec(input.AudioIndex); packet = ffmpeg.av_packet_alloc(); frame = ffmpeg.av_frame_alloc();
            if (packet == null || frame == null) throw new OutOfMemoryException();
            var layout = new AVChannelLayout(); ffmpeg.av_channel_layout_default(&layout, 2);
            SwrContext* context = null;
            NativeRuntime.Check(ffmpeg.swr_alloc_set_opts2(&context, &layout, AVSampleFormat.AV_SAMPLE_FMT_S16, SampleRate,
                &codec->ch_layout, codec->sample_fmt, codec->sample_rate, 0, null), "初始化音频重采样");
            resampler = context; ffmpeg.av_channel_layout_uninit(&layout);
            NativeRuntime.Check(ffmpeg.swr_init(resampler), "启动音频重采样");
            if (start > 0)
            {
                var tb = ffmpeg.av_q2d(input.Format->streams[input.AudioIndex]->time_base);
                NativeRuntime.Check(ffmpeg.av_seek_frame(input.Format, input.AudioIndex, (long)((start + input.Info.Origin) / tb), ffmpeg.AVSEEK_FLAG_BACKWARD), "定位音频");
                ffmpeg.avcodec_flush_buffers(codec);
            }
        }
        catch { Dispose(); throw; }
    }
    public PcmChunk? Read(CancellationToken cancellation = default)
    {
        while (!eof)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = ffmpeg.avcodec_receive_frame(codec, frame);
            if (result == ffmpeg.AVERROR_EOF) { eof = true; break; }
            if (result >= 0)
            {
                var delay = ffmpeg.swr_get_delay(resampler, codec->sample_rate);
                var capacity = checked((int)ffmpeg.av_rescale_rnd(delay + frame->nb_samples, SampleRate, codec->sample_rate, AVRounding.AV_ROUND_UP));
                var bytes = new byte[capacity * 4]; int samples;
                fixed (byte* data = bytes) { var output = data; samples = NativeRuntime.Check(ffmpeg.swr_convert(resampler, &output, capacity, frame->extended_data, frame->nb_samples), "转换音频"); }
                var pts = frame->best_effort_timestamp;
                var time = pts == ffmpeg.AV_NOPTS_VALUE ? endTime : pts * ffmpeg.av_q2d(input.Format->streams[input.AudioIndex]->time_base) - input.Info.Origin - delay / (double)codec->sample_rate;
                endTime = time + samples / (double)SampleRate;
                ffmpeg.av_frame_unref(frame);
                if (samples == 0) continue;
                if (samples * 4 != bytes.Length) Array.Resize(ref bytes, samples * 4);
                return new(bytes, time);
            }
            if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) NativeRuntime.Check(result, "解码音频");
            if (draining) { eof = true; break; }
            do
            {
                ffmpeg.av_packet_unref(packet); result = ffmpeg.av_read_frame(input.Format, packet);
                if (result < 0)
                {
                    if (result != ffmpeg.AVERROR_EOF) NativeRuntime.Check(result, "读取音频数据");
                    NativeRuntime.Check(ffmpeg.avcodec_send_packet(codec, null), "完成音频解码"); draining = true; break;
                }
                cancellation.ThrowIfCancellationRequested();
            } while (packet->stream_index != input.AudioIndex);
            if (!draining) NativeRuntime.Check(ffmpeg.avcodec_send_packet(codec, packet), "提交音频数据");
        }
        var remaining = (int)ffmpeg.swr_get_delay(resampler, SampleRate);
        if (remaining <= 0) return null;
        var tail = new byte[remaining * 4]; int written;
        fixed (byte* data = tail) { var output = data; written = NativeRuntime.Check(ffmpeg.swr_convert(resampler, &output, remaining, null, 0), "完成音频重采样"); }
        if (written == 0) return null;
        Array.Resize(ref tail, written * 4); var chunk = new PcmChunk(tail, endTime); endTime += written / (double)SampleRate; return chunk;
    }
    public void Dispose()
    {
        var r = resampler; if (r != null) ffmpeg.swr_free(&r); resampler = null;
        var p = packet; if (p != null) ffmpeg.av_packet_free(&p); packet = null;
        var f = frame; if (f != null) ffmpeg.av_frame_free(&f); frame = null;
        var c = codec; if (c != null) ffmpeg.avcodec_free_context(&c); codec = null;
        input.Dispose();
    }
}

/// <summary>Maps timestamps to an exact PCM timeline, honoring initial offsets and gaps.</summary>
public sealed class AudioTimeline : IDisposable
{
    private readonly AudioDecoder decoder;
    private readonly long total;
    private long position;
    private PcmChunk? chunk;
    private bool ended;
    public AudioTimeline(string path, double duration, double start = 0)
    { decoder = new(path, start); total = (long)Math.Ceiling(duration * AudioDecoder.SampleRate); position = (long)Math.Round(start * AudioDecoder.SampleRate); }
    public byte[]? Read(int samples, CancellationToken cancellation = default)
    {
        var count = (int)Math.Min(samples, total - position); if (count <= 0) return null;
        var output = new byte[count * 4]; var written = 0;
        while (written < count)
        {
            cancellation.ThrowIfCancellationRequested();
            if (chunk is null && !ended) { chunk = decoder.Read(cancellation); if (chunk is null) ended = true; }
            if (chunk is null) { position += count - written; break; }
            var begin = (long)Math.Round(chunk.Time * AudioDecoder.SampleRate);
            if (begin + chunk.Samples <= position) { chunk = null; continue; }
            if (begin > position) { var gap = (int)Math.Min(count - written, begin - position); position += gap; written += gap; continue; }
            var offset = (int)(position - begin); var copy = Math.Min(count - written, chunk.Samples - offset);
            Buffer.BlockCopy(chunk.Data, offset * 4, output, written * 4, copy * 4); written += copy; position += copy;
            if (offset + copy == chunk.Samples) chunk = null;
        }
        return output;
    }
    public void Dispose() => decoder.Dispose();
}
