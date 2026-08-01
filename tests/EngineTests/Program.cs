using PrimePdf.Core;
using EngineTests;
using UglyToad.PdfPig;

var workDir = args.Length > 0 && !args[0].StartsWith("--")
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "primepdf-tests");
Directory.CreateDirectory(workDir);

// `--sizes <in.pdf>` reports how big the saved file gets across encoder settings.
if (args.Contains("--sizes"))
{
    var input = args.SkipWhile(a => a != "--sizes").Skip(1).FirstOrDefault()
                ?? throw new ArgumentException("--sizes needs an input path");

    var originalSize = new FileInfo(input).Length;
    Console.WriteLine($"original: {originalSize / 1024} KB\n");

    // One redaction on page 1 only — the common case, and the one that forces a raster.
    static DocumentModel WithOneRedaction(string path)
    {
        var d = new DocumentModel();
        d.OpenSingle(path);
        var word = d.Pages[0].Source.Words(0).FirstOrDefault(w => w.Text.Length > 4);
        d.AddMark(0, new RedactMark { Rect = word.Rect.Inflate(1) });
        return d;
    }

    Console.WriteLine($"{"dpi",5} {"quality",8} {"total KB",10} {"vs original",12}");
    foreach (var dpi in new[] { 150, 200, 250, 300 })
    {
        foreach (var quality in new[] { 60, 75, 85, 92 })
        {
            using var d = WithOneRedaction(input);
            var outPath = Path.Combine(workDir, $"size-{dpi}-{quality}.pdf");
            var r = Exporter.Export(d.Pages, outPath, new ExportOptions { Dpi = dpi, JpegQuality = quality });
            Console.WriteLine($"{dpi,5} {quality,8} {r.Bytes / 1024,10} {(double)r.Bytes / originalSize,11:0.0}x");
        }
    }

    // How much of the bloat is simply "we rasterised a page that did not need it"?
    using (var d = new DocumentModel())
    {
        d.OpenSingle(input);
        d.AddMark(0, new InkMark
        {
            Points = { new PtPoint(80, 300), new PtPoint(300, 300) },
            Color = 0xFF1D4ED8,
            Width = 2,
        });
        var outPath = Path.Combine(workDir, "size-ink-only.pdf");
        var r = Exporter.Export(d.Pages, outPath, new ExportOptions { Dpi = 200, JpegQuality = 85 });
        Console.WriteLine($"\nsingle pen stroke, page flattened: {r.Bytes / 1024} KB ({(double)r.Bytes / originalSize:0.0}x)");
    }

    return 0;
}

// `--icon <path>` regenerates the application icon.
if (args.Contains("--icon"))
{
    var iconPath = args.SkipWhile(a => a != "--icon").Skip(1).FirstOrDefault()
                   ?? Path.Combine(workDir, "app.ico");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(iconPath))!);
    File.WriteAllBytes(iconPath, AppIcon.BuildIco());
    Console.WriteLine("Wrote icon: " + iconPath);
    return 0;
}

// `--scan <in.pdf> <out.pdf>` rasterises every page, producing a file that behaves
// exactly like something off a flatbed scanner: pictures of text, no text layer at all.
if (args.Contains("--scan"))
{
    var rest = args.SkipWhile(a => a != "--scan").Skip(1).ToArray();
    var input = rest.ElementAtOrDefault(0) ?? throw new ArgumentException("--scan needs an input path");
    var output = rest.ElementAtOrDefault(1) ?? Path.ChangeExtension(input, ".scanned.pdf");

    using var scanDoc = new DocumentModel();
    scanDoc.OpenSingle(input);
    var scanResult = Exporter.Export(scanDoc.Pages, output,
        new ExportOptions { FlattenEverything = true, Dpi = 200, JpegQuality = 90 });

    using var verify = PdfDocument.Open(output);
    var leftover = string.Concat(verify.GetPages().Select(p => p.Text)).Trim();
    Console.WriteLine($"Wrote scan: {output} ({scanResult.PageCount} pages, {scanResult.Bytes / 1024} KB)");
    Console.WriteLine(leftover.Length == 0
        ? "Confirmed: no text layer remains."
        : $"WARNING: {leftover.Length} characters of text survived.");
    return 0;
}

// `--sample <path>` just writes the demo form used for manual testing and screenshots.
if (args.Contains("--sample"))
{
    var samplePath = args.SkipWhile(a => a != "--sample").Skip(1).FirstOrDefault()
                     ?? Path.Combine(workDir, "Patient Registration Form.pdf");
    Directory.CreateDirectory(Path.GetDirectoryName(samplePath)!);
    File.WriteAllBytes(samplePath, SamplePdf.Build());
    Console.WriteLine("Wrote sample: " + samplePath);
    return 0;
}

int passed = 0, failed = 0;

void Check(string name, bool condition, string? detail = null)
{
    if (condition)
    {
        passed++;
        Console.WriteLine($"  PASS  {name}");
    }
    else
    {
        failed++;
        Console.WriteLine($"  FAIL  {name}{(detail is null ? "" : "  -- " + detail)}");
    }
}

void Section(string title) => Console.WriteLine($"\n=== {title} ===");

static string TextOf(string path, int pageNumber)
{
    using var doc = PdfDocument.Open(path);
    return doc.GetPage(pageNumber).Text;
}

static int PageCountOf(string path)
{
    using var doc = PdfDocument.Open(path);
    return doc.NumberOfPages;
}

// ---------------------------------------------------------------------------
Section("Redaction removes the underlying text");

var secretPdf = TestPdf.WriteTemp(workDir, "secret.pdf", TestPdf.Build(
    new TestPdf.PageSpec(new[] { "Name Jane Roe", "SSN 123-45-6789", "Phone 555-0100" }),
    new TestPdf.PageSpec(new[] { "KEEPME this page is untouched", "Second line here" })));

{
    using var doc = new DocumentModel();
    doc.OpenSingle(secretPdf);
    Check("opened both pages", doc.Pages.Count == 2, $"got {doc.Pages.Count}");

    var words = doc.Pages[0].Source.Words(0);
    Check("extracted words from page 1", words.Length > 0, $"got {words.Length}");

    var ssn = words.FirstOrDefault(w => w.Text.Contains("123-45-6789"));
    Check("found the SSN word box", ssn.Text is not null, string.Join("|", words.Select(w => w.Text)));

    doc.AddMark(0, new RedactMark { Rect = ssn.Rect.Inflate(1) });

    var outPath = Path.Combine(workDir, "secret-redacted.pdf");
    var result = Exporter.Export(doc.Pages, outPath);

    Check("output has the same page count", result.PageCount == 2, $"got {result.PageCount}");
    Check("exactly one page was flattened", result.FlattenedPages == 1, $"got {result.FlattenedPages}");

    var page1 = TextOf(outPath, 1);
    var page2 = TextOf(outPath, 2);

    Check("SSN digits are gone from page 1", !page1.Contains("123-45-6789"), $"page1 text = '{page1}'");
    Check("page 1 has no extractable text at all", page1.Trim().Length == 0, $"page1 text = '{page1}'");
    Check("untouched page 2 keeps its text", page2.Contains("KEEPME"), $"page2 text = '{page2}'");

    // The redaction must also be visually opaque, not just structurally absent.
    using var src = PdfSource.Open(outPath);
    var entry = new PageEntry { Source = src, SourceIndex = 0 };
    using var renderer = new PageRenderer();
    using var bmp = renderer.RenderComposite(entry, 150, showGuides: false);
    double scale = 150 / 72.0;
    var probe = bmp.GetPixel(
        (int)(ssn.Rect.CenterX * scale),
        (int)(ssn.Rect.CenterY * scale));
    Check("redacted area renders solid black", probe.Red < 20 && probe.Green < 20 && probe.Blue < 20,
        $"pixel = {probe}");

    // A raw byte scan is the strongest statement: the digits are not in the file anywhere.
    var raw = File.ReadAllBytes(outPath);
    var needle = System.Text.Encoding.ASCII.GetBytes("123-45-6789");
    bool foundRaw = false;
    for (int i = 0; i + needle.Length <= raw.Length && !foundRaw; i++)
    {
        bool match = true;
        for (int j = 0; j < needle.Length; j++)
            if (raw[i + j] != needle[j]) { match = false; break; }
        foundRaw = match;
    }
    Check("SSN string absent from raw file bytes", !foundRaw);
}

// ---------------------------------------------------------------------------
Section("Untouched documents pass through losslessly");

{
    using var doc = new DocumentModel();
    doc.OpenSingle(secretPdf);
    var outPath = Path.Combine(workDir, "passthrough.pdf");
    var result = Exporter.Export(doc.Pages, outPath);

    Check("nothing was flattened", result.FlattenedPages == 0, $"got {result.FlattenedPages}");
    Check("page 1 text preserved", TextOf(outPath, 1).Contains("123-45-6789"));
    Check("page 2 text preserved", TextOf(outPath, 2).Contains("KEEPME"));
}

// ---------------------------------------------------------------------------
Section("Combining files and organising pages");

var otherPdf = TestPdf.WriteTemp(workDir, "other.pdf", TestPdf.Build(
    new TestPdf.PageSpec(new[] { "ALPHA page" }),
    new TestPdf.PageSpec(new[] { "BETA page" })));

{
    using var doc = new DocumentModel();
    doc.OpenSingle(secretPdf);
    int added = doc.AppendFile(otherPdf);
    Check("appended both pages of second file", added == 2 && doc.Pages.Count == 4, $"count={doc.Pages.Count}");

    // Move the last page to the very front.
    doc.MovePages(new[] { 3 }, 0);
    var outPath = Path.Combine(workDir, "combined.pdf");
    Exporter.Export(doc.Pages, outPath);

    Check("combined file has 4 pages", PageCountOf(outPath) == 4, $"got {PageCountOf(outPath)}");
    Check("reordered page landed first", TextOf(outPath, 1).Contains("BETA"), $"page1 = '{TextOf(outPath, 1)}'");
    Check("original first page moved to slot 2", TextOf(outPath, 2).Contains("Jane Roe"));

    doc.DeletePages(new[] { 0 });
    var trimmed = Path.Combine(workDir, "combined-trimmed.pdf");
    Exporter.Export(doc.Pages, trimmed);
    Check("delete removed one page", PageCountOf(trimmed) == 3, $"got {PageCountOf(trimmed)}");
}

// ---------------------------------------------------------------------------
Section("Rotation");

{
    using var doc = new DocumentModel();
    doc.OpenSingle(otherPdf);
    doc.RotatePages(new[] { 0 }, 90);

    var t = doc.Pages[0].Transform;
    Check("rotated page reports swapped size",
        Math.Abs(t.DisplayWidth - 792) < 0.5 && Math.Abs(t.DisplayHeight - 612) < 0.5,
        $"{t.DisplayWidth} x {t.DisplayHeight}");

    var outPath = Path.Combine(workDir, "rotated.pdf");
    Exporter.Export(doc.Pages, outPath);

    using var check = PdfDocument.Open(outPath);
    var p = check.GetPage(1);
    Check("rotation survives export",
        Math.Abs(p.Width - 792) < 0.5 && Math.Abs(p.Height - 612) < 0.5, $"{p.Width} x {p.Height}");
    Check("rotated page keeps its text (not flattened)", p.Text.Contains("ALPHA"), $"text='{p.Text}'");

    // Round-trip through the transform must land back where it started.
    var disp = t.ToDisplay(100, 200);
    var back = t.ToBase(disp.X, disp.Y);
    Check("display/base transform round-trips",
        Math.Abs(back.X - 100) < 1e-9 && Math.Abs(back.Y - 200) < 1e-9, $"got {back.X},{back.Y}");
}

// ---------------------------------------------------------------------------
Section("Marks land where they were drawn");

{
    using var doc = new DocumentModel();
    doc.OpenSingle(otherPdf);

    // Solid red square at a known spot, then read the pixel back after a round trip.
    var target = new PtRect(100, 150, 60, 40);
    doc.AddMark(0, new TextMark
    {
        Rect = target,
        Text = "",
        CoverBehind = true,
        CoverColor = 0xFFFF0000,
    });

    var outPath = Path.Combine(workDir, "placement.pdf");
    Exporter.Export(doc.Pages, outPath, new ExportOptions { Dpi = 200, JpegQuality = 100 });

    using var src = PdfSource.Open(outPath);
    var entry = new PageEntry { Source = src, SourceIndex = 0 };
    using var renderer = new PageRenderer();
    using var bmp = renderer.RenderComposite(entry, 144, showGuides: false);
    double s = 144 / 72.0;

    var inside = bmp.GetPixel((int)(target.CenterX * s), (int)(target.CenterY * s));
    var outside = bmp.GetPixel((int)((target.X - 20) * s), (int)(target.CenterY * s));

    Check("mark is red at its centre", inside.Red > 200 && inside.Green < 60 && inside.Blue < 60, $"pixel={inside}");
    Check("area outside the mark is untouched",
        outside.Red > 200 && outside.Green > 200 && outside.Blue > 200, $"pixel={outside}");
}

// ---------------------------------------------------------------------------
Section("Undo history");

{
    using var doc = new DocumentModel();
    doc.OpenSingle(otherPdf);
    Check("nothing to undo initially", !doc.CanUndo);

    doc.AddMark(0, new RedactMark { Rect = new PtRect(10, 10, 50, 20) });
    Check("mark added", doc.Pages[0].Marks.Count == 1);
    Check("undo now available", doc.CanUndo);

    doc.Undo();
    Check("undo removed the mark", doc.Pages[0].Marks.Count == 0, $"count={doc.Pages[0].Marks.Count}");

    doc.Redo();
    Check("redo restored the mark", doc.Pages[0].Marks.Count == 1, $"count={doc.Pages[0].Marks.Count}");

    doc.RotatePages(new[] { 0 }, 90);
    doc.Undo();
    Check("undo reverts a rotation too", doc.Pages[0].ExtraRotation == 0, $"rot={doc.Pages[0].ExtraRotation}");
}

// ---------------------------------------------------------------------------
Section("Flatten-everything export");

{
    using var doc = new DocumentModel();
    doc.OpenSingle(secretPdf);
    var outPath = Path.Combine(workDir, "flat.pdf");
    var result = Exporter.Export(doc.Pages, outPath, new ExportOptions { FlattenEverything = true, Dpi = 150 });

    Check("all pages flattened", result.FlattenedPages == 2, $"got {result.FlattenedPages}");
    Check("no text remains anywhere",
        TextOf(outPath, 1).Trim().Length == 0 && TextOf(outPath, 2).Trim().Length == 0);
}

// ---------------------------------------------------------------------------
Section("Searching across word boundaries");

{
    using var doc = new DocumentModel();
    doc.OpenSingle(secretPdf);
    var words = doc.Pages[0].Source.Words(0);

    var single = TextSearch.FindInPage(words, "123-45-6789", 0);
    Check("finds a single word", single.Count == 1, $"got {single.Count}");

    var phrase = TextSearch.FindInPage(words, "Jane Roe", 0);
    Check("finds a phrase spanning two words", phrase.Count == 1, $"got {phrase.Count}");
    if (phrase.Count == 1)
    {
        var jane = words.First(w => w.Text == "Jane");
        var roe = words.First(w => w.Text == "Roe");
        var expected = jane.Rect.Union(roe.Rect);
        var got = phrase[0].Rect;
        Check("phrase rect covers both words",
            got.X <= expected.X + 2 && got.Right >= expected.Right - 2,
            $"expected ~{expected.X:F0}..{expected.Right:F0}, got {got.X:F0}..{got.Right:F0}");
    }

    Check("is case insensitive", TextSearch.FindInPage(words, "jane roe", 0).Count == 1);
    Check("reports nothing for absent text", TextSearch.FindInPage(words, "Nonexistent", 0).Count == 0);
    Check("ignores an empty query", TextSearch.FindInPage(words, "  ", 0).Count == 0);

    // Redacting every hit must actually remove them all.
    var hits = TextSearch.FindInPage(words, "555-0100", 0);
    foreach (var hit in hits) doc.Pages[0].Marks.Add(new RedactMark { Rect = hit.Rect });
    doc.MarkDirty();

    var outPath = Path.Combine(workDir, "search-redacted.pdf");
    Exporter.Export(doc.Pages, outPath);
    Check("every search hit is gone after export", !TextOf(outPath, 1).Contains("555-0100"));
}

// ---------------------------------------------------------------------------
Section("Every mark type survives export");

{
    // Each mark is placed on its own band of the page so they can be probed separately.
    using var doc = new DocumentModel();
    doc.OpenSingle(otherPdf);

    doc.Pages[0].Marks.Add(new InkMark
    {
        Points = { new PtPoint(80, 300), new PtPoint(300, 300) },
        Color = 0xFFFF0000,
        Width = 12,
        Style = InkStyle.Pen,
    });
    doc.Pages[0].Marks.Add(new StampMark
    {
        Rect = new PtRect(80, 400, 40, 40),
        Kind = StampKind.Check,
        Color = 0xFF0000FF,
    });
    doc.Pages[0].Marks.Add(new TextMark
    {
        Rect = new PtRect(80, 500, 300, 40),
        Text = "ADDED TEXT",
        FontSize = 28,
        Color = 0xFF008000,
        CoverBehind = false,
    });
    doc.MarkDirty();

    var outPath = Path.Combine(workDir, "all-marks.pdf");
    var result = Exporter.Export(doc.Pages, outPath, new ExportOptions { Dpi = 200, JpegQuality = 100 });
    Check("additive marks do not force a rasterised page", result.FlattenedPages == 0, $"got {result.FlattenedPages}");
    Check("the page keeps its selectable text", TextOf(outPath, 1).Contains("ALPHA"), $"'{TextOf(outPath, 1)}'");

    using var src = PdfSource.Open(outPath);
    var entry = new PageEntry { Source = src, SourceIndex = 0 };
    using var renderer = new PageRenderer();
    using var bmp = renderer.RenderComposite(entry, 144, showGuides: false);
    double s = 144 / 72.0;

    var onInk = bmp.GetPixel((int)(190 * s), (int)(300 * s));
    Check("pen stroke is on the page", onInk.Red > 180 && onInk.Green < 80, $"pixel={onInk}");

    bool foundBlue = false, foundGreen = false;
    for (int y = (int)(395 * s); y < (int)(445 * s) && !foundBlue; y++)
        for (int x = (int)(78 * s); x < (int)(125 * s); x++)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Blue > 150 && p.Red < 100) { foundBlue = true; break; }
        }
    Check("tick stamp is on the page", foundBlue);

    for (int y = (int)(495 * s); y < (int)(545 * s) && !foundGreen; y++)
        for (int x = (int)(78 * s); x < (int)(380 * s); x++)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Green > 90 && p.Red < 110 && p.Blue < 110) { foundGreen = true; break; }
        }
    Check("added text is on the page", foundGreen);
}

// ---------------------------------------------------------------------------
Section("Marks follow the page when it is rotated");

{
    using var doc = new DocumentModel();
    doc.OpenSingle(otherPdf);

    var target = new PtRect(100, 150, 60, 40);
    doc.Pages[0].Marks.Add(new TextMark { Rect = target, Text = "", CoverBehind = true, CoverColor = 0xFFFF0000 });
    doc.RotatePages(new[] { 0 }, 90);

    var outPath = Path.Combine(workDir, "rotated-mark.pdf");
    Exporter.Export(doc.Pages, outPath, new ExportOptions { Dpi = 150, JpegQuality = 100 });

    using var src = PdfSource.Open(outPath);
    var entry = new PageEntry { Source = src, SourceIndex = 0 };
    using var renderer = new PageRenderer();
    using var bmp = renderer.RenderComposite(entry, 144, showGuides: false);
    double s = 144 / 72.0;

    // Under a 90 degree turn the mark's centre moves to (baseHeight - y, x).
    var t = new PageTransform(612, 792, 90);
    var centre = t.ToDisplay(target.CenterX, target.CenterY);
    var probe = bmp.GetPixel((int)(centre.X * s), (int)(centre.Y * s));
    Check("mark moved with the rotated page", probe.Red > 200 && probe.Green < 60, $"pixel={probe}");
}

// ---------------------------------------------------------------------------
Section("Signing a page does not rasterise it");

{
    // The whole point: adding a signature to a contract should cost kilobytes and keep
    // the document searchable, not replace the page with a picture of itself.
    long Sign(bool forceFlatten, string name)
    {
        using var d = new DocumentModel();
        d.OpenSingle(secretPdf);
        d.Pages[0].Marks.Add(new InkMark
        {
            Points = { new PtPoint(90, 300), new PtPoint(160, 280), new PtPoint(230, 310) },
            Color = 0xFF1B3A8A,
            Width = 2,
        });
        d.MarkDirty();

        var path = Path.Combine(workDir, name);
        return Exporter.Export(d.Pages, path,
            new ExportOptions { FlattenEverything = forceFlatten, Dpi = 200 }).Bytes;
    }

    long overlaid = Sign(false, "signed-overlay.pdf");
    long flattened = Sign(true, "signed-flattened.pdf");

    Check("overlaying is dramatically smaller than rasterising",
        overlaid * 3 < flattened, $"{overlaid / 1024} KB overlaid vs {flattened / 1024} KB flattened");
    Console.WriteLine($"        (signed page: {overlaid / 1024} KB overlaid, {flattened / 1024} KB flattened)");

    Check("both pages keep their text", TextOf(Path.Combine(workDir, "signed-overlay.pdf"), 1).Contains("Jane Roe"));

    // And the stroke must land where it was drawn, not merely exist somewhere.
    using var src = PdfSource.Open(Path.Combine(workDir, "signed-overlay.pdf"));
    var entry = new PageEntry { Source = src, SourceIndex = 0 };
    using var renderer = new PageRenderer();
    using var bmp = renderer.RenderComposite(entry, 144, showGuides: false);
    double s = 144 / 72.0;

    bool foundInk = false;
    for (int y = (int)(270 * s); y < (int)(320 * s) && !foundInk; y++)
        for (int x = (int)(85 * s); x < (int)(235 * s); x++)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Blue > 100 && p.Red < 90 && p.Green < 90) { foundInk = true; break; }
        }
    Check("the overlaid stroke lands where it was drawn", foundInk);

    var awayFromInk = bmp.GetPixel((int)(400 * s), (int)(500 * s));
    Check("the rest of the page is untouched",
        awayFromInk.Red > 200 && awayFromInk.Green > 200 && awayFromInk.Blue > 200, $"pixel={awayFromInk}");
}

// ---------------------------------------------------------------------------
Section("Hostile input: oversized pages cannot exhaust memory");

{
    // 14400 points is the largest page PDF permits — 200 inches. Rendering that at the
    // 300 DPI export default would ask for roughly 3.6 gigapixels, about 14 GB, from a
    // file only a few hundred bytes long.
    var hugePdf = TestPdf.WriteTemp(workDir, "huge-page.pdf", TestPdf.Build(
        new TestPdf.PageSpec(new[] { "SECRET 999-88-7777" }, Width: 14400, Height: 14400)));

    using var doc = new DocumentModel();
    doc.OpenSingle(hugePdf);

    var entry = doc.Pages[0];
    double naive = 300;
    double capped = PageRenderer.EffectiveDpi(entry, naive);

    double naivePixels = (entry.Transform.DisplayWidth * naive / 72) * (entry.Transform.DisplayHeight * naive / 72);
    double cappedPixels = (entry.Transform.DisplayWidth * capped / 72) * (entry.Transform.DisplayHeight * capped / 72);

    Check("an uncapped render would have been enormous", naivePixels > 1_000_000_000, $"{naivePixels:N0} px");
    Check("the cap holds it to the documented ceiling",
        cappedPixels <= PageRenderer.MaxRenderPixels * 1.01, $"{cappedPixels:N0} px");

    // Normal documents must be completely unaffected by the guard.
    using var normal = new DocumentModel();
    normal.OpenSingle(secretPdf);
    Check("a normal page is not capped at all",
        Math.Abs(PageRenderer.EffectiveDpi(normal.Pages[0], 300) - 300) < 0.001,
        $"{PageRenderer.EffectiveDpi(normal.Pages[0], 300)}");

    // And the whole pipeline has to survive it end to end, not merely compute a number.
    using var renderer = new PageRenderer(capacity: 1);
    using var bmp = renderer.RenderComposite(entry, 300, showGuides: false);
    Check("the oversized page actually renders without exhausting memory",
        (long)bmp.Width * bmp.Height <= PageRenderer.MaxRenderPixels * 1.01,
        $"{bmp.Width}x{bmp.Height}");

    var outPath = Path.Combine(workDir, "huge-page-export.pdf");
    doc.AddMark(0, new RedactMark { Rect = new PtRect(50, 50, 400, 40) });
    var result = Exporter.Export(doc.Pages, outPath, new ExportOptions { Dpi = 300 });
    Check("and exports without exhausting memory", result.PageCount == 1 && File.Exists(outPath));
}

// ---------------------------------------------------------------------------
Section("Hostile input: malformed files fail safely");

{
    var cases = new (string Name, byte[] Bytes)[]
    {
        ("empty file", Array.Empty<byte>()),
        ("not a pdf at all", System.Text.Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>")),
        ("truncated pdf", TestPdf.Build(new TestPdf.PageSpec(new[] { "hello" })).Take(120).ToArray()),
        ("header only", System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n")),
    };

    foreach (var (name, bytes) in cases)
    {
        var path = TestPdf.WriteTemp(workDir, $"malformed-{name.Replace(' ', '-')}.pdf", bytes);
        bool handled;
        try
        {
            using var src = PdfSource.Open(path);
            // Parsing may succeed leniently; touching a page must then still be safe.
            _ = src.PageCount > 0 ? src.PageSize(0) : (0, 0);
            handled = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            handled = true;   // a clean exception the UI already catches and reports
        }
        catch
        {
            handled = false;
        }
        Check($"{name} is handled without crashing", handled);
    }

    // A page whose size is nonsense must fall back rather than poison layout maths.
    var degenerate = TestPdf.WriteTemp(workDir, "degenerate-box.pdf",
        TestPdf.Build(new TestPdf.PageSpec(new[] { "x" }, Width: 0, Height: 0)));
    try
    {
        using var src = PdfSource.Open(degenerate);
        var (w, h) = src.PageSize(0);
        Check("a zero-sized page falls back to a sane size", w > 1 && h > 1, $"{w}x{h}");
    }
    catch (Exception ex)
    {
        Check("a zero-sized page falls back to a sane size", false, ex.Message);
    }
}

// ---------------------------------------------------------------------------
Section("Concurrency: the native renderer is serialised");

{
    // Exporting happens on a background thread while the editor may still be drawing
    // thumbnails on the UI thread. PDFium is not thread-safe, so this hammers both paths
    // at once; without the lock the failure mode is native memory corruption.
    using var doc = new DocumentModel();
    doc.OpenSingle(secretPdf);
    doc.AddMark(0, new RedactMark { Rect = new PtRect(70, 100, 200, 20) });

    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    var tasks = new List<Task>();

    for (int i = 0; i < 4; i++)
    {
        int n = i;
        tasks.Add(Task.Run(() =>
        {
            try
            {
                using var renderer = new PageRenderer(capacity: 2);
                for (int r = 0; r < 6; r++)
                {
                    using var bmp = renderer.RenderComposite(doc.Pages[r % doc.Pages.Count], 70 + n * 10, false);
                    if (bmp.Width <= 0) failures.Add("empty bitmap");
                }
            }
            catch (Exception ex) { failures.Add(ex.Message); }
        }));
    }

    for (int i = 0; i < 2; i++)
    {
        int n = i;
        tasks.Add(Task.Run(() =>
        {
            try
            {
                Exporter.Export(doc.Pages, Path.Combine(workDir, $"concurrent-{n}.pdf"),
                    new ExportOptions { Dpi = 150 });
            }
            catch (Exception ex) { failures.Add(ex.Message); }
        }));
    }

    Task.WaitAll(tasks.ToArray());
    Check("concurrent renders and exports all completed", failures.IsEmpty,
        string.Join(" | ", failures.Take(3)));
    Check("both concurrent exports produced files",
        File.Exists(Path.Combine(workDir, "concurrent-0.pdf")) &&
        File.Exists(Path.Combine(workDir, "concurrent-1.pdf")));
}

Console.WriteLine($"\n{passed} passed, {failed} failed.");
Console.WriteLine("Artifacts: " + workDir);
return failed == 0 ? 0 : 1;
