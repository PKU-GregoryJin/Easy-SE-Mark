namespace EasySE.Core;

/// <summary>One transaction per edit or drag, never per pointer move.</summary>
public sealed class EditorSession
{
    private readonly List<Project> past = [];
    private readonly List<Project> future = [];
    private Project? transaction;
    private string saved = ProjectFile.Serialize(new Project());
    public Project Project { get; private set; } = new();
    public string? SelectedId { get; set; }
    public Marker? Selected => Project.Markers.FirstOrDefault(m => m.Id == SelectedId);
    public bool CanUndo => past.Count > 0;
    public bool CanRedo => future.Count > 0;
    public bool Dirty => ProjectFile.Serialize(Project) != saved;
    public bool InTransaction => transaction is not null;
    public event Action? Changed;
    public void Load(Project project)
    {
        Project = project; past.Clear(); future.Clear(); transaction = null; SelectedId = null;
        MarkSaved(); Changed?.Invoke();
    }
    public void MarkSaved() => saved = ProjectFile.Serialize(Project);
    public void MarkUnsaved() => saved = "";
    public void Begin()
    {
        if (transaction is not null) throw new InvalidOperationException("已有未提交的编辑");
        transaction = Project;
    }
    public void Commit()
    {
        if (transaction is null) return;
        if (ProjectFile.Serialize(transaction) != ProjectFile.Serialize(Project))
        {
            past.Add(transaction); if (past.Count > 100) past.RemoveAt(0); future.Clear();
        }
        transaction = null; Changed?.Invoke();
    }
    public void Cancel()
    {
        if (transaction is null) return;
        Project = transaction; transaction = null; ReconcileSelection(); Changed?.Invoke();
    }
    public void Update(Func<Project, Project> edit)
    {
        var own = transaction is null;
        if (own) Begin();
        try { Project = edit(Project); if (own) Commit(); else Changed?.Invoke(); }
        catch { if (own) Cancel(); throw; }
    }
    public void Undo() => Navigate(past, future);
    public void Redo() => Navigate(future, past);
    private void Navigate(List<Project> from, List<Project> to)
    {
        if (InTransaction || from.Count == 0) return;
        to.Add(Project); Project = from[^1]; from.RemoveAt(from.Count - 1); ReconcileSelection(); Changed?.Invoke();
    }
    private void ReconcileSelection() { if (Selected is null) SelectedId = null; }
    public Marker Add(double start, double? end = null)
    {
        if (Project.Video.Duration < Rules.MinLength) throw new InvalidOperationException("请导入视频");
        if (Project.Markers.Count >= Rules.MaxMarkers) throw new InvalidOperationException("标记上限 5000");
        var marker = Marker.Create(start, end ?? start + 1, Project.Markers.Count, Project.Video.Duration);
        SelectedId = marker.Id;
        Update(p => p with { Markers = p.Markers.Append(marker).ToArray() });
        return marker;
    }
    public void Replace(Marker marker) => Update(p => p with
    { Markers = p.Markers.Select(m => m.Id == marker.Id ? Rules.Normalize(marker, p.Video.Duration) : m).ToArray() });
    public void Rename(string text)
    {
        if (Selected is { } m) Replace(m with { Name = Rules.Name(text.Trim()) });
    }
    public void EditTime(bool start, double value)
    {
        if (Selected is not { } marker) return;
        if (!double.IsFinite(value) || value < 0 || value > Project.Video.Duration ||
            (start ? value > marker.End - Rules.MinLength + 1e-9 : value < marker.Start + Rules.MinLength - 1e-9))
            throw new InvalidOperationException("时间无效");
        Replace(start ? marker with { Start = value } : marker with { End = value });
    }
    public void Delete()
    {
        if (Selected is null) return;
        var id = SelectedId; SelectedId = null;
        Update(p => p with { Markers = p.Markers.Where(m => m.Id != id).ToArray() });
    }
    public void Duplicate()
    {
        if (Selected is not { } marker) return;
        if (Project.Markers.Count >= Rules.MaxMarkers) throw new InvalidOperationException("标记上限 5000");
        var copy = marker with { Id = Guid.NewGuid().ToString(), Name = Rules.Limit(marker.Name + " 副本", 160), Color = Rules.Colors[Project.Markers.Count % Rules.Colors.Count] };
        SelectedId = copy.Id;
        Update(p => p with { Markers = p.Markers.Append(copy).ToArray() });
    }
    public double Snap(double value, double pixelsPerSecond, string? excludeId, double playhead, bool disabled)
    {
        value = Math.Clamp(value, 0, Project.Video.Duration);
        if (disabled) return value;
        var tolerance = 7 / Math.Max(.001, pixelsPerSecond);
        var candidates = new List<double> { 0, Project.Video.Duration, playhead };
        candidates.AddRange(Project.Markers.Where(m => m.Id != excludeId).SelectMany(m => new[] { m.Start, m.End }));
        var nearest = candidates.MinBy(t => Math.Abs(t - value));
        return Math.Abs(nearest - value) < tolerance ? nearest : value;
    }
}
