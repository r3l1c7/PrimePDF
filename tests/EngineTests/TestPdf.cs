using System.Text;

namespace EngineTests;

/// <summary>
/// Hand-rolls tiny PDFs with text at exactly known coordinates, so assertions about
/// where words land and whether they survive an export are unambiguous.
/// </summary>
public static class TestPdf
{
    public sealed record PageSpec(string[] Lines, int Rotate = 0, double Width = 612, double Height = 792);

    public static byte[] Build(params PageSpec[] pages)
    {
        var objects = new List<string>();

        // 1 = catalog, 2 = page tree, then 3 objects per page, font last.
        int fontObj = 3 + pages.Length * 2;

        var kids = string.Join(" ", Enumerable.Range(0, pages.Length).Select(i => $"{3 + i * 2} 0 R"));
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} >>");

        foreach (var (spec, i) in pages.Select((p, i) => (p, i)))
        {
            int contentObj = 4 + i * 2;
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {spec.Width} {spec.Height}] /Rotate {spec.Rotate} " +
                $"/Resources << /Font << /F1 {fontObj} 0 R >> >> /Contents {contentObj} 0 R >>");

            var sb = new StringBuilder();
            double y = spec.Height - 100;
            foreach (var line in spec.Lines)
            {
                sb.Append($"BT /F1 18 Tf 72 {y:F0} Td ({Escape(line)}) Tj ET\n");
                y -= 40;
            }
            var content = sb.ToString();
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++) W($"{offsets[i]:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    public static string WriteTemp(string dir, string name, byte[] bytes)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
