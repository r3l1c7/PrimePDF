using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PrimePdf.Core;
using PrimePdf.Dialogs;

namespace PrimePdf;

/// <summary>What the Fill &amp; Sign tool drops when you click the page.</summary>
public enum SignMode { Text, Signature, Check, Cross, Dot }

public partial class MainWindow
{
    // ------------------------------------------------------- tool settings
    private uint _penColor = 0xFF111827;
    private double _penWidth = 2.5;
    private uint _highlightColor = 0xFFFFE45C;
    private double _highlightWidth = 14;
    private SignMode _signMode = SignMode.Text;
    private double _fillFontSize = 12;

    // -------------------------------------------------------- drag state
    private bool _dragging;
    private PtPoint _dragStart;
    private PtPoint _dragCurrent;
    private InkMark? _liveInk;

    private Rectangle? _bandVisual;
    private Polyline? _inkVisual;
    private Rectangle? _hoverVisual;

    private static readonly (string Name, uint Argb)[] PenColors =
    {
        ("Black", 0xFF111827), ("Blue", 0xFF1F6FEB), ("Red", 0xFFD92D20), ("Green", 0xFF0E9F6E),
    };

    private static readonly (string Name, uint Argb)[] HighlightColors =
    {
        ("Yellow", 0xFFFFE45C), ("Green", 0xFF9BE7C4), ("Pink", 0xFFFBB6D0), ("Blue", 0xFFA8CBFB),
    };

    // ============================================================== tools

    private void OnToolChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        if (!Enum.TryParse<Tool>(btn.Tag?.ToString(), out var tool)) return;

        // Rail buttons behave as one radio group; clicking the active one keeps it active.
        if (btn.IsChecked != true) btn.IsChecked = true;
        SetTool(tool);
    }

    private void SetTool(Tool tool)
    {
        _tool = tool;
        CancelActiveDrag();

        foreach (var tb in RailPanel.Children.OfType<ToggleButton>())
        {
            if (ReferenceEquals(tb, TogglePages)) continue;
            tb.IsChecked = Enum.TryParse<Tool>(tb.Tag?.ToString(), out var t) && t == tool;
        }

        ApplyCursorToPages();
        BuildOptionsStrip();
        SetHint(HintFor(tool));
    }

    private string HintFor(Tool tool) => tool switch
    {
        Tool.Select => "Reading mode. Pick a tool on the left to make changes.",
        Tool.Redact => "Click a word to black it out, or drag a box over anything. Click a black box again to take it off.",
        Tool.EditText => "Click the words you want to change, then type the new wording. Click your change again to correct it.",
        Tool.Sign => _signMode switch
        {
            SignMode.Text => "Click any blank line, then type. Drag first if you want a wider box.",
            SignMode.Signature => "Click where your signature should go.",
            _ => "Click a checkbox to mark it.",
        },
        Tool.Draw => "Hold the left mouse button and draw on the page.",
        Tool.Highlight => "Drag across text to highlight it.",
        Tool.Erase => "Click anything you added to remove it. (Original page content is not affected.)",
        _ => "",
    };

    // ===================================================== options strip

    private void BuildOptionsStrip()
    {
        OptionsHost.Children.Clear();

        switch (_tool)
        {
            case Tool.Redact:
                AddLabel("Black out:");
                AddActionButton("Find words…", "Search the whole document and black out every match", OnFind);
                AddSpacer();
                AddNote("Anything you black out is permanently deleted from the saved copy.");
                break;

            case Tool.EditText:
                AddLabel("Text size:");
                AddSizePills(new[] { 9.0, 11, 12, 14, 18, 24 }, _fillFontSize, v => _fillFontSize = v);
                AddSpacer();
                AddNote("Tip: click directly on the words you want to replace.");
                break;

            case Tool.Sign:
                AddLabel("Place:");
                AddSignModePills();
                AddSpacer();
                if (_signMode == SignMode.Text)
                {
                    AddLabel("Size:");
                    AddSizePills(new[] { 9.0, 11, 12, 14, 18, 24 }, _fillFontSize, v => _fillFontSize = v);
                }
                else if (_signMode == SignMode.Signature)
                {
                    AddActionButton("Change my signature…", "Draw or type a new signature",
                        (_, _) => { if (SignatureWindow.Capture(this) is not null) SetHint("Signature saved. Click the page to place it."); });
                }
                break;

            case Tool.Draw:
                AddLabel("Colour:");
                AddColorSwatches(PenColors, _penColor, c => _penColor = c);
                AddSpacer();
                AddLabel("Thickness:");
                AddSizePills(new[] { 1.5, 2.5, 4.0, 7.0 }, _penWidth, v => _penWidth = v, format: v => v switch
                {
                    <= 1.6 => "Thin", <= 2.6 => "Medium", <= 4.1 => "Thick", _ => "Extra",
                });
                break;

            case Tool.Highlight:
                AddLabel("Colour:");
                AddColorSwatches(HighlightColors, _highlightColor, c => _highlightColor = c);
                AddSpacer();
                AddLabel("Thickness:");
                AddSizePills(new[] { 10.0, 14.0, 20.0 }, _highlightWidth, v => _highlightWidth = v, format: v => v switch
                {
                    <= 10.1 => "Thin", <= 14.1 => "Medium", _ => "Thick",
                });
                break;

            case Tool.Erase:
                AddNote("Click on anything you added to take it off the page.");
                AddSpacer();
                AddActionButton("Clear this page", "Remove everything you added to the page you are looking at",
                    (_, _) =>
                    {
                        var page = CurrentPage;
                        if (page is null || page.Marks.Count == 0) { SetHint("There is nothing to clear on this page."); return; }
                        _doc.ClearPageMarks(_pageIndex);
                        SetHint("Cleared everything you added to this page.");
                    });
                break;
        }

        OptionsStrip.Visibility = OptionsHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddLabel(string text) => OptionsHost.Children.Add(new TextBlock
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0),
        FontSize = 14.5,
        Foreground = (Brush)FindResource("MutedBrush"),
    });

    private void AddNote(string text) => OptionsHost.Children.Add(new TextBlock
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 13.5,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 440,
        Foreground = (Brush)FindResource("MutedBrush"),
    });

    private void AddSpacer() => OptionsHost.Children.Add(new Rectangle
    {
        Width = 1,
        Fill = (Brush)FindResource("BorderBrush2"),
        Margin = new Thickness(14, 4, 14, 4),
    });

    private void AddActionButton(string text, string tip, RoutedEventHandler handler)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource("SecondaryButton"),
            Padding = new Thickness(16, 8, 16, 8),
            MinHeight = 38,
            FontSize = 14,
            ToolTip = tip,
            Margin = new Thickness(0, 0, 8, 0),
        };
        b.Click += handler;
        OptionsHost.Children.Add(b);
    }

    private void AddSizePills(double[] values, double current, Action<double> onPick, Func<double, string>? format = null)
    {
        foreach (var v in values)
        {
            var pill = new ToggleButton
            {
                Style = (Style)FindResource("PillToggle"),
                Content = format is null ? v.ToString("0.#") : format(v),
                IsChecked = Math.Abs(v - current) < 0.05,
            };
            pill.Click += (_, _) =>
            {
                onPick(v);
                BuildOptionsStrip();
            };
            OptionsHost.Children.Add(pill);
        }
    }

    private void AddColorSwatches((string Name, uint Argb)[] colors, uint current, Action<uint> onPick)
    {
        foreach (var (name, argb) in colors)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(SkiaInterop.ToWpf(argb)),
            };
            var pill = new ToggleButton
            {
                Style = (Style)FindResource("PillToggle"),
                Content = swatch,
                IsChecked = argb == current,
                ToolTip = name,
                MinWidth = 0,
                Padding = new Thickness(6, 4, 6, 4),
            };
            pill.Click += (_, _) =>
            {
                onPick(argb);
                BuildOptionsStrip();
            };
            OptionsHost.Children.Add(pill);
        }
    }

    private void AddSignModePills()
    {
        (SignMode Mode, string Label)[] modes =
        {
            (SignMode.Text, "Type text"),
            (SignMode.Signature, "My signature"),
            (SignMode.Check, "✓"),
            (SignMode.Cross, "✗"),
            (SignMode.Dot, "●"),
        };

        foreach (var (mode, label) in modes)
        {
            var pill = new ToggleButton
            {
                Style = (Style)FindResource("PillToggle"),
                Content = label,
                IsChecked = _signMode == mode,
            };
            pill.Click += (_, _) =>
            {
                _signMode = mode;
                BuildOptionsStrip();
                SetHint(HintFor(Tool.Sign));
            };
            OptionsHost.Children.Add(pill);
        }
    }

    // ================================================= coordinate helpers

    /// <summary>The page the pointer is currently working on.</summary>
    private PageEntry? ActivePage => _activeView?.Page;

    private int ActivePageIndex => _activeView?.Index ?? _pageIndex;

    private PtPoint ToBasePoint(Point dip)
    {
        var page = ActivePage;
        if (page is null) return default;
        var t = page.Transform;
        return t.ToBase(dip.X / DipPerPoint, dip.Y / DipPerPoint);
    }

    /// <summary>Base-space rect to a rectangle on the active page's overlay, in DIPs.</summary>
    private Rect ToOverlayRect(PtRect rect)
    {
        var page = ActivePage;
        if (page is null) return Rect.Empty;
        var d = page.Transform.ToDisplay(rect);
        return new Rect(d.X * DipPerPoint, d.Y * DipPerPoint, d.W * DipPerPoint, d.H * DipPerPoint);
    }

    private WordBox? WordAt(PtPoint p)
    {
        var page = ActivePage;
        if (page is null) return null;

        foreach (var w in page.Source.Words(page.SourceIndex))
            if (w.Rect.Inflate(1.5).Contains(p.X, p.Y))
                return w;
        return null;
    }

    /// <summary>Every word sharing a text line with the one under the cursor.</summary>
    private (PtRect Rect, string Text, double FontSize)? LineAt(PtPoint p)
    {
        var page = ActivePage;
        if (page is null) return null;

        var words = page.Source.Words(page.SourceIndex);
        var hit = WordAt(p);
        if (hit is null) return null;

        var anchor = hit.Value;
        double centerY = anchor.Rect.CenterY;
        double tolerance = Math.Max(2, anchor.Rect.H * 0.6);

        var line = words
            .Where(w => Math.Abs(w.Rect.CenterY - centerY) <= tolerance)
            .OrderBy(w => w.Rect.X)
            .ToList();
        if (line.Count == 0) return null;

        var rect = line[0].Rect;
        foreach (var w in line.Skip(1)) rect = rect.Union(w.Rect);

        return (rect, string.Join(" ", line.Select(w => w.Text)), anchor.FontSize);
    }

    /// <summary>The topmost mark of a given kind under the point, if any.</summary>
    private T? MarkAt<T>(PtPoint p) where T : Mark
    {
        var page = ActivePage;
        if (page is null) return null;

        for (int i = page.Marks.Count - 1; i >= 0; i--)
            if (page.Marks[i] is T typed && typed.Bounds.Inflate(1).Contains(p.X, p.Y))
                return typed;
        return null;
    }

    private Mark? MarkAt(PtPoint p)
    {
        var page = ActivePage;
        if (page is null) return null;

        // Later marks sit on top, so search back to front.
        for (int i = page.Marks.Count - 1; i >= 0; i--)
        {
            var m = page.Marks[i];
            if (m is InkMark ink)
            {
                double r = Math.Max(ink.Width, 4);
                if (ink.Points.Any(q => Math.Abs(q.X - p.X) <= r && Math.Abs(q.Y - p.Y) <= r)) return m;
            }
            else if (m.Bounds.Inflate(1).Contains(p.X, p.Y))
            {
                return m;
            }
        }
        return null;
    }

    // ======================================================= mouse input

    /// <summary>Makes the page under the pointer the one tools act on.</summary>
    private bool FocusViewFrom(object sender)
    {
        if (sender is not FrameworkElement { Tag: PageView view }) return false;

        if (!ReferenceEquals(_activeView, view))
        {
            ClearOverlay();
            _activeView = view;

            // Reading position follows the pointer, so page-scoped actions and the page
            // counter refer to what the user is actually touching.
            if (_pageIndex != view.Index)
            {
                _pageIndex = view.Index;
                UpdateChrome();
                HighlightThumbnail();
            }
        }
        return true;
    }

    private void OnPageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!FocusViewFrom(sender) || _tool == Tool.Select) return;

        // A double-click is two presses. The first has already acted, so acting again on
        // the second would silently undo it and look like nothing happened — someone
        // double-clicking out of habit gets one clean action instead.
        if (e.ClickCount > 1) return;

        var host = _activeView!.Host;

        var p = ToBasePoint(e.GetPosition(host));
        _dragStart = p;
        _dragCurrent = p;
        _dragging = true;
        host.CaptureMouse();

        if (_tool is Tool.Draw or Tool.Highlight)
        {
            _liveInk = new InkMark
            {
                Points = { p },
                Color = _tool == Tool.Draw ? _penColor : _highlightColor,
                Width = _tool == Tool.Draw ? _penWidth : _highlightWidth,
                Style = _tool == Tool.Draw ? InkStyle.Pen : InkStyle.Highlighter,
            };
            BeginInkVisual();
        }
        else if (_tool == Tool.Erase)
        {
            EraseAt(p);
        }
    }

    private void OnPageMouseMove(object sender, MouseEventArgs e)
    {
        // While dragging, keep working on the page the drag started on even if the
        // pointer strays over a neighbour.
        if (!_dragging && !FocusViewFrom(sender)) return;
        if (_activeView is null) return;

        var p = ToBasePoint(e.GetPosition(_activeView.Host));

        if (!_dragging)
        {
            UpdateHover(p);
            return;
        }

        _dragCurrent = p;

        if (_liveInk is not null)
        {
            _liveInk.Points.Add(p);
            UpdateInkVisual();
        }
        else if (_tool == Tool.Erase)
        {
            EraseAt(p);
        }
        else
        {
            UpdateBandVisual(PtRect.FromCorners(_dragStart.X, _dragStart.Y, p.X, p.Y));
        }
    }

    private void OnPageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _activeView?.Host.ReleaseMouseCapture();
        _dragging = false;

        var page = ActivePage;
        if (page is null) { CancelActiveDrag(); return; }

        var rect = PtRect.FromCorners(_dragStart.X, _dragStart.Y, _dragCurrent.X, _dragCurrent.Y);
        bool isClick = rect.W < 3 && rect.H < 3;

        if (_liveInk is not null)
        {
            var ink = _liveInk;
            _liveInk = null;
            ClearOverlay();
            if (ink.Points.Count > 0) _doc.AddMark(ActivePageIndex, ink);
            return;
        }

        ClearOverlay();

        switch (_tool)
        {
            case Tool.Redact: CommitRedact(rect, isClick); break;
            case Tool.EditText: CommitTextEdit(rect, isClick); break;
            case Tool.Sign: CommitSign(rect, isClick); break;
        }
    }

    private void OnPageMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragging) ClearHover();
    }

    private void CancelActiveDrag()
    {
        _dragging = false;
        _liveInk = null;
        if (_activeView?.Host.IsMouseCaptured == true) _activeView.Host.ReleaseMouseCapture();
        ClearOverlay();
    }

    // ========================================================== commits

    private void CommitRedact(PtRect rect, bool isClick)
    {
        if (isClick)
        {
            // Clicking something already blacked out takes it off again. Without this the
            // second click stacks another identical bar on the first and appears to do
            // nothing, which leaves no obvious way to change your mind.
            if (MarkAt<RedactMark>(_dragStart) is { } existing)
            {
                _doc.RemoveMark(ActivePageIndex, existing);
                SetHint("Black box removed. Click the words again to put it back.");
                return;
            }

            var word = WordAt(_dragStart);
            if (word is null)
            {
                if (NeedsOcrHere()) { _ = OfferOcrForCurrentPageAsync(); return; }
                SetHint("No text there. Drag a box over the area you want to hide instead.");
                return;
            }
            rect = word.Value.Rect.Inflate(1);
        }
        if (rect.W < 1 || rect.H < 1) return;

        _doc.AddMark(ActivePageIndex, new RedactMark { Rect = rect });
        SetHint("Blacked out. It will be permanently deleted from the copy you save.");
    }

    private void CommitTextEdit(PtRect rect, bool isClick)
    {
        string existing = "";
        double fontSize = _fillFontSize;

        if (isClick)
        {
            // Clicking wording you already changed reopens it, so a second click corrects
            // a typo instead of stacking a fresh box on top of the last one.
            if (MarkAt<TextMark>(_dragStart) is { } placed)
            {
                var edited = TextEntryWindow.Prompt(this, "Change this again", placed.Text, placed.FontSize,
                    "This is text you added earlier. Clear the box to remove it.");
                if (edited is null) return;

                if (string.IsNullOrWhiteSpace(edited.Text) && !placed.CoverBehind)
                {
                    _doc.RemoveMark(ActivePageIndex, placed);
                    SetHint("Text removed.");
                    return;
                }

                var updated = (TextMark)placed.Clone();
                updated.Text = edited.Text;
                updated.FontSize = edited.FontSize;
                updated.Bold = edited.Bold;

                _doc.ReplaceMark(ActivePageIndex, placed, updated);
                SetHint("Updated.");
                return;
            }

            var line = LineAt(_dragStart);
            if (line is null)
            {
                if (NeedsOcrHere()) { _ = OfferOcrForCurrentPageAsync(); return; }
                SetHint("No text there. Drag a box around the words you want to replace.");
                return;
            }
            rect = line.Value.Rect.Inflate(1.5);
            existing = line.Value.Text;
            fontSize = line.Value.FontSize > 1 ? line.Value.FontSize : _fillFontSize;
        }
        if (rect.W < 4 || rect.H < 4) return;

        var result = TextEntryWindow.Prompt(this, "Change the wording", existing, fontSize,
            "The old words are removed and replaced with what you type.");
        if (result is null) return;

        _doc.AddMark(ActivePageIndex, new TextMark
        {
            Rect = rect,
            Text = result.Text,
            FontSize = result.FontSize,
            Bold = result.Bold,
            Color = 0xFF000000,
            CoverBehind = true,
            CoverColor = 0xFFFFFFFF,
        });
        SetHint("Wording changed. The original words are deleted from the copy you save. Click it again to correct it.");
    }

    private void CommitSign(PtRect rect, bool isClick)
    {
        switch (_signMode)
        {
            case SignMode.Text:
            {
                if (isClick) rect = new PtRect(_dragStart.X, _dragStart.Y - _fillFontSize * 0.5, 240, _fillFontSize * 1.5);
                var result = TextEntryWindow.Prompt(this, "Type your text", "", _fillFontSize, null);
                if (result is null) return;
                _doc.AddMark(ActivePageIndex, new TextMark
                {
                    Rect = rect,
                    Text = result.Text,
                    FontSize = result.FontSize,
                    Bold = result.Bold,
                    Color = 0xFF111827,
                    CoverBehind = false,
                });
                SetHint("Text added.");
                break;
            }

            case SignMode.Signature:
            {
                var png = SignatureStore.Load() ?? SignatureWindow.Capture(this);
                if (png is null) return;

                double w = isClick ? 180 : Math.Max(60, rect.W);
                double h = w * SignatureStore.AspectRatio(png);
                var place = isClick ? new PtRect(_dragStart.X, _dragStart.Y - h / 2, w, h) : new PtRect(rect.X, rect.Y, w, h);

                _doc.AddMark(ActivePageIndex, new ImageMark { Png = png, Rect = place });
                SetHint("Signature added. Drag with Erase to remove it if it is not quite right.");
                break;
            }

            default:
            {
                double size = isClick ? 16 : Math.Max(8, Math.Min(rect.W, rect.H));
                var place = isClick
                    ? new PtRect(_dragStart.X - size / 2, _dragStart.Y - size / 2, size, size)
                    : rect;
                _doc.AddMark(ActivePageIndex, new StampMark
                {
                    Rect = place,
                    Kind = _signMode switch
                    {
                        SignMode.Cross => StampKind.Cross,
                        SignMode.Dot => StampKind.Dot,
                        _ => StampKind.Check,
                    },
                });
                SetHint("Marked.");
                break;
            }
        }
    }

    /// <summary>
    /// Drives a single click at a page coordinate, so the self-test can check that
    /// clicking twice in the same place turns a mark on and then off again.
    /// Only tools that do not open a dialog can be exercised this way.
    /// </summary>
    internal void SimulateClickForSelfTest(int pageIndex, PtPoint at)
    {
        _activeView = ViewFor(pageIndex);
        _pageIndex = pageIndex;
        _dragStart = at;
        _dragCurrent = at;

        var rect = PtRect.FromCorners(at.X, at.Y, at.X, at.Y);
        switch (_tool)
        {
            case Tool.Redact: CommitRedact(rect, isClick: true); break;
            case Tool.Erase: EraseAt(at); break;
        }
    }

    private void EraseAt(PtPoint p)
    {
        if (ActivePage is null) return;

        var mark = MarkAt(p);
        if (mark is null) return;

        _doc.RemoveMark(ActivePageIndex, mark);
        SetHint("Removed.");
    }

    // ==================================================== overlay visuals

    private void ClearOverlay()
    {
        foreach (var view in _pageViews) view.Overlay.Children.Clear();
        _bandVisual = null;
        _inkVisual = null;
        _hoverVisual = null;
        _hoverIntent = HoverIntent.None;
    }

    private void ClearHover()
    {
        if (_hoverVisual is not null)
        {
            _activeView?.Overlay.Children.Remove(_hoverVisual);
            _hoverVisual = null;
        }
    }

    /// <summary>What a click at the pointer would do right now.</summary>
    private enum HoverIntent { None, Add, Remove, Edit }

    private HoverIntent _hoverIntent = HoverIntent.None;

    private void UpdateHover(PtPoint p)
    {
        if (_tool is not (Tool.Redact or Tool.EditText or Tool.Erase)) { ClearHover(); return; }

        PtRect? target = null;
        var intent = HoverIntent.None;

        switch (_tool)
        {
            case Tool.Redact:
                // An existing black box takes priority: clicking it will lift it off.
                if (MarkAt<RedactMark>(p) is { } redaction)
                {
                    target = redaction.Bounds;
                    intent = HoverIntent.Remove;
                }
                else if (WordAt(p) is { } word)
                {
                    target = word.Rect;
                    intent = HoverIntent.Add;
                }
                break;

            case Tool.EditText:
                if (MarkAt<TextMark>(p) is { } placed)
                {
                    target = placed.Bounds;
                    intent = HoverIntent.Edit;
                }
                else if (LineAt(p) is { } line)
                {
                    target = line.Rect;
                    intent = HoverIntent.Add;
                }
                break;

            case Tool.Erase:
                if (MarkAt(p) is { } any)
                {
                    target = any.Bounds;
                    intent = HoverIntent.Remove;
                }
                break;
        }

        if (target is null)
        {
            if (_hoverIntent != HoverIntent.None) { _hoverIntent = HoverIntent.None; SetHint(HintFor(_tool)); }
            ClearHover();
            return;
        }

        var overlay = _activeView?.Overlay;
        if (overlay is null) return;

        var r = ToOverlayRect(target.Value.Inflate(1));
        _hoverVisual ??= CreateHoverVisual();
        if (!overlay.Children.Contains(_hoverVisual)) overlay.Children.Add(_hoverVisual);

        _hoverVisual.Width = Math.Max(1, r.Width);
        _hoverVisual.Height = Math.Max(1, r.Height);
        Canvas.SetLeft(_hoverVisual, r.X);
        Canvas.SetTop(_hoverVisual, r.Y);

        // Colour alone would not carry this for the people the app is for, so the
        // instruction at the bottom of the window says what the click will do too.
        var accent = intent switch
        {
            HoverIntent.Remove => Color.FromRgb(0xD9, 0x2D, 0x20),
            HoverIntent.Edit => Color.FromRgb(0xB4, 0x69, 0x0E),
            _ => Color.FromRgb(0x1F, 0x6F, 0xEB),
        };
        _hoverVisual.Stroke = new SolidColorBrush(accent);
        _hoverVisual.Fill = new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B));

        if (intent != _hoverIntent)
        {
            _hoverIntent = intent;
            SetHint(intent switch
            {
                HoverIntent.Remove when _tool == Tool.Redact => "Click to take this black box off again.",
                HoverIntent.Remove => "Click to remove this.",
                HoverIntent.Edit => "Click to change what this says.",
                _ => HintFor(_tool),
            });
        }
    }

    private Rectangle CreateHoverVisual() => new()
    {
        StrokeThickness = 2,
        RadiusX = 3,
        RadiusY = 3,
        IsHitTestVisible = false,
    };

    private void UpdateBandVisual(PtRect rect)
    {
        var r = ToOverlayRect(rect);
        if (_bandVisual is null)
        {
            var accent = _tool == Tool.Redact ? Colors.Black : Color.FromRgb(0x1F, 0x6F, 0xEB);
            _bandVisual = new Rectangle
            {
                Stroke = new SolidColorBrush(accent),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 4 },
                Fill = new SolidColorBrush(Color.FromArgb(50, accent.R, accent.G, accent.B)),
                IsHitTestVisible = false,
            };
            _activeView?.Overlay.Children.Add(_bandVisual);
        }
        _bandVisual.Width = Math.Max(1, r.Width);
        _bandVisual.Height = Math.Max(1, r.Height);
        Canvas.SetLeft(_bandVisual, r.X);
        Canvas.SetTop(_bandVisual, r.Y);
    }

    private void BeginInkVisual()
    {
        var color = SkiaInterop.ToWpf(_liveInk!.Color);
        _inkVisual = new Polyline
        {
            Stroke = new SolidColorBrush(_liveInk.Style == InkStyle.Highlighter
                ? Color.FromArgb(120, color.R, color.G, color.B)
                : color),
            StrokeThickness = _liveInk.Width * DipPerPoint,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        _activeView?.Overlay.Children.Add(_inkVisual);
    }

    private void UpdateInkVisual()
    {
        if (_inkVisual is null || _liveInk is null || ActivePage is null) return;
        var t = ActivePage.Transform;

        var points = new PointCollection(_liveInk.Points.Count);
        foreach (var q in _liveInk.Points)
        {
            var d = t.ToDisplay(q.X, q.Y);
            points.Add(new Point(d.X * DipPerPoint, d.Y * DipPerPoint));
        }
        _inkVisual.Points = points;
    }
}
