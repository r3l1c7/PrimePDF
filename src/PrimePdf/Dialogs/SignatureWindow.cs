using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PrimePdf.Dialogs;

/// <summary>Keeps the user's signature on disk so they only ever have to draw it once.</summary>
public static class SignatureStore
{
    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrimePdf");

    private static string FilePath => Path.Combine(Folder, "signature.png");

    public static byte[]? Load()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(byte[] png)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(FilePath, png);
        }
        catch
        {
            // A signature that cannot be remembered is a small loss; the current one still works.
        }
    }

    /// <summary>Height divided by width, used to place the stamp without distorting it.</summary>
    public static double AspectRatio(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return frame.PixelWidth <= 0 ? 0.35 : (double)frame.PixelHeight / frame.PixelWidth;
        }
        catch
        {
            return 0.35;
        }
    }
}

/// <summary>Captures a signature by drawing it, typing it, or loading a picture of one.</summary>
public sealed class SignatureWindow : DialogShell
{
    private readonly InkCanvas _ink;
    private readonly TextBox _typed;
    private readonly TextBlock _typedPreview;
    private readonly TabControl _tabs;
    private byte[]? _imported;
    private readonly Image _importedPreview;

    private static readonly string[] ScriptFonts =
        { "Segoe Script", "Brush Script MT", "Lucida Handwriting", "Gabriola", "Segoe UI" };

    private SignatureWindow(Window? owner) : base(owner, "Your signature", AppDialogKind.Info, width: 660)
    {
        AddParagraph("Sign once and this app will remember it, so next time you only have to click the page.", muted: true);

        _ink = new InkCanvas
        {
            Height = 190,
            Background = Brushes.White,
            DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = Colors.Black,
                Width = 3.2,
                Height = 3.2,
                FitToCurve = true,
                IgnorePressure = false,
            },
        };

        _typed = new TextBox { FontSize = 17, Margin = new Thickness(0, 0, 0, 12) };
        _typedPreview = new TextBlock
        {
            FontSize = 44,
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = PickScriptFont(),
        };
        _typed.TextChanged += (_, _) => _typedPreview.Text = _typed.Text;

        _importedPreview = new Image { Stretch = Stretch.Uniform, Height = 150 };

        _tabs = new TabControl
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 6, 0, 0),
            Items =
            {
                new TabItem { Header = "  Draw it  ", Content = Framed(_ink) },
                new TabItem { Header = "  Type it  ", Content = BuildTypedPanel() },
                new TabItem { Header = "  Use a picture  ", Content = BuildImportPanel() },
            },
        };
        BodyPanel.Children.Add(_tabs);

        AddButton("Cancel", primary: false, () => DialogResult = false);
        AddButton("Clear", primary: false, ClearCurrentTab);
        AddButton("Save signature", primary: true, () => DialogResult = true, isDefault: true);
    }

    private static FontFamily PickScriptFont()
    {
        var installed = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var match = ScriptFonts.FirstOrDefault(installed.Contains) ?? "Segoe UI";
        return new FontFamily(match);
    }

    private static Border Framed(UIElement child) => new()
    {
        Background = Brushes.White,
        BorderBrush = (Brush)App.Current.FindResource("BorderBrush2"),
        BorderThickness = new Thickness(1.4),
        CornerRadius = new CornerRadius(10),
        Margin = new Thickness(0, 10, 0, 0),
        Padding = new Thickness(4),
        Child = child,
    };

    private UIElement BuildTypedPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "Type your name:",
            Margin = new Thickness(0, 0, 0, 6),
            Foreground = (Brush)App.Current.FindResource("MutedBrush"),
        });
        panel.Children.Add(_typed);
        panel.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = (Brush)App.Current.FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1.4),
            CornerRadius = new CornerRadius(10),
            Height = 120,
            Child = _typedPreview,
        });
        return panel;
    }

    private UIElement BuildImportPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        var pick = new Button
        {
            Content = "Choose a picture of your signature…",
            Style = (Style)App.Current.FindResource("SecondaryButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
        };
        pick.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choose a picture",
                Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                _imported = File.ReadAllBytes(dlg.FileName);
                using var ms = new MemoryStream(_imported);
                _importedPreview.Source = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch (Exception ex)
            {
                _imported = null;
                AppDialog.Info(this, "That picture could not be read", ex.Message, AppDialogKind.Error);
            }
        };

        panel.Children.Add(pick);
        panel.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = (Brush)App.Current.FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1.4),
            CornerRadius = new CornerRadius(10),
            Height = 160,
            Padding = new Thickness(8),
            Child = _importedPreview,
        });
        return panel;
    }

    private void ClearCurrentTab()
    {
        switch (_tabs.SelectedIndex)
        {
            case 0: _ink.Strokes.Clear(); break;
            case 1: _typed.Clear(); break;
            default:
                _imported = null;
                _importedPreview.Source = null;
                break;
        }
    }

    /// <summary>Renders whatever the active tab holds into a transparent PNG.</summary>
    private byte[]? Produce() => _tabs.SelectedIndex switch
    {
        0 => RenderStrokes(),
        1 => RenderTypedText(),
        _ => _imported,
    };

    private byte[]? RenderStrokes()
    {
        if (_ink.Strokes.Count == 0) return null;

        var bounds = _ink.Strokes.GetBounds();
        if (bounds.Width < 1 || bounds.Height < 1) return null;

        const double pad = 8;
        const double scale = 3;  // oversample so the signature stays crisp when enlarged

        int w = (int)Math.Ceiling((bounds.Width + pad * 2) * scale);
        int h = (int)Math.Ceiling((bounds.Height + pad * 2) * scale);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X + pad, -bounds.Y + pad));
            _ink.Strokes.Draw(dc);
            dc.Pop();
            dc.Pop();
        }

        return Encode(visual, w, h);
    }

    private byte[]? RenderTypedText()
    {
        if (string.IsNullOrWhiteSpace(_typed.Text)) return null;

        const double fontSize = 120;
        var typeface = new Typeface(_typedPreview.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(
            _typed.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            1.0);

        const double pad = 12;
        int w = (int)Math.Ceiling(formatted.Width + pad * 2);
        int h = (int)Math.Ceiling(formatted.Height + pad * 2);
        if (w < 2 || h < 2) return null;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawText(formatted, new Point(pad, pad));

        return Encode(visual, w, h);
    }

    private static byte[] Encode(DrawingVisual visual, int width, int height)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds the dialog and renders a typed signature without showing anything. Used by
    /// the start-up self-test: this path touches font enumeration and FormattedText, both
    /// of which depend on culture data being present.
    /// </summary>
    internal static byte[]? RenderTypedSignatureForSelfTest(string name)
    {
        var window = new SignatureWindow(null);
        window._typed.Text = name;
        window._tabs.SelectedIndex = 1;
        return window.RenderTypedText();
    }

    /// <returns>The signature PNG, or null if the user cancelled or drew nothing.</returns>
    public static byte[]? Capture(Window? owner)
    {
        var w = new SignatureWindow(owner);
        if (w.ShowDialog() != true) return null;

        var png = w.Produce();
        if (png is null)
        {
            AppDialog.Info(owner, "Nothing to save",
                "Draw your signature, type your name, or choose a picture first.", AppDialogKind.Warning);
            return null;
        }

        SignatureStore.Save(png);
        return png;
    }
}
