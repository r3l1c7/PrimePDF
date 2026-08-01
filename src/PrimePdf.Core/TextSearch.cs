namespace PrimePdf.Core;

public readonly record struct SearchHit(int PageIndex, PtRect Rect, string Context);

/// <summary>
/// Finds a phrase on a page and reports exactly which rectangles cover it.
///
/// Words are stitched back into lines first, so a search for "123-45-6789" or "Jane Roe"
/// matches across the word boundaries the extractor happened to produce.
/// </summary>
public static class TextSearch
{
    public static List<SearchHit> FindInPage(IReadOnlyList<WordBox> words, string query, int pageIndex)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query) || words.Count == 0) return hits;

        query = query.Trim();

        foreach (var line in GroupIntoLines(words))
        {
            // Build the line's text while remembering which characters came from which word.
            var text = new System.Text.StringBuilder();
            var owner = new List<int>();

            for (int i = 0; i < line.Count; i++)
            {
                if (i > 0) { text.Append(' '); owner.Add(-1); }
                foreach (var _ in line[i].Text) owner.Add(i);
                text.Append(line[i].Text);
            }

            var haystack = text.ToString();
            int from = 0;
            while (from <= haystack.Length - query.Length)
            {
                int at = haystack.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0) break;

                var touched = new HashSet<int>();
                for (int c = at; c < at + query.Length && c < owner.Count; c++)
                    if (owner[c] >= 0) touched.Add(owner[c]);

                if (touched.Count > 0)
                {
                    var rect = line[touched.Min()].Rect;
                    foreach (var idx in touched) rect = rect.Union(line[idx].Rect);

                    int ctxStart = Math.Max(0, at - 28);
                    int ctxEnd = Math.Min(haystack.Length, at + query.Length + 28);
                    var context = (ctxStart > 0 ? "…" : "")
                                  + haystack[ctxStart..ctxEnd].Trim()
                                  + (ctxEnd < haystack.Length ? "…" : "");

                    hits.Add(new SearchHit(pageIndex, rect.Inflate(1), context));
                }

                from = at + Math.Max(1, query.Length);
            }
        }

        return hits;
    }

    /// <summary>Buckets words into visual lines by their vertical centre.</summary>
    private static List<List<WordBox>> GroupIntoLines(IReadOnlyList<WordBox> words)
    {
        var lines = new List<List<WordBox>>();

        foreach (var word in words.OrderBy(w => w.Rect.CenterY).ThenBy(w => w.Rect.X))
        {
            var line = lines.FirstOrDefault(l =>
                Math.Abs(l[0].Rect.CenterY - word.Rect.CenterY) <= Math.Max(2, word.Rect.H * 0.6));

            if (line is null) lines.Add(new List<WordBox> { word });
            else line.Add(word);
        }

        foreach (var line in lines) line.Sort((a, b) => a.Rect.X.CompareTo(b.Rect.X));
        return lines;
    }
}
