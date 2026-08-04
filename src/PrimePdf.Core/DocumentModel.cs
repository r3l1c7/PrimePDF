namespace PrimePdf.Core;

/// <summary>One page in the document being assembled, pointing back at the file it came from.</summary>
public sealed class PageEntry
{
    public required PdfSource Source { get; init; }
    public required int SourceIndex { get; init; }

    /// <summary>Extra rotation the user applied here, on top of whatever the file already had.</summary>
    public int ExtraRotation { get; set; }

    public List<Mark> Marks { get; init; } = new();

    public (double W, double H) BaseSize => Source.PageSize(SourceIndex);

    public PageTransform Transform
    {
        get
        {
            var (w, h) = BaseSize;
            return new PageTransform(w, h, PageTransform.Normalize(ExtraRotation));
        }
    }

    /// <summary>
    /// A page only has to be rasterised when something must genuinely disappear from it.
    /// Pages that are merely signed or annotated keep their original vector text.
    /// </summary>
    public bool NeedsFlatten => Marks.Any(m => m.RequiresFlatten);

    public bool HasMarks => Marks.Count > 0;

    /// <summary>
    /// True when the marks on this page can be laid over the original as a small image
    /// instead of replacing the whole page with a picture of itself.
    ///
    /// Rasterising a page to add a signature costs well over a hundred kilobytes and
    /// throws away its text layer, which is a steep price for one pen stroke. Overlaying
    /// only works while the mark space and the PDF's own page space agree, so pages that
    /// carry rotation fall back to flattening rather than risk landing in the wrong place.
    /// </summary>
    public bool CanOverlay =>
        HasMarks
        && !NeedsFlatten
        && PageTransform.Normalize(ExtraRotation) == 0
        && Source.PageRotation(SourceIndex) == 0
        // A CropBox offset means mark space and the page's own drawing space disagree,
        // and the overlay would land shifted by that amount.
        && Math.Abs(Source.CropOrigin(SourceIndex).X) < 0.01
        && Math.Abs(Source.CropOrigin(SourceIndex).Y) < 0.01;

    public PageEntry CloneShallowMarks() => new()
    {
        Source = Source,
        SourceIndex = SourceIndex,
        ExtraRotation = ExtraRotation,
        Marks = Marks.Select(m => m.Clone()).ToList(),
    };
}

/// <summary>
/// The document the user is building: an ordered list of pages drawn from one or more
/// opened files, plus every edit made to them. Undo works on whole-document snapshots,
/// which keeps page reordering and mark editing on one simple, reliable code path.
/// </summary>
public sealed class DocumentModel : IDisposable
{
    private readonly List<PdfSource> _sources = new();
    private readonly Stack<List<PageEntry>> _undo = new();
    private readonly Stack<List<PageEntry>> _redo = new();

    public List<PageEntry> Pages { get; private set; } = new();

    /// <summary>The file the document started from; drives the suggested save name.</summary>
    public string? PrimaryPath { get; private set; }

    public bool IsDirty { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event Action? Changed;

    public bool IsEmpty => Pages.Count == 0;

    public string Title => PrimaryPath is null ? "No file open" : Path.GetFileName(PrimaryPath);

    // ---------------------------------------------------------------- loading

    /// <summary>
    /// Replaces the document with an already-parsed file.
    ///
    /// Parsing is the slow part and callers are expected to do it off the UI thread; this
    /// method only touches the model, so it must be called back on the thread that owns
    /// the <see cref="Changed"/> subscribers.
    /// </summary>
    public void SetSingle(PdfSource source, string path)
    {
        Reset();
        _sources.Add(source);
        PrimaryPath = path;
        for (int i = 0; i < source.PageCount; i++)
            Pages.Add(new PageEntry { Source = source, SourceIndex = i });
        IsDirty = false;
        Changed?.Invoke();
    }

    /// <summary>Adds the pages of an already-parsed file — this is how combining works.</summary>
    public int Append(PdfSource source, int? insertAt = null)
    {
        _sources.Add(source);
        PushUndo();

        int at = Math.Clamp(insertAt ?? Pages.Count, 0, Pages.Count);
        for (int i = 0; i < source.PageCount; i++)
            Pages.Insert(at + i, new PageEntry { Source = source, SourceIndex = i });

        PrimaryPath ??= source.FilePath;
        MarkDirty();
        return source.PageCount;
    }

    /// <summary>Convenience for tests and scripts: parse and adopt in one step.</summary>
    public void OpenSingle(string path, string? password = null) =>
        SetSingle(PdfSource.Open(path, password), path);

    /// <summary>Convenience for tests and scripts: parse and append in one step.</summary>
    public int AppendFile(string path, string? password = null, int? insertAt = null) =>
        Append(PdfSource.Open(path, password), insertAt);

    private void Reset()
    {
        foreach (var s in _sources) s.Dispose();
        _sources.Clear();
        Pages = new List<PageEntry>();
        _undo.Clear();
        _redo.Clear();
        PrimaryPath = null;
        IsDirty = false;
    }

    public void Close()
    {
        Reset();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------ undo / redo

    public void PushUndo()
    {
        _undo.Push(Snapshot());
        _redo.Clear();
        if (_undo.Count > 100) TrimUndo();
    }

    private void TrimUndo()
    {
        var keep = _undo.ToArray().Take(100).Reverse().ToList();
        _undo.Clear();
        foreach (var s in keep) _undo.Push(s);
    }

    private List<PageEntry> Snapshot() => Pages.Select(p => p.CloneShallowMarks()).ToList();

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Snapshot());
        Pages = _undo.Pop();
        MarkDirty();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Snapshot());
        Pages = _redo.Pop();
        MarkDirty();
    }

    public void MarkDirty()
    {
        IsDirty = true;
        Changed?.Invoke();
    }

    public void MarkSaved()
    {
        IsDirty = false;
        Changed?.Invoke();
    }

    // ----------------------------------------------------------- page editing

    public void AddMark(int pageIndex, Mark mark)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        PushUndo();
        Pages[pageIndex].Marks.Add(mark);
        MarkDirty();
    }

    /// <summary>Swaps one mark for another as a single undo step.</summary>
    public void ReplaceMark(int pageIndex, Mark existing, Mark replacement)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        PushUndo();

        var marks = Pages[pageIndex].Marks;
        int at = marks.FindIndex(m => m.Id == existing.Id);
        if (at >= 0) marks[at] = replacement;
        else marks.Add(replacement);

        MarkDirty();
    }

    public void RemoveMark(int pageIndex, Mark mark)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        PushUndo();
        Pages[pageIndex].Marks.RemoveAll(m => m.Id == mark.Id);
        MarkDirty();
    }

    public void ClearPageMarks(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count) return;
        if (Pages[pageIndex].Marks.Count == 0) return;
        PushUndo();
        Pages[pageIndex].Marks.Clear();
        MarkDirty();
    }

    public void RotatePages(IEnumerable<int> indices, int delta)
    {
        var list = indices.Where(i => i >= 0 && i < Pages.Count).ToList();
        if (list.Count == 0) return;
        PushUndo();
        foreach (var i in list)
            Pages[i].ExtraRotation = PageTransform.Normalize(Pages[i].ExtraRotation + delta);
        MarkDirty();
    }

    public void DeletePages(IEnumerable<int> indices)
    {
        var set = indices.Where(i => i >= 0 && i < Pages.Count).ToHashSet();
        if (set.Count == 0 || set.Count == Pages.Count) return;  // never delete every page
        PushUndo();
        Pages = Pages.Where((_, i) => !set.Contains(i)).ToList();
        MarkDirty();
    }

    public void DuplicatePages(IEnumerable<int> indices)
    {
        var list = indices.Where(i => i >= 0 && i < Pages.Count).OrderByDescending(i => i).ToList();
        if (list.Count == 0) return;
        PushUndo();
        foreach (var i in list) Pages.Insert(i + 1, Pages[i].CloneShallowMarks());
        MarkDirty();
    }

    /// <summary>Moves the selected pages so they land in front of <paramref name="target"/>.</summary>
    public void MovePages(IEnumerable<int> indices, int target)
    {
        var set = indices.Where(i => i >= 0 && i < Pages.Count).OrderBy(i => i).ToList();
        if (set.Count == 0) return;

        PushUndo();
        var moving = set.Select(i => Pages[i]).ToList();
        int before = set.Count(i => i < target);
        var rest = Pages.Where((_, i) => !set.Contains(i)).ToList();
        int insertAt = Math.Clamp(target - before, 0, rest.Count);
        rest.InsertRange(insertAt, moving);
        Pages = rest;
        MarkDirty();
    }

    public void Dispose() => Reset();
}
