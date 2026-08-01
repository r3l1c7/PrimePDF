using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PrimePdf.Dialogs;

public sealed record TextEntryResult(string Text, double FontSize, bool Bold);

/// <summary>
/// Asks for a piece of text, showing it at the size it will actually appear so the user
/// can judge the result before committing to it.
/// </summary>
public sealed class TextEntryWindow : DialogShell
{
    private static readonly double[] Sizes = { 8, 9, 10, 11, 12, 14, 16, 18, 24, 32 };

    private readonly TextBox _input;
    private readonly TextBlock _preview;
    private readonly StackPanel _sizeRow = new() { Orientation = Orientation.Horizontal };
    private readonly ToggleButton _bold;

    private double _fontSize;

    private TextEntryWindow(Window? owner, string heading, string initial, double fontSize, string? note)
        : base(owner, heading, AppDialogKind.Info, width: 600)
    {
        _fontSize = Math.Clamp(fontSize <= 0 ? 12 : fontSize, 6, 48);

        if (note is not null) AddParagraph(note, muted: true);

        _input = new TextBox
        {
            Text = initial,
            FontSize = 17,
            MinHeight = 92,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 14),
        };
        BodyPanel.Children.Add(_input);

        // ------------------------------------------------------ size + bold
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        controls.Children.Add(new TextBlock
        {
            Text = "Size:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = (Brush)App.Current.FindResource("MutedBrush"),
        });

        var sizeScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxWidth = 340,
            Content = _sizeRow,
        };
        controls.Children.Add(sizeScroll);

        _bold = new ToggleButton
        {
            Style = (Style)App.Current.FindResource("PillToggle"),
            Content = new TextBlock { Text = "B", FontWeight = FontWeights.Bold, FontSize = 16 },
            Margin = new Thickness(14, 0, 0, 0),
            ToolTip = "Make the text bold",
        };
        _bold.Click += (_, _) => UpdatePreview();
        controls.Children.Add(_bold);

        BodyPanel.Children.Add(controls);
        BuildSizePills();

        // ---------------------------------------------------------- preview
        _preview = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Black,
        };

        BodyPanel.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = (Brush)App.Current.FindResource("BorderBrush2"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14),
            MinHeight = 66,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "How it will look on the page",
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 0, 8),
                        Foreground = (Brush)App.Current.FindResource("MutedBrush"),
                    },
                    _preview,
                },
            },
        });

        _input.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        AddButton("Cancel", primary: false, () => DialogResult = false);
        AddButton("Apply", primary: true, () => DialogResult = true, isDefault: true);

        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    private void BuildSizePills()
    {
        _sizeRow.Children.Clear();
        foreach (var size in Sizes)
        {
            var pill = new ToggleButton
            {
                Style = (Style)App.Current.FindResource("PillToggle"),
                Content = size.ToString("0"),
                IsChecked = Math.Abs(size - _fontSize) < 0.6,
                MinWidth = 42,
            };
            pill.Click += (_, _) =>
            {
                _fontSize = size;
                BuildSizePills();
                UpdatePreview();
            };
            _sizeRow.Children.Add(pill);
        }
    }

    private void UpdatePreview()
    {
        _preview.Text = string.IsNullOrEmpty(_input.Text) ? "(nothing yet)" : _input.Text;
        // Points to DIPs, so the preview is the true on-page size at 100% zoom.
        _preview.FontSize = Math.Max(6, _fontSize * 96.0 / 72.0);
        _preview.FontWeight = _bold.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        _preview.Foreground = string.IsNullOrEmpty(_input.Text)
            ? (Brush)App.Current.FindResource("MutedBrush")
            : Brushes.Black;
    }

    /// <summary>Builds the dialog without showing it, for the start-up self-test.</summary>
    internal static Window CreateForSelfTest() =>
        new TextEntryWindow(null, "Change the wording", "example", 12, "note");

    public static TextEntryResult? Prompt(Window? owner, string heading, string initial, double fontSize, string? note)
    {
        var w = new TextEntryWindow(owner, heading, initial, fontSize, note);
        if (w.ShowDialog() != true) return null;
        return new TextEntryResult(w._input.Text, w._fontSize, w._bold.IsChecked == true);
    }
}
