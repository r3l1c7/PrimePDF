using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace PrimePdf.Core;

public sealed record ExportOptions
{
    /// <summary>
    /// Resolution used for pages that have to be rasterised. 200 keeps small print
    /// comfortably readable while producing roughly half the bytes of 300.
    /// </summary>
    public int Dpi { get; init; } = 200;

    public int JpegQuality { get; init; } = 80;

    /// <summary>
    /// Encode pages that contain no real colour as grayscale, which drops the two chroma
    /// planes. Most documents are black on white, so this is close to free size.
    /// </summary>
    public bool PreferGrayscale { get; init; } = true;

    /// <summary>Rasterise every page, not just edited ones, so nothing stays selectable.</summary>
    public bool FlattenEverything { get; init; }

    /// <summary>Drop author/title/keywords from the output.</summary>
    public bool StripMetadata { get; init; } = true;

    /// <summary>
    /// Write recognised text from scanned pages into the saved file as an invisible
    /// layer, so the result is searchable in any PDF reader rather than only here.
    /// </summary>
    public bool EmbedRecognisedText { get; init; } = true;
}

public sealed record ExportResult(string Path, int PageCount, int FlattenedPages, long Bytes, int SearchablePages = 0);

/// <summary>
/// Writes the finished document out.
///
/// Pages the user never touched are copied across object-for-object, so their text stays
/// selectable and the file stays small. Pages that were edited are rendered to pixels
/// through the very same painter the editor previews with. That has one property worth
/// the trade-off: a blacked-out word is not merely covered up, it no longer exists in the
/// output file — there is no text object left to copy out or search for.
/// </summary>
public static class Exporter
{
    public static ExportResult Export(IReadOnlyList<PageEntry> pages, string outputPath, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        if (pages.Count == 0) throw new InvalidOperationException("There are no pages to save.");

        using var output = new PdfDocument();
        output.Info.Creator = "Prime PDF";
        if (!options.StripMetadata) output.Info.Title = Path.GetFileNameWithoutExtension(outputPath);

        // XImage reads lazily, so every stream has to stay open until Save() has run.
        var pending = new List<MemoryStream>();
        int flattened = 0;
        int searchable = 0;

        try
        {
            foreach (var page in pages)
            {
                PdfPage produced;

                if (!options.FlattenEverything && !page.HasMarks)
                {
                    produced = AddCopiedPage(output, page);
                }
                else if (!options.FlattenEverything && page.CanOverlay
                         && TryAddOverlayPage(output, page, options, pending) is { } overlaid)
                {
                    // Additive marks — ink, signatures, ticks, added text — hide nothing,
                    // so the original page is kept intact with a small image laid on top.
                    produced = overlaid;
                }
                else
                {
                    produced = AddFlattenedPage(output, page, options, pending);
                    flattened++;
                }

                if (options.EmbedRecognisedText && TryAddSearchableText(output, produced, page))
                    searchable++;
            }

            output.Save(outputPath);
        }
        finally
        {
            foreach (var s in pending) s.Dispose();
        }

        var info = new FileInfo(outputPath);
        return new ExportResult(outputPath, pages.Count, flattened, info.Length, searchable);
    }

    private static PdfPage AddCopiedPage(PdfDocument output, PageEntry page)
    {
        var src = page.Source.SharpDocument;
        var imported = output.AddPage(src.Pages[page.SourceIndex]);

        if (page.ExtraRotation != 0)
        {
            int existing = 0;
            try { existing = imported.Rotate; } catch { /* absent or malformed /Rotate */ }
            imported.Rotate = PageTransform.Normalize(existing + page.ExtraRotation);
        }

        return imported;
    }

    private static PdfPage AddFlattenedPage(PdfDocument output, PageEntry page, ExportOptions options, List<MemoryStream> pending)
    {
        using var renderer = new PageRenderer(capacity: 1);
        using var bitmap = renderer.RenderComposite(page, options.Dpi, showGuides: false);

        using var data = EncodePage(bitmap, options);

        var stream = new MemoryStream(data.ToArray(), writable: false);
        pending.Add(stream);

        var t = page.Transform;
        var pdfPage = output.AddPage();
        pdfPage.Width = XUnit.FromPoint(t.DisplayWidth);
        pdfPage.Height = XUnit.FromPoint(t.DisplayHeight);

        using (var gfx = XGraphics.FromPdfPage(pdfPage))
        {
            var image = XImage.FromStream(stream);
            gfx.DrawImage(image, 0, 0, t.DisplayWidth, t.DisplayHeight);
        }

        return pdfPage;
    }

    /// <summary>
    /// Keeps the original page and draws the marks over it as one transparent image
    /// covering only the area they occupy. The page's text stays selectable and the file
    /// grows by kilobytes rather than hundreds of them.
    ///
    /// The marks are drawn by the same painter the editor previews with, so there is
    /// still only one piece of code deciding what a mark looks like.
    /// </summary>
    /// <returns>The page, or null if the overlay could not be produced so the caller flattens.</returns>
    private static PdfPage? TryAddOverlayPage(PdfDocument output, PageEntry page, ExportOptions options, List<MemoryStream> pending)
    {
        try
        {
            var t = page.Transform;

            // Area the marks actually cover, padded so strokes are not clipped.
            var bounds = page.Marks[0].Bounds;
            foreach (var mark in page.Marks.Skip(1)) bounds = bounds.Union(mark.Bounds);
            bounds = bounds.Inflate(4);

            double x0 = Math.Max(0, bounds.X);
            double y0 = Math.Max(0, bounds.Y);
            double x1 = Math.Min(t.DisplayWidth, bounds.Right);
            double y1 = Math.Min(t.DisplayHeight, bounds.Bottom);
            if (x1 - x0 < 1 || y1 - y0 < 1) return null;

            double scale = options.Dpi / 72.0;
            int pxW = (int)Math.Ceiling((x1 - x0) * scale);
            int pxH = (int)Math.Ceiling((y1 - y0) * scale);
            if (pxW < 1 || pxH < 1 || (long)pxW * pxH > PageRenderer.MaxRenderPixels) return null;

            using var bitmap = new SKBitmap(pxW, pxH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Translate((float)(-x0 * scale), (float)(-y0 * scale));
                MarkPainter.Paint(canvas, page, scale, showGuides: false);
            }

            // PNG, because the overlay has to keep its transparency.
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null) return null;

            var stream = new MemoryStream(data.ToArray(), writable: false);
            var imported = output.AddPage(page.Source.SharpDocument.Pages[page.SourceIndex]);

            using var gfx = XGraphics.FromPdfPage(imported, XGraphicsPdfPageOptions.Append);
            var image = XImage.FromStream(stream);
            gfx.DrawImage(image, x0, y0, x1 - x0, y1 - y0);

            pending.Add(stream);
            return imported;
        }
        catch
        {
            // Anything unexpected here is not worth risking a wrong-looking page over;
            // the caller falls back to rasterising, which always works.
            return null;
        }
    }

    /// <summary>
    /// Writes recognised words onto a page as invisible text, turning a scan into a
    /// document that any reader can search and copy from.
    ///
    /// Text rendering mode 3 draws nothing but is still real text in the content stream,
    /// which is exactly what search and selection read. The font is the standard
    /// Helvetica, so nothing has to be embedded — the whole layer costs a few kilobytes
    /// and carries no font licensing with it.
    ///
    /// Words sitting under a redaction are dropped. Recognised text is the one path that
    /// could quietly put blacked-out words back into the file in a searchable form, which
    /// would undo the single most important thing this application does.
    /// </summary>
    /// <returns>True if a text layer was written.</returns>
    private static bool TryAddSearchableText(PdfDocument output, PdfPage pdfPage, PageEntry page)
    {
        try
        {
            var words = page.Source.RecognisedWords(page.SourceIndex);
            if (words.Length == 0) return false;

            // The layer is positioned in the page's own unrotated space; a rotated page
            // would need the inverse transform, and getting that subtly wrong would put
            // invisible text in the wrong place where nobody would ever see it.
            if (PageTransform.Normalize(page.ExtraRotation) != 0) return false;
            if (page.Source.PageRotation(page.SourceIndex) != 0) return false;

            var hidden = page.Marks.Where(m => m.RequiresFlatten).Select(m => m.Bounds).ToList();
            var (cropX, cropY) = page.Source.CropOrigin(page.SourceIndex);
            double pageHeight = page.BaseSize.H;

            var content = new StringBuilder();
            content.Append("BT\n3 Tr\n");   // 3 Tr = render nothing, but still be text

            int written = 0;
            foreach (var word in words)
            {
                if (hidden.Any(h => h.Intersects(word.Rect))) continue;

                var text = EscapePdfString(word.Text);
                if (text.Length == 0) continue;

                double size = Math.Clamp(word.Rect.H * 0.92, 1, 300);
                double x = word.Rect.X + cropX;
                double y = pageHeight - word.Rect.Bottom + cropY;

                content.Append($"/PrimeOcr {size:0.##} Tf 1 0 0 1 {x:0.##} {y:0.##} Tm ({text}) Tj\n");
                written++;
            }
            content.Append("ET\n");

            if (written == 0) return false;

            // A base-14 font needs no embedding and is understood everywhere.
            var font = new PdfDictionary(output);
            font.Elements["/Type"] = new PdfName("/Font");
            font.Elements["/Subtype"] = new PdfName("/Type1");
            font.Elements["/BaseFont"] = new PdfName("/Helvetica");
            font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
            output.Internals.AddObject(font);

            var fonts = pdfPage.Resources.Elements.GetDictionary("/Font");
            if (fonts is null)
            {
                fonts = new PdfDictionary(output);
                pdfPage.Resources.Elements["/Font"] = fonts;
            }
            fonts.Elements["/PrimeOcr"] = font.Reference;

            var stream = pdfPage.Contents.AppendContent();
            stream.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
            return true;
        }
        catch
        {
            // A page that will not take the layer simply stays unsearchable; that is a
            // missing convenience, not a broken document.
            return false;
        }
    }

    /// <summary>
    /// Escapes a word for a PDF literal string and drops anything WinAnsi cannot carry,
    /// so an unusual character cannot corrupt the content stream.
    /// </summary>
    private static string EscapePdfString(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c >= ' ' && c <= 'ÿ') sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static SKData EncodePage(SKBitmap bitmap, ExportOptions options)
    {
        if (options.PreferGrayscale && IsEffectivelyGrayscale(bitmap))
        {
            using var gray = bitmap.Copy(SKColorType.Gray8);
            var grayData = gray?.Encode(SKEncodedImageFormat.Jpeg, options.JpegQuality);
            if (grayData is not null) return grayData;
        }

        return bitmap.Encode(SKEncodedImageFormat.Jpeg, options.JpegQuality)
               ?? throw new InvalidOperationException("Could not encode page image.");
    }

    /// <summary>
    /// Samples a grid of pixels looking for any real colour. Sampling rather than scanning
    /// every pixel keeps this negligible next to the render itself; a page with so little
    /// colour that the grid misses it entirely loses nothing worth seeing.
    /// </summary>
    private static bool IsEffectivelyGrayscale(SKBitmap bitmap)
    {
        const int samplesPerAxis = 160;
        const int tolerance = 16;

        int stepX = Math.Max(1, bitmap.Width / samplesPerAxis);
        int stepY = Math.Max(1, bitmap.Height / samplesPerAxis);

        for (int y = 0; y < bitmap.Height; y += stepY)
        {
            for (int x = 0; x < bitmap.Width; x += stepX)
            {
                var p = bitmap.GetPixel(x, y);
                int max = Math.Max(p.Red, Math.Max(p.Green, p.Blue));
                int min = Math.Min(p.Red, Math.Min(p.Green, p.Blue));
                if (max - min > tolerance) return false;
            }
        }
        return true;
    }
}
