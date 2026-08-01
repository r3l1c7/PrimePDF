using System.Windows;
using PrimePdf.Core;
using PrimePdf.Dialogs;
using PrimePdf.Ocr;

namespace PrimePdf;

/// <summary>
/// Scanned pages carry no text, so clicking a word, correcting wording and searching all
/// have nothing to work with. Reading them with OCR feeds recognised words into the same
/// index the rest of the app already uses, and every one of those features starts working
/// without any further special handling.
/// </summary>
public partial class MainWindow
{
    private bool _ocrOfferedForDocument;
    private bool _ocrRunning;

    /// <summary>Distinct source pages that are scans and have not been read yet.</summary>
    private List<(PdfSource Source, int Index)> ScannedPages() => _doc.Pages
        .Select(p => (p.Source, p.SourceIndex))
        .Distinct()
        .Where(x => x.Source.NeedsOcr(x.SourceIndex))
        .OrderBy(x => x.SourceIndex)
        .ToList();

    /// <summary>Offers once per opened document, if there is anything worth reading.</summary>
    private async Task OfferOcrIfNeededAsync()
    {
        if (_ocrOfferedForDocument || App.Headless) return;
        _ocrOfferedForDocument = true;

        var scans = ScannedPages();
        if (scans.Count == 0) return;

        if (!OcrService.IsAvailable)
        {
            SetHint("Some pages are pictures of text, so words cannot be clicked on them. "
                    + "You can still drag a box over anything you want to hide.");
            return;
        }

        var many = scans.Count > 1;
        bool accepted = AppDialog.Ask(this,
            many ? $"{scans.Count} pages are scans" : "This page is a scan",
            (many
                ? "Some pages in this document are pictures of text rather than text itself. "
                : "This page is a picture of text rather than text itself. ")
            + "That means you cannot click a word to black it out, and searching will not find anything.\n\n"
            + "Would you like Prime PDF to read "
            + (many ? "those pages" : "it") + " first? It takes a few seconds and happens entirely on this computer.",
            "Read the text", "Not now",
            AppDialogKind.Info);

        if (accepted) await RunOcrAsync(scans);
    }

    /// <summary>True when the page on screen is an unread scan.</summary>
    private bool NeedsOcrHere() =>
        CurrentPage is { } page && page.Source.NeedsOcr(page.SourceIndex);

    /// <summary>Offers to read just the page being looked at, when a tool needs text.</summary>
    private async Task OfferOcrForCurrentPageAsync()
    {
        var page = CurrentPage;
        if (page is null || _ocrRunning) return;
        if (!page.Source.NeedsOcr(page.SourceIndex)) return;

        if (!OcrService.IsAvailable)
        {
            SetHint("This page is a picture of text. Drag a box over what you want to hide.");
            return;
        }

        bool accepted = AppDialog.Ask(this,
            "This page is a scan",
            "There is no text on this page for the app to find — it is a picture. "
            + "Shall I read it so you can click on words?\n\nIt takes a few seconds.",
            "Read this page", "Not now",
            AppDialogKind.Info);

        if (accepted)
            await RunOcrAsync(new List<(PdfSource, int)> { (page.Source, page.SourceIndex) });
    }

    private async Task RunOcrAsync(IReadOnlyList<(PdfSource Source, int Index)> targets)
    {
        if (targets.Count == 0 || _ocrRunning) return;
        _ocrRunning = true;

        int read = 0, wordsFound = 0;
        try
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var (source, index) = targets[i];
                ShowBusy(targets.Count == 1
                    ? "Reading the page…"
                    : $"Reading page {i + 1} of {targets.Count}…");

                try
                {
                    // Rasterising happens here on the UI thread (PDFium is not safe to call
                    // concurrently); the recognition itself is genuinely asynchronous, which
                    // is where the bulk of the time goes.
                    var words = await OcrService.RecognizePageAsync(source, index, _renderer);
                    source.SetOcrWords(index, words);
                    wordsFound += words.Length;
                    read++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"OCR failed on page {index + 1}: {ex.Message}");

                    // Record the failure so the same page is not offered again and again.
                    source.SetOcrWords(index, Array.Empty<WordBox>());
                }
            }
        }
        finally
        {
            _ocrRunning = false;
            HideBusy();
        }

        UpdateChrome();

        if (wordsFound == 0)
        {
            AppDialog.Info(this, "No text could be read",
                "Prime PDF could not make out any words on "
                + (read == 1 ? "that page" : "those pages")
                + ". This happens with faint or handwritten scans.\n\n"
                + "You can still drag a box over anything you want to black out.",
                AppDialogKind.Warning);
            return;
        }

        var language = OcrService.LanguageName;
        SetHint($"Read {wordsFound} word(s)"
                + (language is null ? "" : $" in {language}")
                + ". You can now click words to black them out, correct wording, and use Find.");
    }
}
