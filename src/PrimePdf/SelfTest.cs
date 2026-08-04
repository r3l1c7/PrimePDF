using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using PrimePdf.Dialogs;
using PrimePdf.Ocr;
using PrimePdf.Shell;

namespace PrimePdf;

/// <summary>
/// Exercises the paths that only run when a person is actually using the app: building
/// every dialog, switching every tool, and the culture-dependent text APIs underneath.
///
/// This exists because of a real bug. Trimming globalization data out of the build made
/// the engine tests and screenshots pass exactly as before, then threw
/// "1033 is an invalid culture identifier" the moment a user touched the interface.
/// Headless assertions could not see it; this can.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string? samplePdf)
    {
        int passed = 0, failed = 0;

        void Check(string name, Action action)
        {
            try
            {
                action();
                passed++;
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}  -- {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Opening a document is genuinely asynchronous. Blocking on it here would deadlock
        // the dispatcher this method is running on, so it gets its own awaiting variant.
        async Task CheckAsync(string name, Func<Task> action)
        {
            try
            {
                await action();
                passed++;
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  FAIL  {name}  -- {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine("=== Display ===");
        var awareness = DpiAwareness.Describe();
        Console.WriteLine($"  Process DPI awareness: {awareness}");
        Check("process is per-monitor DPI aware", () =>
        {
            // Anything less and Windows stretches a 96 DPI window onto a scaled display,
            // which blurs the entire interface. Publishing as a single file drops the
            // manifest, so this has to be asserted against the shipped binary.
            if (awareness is not ("PerMonitorV2" or "PerMonitor"))
                throw new InvalidOperationException($"awareness is '{awareness}' — the UI will be blurry when scaled");
        });

        Console.WriteLine("\n=== Globalization ===");
        Console.WriteLine($"  Invariant mode: {AppContext.TryGetSwitch("System.Globalization.Invariant", out var inv) && inv}");

        Check("current culture resolves", () =>
        {
            var c = CultureInfo.CurrentCulture;
            if (string.IsNullOrEmpty(c.IetfLanguageTag)) throw new InvalidOperationException("no language tag");
        });
        Check("culture 1033 (en-US) resolves", () => _ = CultureInfo.GetCultureInfo(1033));
        Check("culture by name resolves", () => _ = CultureInfo.GetCultureInfo("en-US"));
        Check("XmlLanguage resolves", () => _ = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag));

        Check("FormattedText builds", () =>
        {
            var ft = new FormattedText("Sample", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 24, Brushes.Black, 1.0);
            if (ft.Width <= 0) throw new InvalidOperationException("zero width");
        });

        Check("system font families enumerate", () =>
        {
            if (!Fonts.SystemFontFamilies.Any()) throw new InvalidOperationException("no fonts");
        });

        Console.WriteLine("\n=== Theme resources ===");
        Check("brush converter parses theme colours", () =>
        {
            foreach (var hex in new[] { "#E7F0FE", "#1F6FEB", "#DFF5EA", "#FDECEA", "#FDF0DC" })
                _ = new BrushConverter().ConvertFromString(hex)
                    ?? throw new InvalidOperationException(hex);
        });
        Check("icon geometries resolve", () =>
        {
            foreach (var key in new[] { "IconOpen", "IconRedact", "IconSign", "IconHand", "IconCheck", "IconClose", "IconSearch" })
                _ = (Geometry)Application.Current.FindResource(key);
        });

        Console.WriteLine("\n=== Dialogs build ===");
        Check("default-app prompt", () => DefaultAppWindow.CreateForPreview().Close());
        Check("text entry dialog", () => TextEntryWindow.CreateForSelfTest().Close());
        Check("typed signature renders", () =>
        {
            var png = SignatureWindow.RenderTypedSignatureForSelfTest("Margaret Whitfield");
            if (png is null || png.Length < 100) throw new InvalidOperationException("no image produced");
        });

        Console.WriteLine("\n=== Services ===");
        Check("settings load", () => _ = AppSettings.Load());
        Check("file association can be queried", () =>
        {
            _ = FileAssociation.IsDefaultForPdf();
            _ = FileAssociation.IsRegistered();
            if (string.IsNullOrEmpty(FileAssociation.ExecutablePath))
                throw new InvalidOperationException("no executable path");
        });
        Check("OCR engine reports availability", () =>
        {
            var available = OcrService.IsAvailable;
            Console.WriteLine($"        (OCR available: {available}, language: {OcrService.LanguageName ?? "none"})");
        });

        Console.WriteLine("\n=== Main window ===");
        MainWindow? window = null;
        Check("main window constructs", () => { window = new MainWindow(); window.Show(); });

        if (window is not null && samplePdf is not null && File.Exists(samplePdf))
        {
            await CheckAsync("document opens", () => window.PrepareForScreenshotAsync(samplePdf, null, false, null));

            foreach (var tool in Enum.GetValues<Tool>())
                Check($"tool '{tool}' selects and builds its options", () => window.SelectToolForSelfTest(tool));

            Check("pages panel opens", () => window.ShowPagesPanelForSelfTest());
            Check("zoom controls work", () => window.ZoomForSelfTest());

            Console.WriteLine("\n=== Wheel scrolls, Ctrl+wheel zooms ===");
            Check("a plain wheel turn does not change zoom", () =>
            {
                window.FitForSelfTest();
                double before = window.CurrentZoomForSelfTest;
                double after = window.SimulateWheelForSelfTest(120, ctrlHeld: false);
                if (Math.Abs(after - before) > 1e-9)
                    throw new InvalidOperationException($"zoom moved {before:P0} -> {after:P0}; it should have scrolled");
            });

            Check("Ctrl and wheel up zooms in", () =>
            {
                double before = window.CurrentZoomForSelfTest;
                double after = window.SimulateWheelForSelfTest(120, ctrlHeld: true);
                if (after <= before)
                    throw new InvalidOperationException($"zoom went {before:P0} -> {after:P0}");
            });

            Check("Ctrl and wheel down zooms out", () =>
            {
                double before = window.CurrentZoomForSelfTest;
                double after = window.SimulateWheelForSelfTest(-120, ctrlHeld: true);
                if (after >= before)
                    throw new InvalidOperationException($"zoom went {before:P0} -> {after:P0}");
            });

            Check("the wheel handler is actually attached to the canvas", () =>
            {
                // Guards the wiring, not the maths: a detached handler would leave every
                // check above passing while the real gesture did nothing.
                window.RaiseWheelForSelfTest(120);
            });

            Console.WriteLine("\n=== Zoom always passes through 100% ===");
            Check("zooming in from a fitted page reaches exactly 100%", () =>
            {
                window.FitForSelfTest();
                double start = window.CurrentZoomForSelfTest;

                bool hit = Math.Abs(start - 1.0) < 0.0001;
                for (int i = 0; i < 24 && !hit; i++)
                {
                    double before = window.CurrentZoomForSelfTest;
                    window.ZoomInForSelfTest();
                    if (Math.Abs(window.CurrentZoomForSelfTest - before) < 1e-9) break;   // at the ceiling
                    if (Math.Abs(window.CurrentZoomForSelfTest - 1.0) < 0.0001) hit = true;
                }

                if (!hit)
                    throw new InvalidOperationException(
                        $"stepped up from {start:P0} to {window.CurrentZoomForSelfTest:P0} without landing on 100%");
            });

            Check("zooming out from the top reaches exactly 100%", () =>
            {
                for (int i = 0; i < 24; i++) window.ZoomInForSelfTest();

                bool hit = false;
                for (int i = 0; i < 24 && !hit; i++)
                {
                    double before = window.CurrentZoomForSelfTest;
                    window.ZoomOutForSelfTest();
                    if (Math.Abs(window.CurrentZoomForSelfTest - before) < 1e-9) break;
                    if (Math.Abs(window.CurrentZoomForSelfTest - 1.0) < 0.0001) hit = true;
                }

                if (!hit) throw new InvalidOperationException("stepping down never landed on 100%");
            });

            Console.WriteLine("\n=== Clicking the same word twice ===");
            Check("black out toggles on and off", () =>
            {
                window.SelectToolForSelfTest(Tool.Redact);

                var page = window.PageForSelfTest(0)
                           ?? throw new InvalidOperationException("no page");
                var word = page.Source.Words(page.SourceIndex).FirstOrDefault(w => w.Text.Length > 3);
                if (word.Text is null) throw new InvalidOperationException("no word to click");

                var centre = new Core.PtPoint(word.Rect.CenterX, word.Rect.CenterY);
                int before = page.Marks.Count;

                window.SimulateClickForSelfTest(0, centre);
                if (page.Marks.Count != before + 1)
                    throw new InvalidOperationException($"first click should add one mark, got {page.Marks.Count - before}");

                // The bug this guards: the second click used to stack an identical bar on
                // the first, so nothing appeared to happen and there was no way back.
                window.SimulateClickForSelfTest(0, centre);
                if (page.Marks.Count != before)
                    throw new InvalidOperationException($"second click should remove it, got {page.Marks.Count - before} extra");

                window.SimulateClickForSelfTest(0, centre);
                if (page.Marks.Count != before + 1)
                    throw new InvalidOperationException("third click should put it back");
            });
        }

        window?.Close();

        Console.WriteLine($"\n{passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
}
