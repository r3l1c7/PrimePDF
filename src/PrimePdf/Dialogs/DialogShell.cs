using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace PrimePdf.Dialogs;

public enum AppDialogKind { Info, Success, Warning, Error }

/// <summary>
/// Shared chrome for every dialog in the app: one rounded card, a clear heading, a body,
/// and a row of large buttons. Keeping it in one place means every prompt the user meets
/// looks and behaves the same, which is most of what "easy to use" actually means.
/// </summary>
public class DialogShell : Window
{
    protected readonly StackPanel BodyPanel = new();
    protected readonly StackPanel ButtonRow = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    private readonly TextBlock _heading = new();
    private readonly Border _iconBadge = new();
    private readonly Path _iconPath = new();

    protected DialogShell(Window? owner, string heading, AppDialogKind kind = AppDialogKind.Info, double width = 520)
    {
        Owner = owner;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = width;
        ShowInTaskbar = false;
        FontFamily = (FontFamily)App.Current.FindResource("UiFont");
        Title = heading;

        _heading.Text = heading;
        _heading.FontSize = 21;
        _heading.FontWeight = FontWeights.SemiBold;
        _heading.TextWrapping = TextWrapping.Wrap;
        _heading.VerticalAlignment = VerticalAlignment.Center;
        _heading.FontFamily = (FontFamily)App.Current.FindResource("UiFontDisplay");

        ConfigureBadge(kind);

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(_iconBadge);
        header.Children.Add(_heading);

        BodyPanel.Margin = new Thickness(0, 0, 0, 22);

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(BodyPanel);
        root.Children.Add(ButtonRow);

        var card = new Border
        {
            Background = (Brush)App.Current.FindResource("Surface"),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(30, 26, 30, 24),
            Margin = new Thickness(24),
            Child = root,
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 6, Opacity = 0.28, Color = Color.FromRgb(0x18, 0x20, 0x2A) },
        };

        Content = card;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { OnEscape(); e.Handled = true; }
        };
    }

    private void ConfigureBadge(AppDialogKind kind)
    {
        var (badge, stroke, geometryKey) = kind switch
        {
            AppDialogKind.Success => ("#DFF5EA", "#0E9F6E", "IconCheck"),
            AppDialogKind.Warning => ("#FDF0DC", "#B4690E", "IconRedact"),
            AppDialogKind.Error => ("#FDECEA", "#D92D20", "IconClose"),
            _ => ("#E7F0FE", "#1F6FEB", "IconSearch"),
        };

        _iconPath.Data = (Geometry)App.Current.FindResource(geometryKey);
        _iconPath.Stroke = (Brush)new BrushConverter().ConvertFromString(stroke)!;
        _iconPath.StrokeThickness = 2;
        _iconPath.StrokeStartLineCap = PenLineCap.Round;
        _iconPath.StrokeEndLineCap = PenLineCap.Round;
        _iconPath.StrokeLineJoin = PenLineJoin.Round;
        _iconPath.Stretch = Stretch.Uniform;
        _iconPath.Width = 22;
        _iconPath.Height = 22;

        _iconBadge.Background = (Brush)new BrushConverter().ConvertFromString(badge)!;
        _iconBadge.CornerRadius = new CornerRadius(11);
        _iconBadge.Width = 42;
        _iconBadge.Height = 42;
        _iconBadge.Margin = new Thickness(0, 0, 14, 0);
        _iconBadge.VerticalAlignment = VerticalAlignment.Center;
        _iconBadge.Child = _iconPath;
    }

    protected virtual void OnEscape() => DialogResult = false;

    protected TextBlock AddParagraph(string text, bool muted = false)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 15.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 23,
            Margin = new Thickness(0, 0, 0, 10),
        };
        if (muted) tb.Foreground = (Brush)App.Current.FindResource("MutedBrush");
        BodyPanel.Children.Add(tb);
        return tb;
    }

    protected Button AddButton(string text, bool primary, Action onClick, bool isDefault = false)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)App.Current.FindResource(primary ? "PrimaryButton" : "SecondaryButton"),
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 118,
            IsDefault = isDefault,
        };
        b.Click += (_, _) => onClick();
        ButtonRow.Children.Add(b);
        return b;
    }

    /// <summary>A soft callout box for the one thing the user most needs to notice.</summary>
    protected Border AddCallout(string text, string background, string accent)
    {
        var border = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(background)!,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 13, 16, 13),
            Margin = new Thickness(0, 6, 0, 4),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString(accent)!,
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14.5,
                LineHeight = 22,
            },
        };
        BodyPanel.Children.Add(border);
        return border;
    }
}
