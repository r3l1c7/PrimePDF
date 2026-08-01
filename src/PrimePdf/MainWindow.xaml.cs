using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PrimePdf.Core;
using PrimePdf.Dialogs;
using PrimePdf.Shell;
using Microsoft.Win32;

namespace PrimePdf;

public enum Tool { Select, Redact, EditText, Sign, Draw, Highlight, Erase }

public partial class MainWindow : Window
{
    private readonly DocumentModel _doc = new();
    private readonly PageRenderer _renderer = new(capacity: 16);
    private readonly AppSettings _settings = AppSettings.Load();

    private int _pageIndex;
    private double _zoom = 1.0;
    private bool _fitMode = true;
    private Tool _tool = Tool.Select;
    private double _uiScale = 1.0;

    /// <summary>Device pixels per WPF DIP, so pages stay sharp on high-resolution screens.</summary>
    private double _dpiScale = 1.0;

    /// <summary>DIPs per PDF point at the current zoom.</summary>
    private double DipPerPoint => 96.0 / 72.0 * _zoom;

    private PageEntry? CurrentPage =>
        _pageIndex >= 0 && _pageIndex < _doc.Pages.Count ? _doc.Pages[_pageIndex] : null;

    public MainWindow()
    {
        InitializeComponent();

        _doc.Changed += OnDocumentChanged;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => { if (_fitMode) ApplyFit(); };
        Closing += OnClosing;

        SetTool(Tool.Select);
        UpdateChrome();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        ApplyUiScale(_settings.UiScale);

        if (App.StartupFile is { } path)
            _ = OpenPathAsync(path);

        if (!App.Headless)
            Dispatcher.BeginInvoke(OfferDefaultAppIfNeeded, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    // ==================================================== default PDF handler

    /// <summary>
    /// Asks once, on first run, whether PDFs should open here. Never asks again after
    /// that, whatever the answer, and never asks at all if it is already the default.
    /// </summary>
    private void OfferDefaultAppIfNeeded()
    {
        RefreshDefaultAppLink();

        if (_settings.AskedAboutDefaultApp) return;

        _settings.AskedAboutDefaultApp = true;

        if (FileAssociation.IsDefaultForPdf())
        {
            _settings.Save();
            return;
        }

        bool accepted = DefaultAppWindow.Offer(this);
        _settings.DeclinedDefaultApp = !accepted;
        _settings.Save();

        RefreshDefaultAppLink();
    }

    private void RefreshDefaultAppLink()
    {
        bool alreadyDefault = FileAssociation.IsDefaultForPdf();
        DefaultAppLink.Visibility = alreadyDefault ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnMakeDefaultApp(object sender, RoutedEventArgs e)
    {
        DefaultAppWindow.MakeDefault(this);
        _settings.AskedAboutDefaultApp = true;
        _settings.Save();
        RefreshDefaultAppLink();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_doc.IsDirty || App.Headless) return;

        try
        {
            var answer = AppDialog.Ask(this,
                "Close without saving?",
                "You have changes that have not been saved to a new file yet. If you close now, those changes are lost.",
                "Close anyway", "Go back", AppDialogKind.Warning);

            if (!answer) e.Cancel = true;
        }
        catch
        {
            // If the confirmation itself cannot be shown, let the window close. Blocking
            // the close because a dialog failed leaves no way out but Task Manager.
        }
    }

    // ======================================================== opening files

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_doc.IsDirty)
        {
            var ok = AppDialog.Ask(this,
                "Open a different file?",
                "Your current changes have not been saved. Opening another file will discard them.",
                "Open anyway", "Go back", AppDialogKind.Warning);
            if (!ok) return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Choose a PDF",
            Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
            await OpenPathAsync(dlg.FileName);
    }

    private async Task OpenPathAsync(string path)
    {
        if (!File.Exists(path))
        {
            AppDialog.Info(this, "File not found", $"'{Path.GetFileName(path)}' could not be found.", AppDialogKind.Error);
            return;
        }

        string? password = null;
        while (true)
        {
            try
            {
                ShowBusy("Opening your PDF…");
                var pw = password;

                // Parse off the UI thread, but adopt the result back on it: the model's
                // change event drives WPF elements and must not fire from a worker.
                var source = await Task.Run(() => PdfSource.Open(path, pw));
                _doc.SetSingle(source, path);

                HideBusy();
                break;
            }
            catch (PdfPasswordRequiredException)
            {
                HideBusy();
                var entered = PasswordWindow.Prompt(this, Path.GetFileName(path));
                if (entered is null) return;
                password = entered;
            }
            catch (Exception ex)
            {
                HideBusy();
                AppDialog.Info(this, "That file could not be opened",
                    $"'{Path.GetFileName(path)}' does not look like a PDF this app can read.\n\nDetails: {ex.Message}",
                    AppDialogKind.Error);
                return;
            }
        }

        _pageIndex = 0;
        _fitMode = true;
        _ocrOfferedForDocument = false;
        StartOverlay.Visibility = Visibility.Collapsed;
        SetTool(Tool.Select);
        RebuildPageColumn();
        ApplyCursorToPages();
        RefreshThumbnails();
        ApplyFit();
        UpdateChrome();
        SetHint("Use the tools on the left. Nothing you do here changes your original file.");

        await OfferOcrIfNeededAsync();
    }

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (_doc.IsEmpty)
        {
            OnOpen(sender, e);
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Choose PDFs to add to the end of this document",
            Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        int total = 0;
        var failures = new List<string>();

        ShowBusy("Adding pages…");
        try
        {
            foreach (var file in dlg.FileNames)
            {
                try
                {
                    var f = file;
                    var source = await Task.Run(() => PdfSource.Open(f));
                    total += _doc.Append(source);
                }
                catch (PdfPasswordRequiredException)
                {
                    failures.Add($"{Path.GetFileName(file)} (needs a password)");
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)} ({ex.Message})");
                }
            }
        }
        finally
        {
            HideBusy();
        }

        RefreshThumbnails();
        UpdateChrome();

        if (failures.Count > 0)
        {
            AppDialog.Info(this, "Some files could not be added",
                $"Added {total} page(s).\n\nCould not add:\n• " + string.Join("\n• ", failures),
                AppDialogKind.Warning);
        }
        else
        {
            SetHint($"Added {total} page(s). Open Pages on the left to put them in the order you want.");
        }
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasPdf(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!HasPdf(e)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var pdfs = files.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pdfs.Length == 0) return;

        var failed = new List<string>();

        if (_doc.IsEmpty)
        {
            await OpenPathAsync(pdfs[0]);
            foreach (var extra in pdfs.Skip(1))
            {
                try { _doc.AppendFile(extra); } catch { failed.Add(Path.GetFileName(extra)); }
            }
        }
        else
        {
            foreach (var p in pdfs)
            {
                try { _doc.AppendFile(p); } catch { failed.Add(Path.GetFileName(p)); }
            }
            SetHint($"Added pages from {pdfs.Length - failed.Count} file(s).");
        }

        RefreshThumbnails();
        UpdateChrome();

        if (failed.Count > 0)
            AppDialog.Info(this, "Some files could not be added",
                "These could not be read:\n• " + string.Join("\n• ", failed), AppDialogKind.Warning);
    }

    private static bool HasPdf(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) &&
        ((string[])e.Data.GetData(DataFormats.FileDrop)!)
            .Any(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    // =========================================================== rendering

    private void OnDocumentChanged()
    {
        if (_pageIndex >= _doc.Pages.Count) _pageIndex = Math.Max(0, _doc.Pages.Count - 1);

        // Undo, reordering and page deletion all replace the page objects, so compare
        // identity: same objects means only a mark changed and one page needs redrawing.
        bool structural = _pageViews.Count != _doc.Pages.Count;
        if (!structural)
        {
            for (int i = 0; i < _pageViews.Count; i++)
                if (!ReferenceEquals(_pageViews[i].Page, _doc.Pages[i])) { structural = true; break; }
        }

        UpdateChrome();

        if (structural)
        {
            RebuildPageColumn();
            ApplyCursorToPages();
        }
        else
        {
            RefreshPage(_pageIndex);
        }

        RefreshThumbnails();
    }

    private void ApplyCursorToPages()
    {
        var cursor = _tool switch
        {
            Tool.Select => Cursors.Arrow,
            Tool.Erase => Cursors.Hand,
            _ => Cursors.Cross,
        };
        foreach (var view in _pageViews) view.Host.Cursor = cursor;
    }

    /// <summary>
    /// Rasterising at exactly the on-screen size leaves small print thin and grey, because
    /// a 10pt line only gets a handful of pixels. Rendering at a multiple of the display
    /// size and letting WPF scale it back down keeps body text properly black and legible,
    /// which matters more here than anywhere else. Capped so deep zoom cannot blow up memory.
    /// </summary>
    private double ChooseRenderDpi(PageTransform t)
    {
        const double supersample = 2.0;
        const double maxPixels = 14_000_000;

        double dpi = Math.Clamp(96.0 * _zoom * _dpiScale * supersample, 48, 900);

        double pixels = (t.DisplayWidth * dpi / 72.0) * (t.DisplayHeight * dpi / 72.0);
        if (pixels > maxPixels) dpi *= Math.Sqrt(maxPixels / pixels);

        return Math.Max(48, dpi);
    }

    private void UpdateChrome()
    {
        bool has = !_doc.IsEmpty;

        StartOverlay.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = has ? _doc.Title : "Prime PDF";
        SubtitleText.Text = has
            ? (_doc.IsDirty ? "Not saved yet — your original file is untouched" : "Ready")
            : "No file open yet";

        PageLabel.Text = has ? $"Page {_pageIndex + 1} of {_doc.Pages.Count}" : "No pages";
        ZoomLabel.Text = $"{Math.Round(_zoom * 100)}%";

        BtnPrev.IsEnabled = has && _pageIndex > 0;
        BtnNext.IsEnabled = has && _pageIndex < _doc.Pages.Count - 1;
        BtnUndo.IsEnabled = _doc.CanUndo;
        BtnRedo.IsEnabled = _doc.CanRedo;
        BtnSave.IsEnabled = has;
        BtnFind.IsEnabled = has;
        BtnZoomIn.IsEnabled = has;
        BtnZoomOut.IsEnabled = has;
        BtnFit.IsEnabled = has;
        TogglePages.IsEnabled = has;

        foreach (var tb in RailPanel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
            if (!ReferenceEquals(tb, TogglePages)) tb.IsEnabled = has;
    }

    // ======================================================= navigation/zoom

    private void GoToPage(int index)
    {
        if (_doc.IsEmpty) return;
        _pageIndex = Math.Clamp(index, 0, _doc.Pages.Count - 1);
        _activeView = ViewFor(_pageIndex);
        ScrollToPage(_pageIndex);
        UpdateChrome();
        HighlightThumbnail();
    }

    private void OnPrevPage(object sender, RoutedEventArgs e) => GoToPage(_pageIndex - 1);
    private void OnNextPage(object sender, RoutedEventArgs e) => GoToPage(_pageIndex + 1);

    /// <summary>
    /// Fixed zoom stops rather than a multiplier.
    ///
    /// Multiplying by a constant means the steps depend on wherever you started, so from a
    /// 58% fit you land on 72%, 91%, 113% and sail straight past 100% — the one value
    /// people actually want, because it is the document at its true size. These are the
    /// stops every reader and browser uses.
    /// </summary>
    private static readonly double[] ZoomStops =
        { 0.25, 0.33, 0.50, 0.67, 0.75, 0.85, 1.00, 1.25, 1.50, 1.75, 2.00, 2.50, 3.00, 4.00, 5.00, 6.00 };

    private void ZoomIn() => SetZoom(ZoomStops.FirstOrDefault(z => z > _zoom + 0.005, ZoomStops[^1]));

    private void ZoomOut() => SetZoom(ZoomStops.LastOrDefault(z => z < _zoom - 0.005, ZoomStops[0]));

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomIn();
    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomOut();
    private void OnZoomFit(object sender, RoutedEventArgs e) { _fitMode = true; ApplyFit(); }

    private void SetZoom(double zoom)
    {
        _fitMode = false;
        ApplyZoom(Math.Clamp(zoom, 0.15, 6.0));
    }

    private void ApplyFit()
    {
        var page = CurrentPage;
        if (page is null) return;

        double availW = Math.Max(120, CanvasScroller.ActualWidth - 110);
        double availH = Math.Max(120, CanvasScroller.ActualHeight - 80);
        var t = page.Transform;

        double zw = availW / (t.DisplayWidth * 96.0 / 72.0);
        double zh = availH / (t.DisplayHeight * 96.0 / 72.0);
        ApplyZoom(Math.Clamp(Math.Min(zw, zh), 0.15, 6.0));
    }

    /// <summary>Resizes every page for the new zoom, keeping the reader's place.</summary>
    private void ApplyZoom(double zoom)
    {
        if (_pageViews.Count == 0)
        {
            _zoom = zoom;
            UpdateChrome();
            return;
        }

        // Remember the position as a page plus how far down that page we are, so zooming
        // holds your place instead of throwing you to the top of the page.
        int anchor = Math.Clamp(_pageIndex, 0, _pageViews.Count - 1);
        double fraction = (CanvasScroller.VerticalOffset - _pageOffsets[anchor])
                          / Math.Max(1, _pageViews[anchor].Shadow.Height);

        _zoom = zoom;
        ClearOverlay();
        LayoutPages();

        // The ScrollViewer clamps to whatever extent it currently knows about, so the new
        // page sizes have to be measured before asking it to move or the offset is
        // silently truncated and the view lands somewhere else.
        CanvasScroller.UpdateLayout();

        double target = _pageOffsets[anchor] + fraction * Math.Max(1, _pageViews[anchor].Shadow.Height);

        _suppressScrollSync = true;
        try { CanvasScroller.ScrollToVerticalOffset(Math.Max(0, target)); }
        finally { _suppressScrollSync = false; }

        UpdateVisiblePages();
        UpdateChrome();
    }

    /// <summary>
    /// The wheel zooms, as asked. Shift held scrolls instead, and the scrollbar, the page
    /// buttons and Page Up/Down all still move through the document.
    /// </summary>
    private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        if (_doc.IsEmpty) return;

        // Shift is the escape hatch back to scrolling; let the ScrollViewer handle it.
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        if (e.Delta > 0) ZoomIn(); else ZoomOut();
        e.Handled = true;
    }

    // ============================================================= saving

    private async void OnSave(object sender, RoutedEventArgs e) =>
        await SaveAsync(_doc.Pages, "Save a copy of your PDF");

    private async Task SaveAsync(IReadOnlyList<PageEntry> pages, string title)
    {
        if (pages.Count == 0) return;

        var baseName = _doc.PrimaryPath is null
            ? "document"
            : Path.GetFileNameWithoutExtension(_doc.PrimaryPath);

        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = "PDF documents (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            AddExtension = true,
            FileName = baseName + " (edited).pdf",
            InitialDirectory = _doc.PrimaryPath is null ? null : Path.GetDirectoryName(_doc.PrimaryPath),
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        // Refuse to write over any file the document is currently reading from.
        var target = Path.GetFullPath(dlg.FileName);
        bool clobbersSource = pages
            .Select(p => Path.GetFullPath(p.Source.FilePath))
            .Distinct()
            .Any(src => string.Equals(src, target, StringComparison.OrdinalIgnoreCase));

        if (clobbersSource)
        {
            AppDialog.Info(this, "Please pick a different name",
                "That is a file this document is reading from. To keep your original safe, save under a new name.",
                AppDialogKind.Warning);
            await SaveAsync(pages, title);
            return;
        }

        ShowBusy("Saving your PDF…");
        ExportResult result;
        try
        {
            var snapshot = pages.ToList();
            result = await Task.Run(() => Exporter.Export(snapshot, target));
        }
        catch (Exception ex)
        {
            HideBusy();
            AppDialog.Info(this, "Could not save",
                "The file could not be written.\n\nDetails: " + ex.Message, AppDialogKind.Error);
            return;
        }
        finally
        {
            HideBusy();
        }

        if (ReferenceEquals(pages, _doc.Pages)) _doc.MarkSaved();
        UpdateChrome();

        bool anyRedaction = pages.Any(p => p.Marks.Any(m => m.RequiresFlatten));
        SaveResultWindow.ShowResult(this, result, anyRedaction);
    }

    // ============================================================ commands

    private void OnUndo(object sender, RoutedEventArgs e) { _doc.Undo(); SetHint("Undone."); }
    private void OnRedo(object sender, RoutedEventArgs e) { _doc.Redo(); SetHint("Redone."); }

    private void OnToggleUiScale(object sender, RoutedEventArgs e)
    {
        ApplyUiScale(_uiScale switch { 1.0 => 1.15, 1.15 => 1.3, _ => 1.0 });

        _settings.UiScale = _uiScale;
        _settings.Save();
    }

    private void ApplyUiScale(double scale)
    {
        _uiScale = Math.Clamp(scale, 1.0, 1.3);
        RootScale.LayoutTransform = Math.Abs(_uiScale - 1.0) < 0.001
            ? Transform.Identity
            : new ScaleTransform(_uiScale, _uiScale);
        TextSizeLabel.Text = _uiScale > 1.2 ? "Reset" : "Bigger";
        if (_fitMode) Dispatcher.BeginInvoke(ApplyFit);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool typing = Keyboard.FocusedElement is System.Windows.Controls.TextBox;

        if (ctrl && e.Key == Key.Z) { _doc.Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { _doc.Redo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { OnSave(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { OnOpen(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { OnFind(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape) { CancelActiveDrag(); e.Handled = true; }
        else if (!typing && (e.Key == Key.PageDown || e.Key == Key.Right)) { GoToPage(_pageIndex + 1); e.Handled = true; }
        else if (!typing && (e.Key == Key.PageUp || e.Key == Key.Left)) { GoToPage(_pageIndex - 1); e.Handled = true; }

        base.OnPreviewKeyDown(e);
    }

    // ========================================================== screenshot

    /// <summary>Drives the window into a given state so its appearance can be captured.</summary>
    internal async Task PrepareForScreenshotAsync(string pdfPath, string? toolName, bool showPages, double? zoom)
    {
        await OpenPathAsync(pdfPath);

        if (toolName is not null && Enum.TryParse<Tool>(toolName, ignoreCase: true, out var tool))
            SetTool(tool);

        if (showPages)
        {
            TogglePages.IsChecked = true;
            OnTogglePages(TogglePages, new RoutedEventArgs());
        }

        if (zoom is { } z) SetZoom(z);
    }

    /// <summary>
    /// Applies one of every kind of mark, positioned by looking up real words on the page.
    /// Used to eyeball that what the painter draws lines up with the document underneath.
    /// </summary>
    internal void ApplyDemoMarks()
    {
        var page = CurrentPage;
        if (page is null) return;

        var words = page.Source.Words(page.SourceIndex);

        PtRect? Find(string text) => words
            .Where(w => w.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(w => (PtRect?)w.Rect)
            .FirstOrDefault();

        // Some words appear both in the page heading and again as a form label lower down;
        // the checkbox we want is always the lower one.
        PtRect? FindLowest(string text) => words
            .Where(w => w.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Rect.Y)
            .Select(w => (PtRect?)w.Rect)
            .FirstOrDefault();

        PtRect? Line(string text)
        {
            var anchor = words.FirstOrDefault(w => w.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
            if (anchor.Text is null) return null;
            var row = words.Where(w => Math.Abs(w.Rect.CenterY - anchor.Rect.CenterY) <= anchor.Rect.H * 0.6).ToList();
            var rect = row[0].Rect;
            foreach (var w in row.Skip(1)) rect = rect.Union(w.Rect);
            return rect;
        }

        if (Find("412-77-9330") is { } ssn)
            page.Marks.Add(new RedactMark { Rect = ssn.Inflate(1.5) });

        if (Find("m.whitfield48@example.com") is { } email)
            page.Marks.Add(new RedactMark { Rect = email.Inflate(1.5) });

        if (Line("Cedar") is { } address)
            page.Marks.Add(new TextMark
            {
                Rect = address.Inflate(1.5),
                Text = "Home address        [ withheld ]",
                FontSize = 11,
                CoverBehind = true,
                Color = 0xFF111827,
            });

        if (Line("Mountain") is { } insurer)
            page.Marks.Add(new InkMark
            {
                Points = { new PtPoint(insurer.X, insurer.CenterY), new PtPoint(insurer.Right, insurer.CenterY) },
                Color = 0xFFFFE45C,
                Width = insurer.H * 1.5,
                Style = InkStyle.Highlighter,
            });

        // The two consent checkboxes sit just left of their labels.
        if (FindLowest("consent") is { } consent)
            page.Marks.Add(new StampMark
            {
                Rect = new PtRect(consent.X - 18, consent.Y - 2, 12, 12),
                Kind = StampKind.Check,
            });

        if (FindLowest("privacy") is { } privacy)
            page.Marks.Add(new StampMark
            {
                Rect = new PtRect(privacy.X - 33, privacy.Y - 2, 12, 12),
                Kind = StampKind.Check,
            });

        // A hand-drawn-looking signature on the signature rule.
        if (Find("Signature") is { } sig)
        {
            var pts = new List<PtPoint>();
            double x0 = sig.Right + 24, y0 = sig.Bottom + 2;
            for (int i = 0; i <= 60; i++)
            {
                double t = i / 60.0;
                pts.Add(new PtPoint(
                    x0 + t * 150,
                    y0 - Math.Sin(t * Math.PI * 3.2) * 9 - t * 4));
            }
            page.Marks.Add(new InkMark { Points = pts, Color = 0xFF1B3A8A, Width = 1.6, Style = InkStyle.Pen });

            page.Marks.Add(new TextMark
            {
                Rect = new PtRect(x0 + 200, y0 - 10, 120, 14),
                Text = "9 Feb 2026",
                FontSize = 11,
                Color = 0xFF1B3A8A,
            });
        }

        _doc.MarkDirty();
    }

    // Small seams so the start-up self-test can drive the interface without a mouse.
    internal void SelectToolForSelfTest(Tool tool) => SetTool(tool);

    internal void ShowPagesPanelForSelfTest()
    {
        TogglePages.IsChecked = true;
        OnTogglePages(TogglePages, new RoutedEventArgs());
    }

    internal PageEntry? PageForSelfTest(int index) =>
        index >= 0 && index < _doc.Pages.Count ? _doc.Pages[index] : null;

    internal void ZoomForSelfTest()
    {
        ZoomIn();
        ZoomOut();
        ApplyFit();
    }

    internal double CurrentZoomForSelfTest => _zoom;

    /// <summary>
    /// Raises a real wheel event at the scroller, so the self-test checks the handler is
    /// actually wired up rather than just that the zoom maths is right.
    /// </summary>
    internal double SimulateWheelForSelfTest(int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        CanvasScroller.RaiseEvent(args);
        return _zoom;
    }
    internal void ZoomInForSelfTest() => ZoomIn();
    internal void ZoomOutForSelfTest() => ZoomOut();
    internal void FitForSelfTest() { _fitMode = true; ApplyFit(); }

    // ============================================================== chrome

    private void SetHint(string text) => HintText.Text = text;

    private void ShowBusy(string text)
    {
        BusyText.Text = text;
        BusyVeil.Visibility = Visibility.Visible;
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void HideBusy() => BusyVeil.Visibility = Visibility.Collapsed;
}
