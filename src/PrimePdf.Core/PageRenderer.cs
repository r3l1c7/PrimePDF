using PDFtoImage;
using SkiaSharp;

namespace PrimePdf.Core;

/// <summary>
/// Turns pages into pixels. Keeps a small cache of un-marked page renders so that
/// drawing, dragging and undo stay responsive without re-rasterising the PDF each time.
/// </summary>
public sealed class PageRenderer : IDisposable
{
    private readonly record struct Key(PdfSource Source, int Index, int Rotation, int DpiTimes100);

    private readonly Dictionary<Key, SKBitmap> _cache = new();
    private readonly LinkedList<Key> _lru = new();
    private readonly object _gate = new();
    private readonly int _capacity;

    /// <summary>
    /// PDFium is a single native library with global state and is not safe to call from
    /// two threads at once. Exporting runs on a background thread while the editor may
    /// still be drawing thumbnails on the UI thread, so every entry into the native
    /// renderer — from any PageRenderer instance — is serialised through this one lock.
    /// Without it the failure mode is native memory corruption, not a tidy exception.
    /// </summary>
    private static readonly object PdfiumLock = new();

    /// <summary>
    /// Ceiling on the pixels any single page render may allocate.
    ///
    /// A PDF may legitimately declare a page up to 200 inches square. At 300 DPI that is
    /// 3.6 gigapixels — around 14 GB — which a handful of bytes in a crafted file can ask
    /// for. Capping here, at the one place every render funnels through, means display,
    /// export, thumbnails and OCR are all covered by a single guard. 40 megapixels is far
    /// above anything a real document needs (US Letter at 600 DPI is 33).
    /// </summary>
    public const long MaxRenderPixels = 40_000_000;

    public PageRenderer(int capacity = 12) => _capacity = capacity;

    /// <summary>
    /// The DPI a render will actually use once the pixel ceiling is applied. Callers that
    /// have to map pixels back to page coordinates (OCR) must ask for this rather than
    /// assuming they got the resolution they requested.
    /// </summary>
    public static double EffectiveDpi(PageEntry page, double requestedDpi)
    {
        var t = page.Transform;
        double w = Math.Max(1, t.DisplayWidth);
        double h = Math.Max(1, t.DisplayHeight);

        double dpi = Math.Clamp(double.IsFinite(requestedDpi) ? requestedDpi : 96, 4, 1200);
        double pixels = (w * dpi / 72.0) * (h * dpi / 72.0);

        if (pixels > MaxRenderPixels) dpi *= Math.Sqrt(MaxRenderPixels / pixels);

        // PDFium takes a whole number of DPI. Round down, not to nearest: rounding up
        // would push a capped render back over the ceiling the cap exists to enforce,
        // and it keeps this value identical to the one the renderer actually uses, which
        // is what lets OCR map pixels back to page coordinates exactly.
        return Math.Max(4, Math.Floor(dpi));
    }

    private static PdfRotation ToPdfRotation(int degrees) => PageTransform.Normalize(degrees) switch
    {
        90 => PdfRotation.Rotate90,
        180 => PdfRotation.Rotate180,
        270 => PdfRotation.Rotate270,
        _ => PdfRotation.Rotate0,
    };

    /// <summary>The page as the source file draws it, with extra rotation but no user marks.</summary>
    public SKBitmap RenderBase(PageEntry page, double dpi)
    {
        var key = new Key(page.Source, page.SourceIndex, PageTransform.Normalize(page.ExtraRotation), (int)Math.Round(dpi * 100));

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit))
            {
                _lru.Remove(key);
                _lru.AddFirst(key);
                return hit;
            }
        }

        var bitmap = RenderRaw(page, dpi);

        lock (_gate)
        {
            if (!_cache.ContainsKey(key))
            {
                _cache[key] = bitmap;
                _lru.AddFirst(key);
                Trim();
            }
            return _cache[key];
        }
    }

    /// <summary>
    /// A caller-owned copy in Bgra8888, deliberately not cached. Used for one-off
    /// high-resolution passes such as OCR, where holding the bitmap would evict every
    /// render the editor is actually displaying.
    /// </summary>
    public SKBitmap RenderBaseCopy(PageEntry page, double dpi)
    {
        var raw = RenderRaw(page, dpi);
        if (raw.ColorType == SKColorType.Bgra8888) return raw;

        try
        {
            var converted = new SKBitmap(raw.Width, raw.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(converted);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(raw, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), null);
            return converted;
        }
        finally
        {
            raw.Dispose();
        }
    }

    private SKBitmap RenderRaw(PageEntry page, double requestedDpi)
    {
        // Every render in the app reaches PDFium through here, so this is the right place
        // to bound the allocation a crafted page can demand.
        double dpi = EffectiveDpi(page, requestedDpi);

        var options = new RenderOptions
        {
            Dpi = (int)dpi,
            Rotation = ToPdfRotation(page.ExtraRotation),
            WithAnnotations = true,
            WithFormFill = true,
            AntiAliasing = PdfAntiAliasing.All,
            BackgroundColor = SKColors.White,
        };

        SKBitmap bmp;
        try
        {
            lock (PdfiumLock)
                bmp = Conversion.ToImage(page.Source.Bytes, page.SourceIndex, page.Source.Password, options);
        }
        catch
        {
            // A page PDFium refuses to draw — malformed, or simply beyond it — should not
            // take the whole app down; show a blank sheet of the right size so the rest of
            // the document stays usable.
            var t = page.Transform;
            bmp = new SKBitmap(
                (int)Math.Clamp(t.DisplayWidth * dpi / 72, 1, 30000),
                (int)Math.Clamp(t.DisplayHeight * dpi / 72, 1, 30000));
            using var c = new SKCanvas(bmp);
            c.Clear(SKColors.White);
        }

        return bmp;
    }

    private void Trim()
    {
        while (_lru.Count > _capacity)
        {
            var last = _lru.Last!.Value;
            _lru.RemoveLast();
            if (_cache.Remove(last, out var old)) old.Dispose();
        }
    }

    /// <summary>The page with every user mark composited on top — what the editor shows.</summary>
    public SKBitmap RenderComposite(PageEntry page, double dpi, bool showGuides)
    {
        var basemap = RenderBase(page, dpi);
        var result = new SKBitmap(basemap.Width, basemap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(basemap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), null);
            MarkPainter.Paint(canvas, page, dpi / 72.0, showGuides);
        }
        return result;
    }

    /// <summary>Small preview for the page organiser.</summary>
    public SKBitmap RenderThumbnail(PageEntry page, int maxEdge)
    {
        var t = page.Transform;
        double longest = Math.Max(t.DisplayWidth, t.DisplayHeight);
        double dpi = Math.Clamp(maxEdge / longest * 72.0, 8, 96);
        return RenderComposite(page, dpi, showGuides: false);
    }

    public void InvalidateSource(PdfSource source)
    {
        lock (_gate)
        {
            foreach (var key in _cache.Keys.Where(k => k.Source == source).ToList())
            {
                if (_cache.Remove(key, out var bmp)) bmp.Dispose();
                _lru.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var bmp in _cache.Values) bmp.Dispose();
            _cache.Clear();
            _lru.Clear();
        }
    }
}
