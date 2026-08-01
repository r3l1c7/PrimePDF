using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PrimePdf.Core;
using PrimePdf.Dialogs;

namespace PrimePdf;

public partial class MainWindow
{
    /// <summary>Thumbnails get their own cache so they never evict the full-size page render.</summary>
    private readonly PageRenderer _thumbRenderer = new(capacity: 64);

    private readonly HashSet<int> _selectedPages = new();
    private int _thumbGeneration;
    private int _dragSourceIndex = -1;
    private Point _thumbPressPoint;

    private const double ThumbMaxEdge = 168;

    private void OnTogglePages(object sender, RoutedEventArgs e)
    {
        bool show = TogglePages.IsChecked == true;
        PagesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            RefreshThumbnails();
            SetHint("Drag a page to move it. Click to select, then use the buttons at the bottom.");
        }
        else
        {
            SetHint(HintFor(_tool));
        }
    }

    // ======================================================== thumbnails

    private void RefreshThumbnails()
    {
        if (PagesPanel.Visibility != Visibility.Visible) return;

        int generation = ++_thumbGeneration;
        ThumbList.Items.Clear();
        _selectedPages.RemoveWhere(i => i >= _doc.Pages.Count);

        for (int i = 0; i < _doc.Pages.Count; i++)
            ThumbList.Items.Add(BuildThumbCard(i));

        HighlightThumbnail();
        _ = FillThumbnailsAsync(generation);
    }

    private Border BuildThumbCard(int index)
    {
        var page = _doc.Pages[index];
        var t = page.Transform;
        double aspect = t.DisplayHeight <= 0 ? 1.29 : t.DisplayHeight / t.DisplayWidth;

        double w = aspect >= 1 ? ThumbMaxEdge / aspect : ThumbMaxEdge;
        double h = aspect >= 1 ? ThumbMaxEdge : ThumbMaxEdge * aspect;

        var image = new Image
        {
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var sheet = new Border
        {
            Background = Brushes.White,
            Width = w,
            Height = h,
            Child = image,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 1,
                Opacity = 0.2,
                Color = Color.FromRgb(0x26, 0x30, 0x3B),
            },
        };

        var label = new TextBlock
        {
            Text = (index + 1).ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 13,
        };

        if (page.HasMarks)
        {
            label.Text += "  •";
            label.ToolTip = "You have made changes to this page";
        }

        var card = new Border
        {
            Padding = new Thickness(10, 10, 10, 8),
            Margin = new Thickness(4, 4, 4, 4),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = Cursors.Hand,
            AllowDrop = true,
            Tag = index,
            Child = new StackPanel { Children = { sheet, label } },
        };

        card.MouseLeftButtonDown += ThumbMouseDown;
        card.MouseMove += ThumbMouseMove;
        card.DragOver += ThumbDragOver;
        card.Drop += ThumbDrop;
        card.DragLeave += (s, _) => { if (s is Border b) b.BorderBrush = BorderFor(b); };

        return card;
    }

    private async Task FillThumbnailsAsync(int generation)
    {
        for (int i = 0; i < ThumbList.Items.Count; i++)
        {
            if (generation != _thumbGeneration) return;

            // Yield between pages so typing and scrolling stay responsive on long documents.
            await Dispatcher.InvokeAsync(() =>
            {
                if (generation != _thumbGeneration) return;
                if (i >= _doc.Pages.Count || i >= ThumbList.Items.Count) return;

                if (ThumbList.Items[i] is not Border card ||
                    card.Child is not StackPanel stack ||
                    stack.Children[0] is not Border sheet ||
                    sheet.Child is not Image image) return;

                try
                {
                    using var bmp = _thumbRenderer.RenderThumbnail(_doc.Pages[i], (int)ThumbMaxEdge);
                    image.Source = SkiaInterop.ToBitmapSource(bmp);
                }
                catch
                {
                    // A page that will not render still gets a card, just a blank one.
                }
            }, DispatcherPriority.Background);
        }
    }

    private void HighlightThumbnail()
    {
        for (int i = 0; i < ThumbList.Items.Count; i++)
            if (ThumbList.Items[i] is Border b)
                b.BorderBrush = BorderFor(b);
    }

    private Brush BorderFor(Border card)
    {
        int index = card.Tag is int i ? i : -1;
        if (_selectedPages.Contains(index)) return (Brush)FindResource("AccentBrush");
        if (index == _pageIndex) return new SolidColorBrush(Color.FromRgb(0xA8, 0xC4, 0xF5));
        return Brushes.Transparent;
    }

    // ==================================================== thumb selection

    private void ThumbMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border card || card.Tag is not int index) return;

        _thumbPressPoint = e.GetPosition(this);
        _dragSourceIndex = index;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            if (!_selectedPages.Remove(index)) _selectedPages.Add(index);
        }
        else if (shift && _selectedPages.Count > 0)
        {
            int anchor = _selectedPages.Min();
            _selectedPages.Clear();
            for (int i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++) _selectedPages.Add(i);
        }
        else
        {
            _selectedPages.Clear();
            _selectedPages.Add(index);
            GoToPage(index);
        }

        HighlightThumbnail();
        UpdateSelectionHint();
    }

    private void UpdateSelectionHint()
    {
        if (_selectedPages.Count > 1)
            SetHint($"{_selectedPages.Count} pages selected. Use the buttons below to turn, copy, save or delete them.");
    }

    private void ThumbMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSourceIndex < 0) return;
        if (sender is not Border card) return;

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _thumbPressPoint.X) < 6 && Math.Abs(now.Y - _thumbPressPoint.Y) < 6) return;

        DragDrop.DoDragDrop(card, new ThumbDragPayload(_dragSourceIndex), DragDropEffects.Move);
    }

    private void ThumbDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ThumbDragPayload))) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        if (sender is Border card) card.BorderBrush = (Brush)FindResource("AccentBrush");
    }

    private void ThumbDrop(object sender, DragEventArgs e)
    {
        if (sender is not Border card || card.Tag is not int target) return;
        if (e.Data.GetData(typeof(ThumbDragPayload)) is not ThumbDragPayload payload) return;

        var moving = _selectedPages.Contains(payload.Index)
            ? _selectedPages.ToList()
            : new List<int> { payload.Index };

        _doc.MovePages(moving, target);
        _selectedPages.Clear();
        _dragSourceIndex = -1;
        RefreshThumbnails();
        SetHint("Page order changed.");
    }

    private sealed record ThumbDragPayload(int Index);

    // ====================================================== page actions

    private IReadOnlyList<int> TargetPages()
    {
        if (_selectedPages.Count > 0) return _selectedPages.OrderBy(i => i).ToList();
        return new[] { _pageIndex };
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e)
    {
        _doc.RotatePages(TargetPages(), -90);
        if (_fitMode) ApplyFit();
        SetHint("Turned left.");
    }

    private void OnRotateRight(object sender, RoutedEventArgs e)
    {
        _doc.RotatePages(TargetPages(), 90);
        if (_fitMode) ApplyFit();
        SetHint("Turned right.");
    }

    private void OnDuplicatePages(object sender, RoutedEventArgs e)
    {
        _doc.DuplicatePages(TargetPages());
        _selectedPages.Clear();
        RefreshThumbnails();
        SetHint("Copied.");
    }

    private void OnDeletePages(object sender, RoutedEventArgs e)
    {
        var targets = TargetPages();
        if (targets.Count >= _doc.Pages.Count)
        {
            AppDialog.Info(this, "Cannot delete every page",
                "A PDF needs at least one page. Leave one page in the document, or close the file instead.",
                AppDialogKind.Warning);
            return;
        }

        var what = targets.Count == 1 ? $"page {targets[0] + 1}" : $"{targets.Count} pages";
        if (!AppDialog.Ask(this, $"Delete {what}?",
                "You can bring them back with Undo if you change your mind.",
                "Delete", "Keep them", AppDialogKind.Warning))
            return;

        _doc.DeletePages(targets);
        _selectedPages.Clear();
        RefreshThumbnails();
        SetHint($"Deleted {what}.");
    }

    private async void OnExtractPages(object sender, RoutedEventArgs e)
    {
        var targets = TargetPages();
        var pages = targets.Select(i => _doc.Pages[i]).ToList();
        await SaveAsync(pages, $"Save {pages.Count} page(s) as a new PDF");
    }

    // ============================================================== find

    private async void OnFind(object sender, RoutedEventArgs e)
    {
        if (_doc.IsEmpty) return;

        // Searching a document whose pages are scans would silently find nothing, so
        // offer to read them before the user concludes the text is not there.
        await OfferOcrIfNeededAsync();

        var result = FindWindow.Run(this, _doc);
        if (result is null) return;

        if (result.GoToPage is { } target)
        {
            GoToPage(target);
            return;
        }

        if (result.RedactAll.Count > 0)
        {
            _doc.PushUndo();
            foreach (var (pageIndex, rect) in result.RedactAll)
                _doc.Pages[pageIndex].Marks.Add(new RedactMark { Rect = rect });
            _doc.MarkDirty();

            SetTool(Tool.Redact);
            SetHint($"Blacked out {result.RedactAll.Count} match(es) across the document. They are deleted for good when you save.");
        }
    }
}
