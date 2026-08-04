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

    /// <summary>
    /// The deliberate "make this searchable" action, as opposed to the incidental OCR
    /// offered when a tool needs text. Reading a whole document so it can be searched in
    /// any reader is a task in its own right, and it should not require guessing that
    /// clicking a word is how you start it.
    /// </summary>
    private async void OnMakeSearchable(object sender, RoutedEventArgs e)
    {
        if (_doc.IsEmpty || _ocrRunning) return;

        if (!OcrService.IsAvailable)
        {
            AppDialog.Info(this, "Text recognition is not available",
                "Windows does not have a language pack installed for reading text from pictures.\n\n"
                + "You can add one under Settings, Time & language, Language & region.",
                AppDialogKind.Warning);
            return;
        }

        var scans = ScannedPages();

        if (scans.Count == 0)
        {
            bool alreadyRead = _doc.Pages.Any(p => p.Source.RecognisedWords(p.SourceIndex).Length > 0);

            AppDialog.Info(this,
                alreadyRead ? "Already done" : "Nothing to read here",
                alreadyRead
                    ? "The scanned pages in this document have already been read. Use Save a Copy to keep a searchable version."
                    : "Every page in this document already contains real text, so it can be searched as it is. "
                      + "Reading is only needed for pages that are pictures of text.",
                AppDialogKind.Info);
            return;
        }

        bool confirmed = AppDialog.Ask(this,
            scans.Count == 1 ? "Read 1 scanned page?" : $"Read {scans.Count} scanned pages?",
            "The words on those pages will be recognised and stored invisibly behind the picture, so the saved "
            + "copy can be searched and copied from in any PDF reader.\n\n"
            + "The page will look exactly the same. This happens entirely on this computer.",
            "Read them", "Cancel",
            AppDialogKind.Info);

        if (!confirmed) return;

        await RunOcrAsync(scans, markDirty: true);

        int read = _doc.Pages.Count(p => p.Source.RecognisedWords(p.SourceIndex).Length > 0);
        if (read == 0) return;   // RunOcrAsync has already explained the failure

        AppDialog.Info(this, "Ready to save",
            $"{read} page(s) can now be searched.\n\n"
            + "Use Save a Copy to write a searchable version of the file. The text is added invisibly, "
            + "so nothing on the page looks different.",
            AppDialogKind.Success);
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

    private async Task RunOcrAsync(IReadOnlyList<(PdfSource Source, int Index)> targets, bool markDirty = false)
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

        // Recognised text only reaches the file when it is saved, so the document counts
        // as changed from here on.
        if (markDirty) _doc.MarkDirty();

        var language = OcrService.LanguageName;
        SetHint($"Read {wordsFound} word(s)"
                + (language is null ? "" : $" in {language}")
                + ". You can now click words to black them out, correct wording, and use Find.");
    }
}
