namespace EasySE.Media;

/// <summary>One decoder operation in flight; dense pointer requests replace a single pending target.</summary>
public sealed class PreviewDecoder : IAsyncDisposable
{
    private readonly VideoDecoder decoder;
    private readonly object gate = new();
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private double? pending;
    private long sequence;
    private bool disposed;
    public MediaInfo Info => decoder.Info;
    public VideoFrame FirstFrame { get; }
    public int CompletedRequests { get; private set; }
    public event Action<VideoFrame>? FrameReady;
    public event Action<Exception>? Failed;
    private PreviewDecoder(VideoDecoder decoder, VideoFrame first)
    { this.decoder = decoder; FirstFrame = first; worker = Task.Run(Work); }
    public static Task<PreviewDecoder> OpenAsync(string path) => Task.Run(() =>
    {
        var decoder = new VideoDecoder(path);
        try { return new PreviewDecoder(decoder, decoder.At(0)); } catch { decoder.Dispose(); throw; }
    });
    public void Request(double time)
    {
        lock (gate)
        {
            if (disposed) return;
            pending = time; sequence++;
            if (signal.CurrentCount == 0) signal.Release();
        }
    }
    private async Task Work()
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync(stop.Token).ConfigureAwait(false);
                double? target; long revision;
                lock (gate) { target = pending; pending = null; revision = sequence; }
                if (target is not { } time) continue;
                try
                {
                    var frame = decoder.At(time, stop.Token); CompletedRequests++;
                    // Do not clear the UI while seeking. Publish a completed frame even if a newer
                    // request is pending, so slow machines still show progress while scrubbing.
                    if (!stop.IsCancellationRequested) FrameReady?.Invoke(frame);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) { if (!stop.IsCancellationRequested) Failed?.Invoke(ex); }
                lock (gate) { if (revision != sequence && signal.CurrentCount == 0) signal.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { decoder.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; stop.Cancel(); }
        await worker.ConfigureAwait(false); signal.Dispose(); stop.Dispose();
    }
}
