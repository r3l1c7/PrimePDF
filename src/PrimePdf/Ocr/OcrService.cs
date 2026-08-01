using System.Runtime.InteropServices.WindowsRuntime;
using PrimePdf.Core;
using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PrimePdf.Ocr;

/// <summary>
/// Reads the text on scanned pages using the OCR engine built into Windows.
///
/// Chosen over bundling something like Tesseract because it needs no extra download,
/// works with no internet connection, and uses whatever language packs the machine
/// already has — which for the people this app is aimed at means it simply works.
/// </summary>
public static class OcrService
{
    /// <summary>
    /// Scans are usually 200-300 DPI. Rendering for recognition at 300 gives the engine
    /// enough detail on small print without producing needlessly huge bitmaps.
    /// </summary>
    public const int RecognitionDpi = 300;

    private static OcrEngine? _engine;
    private static bool _probed;

    /// <summary>The engine, or null when Windows has no usable language pack installed.</summary>
    private static OcrEngine? Engine
    {
        get
        {
            if (_probed) return _engine;
            _probed = true;
            try
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                          ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            }
            catch
            {
                _engine = null;
            }
            return _engine;
        }
    }

    public static bool IsAvailable => Engine is not null;

    /// <summary>Language the recognised text will be read as, for messages to the user.</summary>
    public static string? LanguageName
    {
        get
        {
            try { return Engine?.RecognizerLanguage?.DisplayName; }
            catch { return null; }
        }
    }

    /// <summary>
    /// Recognises one page and returns word boxes in the page's own coordinate space —
    /// top-left origin, points — so they slot straight into the same index the editor
    /// already uses for embedded text.
    /// </summary>
    public static async Task<WordBox[]> RecognizePageAsync(PdfSource source, int pageIndex, PageRenderer renderer)
    {
        var engine = Engine;
        if (engine is null) return Array.Empty<WordBox>();

        // Deliberately rendered without any rotation the user added in this app: marks and
        // word boxes both live in the page's unrotated base space.
        var entry = new PageEntry { Source = source, SourceIndex = pageIndex };

        // The renderer caps resolution to bound what an oversized page can allocate, so
        // ask what it will actually use. Assuming the requested DPI here would put every
        // recognised box in the wrong place on a page that got capped.
        double dpi = PageRenderer.EffectiveDpi(entry, RecognitionDpi);

        using var bitmap = renderer.RenderBaseCopy(entry, dpi);

        using var software = ToSoftwareBitmap(bitmap);
        var result = await engine.RecognizeAsync(software);

        double toPoints = 72.0 / dpi;
        var words = new List<WordBox>();

        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                if (r.Width <= 0 || r.Height <= 0) continue;

                var rect = new PtRect(r.X * toPoints, r.Y * toPoints, r.Width * toPoints, r.Height * toPoints);

                // Cap height is roughly three quarters of the glyph box; good enough to
                // pre-fill a sensible size when the user retypes the line.
                words.Add(new WordBox(word.Text, rect, rect.H * 0.75, "OCR"));
            }
        }

        return words.ToArray();
    }

    private static SoftwareBitmap ToSoftwareBitmap(SKBitmap bitmap)
    {
        // RenderBaseCopy hands back Bgra8888/Premul, which maps straight onto the WinRT
        // pixel format with no conversion pass.
        var pixels = bitmap.Bytes;
        return SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            bitmap.Width,
            bitmap.Height,
            BitmapAlphaMode.Premultiplied);
    }
}
