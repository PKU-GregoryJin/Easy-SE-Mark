using System.Runtime.InteropServices;

namespace EasySE.Media;

/// <summary>Windows PCM output, no external sound runtime or callbacks into disposed memory.</summary>
internal sealed class WindowsAudio : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat
    { public ushort Format, Channels; public uint Rate, BytesPerSecond; public ushort BlockAlign, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Header
    { public nint Data; public uint Length, Recorded; public nuint User; public uint Flags, Loops; public nint Next; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Position { public uint Type, Value, UnionPadding; }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint device, uint id, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutPause(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutRestart(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutSetVolume(nint device, uint volume);
    [DllImport("winmm.dll")] private static extern uint waveOutGetPosition(nint device, ref Position position, uint size);
    private readonly object gate = new();
    private nint device;
    private readonly List<(nint Header, nint Data, int Samples)> buffers = [];
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<Header>();
    private long submitted, wrapped;
    private uint lastPosition;
    public WindowsAudio(bool muted)
    {
        var format = new WaveFormat { Format = 1, Channels = 2, Rate = 48000, BytesPerSecond = 192000, BlockAlign = 4, Bits = 16 };
        Check(waveOutOpen(out device, uint.MaxValue, ref format, 0, 0, 0));
        Check(waveOutPause(device)); Mute(muted);
    }
    private static void Check(uint result) { if (result != 0) throw new InvalidOperationException("Windows 音频设备错误：" + result); }
    public double Seconds
    {
        get
        {
            lock (gate)
            {
                if (device == 0) return 0;
                var position = new Position { Type = 2 }; // TIME_SAMPLES
                Check(waveOutGetPosition(device, ref position, (uint)Marshal.SizeOf<Position>()));
                var value = position.Type == 2 ? position.Value : position.Type == 1 ? (uint)(position.Value * 48L) : position.Value / 4;
                if (value < lastPosition) wrapped += 1L << 32; lastPosition = value;
                return (wrapped + value) / 48000.0;
            }
        }
    }
    public double BufferedSeconds { get { lock (gate) return Math.Max(0, submitted / 48000.0 - Seconds); } }
    public void Mute(bool value) { lock (gate) { if (device != 0) Check(waveOutSetVolume(device, value ? 0 : uint.MaxValue)); } }
    public void Start() { lock (gate) { if (device != 0) Check(waveOutRestart(device)); } }
    public void Write(byte[] bytes)
    {
        lock (gate)
        {
            if (device == 0) return;
            Reclaim();
            var data = Marshal.AllocHGlobal(bytes.Length); var header = Marshal.AllocHGlobal((int)HeaderSize);
            try
            {
                Marshal.Copy(bytes, 0, data, bytes.Length);
                Marshal.StructureToPtr(new Header { Data = data, Length = (uint)bytes.Length }, header, false);
                Check(waveOutPrepareHeader(device, header, HeaderSize));
                var result = waveOutWrite(device, header, HeaderSize);
                if (result != 0) { waveOutUnprepareHeader(device, header, HeaderSize); Check(result); }
                buffers.Add((header, data, bytes.Length / 4)); submitted += bytes.Length / 4;
            }
            catch { Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data); throw; }
        }
    }
    private void Reclaim()
    {
        for (var i = buffers.Count - 1; i >= 0; i--)
        {
            var b = buffers[i]; var h = Marshal.PtrToStructure<Header>(b.Header);
            if ((h.Flags & 1) == 0) continue; // WHDR_DONE
            Check(waveOutUnprepareHeader(device, b.Header, HeaderSize));
            Marshal.FreeHGlobal(b.Header); Marshal.FreeHGlobal(b.Data); buffers.RemoveAt(i);
        }
    }
    public void Stop() { lock (gate) { if (device != 0) waveOutReset(device); } }
    public void Dispose()
    {
        lock (gate)
        {
            if (device == 0) return;
            waveOutReset(device); Reclaim();
            waveOutClose(device); device = 0;
        }
    }
}

public sealed class AudioPlayback : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private readonly object gate = new();
    private WindowsAudio? output;
    private readonly double start, speed;
    private bool muted;
    private volatile bool started;
    public bool Started => started;
    public Exception? Error { get; private set; }
    public double? SourceTime { get { lock (gate) return started && output is not null ? start + output.Seconds * speed : null; } }
    public AudioPlayback(string path, double duration, double start, double speed, bool muted)
    { this.start = start; this.speed = speed; this.muted = muted; worker = Task.Run(() => Work(path, duration)); }
    private void Work(string path, double duration)
    {
        try
        {
            using var source = new AudioTimeline(path, duration, start);
            using var tempo = new AudioTempo(speed);
            using var sink = new WindowsAudio(muted);
            lock (gate) { output = sink; sink.Mute(muted); }
            var end = false;
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (sink.BufferedSeconds > .25) { cancellation.Token.WaitHandle.WaitOne(5); continue; }
                var data = tempo.Receive();
                if (data is not null) { sink.Write(data); if (!started && sink.BufferedSeconds >= .1) { sink.Start(); started = true; } continue; }
                if (end) break;
                var pcm = source.Read(2048, cancellation.Token); tempo.Push(pcm); end = pcm is null;
            }
            if (!started) { sink.Start(); started = true; }
            while (sink.BufferedSeconds > .005) { cancellation.Token.ThrowIfCancellationRequested(); cancellation.Token.WaitHandle.WaitOne(5); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Error = ex; }
        finally { lock (gate) { output = null; started = false; } }
    }
    public void Mute(bool value) { lock (gate) { muted = value; output?.Mute(value); } }
    public void Stop() { cancellation.Cancel(); lock (gate) output?.Stop(); }
    public async ValueTask DisposeAsync() { Stop(); await worker.ConfigureAwait(false); cancellation.Dispose(); }
}
