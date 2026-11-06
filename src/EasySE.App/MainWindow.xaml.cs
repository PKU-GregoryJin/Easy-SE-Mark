using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EasySE.Core;
using Microsoft.Win32;

namespace EasySE.App;

public sealed record MarkerRow(Marker Value)
{
    public string Id => Value.Id;
    public string Name => Value.Name;
    public string Color => Value.Color;
    public string StartText => Timecode.Clock(Value.Start, 3);
    public string EndText => Timecode.Clock(Value.End, 3);
    public string DurationText => Value.Duration.ToString("0.00", CultureInfo.InvariantCulture) + "s";
}

public partial class MainWindow : Window
{
    private readonly EditorSession session = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Stopwatch watch = new();
    private bool ready, rendering, playing, muted, fontDragging, preserveList;
    private double time, lastTick, speed = 1;
    private double? rangeStart, stopAt;
    private string? projectPath;
    private TextBox? inline;
    private string? inlineOriginal;
    private bool timelineLayoutQueued, timelineLayoutApplying;
    private (double Time, double Fraction)? timelineAnchor;
    private double SourceFrameRate => TimelineGeometry.FrameRate(media?.Info.FrameRate ?? 30);

    public MainWindow()
    {
        InitializeComponent();
        Timeline.Session = session; Preview.Session = session;
        Timeline.Seek = Seek; Timeline.Pause = Pause;
        Timeline.Selected = id => Select(id, false);
        Timeline.AddRange = (start, end) => Safe(() => Add(start, end));
        Timeline.ScrollBy = delta => TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset + delta);
        Preview.Selected = id => Select(id, false);
        session.Changed += Render;
        timer.Tick += Tick;
        toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; toastTimer.Stop(); };
        TimelineScroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 && !timelineLayoutApplying) ResizeTimeline();
            Timeline.ViewportLeft = TimelineScroll.HorizontalOffset;
            Timeline.ViewportWidth = TimelineScroll.ViewportWidth;
            Timeline.InvalidateVisual();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) { Timeline.CancelDrag(); Preview.CancelDrag(); }
            else ResizeTimeline();
        };
        ready = true; watch.Start(); Render(); timer.Start();
    }
    private void Safe(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { Notify(ex.Message); }
    }
    private void Notify(string text)
    {
        ToastText.Text = text; Toast.Visibility = Visibility.Visible; toastTimer.Stop(); toastTimer.Start();
    }
    private void Render()
    {
        if (!ready) return;
        rendering = true;
        try
        {
            var p = session.Project;
            if (!ProjectTitle.IsKeyboardFocused) ProjectTitle.Text = p.Title;
            SaveDot.Foreground = Draw.Brush(session.Dirty ? "#b79bf1" : "#668f7c");
            MarkerCount.Text = p.Markers.Count.ToString(CultureInfo.InvariantCulture);
            if (inline is null && !session.InTransaction && !preserveList)
            {
                MarkerList.ItemsSource = p.Markers.OrderBy(m => m.Start).ThenBy(m => m.End).Select(m => new MarkerRow(m)).ToArray();
                MarkerList.SelectedItem = MarkerList.Items.Cast<MarkerRow>().FirstOrDefault(row => row.Id == session.SelectedId);
            }
            FontSlider.Value = p.Visual.FontSize;
            if (!FontNumber.IsKeyboardFocused) FontNumber.Text = p.Visual.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
            Resolution.Text = p.Video.Duration > 0 ? $"{p.Video.Width} × {p.Video.Height}" : "";
            DurationText.Text = Timecode.Clock(p.Video.Duration, 3);
            EmptyState.Visibility = p.Video.Duration > 0 ? Visibility.Collapsed : Visibility.Visible;
            RelinkState.Visibility = p.Video.Duration > 0 && media is null ? Visibility.Visible : Visibility.Collapsed;
            RelinkName.Text = p.Video.Name;
            UndoButton.IsEnabled = session.CanUndo; RedoButton.IsEnabled = session.CanRedo;
            DeleteButton.IsEnabled = DuplicateButton.IsEnabled = session.Selected is not null;
            AddButton.IsEnabled = SaveButton.IsEnabled = FontSlider.IsEnabled = FontNumber.IsEnabled = p.Video.Duration >= Rules.MinLength;
            PlayButton.IsEnabled = media is not null;
            ExportButton.IsEnabled = media is not null;
            PlayButton.Content = playing ? "Ⅱ" : "▶";
            InButton.BorderBrush = Draw.Brush(rangeStart is not null ? "#ad86f4" : "#3a3845");
            ResizeTimeline(); RenderTime();
        }
        finally { rendering = false; }
    }
    private void ResizeTimeline()
    {
        if (!ready || timelineLayoutApplying) return;
        if (timelineAnchor is null)
        {
            var width = Timeline.ViewportWidth > 0 ? Timeline.ViewportWidth : TimelineScroll.ViewportWidth;
            timelineAnchor = ((TimelineScroll.HorizontalOffset + width / 2) / Timeline.PixelsPerSecond, .5);
        }
        if (timelineLayoutQueued) return;
        timelineLayoutQueued = true;
        // ScrollViewer's viewport is only authoritative after measure/arrange (including scrollbars).
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            timelineLayoutQueued = false;
            ApplyTimelineLayout();
        });
    }
    private void ApplyTimelineLayout()
    {
        if (timelineLayoutApplying || WindowState == WindowState.Minimized || TimelineScroll.ViewportWidth <= 0) return;
        timelineLayoutApplying = true;
        try
        {
            var anchor = timelineAnchor ?? ((TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth / 2) / Timeline.PixelsPerSecond, .5);
            Timeline.FrameRate = SourceFrameRate;
            // A changed lane count can add/remove the vertical scrollbar; settle its width first.
            for (var pass = 0; pass < 3; pass++)
            {
                var viewport = TimelineScroll.ViewportWidth;
                Timeline.Resize(viewport, Zoom.Value);
                TimelineScroll.UpdateLayout();
                if (Math.Abs(TimelineScroll.ViewportWidth - viewport) < .01) break;
            }
            var offset = Zoom.Value == 0 ? 0 : TimelineGeometry.Offset(anchor.Item1, anchor.Item2, Timeline.PixelsPerSecond, TimelineScroll.ViewportWidth, session.Project.Video.Duration > 0 ? session.Project.Video.Duration : 30);
            TimelineScroll.ScrollToHorizontalOffset(offset);
            TimelineScroll.UpdateLayout();
            Timeline.ViewportLeft = TimelineScroll.HorizontalOffset;
            Timeline.ViewportWidth = TimelineScroll.ViewportWidth;
            Timeline.InvalidateVisual();
            timelineAnchor = null;
        }
        finally { timelineLayoutApplying = false; }
    }
    private void RenderTime()
    {
        CurrentTimeText.Text = Timecode.Clock(time, 3);
        Timeline.Time = time; Timeline.RangeStart = rangeStart; 
        Timeline.InvalidateVisual(); Preview.InvalidateVisual();
    }
    private void Seek(double target)
    {
        time = Math.Clamp(target, 0, session.Project.Video.Duration); media?.Request(time); RenderTime();
    }
    private void Select(string id, bool seek)
    {
        Pause(); session.SelectedId = id;
        if (seek && session.Selected is { } marker) Seek(marker.Start);
        Render();
    }
    private void Pause() { playing = false; stopAt = null; StopAudio(); if (ready) PlayButton.Content = "▶"; }
    private void Play()
    {
        if (media is null) return;
        if (time >= session.Project.Video.Duration - .001) Seek(0);
        playing = true; lastTick = watch.Elapsed.TotalSeconds; PlayButton.Content = "Ⅱ"; StartAudio();
    }
    private void TogglePlay() { if (playing) Pause(); else Play(); }
    private void Tick(object? sender, EventArgs e)
    {
        if (!playing) return;
        var now = watch.Elapsed.TotalSeconds; var elapsed = Math.Min(.25, now - lastTick); lastTick = now;
        var target = Math.Min(session.Project.Video.Duration, time + elapsed * speed);
        if (audio is not null)
        {
            if (audio.Error is { } error && !audioErrorReported) { audioErrorReported = true; Notify(error.Message); StopAudio(); }
            else if (audio.SourceTime is { } audioTime) { audioClockStarted = true; target = Math.Clamp(audioTime, 0, session.Project.Video.Duration); }
            else if (!audioClockStarted) return;
        }
        if (stopAt is { } end && target >= end) { Pause(); Seek(end); }
        else { Seek(target); if (target >= session.Project.Video.Duration) Pause(); }
        var x = time * Timeline.PixelsPerSecond;
        if (x < TimelineScroll.HorizontalOffset || x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth - 10)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - TimelineScroll.ViewportWidth * .2));
    }
    private bool ConfirmDiscard(bool closing = false) => !session.Dirty || MessageBox.Show(this, closing ? "当前项目尚未保存，是否关闭？" : "当前项目尚未保存，是否继续？", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    private void Reset(Project project, string? path = null)
    {
        FinishInline(false); Timeline.CancelDrag(); Preview.CancelDrag(); Pause(); ReleaseMedia();
        time = 0; rangeStart = null; Zoom.Value = 0; timelineAnchor = (0, 0); projectPath = path; session.Load(project); TimelineScroll.ScrollToHome(); Render();
    }
    private void NewClick(object sender, RoutedEventArgs e) { Keyboard.ClearFocus(); if (ConfirmDiscard()) Reset(new()); }
    private void OpenClick(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus(); if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "Easy SE Mark 项目|*.semark.json;*.json;*.semark", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) Open(dialog.FileName);
    }
    private void Open(string path) => Safe(() =>
    {
        var info = new FileInfo(path);
        if (info.Length > 16 * 1024 * 1024) throw new InvalidDataException("项目文件过大");
        var project = ProjectFile.Read(File.ReadAllText(path)); Reset(project, path);
    });
    private void SaveClick(object sender, RoutedEventArgs e) => Save();
    private void Save()
    {
        Keyboard.ClearFocus(); FinishInline(false); CommitTitle();
        if (session.Project.Video.Duration <= 0) return;
        Safe(() =>
        {
            var dialog = new SaveFileDialog { Filter = "Easy SE Mark 项目|*.semark.json", DefaultExt = ".semark.json", AddExtension = true,
                FileName = projectPath is null ? ExportPolicy.SafeName(session.Project.Title, ".semark.json") : Path.GetFileName(projectPath),
                InitialDirectory = projectPath is null ? "" : Path.GetDirectoryName(projectPath), OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            ProjectFile.Save(dialog.FileName, session.Project); projectPath = dialog.FileName; session.MarkSaved(); Render();
        });
    }
    private void Add(double? start = null, double? end = null)
    {
        Pause(); FinishInline(false); var marker = session.Add(start ?? time, end); Seek(marker.Start); Render();
        MarkerList.ScrollIntoView(MarkerList.SelectedItem);
        Dispatcher.BeginInvoke(() =>
        {
            if (MarkerList.SelectedItem is not MarkerRow row || row.Id != marker.Id) return;
            MarkerList.UpdateLayout();
            if (MarkerList.ItemContainerGenerator.ContainerFromItem(row) is not DependencyObject container) return;
            var editor = FindNameEditor(container);
            editor?.Focus();
        });
    }
    private static TextBox? FindNameEditor(DependencyObject root)
    {
        if (root is TextBox box && box.Tag as string == "name") return box;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindNameEditor(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }
    private void AddClick(object sender, RoutedEventArgs e) => Safe(() => Add());
    private void DuplicateClick(object sender, RoutedEventArgs e) => Safe(session.Duplicate);
    private void DeleteClick(object sender, RoutedEventArgs e) => session.Delete();
    private void UndoClick(object sender, RoutedEventArgs e) => session.Undo();
    private void RedoClick(object sender, RoutedEventArgs e) => session.Redo();
    private void InClick(object sender, RoutedEventArgs e) { if (session.Project.Video.Duration <= 0) return; rangeStart = time; Render(); }
    private void OutClick(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (rangeStart is not { } start) throw new InvalidOperationException("请设置起点");
        if (time < start + Rules.MinLength) throw new InvalidOperationException("终点需晚于起点");
        var end = time; rangeStart = null; Add(start, end);
    });
    private void StepFrame(int direction)
    {
        Pause(); Seek(TimelineGeometry.StepFrame(time, direction, SourceFrameRate, session.Project.Video.Duration)); EnsureTimeVisible();
    }
    private void EnsureTimeVisible()
    {
        var x = time * Timeline.PixelsPerSecond;
        if (x < TimelineScroll.HorizontalOffset || x > TimelineScroll.HorizontalOffset + TimelineScroll.ViewportWidth - 10)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - TimelineScroll.ViewportWidth / 2));
    }
    private void PreviousClick(object sender, RoutedEventArgs e) => StepFrame(-1);
    private void NextClick(object sender, RoutedEventArgs e) => StepFrame(1);
    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void MuteClick(object sender, RoutedEventArgs e) { muted = !muted; MuteButton.Content = muted ? "♪×" : "♫"; Safe(() => audio?.Mute(muted)); }
    private void SpeedChanged(object sender, SelectionChangedEventArgs e) { if (ready) { speed = new[] { .5, 1, 1.5, 2 }[Speed.SelectedIndex]; if (playing) StartAudio(); } }
    private void MarkerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && !rendering && MarkerList.SelectedItem is MarkerRow row)
        {
            preserveList = true;
            try { Select(row.Id, true); } finally { preserveList = false; }
        }
    }
    private void BeginInline(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ready || rendering || sender is not TextBox box || box.DataContext is not MarkerRow row) return;
        if (inline is not null) FinishInline(false);
        inline = box; inlineOriginal = box.Text; Pause(); session.SelectedId = row.Id;
        if (session.Selected is { } marker) Seek(marker.Start);
        session.Begin(); Render(); box.SelectAll();
    }
    private void FinishInline(bool cancel)
    {
        if (inline is not { } box) return;
        var field = box.Tag as string; var text = box.Text;
        inline = null;
        if (cancel) { box.Text = inlineOriginal ?? ""; session.Cancel(); }
        else
        {
            try { if (field == "name") session.Rename(text); else session.EditTime(field == "start", Timecode.Parse(text)); session.Commit(); }
            catch (InvalidOperationException ex) { session.Cancel(); Notify(ex.Message); }
        }
        Render();
    }
    private void EndInline(object sender, KeyboardFocusChangedEventArgs e) { if (ReferenceEquals(sender, inline)) FinishInline(false); }
    private void InlineKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        FinishInline(e.Key == Key.Escape); Keyboard.ClearFocus(); e.Handled = true;
    }
    private void CommitTitle()
    {
        var title = Rules.Limit(string.IsNullOrWhiteSpace(ProjectTitle.Text) ? "未命名项目" : ProjectTitle.Text.Trim(), 100);
        session.Update(p => p with { Title = title });
    }
    private void TitleCommit(object sender, KeyboardFocusChangedEventArgs e) { if (ready) CommitTitle(); }
    private void TitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ProjectTitle.Text = session.Project.Title; Keyboard.ClearFocus(); e.Handled = true; }
        else if (e.Key == Key.Enter) { CommitTitle(); Keyboard.ClearFocus(); e.Handled = true; }
    }
    private void FontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || rendering) return;
        session.Update(p => p with { Visual = new(Math.Round(FontSlider.Value)) });
    }
    private void FontDragBegin(object sender, MouseButtonEventArgs e)
    {
        if (!ready) return;
        FinishInline(false);
        if (session.InTransaction) return;
        fontDragging = true; session.Begin();
    }
    private void FontDragEnd(object sender, RoutedEventArgs e)
    {
        if (!fontDragging) return;
        fontDragging = false; session.Commit();
    }
    private void FontNumberCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitFont();
    private void CommitFont()
    {
        if (!ready) return;
        if (double.TryParse(FontNumber.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && double.IsFinite(size))
            session.Update(p => p with { Visual = new(Math.Clamp(size, 12, 288)) });
        FontNumber.Text = session.Project.Visual.FontSize.ToString("0.#", CultureInfo.InvariantCulture);
    }
    private void FontNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitFont(); Keyboard.ClearFocus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { FontNumber.Text = session.Project.Visual.FontSize.ToString(CultureInfo.InvariantCulture); Keyboard.ClearFocus(); e.Handled = true; }
    }
    private void ZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        if (timelineAnchor is null)
        {
            var width = TimelineScroll.ViewportWidth;
            var x = time * Timeline.PixelsPerSecond - TimelineScroll.HorizontalOffset;
            // Toolbar zoom keeps the visible playhead fixed; otherwise preserve the viewport center.
            timelineAnchor = x >= 0 && x <= width && width > 0 ? (time, x / width) : ((TimelineScroll.HorizontalOffset + width / 2) / Timeline.PixelsPerSecond, .5);
        }
        ResizeTimeline();
    }
    private void ZoomOutClick(object sender, RoutedEventArgs e) => Zoom.Value -= 6.25;
    private void ZoomInClick(object sender, RoutedEventArgs e) => Zoom.Value += 6.25;
    private void FitClick(object sender, RoutedEventArgs e) { timelineAnchor = (0, 0); Zoom.Value = 0; ResizeTimeline(); }
    private void TimelineWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (Timeline.IsMouseCaptured) { e.Handled = true; return; }
            var width = TimelineScroll.ViewportWidth;
            var x = Math.Clamp(e.GetPosition(Timeline).X - TimelineScroll.HorizontalOffset, 0, width);
            timelineAnchor = ((TimelineScroll.HorizontalOffset + x) / Timeline.PixelsPerSecond, width > 0 ? x / width : 0);
            Zoom.Value = Math.Clamp(Zoom.Value + e.Delta / 120.0 * 6.25, Zoom.Minimum, Zoom.Maximum);
            ResizeTimeline(); e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            TimelineScroll.ScrollToHorizontalOffset(TimelineScroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }
    private void TimelineResized(object sender, SizeChangedEventArgs e) { if (ready) ResizeTimeline(); }
    private static Key RangeHotkey(KeyEventArgs e, ModifierKeys modifiers)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return Key.None;
        // IME wraps the key event, not the physical key. Handle it before text composition.
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        return key is Key.I or Key.O ? key : Key.None;
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (loading) { e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.S) { Save(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;
        if (RangeHotkey(e, Keyboard.Modifiers) is var rangeKey && rangeKey != Key.None)
        {
            // Holding O must not create another range or produce repeated "set start" prompts.
            if (!e.IsRepeat) { if (rangeKey == Key.I) InClick(this, e); else OutClick(this, e); }
            e.Handled = true; return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.Z) { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) session.Redo(); else session.Undo(); }
            else if (e.Key == Key.Y) session.Redo();
            else if (e.Key == Key.D) Safe(session.Duplicate);
            else return;
        }
        else if (e.Key == Key.Space) TogglePlay();
        else if (e.Key == Key.M) Safe(() => Add());
        else if (e.Key == Key.P && session.Selected is { } marker) { Pause(); Seek(marker.Start); Play(); stopAt = marker.End; }
        else if (e.Key is Key.Delete or Key.Back) session.Delete();
        else if (e.Key is Key.Left or Key.Right)
        {
            var direction = e.Key == Key.Left ? -1 : 1;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { Pause(); Seek(time + direction); EnsureTimeVisible(); }
            else StepFrame(direction);
        }
        else if (e.Key == Key.Escape) { Timeline.CancelDrag(); Preview.CancelDrag(); session.SelectedId = null; rangeStart = null; Render(); }
        else return;
        e.Handled = true;
    }
    private void WindowDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void WindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        var path = paths[0];
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".semark", StringComparison.OrdinalIgnoreCase))
        { Keyboard.ClearFocus(); if (ConfirmDiscard()) Open(path); }
        else if (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase)) await LoadVideoAsync(path);
        else Notify("仅支持 MP4 视频和 Easy SE Mark 项目");
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        Keyboard.ClearFocus(); if (!ConfirmDiscard(closing: true)) { e.Cancel = true; return; }
        timer.Stop(); toastTimer.Stop(); ReleaseMedia();
    }
}
