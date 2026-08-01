using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PrimePdf.Core;
using TextSearch = PrimePdf.Core.TextSearch;

namespace PrimePdf.Dialogs;

public sealed class FindOutcome
{
    public int? GoToPage { get; init; }
    public List<(int PageIndex, PtRect Rect)> RedactAll { get; init; } = new();
}

/// <summary>
/// Searches every page and offers to black out all matches at once — the fastest safe way
/// to remove a name, an account number or an address from a long document.
/// </summary>
public sealed class FindWindow : DialogShell
{
    private readonly DocumentModel _doc;
    private readonly TextBox _query;
    private readonly ListBox _results = new()
    {
        MaxHeight = 260,
        BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 12, 0, 0),
    };
    private readonly TextBlock _summary;
    private readonly Button _redactAll;

    private List<SearchHit> _hits = new();
    private FindOutcome? _outcome;

    private FindWindow(Window? owner, DocumentModel doc) : base(owner, "Find words", AppDialogKind.Info, width: 640)
    {
        _doc = doc;

        AddParagraph("Type what you are looking for — a name, a phone number, an address.", muted: true);

        _query = new TextBox { FontSize = 17 };
        _query.TextChanged += (_, _) => RunSearch();
        _query.KeyDown += (_, e) => { if (e.Key == Key.Enter) RunSearch(); };
        BodyPanel.Children.Add(_query);

        _summary = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 14.5,
            Foreground = (Brush)App.Current.FindResource("MutedBrush"),
            Text = "",
        };
        BodyPanel.Children.Add(_summary);

        _results.MouseDoubleClick += (_, _) => GoToSelected();
        _results.BorderBrush = (Brush)App.Current.FindResource("BorderBrush2");
        BodyPanel.Children.Add(_results);

        AddButton("Close", primary: false, () => DialogResult = false);
        AddButton("Go to page", primary: false, GoToSelected);
        _redactAll = AddButton("Black out every match", primary: true, RedactEverything);
        _redactAll.IsEnabled = false;

        Loaded += (_, _) => _query.Focus();
    }

    private void RunSearch()
    {
        _results.Items.Clear();
        _hits = new List<SearchHit>();

        var query = _query.Text.Trim();
        if (query.Length < 2)
        {
            _summary.Text = query.Length == 0 ? "" : "Type at least two characters.";
            _redactAll.IsEnabled = false;
            return;
        }

        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            var page = _doc.Pages[i];
            var words = page.Source.Words(page.SourceIndex);
            _hits.AddRange(TextSearch.FindInPage(words, query, i));
        }

        foreach (var hit in _hits)
        {
            var row = new StackPanel { Margin = new Thickness(4, 6, 4, 6) };
            row.Children.Add(new TextBlock
            {
                Text = $"Page {hit.PageIndex + 1}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13.5,
            });
            row.Children.Add(new TextBlock
            {
                Text = hit.Context,
                FontSize = 13.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)App.Current.FindResource("MutedBrush"),
            });
            _results.Items.Add(new ListBoxItem { Content = row, Tag = hit });
        }

        int pagesWith = _hits.Select(h => h.PageIndex).Distinct().Count();
        _summary.Text = _hits.Count == 0
            ? "No matches. (Scanned pages have no searchable text.)"
            : $"{_hits.Count} match(es) on {pagesWith} page(s).";

        _redactAll.IsEnabled = _hits.Count > 0;
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
    }

    private void GoToSelected()
    {
        if (_results.SelectedItem is not ListBoxItem { Tag: SearchHit hit }) return;
        _outcome = new FindOutcome { GoToPage = hit.PageIndex };
        DialogResult = true;
    }

    private void RedactEverything()
    {
        if (_hits.Count == 0) return;

        var confirmed = AppDialog.Ask(this,
            $"Black out {_hits.Count} match(es)?",
            $"Every place “{_query.Text.Trim()}” appears will be covered with a black bar, and the text underneath "
            + "will be permanently deleted when you save.\n\nYou can still undo this before saving.",
            "Black them out", "Cancel", AppDialogKind.Warning);

        if (!confirmed) return;

        _outcome = new FindOutcome
        {
            RedactAll = _hits.Select(h => (h.PageIndex, h.Rect)).ToList(),
        };
        DialogResult = true;
    }

    public static FindOutcome? Run(Window? owner, DocumentModel doc)
    {
        var w = new FindWindow(owner, doc);
        return w.ShowDialog() == true ? w._outcome : null;
    }
}
