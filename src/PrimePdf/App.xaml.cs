using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PrimePdf;

public partial class App : Application
{
    /// <summary>File passed on the command line, e.g. when a PDF is opened with this app.</summary>
    public static string? StartupFile { get; private set; }

    /// <summary>
    /// Set while capturing screenshots. Prompts would block forever with nobody there to
    /// answer them, so they are written to the console instead.
    /// </summary>
    public static bool Headless { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A crash inside a PDF library should surface as a readable message, not a silent exit.
        DispatcherUnhandledException += OnUnhandledException;

        var window = new MainWindow();
        MainWindow = window;

        // Deployment helpers: register or remove the PDF association without any UI.
        if (e.Args.Length >= 1 && (e.Args[0] == "--register" || e.Args[0] == "--unregister"))
        {
            Headless = true;
            try
            {
                if (e.Args[0] == "--register") Shell.FileAssociation.RegisterForCurrentUser();
                else Shell.FileAssociation.UnregisterForCurrentUser();
                Console.WriteLine($"{e.Args[0]} completed for {Shell.FileAssociation.ExecutablePath}");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0] == "--selftest")
        {
            Headless = true;
            Dispatcher.InvokeAsync(async () =>
            {
                int code;
                try { code = await SelfTest.RunAsync(e.Args.ElementAtOrDefault(1)); }
                catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
                Shutdown(code);
            }, DispatcherPriority.Loaded);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--shot-dialog")
        {
            Headless = true;
            RunDialogScreenshot(e.Args[1]);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--ocr-test")
        {
            Headless = true;
            RunOcrSelfTest(e.Args[1], e.Args.ElementAtOrDefault(2));
            return;
        }

        if (TryParseScreenshotRequest(e.Args, out var shot))
        {
            Headless = true;
            RunScreenshot(window, shot);
            return;
        }

        if (e.Args.Length > 0 && File.Exists(e.Args[0]))
            StartupFile = e.Args[0];

        window.Show();
    }

    private readonly Dictionary<string, int> _errorCounts = new();
    private bool _errorDialogOpen;

    /// <summary>
    /// Keeps one failure from becoming an inescapable application.
    ///
    /// Swallowing every exception and showing a message box sounds friendly, but if the
    /// fault repeats on a timer or on every repaint the user gets an endless stack of
    /// dialogs and no way out except Task Manager. So: never re-enter, stop reporting a
    /// fault that keeps recurring, and always offer a way to quit.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        if (Headless)
        {
            Console.Error.WriteLine("Unhandled: " + e.Exception);
            return;
        }

        if (_errorDialogOpen) return;

        var key = e.Exception.GetType().FullName + "|" + e.Exception.Message;
        _errorCounts.TryGetValue(key, out int seen);
        _errorCounts[key] = ++seen;

        // After a few repeats of the identical fault, stop interrupting entirely.
        if (seen > 3) return;

        _errorDialogOpen = true;
        try
        {
            var suffix = seen == 3
                ? "\n\nThis keeps happening. Closing the app is the safest thing to do."
                : "";

            var result = MessageBox.Show(
                "Something went wrong, but your file on disk has not been changed.\n\n"
                + "Details: " + e.Exception.Message + suffix
                + "\n\nClose Prime PDF?",
                "Prime PDF",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                seen >= 3 ? MessageBoxResult.Yes : MessageBoxResult.No);

            if (result == MessageBoxResult.Yes) Shutdown();
        }
        finally
        {
            _errorDialogOpen = false;
        }
    }

    // ==================================================== screenshot mode

    private sealed record ScreenshotRequest(string Pdf, string Output, string? Tool, bool ShowPages, double? Zoom, bool Demo);

    /// <summary>
    /// `--shot &lt;input.pdf&gt; &lt;output.png&gt; [toolName] [--pages]` renders the window to an
    /// image and exits. Used to review the interface without a person at the keyboard.
    /// </summary>
    private static bool TryParseScreenshotRequest(string[] args, out ScreenshotRequest request)
    {
        request = null!;
        if (args.Length < 3 || args[0] != "--shot") return false;

        var extras = args.Skip(3).ToArray();

        double? zoom = null;
        var zoomArg = extras.FirstOrDefault(a => a.StartsWith("--zoom="));
        if (zoomArg is not null && double.TryParse(zoomArg[7..], out var z)) zoom = z;

        request = new ScreenshotRequest(
            args[1],
            args[2],
            extras.FirstOrDefault(a => !a.StartsWith("--")),
            extras.Contains("--pages"),
            zoom,
            extras.Contains("--demo"));
        return true;
    }

    private void RunScreenshot(MainWindow window, ScreenshotRequest request)
    {
        window.Show();

        window.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await window.PrepareForScreenshotAsync(request.Pdf, request.Tool, request.ShowPages, request.Zoom);
                if (request.Demo) window.ApplyDemoMarks();

                // Let layout, thumbnails and the page render all settle before capturing.
                for (int i = 0; i < 6; i++)
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(700);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                CaptureWindow(window, request.Output);
                Console.WriteLine("Wrote " + request.Output);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Screenshot failed: " + ex);
            }
            finally
            {
                Shutdown();
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Renders a dialog to an image so its wording and layout can be reviewed.</summary>
    private void RunDialogScreenshot(string outputPath)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var dialog = Dialogs.DefaultAppWindow.CreateForPreview();
                dialog.Show();

                for (int i = 0; i < 6; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(400);

                CaptureWindow(dialog, outputPath);
                Console.WriteLine("Wrote " + outputPath);
                dialog.Close();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Dialog screenshot failed: " + ex);
            }
            finally
            {
                Shutdown();
            }
        }, DispatcherPriority.Loaded);
    }

    // ====================================================== OCR self-test

    /// <summary>
    /// `--ocr-test &lt;file.pdf&gt; [overlay.png]` reads every page with OCR and reports what
    /// it found. The optional overlay draws each recognised word box back onto the page,
    /// which is the only way to be sure the coordinates line up rather than merely parse.
    /// </summary>
    private void RunOcrSelfTest(string pdfPath, string? overlayPath)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                Console.WriteLine($"OCR engine available: {Ocr.OcrService.IsAvailable}");
                Console.WriteLine($"Language: {Ocr.OcrService.LanguageName ?? "(none)"}");
                if (!Ocr.OcrService.IsAvailable) { Shutdown(1); return; }

                using var source = Core.PdfSource.Open(pdfPath);
                using var renderer = new Core.PageRenderer(capacity: 2);

                Console.WriteLine($"Pages: {source.PageCount}");

                for (int i = 0; i < source.PageCount; i++)
                {
                    Console.WriteLine($"\n--- page {i + 1} ---");
                    Console.WriteLine($"  has embedded text: {source.HasTextLayer(i)}");
                    Console.WriteLine($"  needs OCR: {source.NeedsOcr(i)}");

                    var started = Environment.TickCount64;
                    var words = await Ocr.OcrService.RecognizePageAsync(source, i, renderer);
                    source.SetOcrWords(i, words);

                    Console.WriteLine($"  recognised {words.Length} words in {Environment.TickCount64 - started} ms");
                    Console.WriteLine("  first words: " + string.Join(" ", words.Take(14).Select(w => w.Text)));

                    if (i == 0 && overlayPath is not null) WriteOverlay(source, renderer, words, overlayPath);
                }

                // Prove the recognised words flow into the shared index that every feature reads.
                var hits = Core.TextSearch.FindInPage(source.Words(0), "Whitfield", 0);
                Console.WriteLine($"\nSearch for 'Whitfield' on page 1 via the normal index: {hits.Count} hit(s)");
                foreach (var hit in hits.Take(3))
                    Console.WriteLine($"   at ({hit.Rect.X:F0},{hit.Rect.Y:F0}) {hit.Rect.W:F0}x{hit.Rect.H:F0}pt  '{hit.Context}'");

                Shutdown(hits.Count > 0 ? 0 : 2);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("OCR self-test failed: " + ex);
                Shutdown(1);
            }
        }, DispatcherPriority.Loaded);
    }

    private static void WriteOverlay(Core.PdfSource source, Core.PageRenderer renderer,
        IReadOnlyList<Core.WordBox> words, string outputPath)
    {
        const double dpi = 150;
        var entry = new Core.PageEntry { Source = source, SourceIndex = 0 };
        using var bitmap = renderer.RenderBaseCopy(entry, dpi);

        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        using (var pen = new SkiaSharp.SKPaint
        {
            Color = SkiaSharp.SKColors.Red,
            Style = SkiaSharp.SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true,
        })
        {
            double scale = dpi / 72.0;
            foreach (var w in words)
                canvas.DrawRect(
                    (float)(w.Rect.X * scale), (float)(w.Rect.Y * scale),
                    (float)(w.Rect.W * scale), (float)(w.Rect.H * scale), pen);
        }

        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 92);
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
        Console.WriteLine("  overlay written to " + outputPath);
    }

    private static void CaptureWindow(Window window, string outputPath)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Window has no size.");

        // Captured 1:1 on purpose — the point is to see exactly what a user sees, and
        // upscaling would make page rendering look softer than it really is.
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }
}
