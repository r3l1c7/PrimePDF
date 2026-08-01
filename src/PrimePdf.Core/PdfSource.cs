using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using SharpDocument = PdfSharp.Pdf.PdfDocument;

namespace PrimePdf.Core;

public sealed class PdfPasswordRequiredException(string path)
    : Exception($"'{System.IO.Path.GetFileName(path)}' is locked with a password.")
{
    public string FilePath { get; } = path;
}

/// <summary>
/// One PDF file the user has opened. A document can draw pages from several sources at
/// once (that is what combining files does), so this owns the parsed handles and hands
/// out page geometry, rendered pixels and text positions.
/// </summary>
public sealed class PdfSource : IDisposable
{
    private readonly object _gate = new();
    private PigDocument? _pig;
    private SharpDocument? _sharp;
    private readonly Dictionary<int, WordBox[]> _wordCache = new();
    private readonly Dictionary<int, WordBox[]> _ocrCache = new();

    public string FilePath { get; }
    public string DisplayName { get; }
    public byte[] Bytes { get; }
    public string? Password { get; }
    public int PageCount { get; }

    /// <summary>
    /// Page sizes in points, with any /Rotate in the file already applied. Filled in on
    /// demand: reading every page up front means a file that merely *declares* a very
    /// large number of pages stalls the whole app before anything appears on screen, and
    /// page counts are cheap to inflate in a crafted document.
    /// </summary>
    private readonly (double W, double H)?[] _sizes;

    /// <summary>Per-page /Rotate, read on demand alongside the sizes.</summary>
    private readonly int?[] _rotations;

    /// <summary>Fallback used when a page will not parse at all: US Letter.</summary>
    private static readonly (double W, double H) DefaultPageSize = (612, 792);

    private PdfSource(string path, byte[] bytes, string? password, PigDocument pig)
    {
        FilePath = path;
        DisplayName = Path.GetFileNameWithoutExtension(path);
        Bytes = bytes;
        Password = password;
        _pig = pig;
        PageCount = pig.NumberOfPages;
        _sizes = new (double, double)?[PageCount];
        _rotations = new int?[PageCount];
    }

    public static PdfSource Open(string path, string? password = null)
    {
        var bytes = File.ReadAllBytes(path);
        var options = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
        if (!string.IsNullOrEmpty(password)) options.Password = password;

        PigDocument pig;
        try
        {
            pig = PigDocument.Open(bytes, options);
        }
        catch (UglyToad.PdfPig.Exceptions.PdfDocumentEncryptedException)
        {
            throw new PdfPasswordRequiredException(path);
        }

        return new PdfSource(path, bytes, password, pig);
    }

    /// <summary>Base page size in points (top-left origin space), file rotation applied.</summary>
    public (double W, double H) PageSize(int index)
    {
        if (index < 0 || index >= PageCount) return DefaultPageSize;

        lock (_gate)
        {
            if (_sizes[index] is { } cached) return cached;

            var size = DefaultPageSize;
            try
            {
                if (_pig is not null)
                {
                    var page = _pig.GetPage(index + 1);
                    // A page declaring a nonsensical box would otherwise propagate straight
                    // into render sizes and layout maths.
                    if (page.Width > 1 && page.Height > 1 &&
                        double.IsFinite(page.Width) && double.IsFinite(page.Height))
                    {
                        size = (page.Width, page.Height);
                    }
                }
            }
            catch
            {
                // Leave the fallback in place; the page will render blank rather than
                // taking the document down with it.
            }

            _sizes[index] = size;
            return size;
        }
    }

    /// <summary>PDFsharp handle used to copy untouched pages through byte-for-byte.</summary>
    public SharpDocument SharpDocument
    {
        get
        {
            lock (_gate)
            {
                if (_sharp is null)
                {
                    using var ms = new MemoryStream(Bytes, writable: false);
                    _sharp = Password is null
                        ? PdfReader.Open(ms, PdfDocumentOpenMode.Import)
                        : PdfReader.Open(ms, Password, PdfDocumentOpenMode.Import);
                }
                return _sharp;
            }
        }
    }

    /// <summary>
    /// Words available for a page: the ones embedded in the file, or — for scans, which
    /// carry no text at all — whatever an OCR pass has since recognised.
    /// </summary>
    public WordBox[] Words(int index)
    {
        var embedded = EmbeddedWords(index);
        if (embedded.Length > 0) return embedded;

        lock (_gate)
            return _ocrCache.TryGetValue(index, out var recognised) ? recognised : Array.Empty<WordBox>();
    }

    /// <summary>True when the page carries real text; false for a scanned image.</summary>
    public bool HasTextLayer(int index) => EmbeddedWords(index).Length > 0;

    /// <summary>The /Rotate the file itself declares for a page, normalised to 0/90/180/270.</summary>
    public int PageRotation(int index)
    {
        if (index < 0 || index >= PageCount) return 0;

        lock (_gate)
        {
            if (_rotations[index] is { } cached) return cached;

            int rotation = 0;
            try
            {
                if (_pig is not null) rotation = PageTransform.Normalize(_pig.GetPage(index + 1).Rotation.Value);
            }
            catch
            {
                // Unreadable page: treat as unrotated, which is the safe assumption.
            }

            _rotations[index] = rotation;
            return rotation;
        }
    }

    /// <summary>True when the page is a scan that has not been read yet.</summary>
    public bool NeedsOcr(int index)
    {
        if (HasTextLayer(index)) return false;
        lock (_gate) return !_ocrCache.ContainsKey(index);
    }

    /// <summary>Stores the result of reading a scanned page, in top-left-origin points.</summary>
    public void SetOcrWords(int index, WordBox[] words)
    {
        lock (_gate) _ocrCache[index] = words;
    }

    /// <summary>
    /// Words the PDF itself declares, with rectangles converted to top-left-origin points,
    /// which is the space marks live in. Cached because extraction is not cheap.
    /// </summary>
    private WordBox[] EmbeddedWords(int index)
    {
        lock (_gate)
        {
            if (_wordCache.TryGetValue(index, out var cached)) return cached;
            if (_pig is null) return Array.Empty<WordBox>();

            WordBox[] result;
            try
            {
                var page = _pig.GetPage(index + 1);
                double h = page.Height;
                var list = new List<WordBox>();

                foreach (var word in page.GetWords(NearestNeighbourWordExtractor.Instance))
                {
                    if (string.IsNullOrWhiteSpace(word.Text)) continue;
                    var bb = word.BoundingBox;

                    // Rotated glyph rectangles scramble Left/Top/Right/Bottom, so normalise
                    // across all four corners instead of trusting those accessors.
                    double minX = Math.Min(Math.Min(bb.BottomLeft.X, bb.TopLeft.X), Math.Min(bb.BottomRight.X, bb.TopRight.X));
                    double maxX = Math.Max(Math.Max(bb.BottomLeft.X, bb.TopLeft.X), Math.Max(bb.BottomRight.X, bb.TopRight.X));
                    double minY = Math.Min(Math.Min(bb.BottomLeft.Y, bb.TopLeft.Y), Math.Min(bb.BottomRight.Y, bb.TopRight.Y));
                    double maxY = Math.Max(Math.Max(bb.BottomLeft.Y, bb.TopLeft.Y), Math.Max(bb.BottomRight.Y, bb.TopRight.Y));

                    double size = word.Letters.Count > 0 ? word.Letters[0].PointSize : 11;
                    string font = word.FontName ?? "";
                    list.Add(new WordBox(
                        word.Text,
                        new PtRect(minX, h - maxY, maxX - minX, maxY - minY),
                        size,
                        font));
                }
                result = list.ToArray();
            }
            catch
            {
                // Scanned pages and unusual encodings can fail extraction; the app still
                // works fine, the user just draws boxes by hand instead of clicking words.
                result = Array.Empty<WordBox>();
            }

            _wordCache[index] = result;
            return result;
        }
    }

    public bool HasText(int index) => Words(index).Length > 0;

    /// <summary>Indexes of pages that are scans and still need reading.</summary>
    public IEnumerable<int> PagesNeedingOcr() => Enumerable.Range(0, PageCount).Where(NeedsOcr);

    public void Dispose()
    {
        lock (_gate)
        {
            _pig?.Dispose();
            _pig = null;
            _sharp?.Dispose();
            _sharp = null;
        }
    }
}

/// <summary>A single extracted word and where it sits, in top-left-origin points.</summary>
public readonly record struct WordBox(string Text, PtRect Rect, double FontSize, string FontName);
