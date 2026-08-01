using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PrimePdf.Core;

namespace PrimePdf;

/// <summary>
/// The continuous page column.
///
/// Pages are laid out one under another so the wheel scrolls through the document, and
/// only the pages near the viewport are actually rasterised — a hundred-page file would
/// otherwise cost a hundred full-size bitmaps to look at one of them.
/// </summary>
public partial class MainWindow
{
    /// <summary>One page's visuals in the column.</summary>
    private sealed class PageView
    {
        public required int Index { get; init; }
        public required PageEntry Page { get; init; }
        public required Border Shadow { get; init; }
        public required Grid Host { get; init; }
        public required Image Image { get; init; }
        public required Canvas Overlay { get; init; }
        public required StackPanel Container { get; init; }
        public required TextBlock Label { get; init; }

        /// <summary>Zoom the current bitmap was produced at, so it is redrawn when it changes.</summary>
        public double RenderedAtZoom { get; set; } = -1;

        public bool IsRendered => Image.Source is not null;
    }

    private readonly List<PageView> _pageViews = new();
    private double[] _pageOffsets = Array.Empty<double>();
    private PageView? _activeView;
    private bool _suppressScrollSync;

    private const double PageGap = 26;

    /// <summary>Height reserved under each page for its number.</summary>
    private const double PageLabelHeight = 24;

    /// <summary>
    /// Beyond this many pages, measuring each one up front costs more than it is worth, so
    /// the first page's size is used for all of them. Real sizes still arrive as pages are
    /// rendered. This also keeps a file that merely *declares* a huge page count cheap.
    /// </summary>
    private const int MeasureAllPagesLimit = 750;

    private PageView? ViewFor(int index) =>
        index >= 0 && index < _pageViews.Count ? _pageViews[index] : null;

    // ============================================================== building

    private void RebuildPageColumn()
    {
        PageColumn.Children.Clear();
        _pageViews.Clear();
        _activeView = null;

        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            var page = _doc.Pages[i];

            var image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var overlay = new Canvas { IsHitTestVisible = false };

            var host = new Grid { Background = Brushes.Transparent };
            host.Children.Add(image);
            host.Children.Add(overlay);

            var shadow = new Border
            {
                Background = Brushes.White,
                SnapsToDevicePixels = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = host,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 4,
                    Opacity = 0.16,
                    Color = Color.FromRgb(0x0F, 0x1A, 0x2B),
                },
            };

            var label = new TextBlock
            {
                Text = $"Page {i + 1}",
                FontSize = 12,
                Height = PageLabelHeight,
                Foreground = (Brush)FindResource("MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 7, 0, 0),
            };

            var container = new StackPanel { Margin = new Thickness(0, 0, 0, PageGap) };
            container.Children.Add(shadow);
            container.Children.Add(label);

            var view = new PageView
            {
                Index = i,
                Page = page,
                Shadow = shadow,
                Host = host,
                Image = image,
                Overlay = overlay,
                Container = container,
                Label = label,
            };

            host.Tag = view;
            host.MouseLeftButtonDown += OnPageMouseDown;
            host.MouseMove += OnPageMouseMove;
            host.MouseLeftButtonUp += OnPageMouseUp;
            host.MouseLeave += OnPageMouseLeave;

            _pageViews.Add(view);
            PageColumn.Children.Add(container);
        }

        _activeView = ViewFor(_pageIndex) ?? _pageViews.FirstOrDefault();
        LayoutPages();
        UpdateVisiblePages();
    }

    /// <summary>Sizes every page for the current zoom and records where each one starts.</summary>
    private void LayoutPages()
    {
        _pageOffsets = new double[_pageViews.Count + 1];
        if (_pageViews.Count == 0) return;

        bool measureAll = _doc.Pages.Count <= MeasureAllPagesLimit;
        var estimate = _doc.Pages[0].Transform;

        double y = 0;
        for (int i = 0; i < _pageViews.Count; i++)
        {
            var view = _pageViews[i];

            // Pages already drawn always use their true size; the estimate only ever
            // applies to distant, unrendered pages in a very long document.
            var t = measureAll || view.IsRendered || i == 0 ? view.Page.Transform : estimate;

            double w = Math.Max(1, t.DisplayWidth * DipPerPoint);
            double h = Math.Max(1, t.DisplayHeight * DipPerPoint);

            view.Shadow.Width = w;
            view.Shadow.Height = h;

            _pageOffsets[i] = y;
            y += h + PageLabelHeight + 7 + PageGap;
        }
        _pageOffsets[^1] = y;
    }

    // ============================================================ rendering

    /// <summary>Draws the pages near the viewport and releases the bitmaps of those far from it.</summary>
    private void UpdateVisiblePages()
    {
        if (_pageViews.Count == 0) return;

        double top = CanvasScroller.VerticalOffset;
        double viewport = Math.Max(1, CanvasScroller.ViewportHeight);

        // One screenful of slack either side, so scrolling meets ready pages.
        double from = top - viewport;
        double to = top + viewport * 2;

        for (int i = 0; i < _pageViews.Count; i++)
        {
            var view = _pageViews[i];
            double pageTop = _pageOffsets[i];
            double pageBottom = pageTop + view.Shadow.Height;

            bool near = pageBottom >= from && pageTop <= to;

            if (near)
            {
                if (!view.IsRendered || Math.Abs(view.RenderedAtZoom - _zoom) > 0.0001)
                    RenderPageView(view);
            }
            else if (view.IsRendered)
            {
                view.Image.Source = null;
                view.RenderedAtZoom = -1;
            }
        }
    }

    private void RenderPageView(PageView view)
    {
        try
        {
            double dpi = ChooseRenderDpi(view.Page.Transform);
            using var bmp = _renderer.RenderComposite(view.Page, dpi, showGuides: true);
            view.Image.Source = SkiaInterop.ToBitmapSource(bmp);
            view.RenderedAtZoom = _zoom;
        }
        catch
        {
            // A page that will not draw leaves a blank sheet rather than breaking the view.
            view.Image.Source = null;
        }
    }

    /// <summary>Re-renders one page in place, after a mark is added or removed.</summary>
    private void RefreshPage(int index)
    {
        var view = ViewFor(index);
        if (view is null) return;

        ClearOverlay();
        if (view.IsRendered || IsNearViewport(index)) RenderPageView(view);
    }

    private bool IsNearViewport(int index)
    {
        if (index < 0 || index >= _pageOffsets.Length - 1) return false;
        double top = CanvasScroller.VerticalOffset;
        double viewport = Math.Max(1, CanvasScroller.ViewportHeight);
        return _pageOffsets[index + 1] >= top - viewport && _pageOffsets[index] <= top + viewport * 2;
    }

    // ============================================================ scrolling

    private void OnCanvasScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateVisiblePages();

        if (_suppressScrollSync || _pageViews.Count == 0) return;
        if (Math.Abs(e.VerticalChange) < 0.5 && Math.Abs(e.ViewportHeightChange) < 0.5) return;

        // Whichever page covers the middle of the viewport is the one being read.
        double middle = CanvasScroller.VerticalOffset + CanvasScroller.ViewportHeight / 2;
        int index = 0;
        for (int i = 0; i < _pageViews.Count; i++)
        {
            if (_pageOffsets[i] <= middle) index = i;
            else break;
        }

        if (index == _pageIndex) return;

        _pageIndex = index;
        _activeView = ViewFor(index);
        UpdateChrome();
        HighlightThumbnail();
    }

    private void ScrollToPage(int index)
    {
        if (index < 0 || index >= _pageOffsets.Length - 1) return;

        _suppressScrollSync = true;
        try
        {
            // Measure first: the ScrollViewer clamps to the extent it already knows, so
            // scrolling before layout silently lands short of the target.
            CanvasScroller.UpdateLayout();
            CanvasScroller.ScrollToVerticalOffset(_pageOffsets[index]);
        }
        finally
        {
            _suppressScrollSync = false;
        }
        UpdateVisiblePages();
    }
}
