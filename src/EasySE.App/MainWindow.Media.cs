using System.IO;
using System.Windows;
using EasySE.Core;
using EasySE.Media;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasySE.App;

public partial class MainWindow
{
    private PreviewDecoder? media;
    private AudioPlayback? audio;
    private bool loading, audioErrorReported, audioClockStarted;
    private long loadGeneration;
    private CancellationTokenSource? thumbnailsCancellation;
    private void ReleaseMedia()
    {
        loadGeneration++;
        thumbnailsCancellation?.Cancel(); thumbnailsCancellation = null;
        Timeline.Thumbnails = [];
        StopAudio(); var old = media; media = null; Preview.ClearFrame();
        if (old is not null) _ = old.DisposeAsync().AsTask();
    }
    private void StopAudio()
    {
        var old = audio; audio = null;
        if (old is not null) { old.Stop(); _ = old.DisposeAsync().AsTask(); }
        audioClockStarted = false;
    }
    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        var dialog = new OpenFileDialog { Filter = "MP4 视频|*.mp4", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await LoadVideoAsync(dialog.FileName);
    }
    internal async Task LoadStartupMediaAsync(string[] arguments)
    {
        if (StartupMedia.Select(arguments) is { } path) await LoadVideoAsync(path);
        else if (arguments.Length > 0) Notify("仅支持 MP4 视频");
    }
    private async Task LoadVideoAsync(string path)
    {
        if (loading) return;
        Pause(); var generation = ++loadGeneration;
        loading = true; WorkspaceRoot.IsEnabled = false;
        PreviewDecoder? loaded = null;
        try
        {
            loaded = await PreviewDecoder.OpenAsync(path);
            if (generation != loadGeneration) return;
            var info = loaded.Info; var oldProject = session.Project;
            var reconnect = oldProject.Video.Duration > 0 && media is null;
            if (oldProject.Markers.Count > 0 && (!reconnect || oldProject.Video.Name != Path.GetFileName(path) || Math.Abs(info.Duration - oldProject.Video.Duration) > .25) &&
                MessageBox.Show(this, "替换视频并保留标记？超出时长的标记将裁剪。", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            var old = media; media = loaded; loaded = null;
            var current = media;
            current.FrameReady += frame => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(media, current)) Preview.Present(frame); });
            current.Failed += error => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(media, current)) { Pause(); Notify(error.Message); } });
            if (old is not null) _ = old.DisposeAsync().AsTask();
            var title = oldProject.Title == "未命名项目" ? Path.GetFileNameWithoutExtension(path) : oldProject.Title;
            var project = oldProject with
            {
                Title = title, Video = info.ToVideoInfo(),
                Markers = oldProject.Markers.Where(m => m.Start < info.Duration - .039).Select(m => Rules.Normalize(m, info.Duration)).ToArray()
            };
            session.Load(project); if (project != oldProject) session.MarkUnsaved();
            time = 0; rangeStart = null; Zoom.Value = 0; TimelineScroll.ScrollToHome();
            Preview.Present(current.FirstFrame); Render();
            _ = LoadThumbnailsAsync(current);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Notify(ex.Message); }
        finally
        {
            if (loaded is not null) await loaded.DisposeAsync();
            loading = false; WorkspaceRoot.IsEnabled = true;
        }
    }
    private void StartAudio()
    {
        StopAudio(); audioErrorReported = false;
        if (media?.Info.HasAudio == true) audio = new AudioPlayback(media.Info.Path, media.Info.Duration, time, speed, muted);
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        System.Windows.Input.Keyboard.ClearFocus(); FinishInline(false); Pause();
        if (media is null) { Notify("请连接视频"); return; }
        var dialog = new ExportWindow(session.Project, media?.Info.Path) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } result) Notify("已导出：" + result.Path);
    }
    private async Task LoadThumbnailsAsync(PreviewDecoder current)
    {
        thumbnailsCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); thumbnailsCancellation = cancellation;
        try
        {
            var images = await Task.Run(() =>
            {
                using var decoder = new VideoDecoder(current.Info.Path, 160, 90);
                var result = new List<ImageSource>();
                for (var i = 0; i < 12; i++)
                {
                    var frame = decoder.At(current.Info.Duration * i / 12, cancellation.Token);
                    var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Stride); image.Freeze(); result.Add(image);
                }
                return result;
            });
            if (ReferenceEquals(media, current) && !cancellation.IsCancellationRequested) { Timeline.Thumbnails = images; Timeline.InvalidateVisual(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Thumbnails are optional; the main decoder reports playback failures. */ }
        finally { if (ReferenceEquals(thumbnailsCancellation, cancellation)) thumbnailsCancellation = null; }
    }
}
