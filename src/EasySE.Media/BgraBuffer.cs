using FFmpeg.AutoGen;

namespace EasySE.Media;

/// <summary>FFmpeg-owned, SIMD-aligned image storage. Never let swscale write into a packed managed array.</summary>
internal sealed unsafe class BgraBuffer : IDisposable
{
    private AVFrame* frame;
    public AVFrame* Frame => frame;
    private readonly int width, height, rowBytes;
    public BgraBuffer(int width, int height)
    {
        this.width = width; this.height = height; rowBytes = checked(width * 4);
        frame = ffmpeg.av_frame_alloc();
        if (frame == null) throw new OutOfMemoryException();
        try
        {
            frame->width = width; frame->height = height; frame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            NativeRuntime.Check(ffmpeg.av_frame_get_buffer(frame, 64), "分配对齐画面缓冲");
        }
        catch { Dispose(); throw; }
    }
    public byte[] CopyPixels()
    {
        var pixels = new byte[checked(rowBytes * height)];
        fixed (byte* destination = pixels)
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(frame->data[0] + y * frame->linesize[0], destination + y * rowBytes, rowBytes, rowBytes);
        return pixels;
    }
    public void CopyFrom(VideoFrame source)
    {
        if (source.Width != width || source.Height != height || source.Pixels.Length != checked(rowBytes * height))
            throw new InvalidOperationException("导出画面分辨率不匹配");
        fixed (byte* input = source.Pixels)
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(input + y * rowBytes, frame->data[0] + y * frame->linesize[0], frame->linesize[0], rowBytes);
    }
    public void Dispose() { var f = frame; if (f != null) ffmpeg.av_frame_free(&f); frame = null; }
}
