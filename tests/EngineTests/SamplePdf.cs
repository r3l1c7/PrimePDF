using System.Text;

namespace EngineTests;

/// <summary>
/// A realistic-looking form used to exercise the editor by hand: it has the kind of
/// personal details someone would actually want to black out before emailing it on.
/// </summary>
public static class SamplePdf
{
    private const double W = 612, H = 792;

    public static byte[] Build()
    {
        var pages = new[] { Page1(), Page2(), Page3() };
        return Assemble(pages);
    }

    private sealed class Canvas
    {
        private readonly StringBuilder _sb = new();

        /// <summary>
        /// Always states its fill colour. PDF graphics state is sticky, so a filled
        /// rectangle earlier in the stream would otherwise tint every later run of text.
        /// </summary>
        public void Text(string font, double size, double x, double y, string value, double gray = 0) =>
            _sb.Append($"{gray:0.##} g BT /{font} {size:0.##} Tf {x:0.##} {y:0.##} Td ({Escape(value)}) Tj ET\n");

        public void Line(double x1, double y1, double x2, double y2, double width = 0.8, double gray = 0.75)
            => _sb.Append($"{gray:0.##} G {width:0.##} w {x1:0.##} {y1:0.##} m {x2:0.##} {y2:0.##} l S\n");

        public void Rect(double x, double y, double w, double h, double gray)
            => _sb.Append($"{gray:0.###} g {x:0.##} {y:0.##} {w:0.##} {h:0.##} re f\n");

        public void Box(double x, double y, double size)
            => _sb.Append($"0.35 G 1 w {x:0.##} {y:0.##} {size:0.##} {size:0.##} re S\n");

        public override string ToString() => _sb.ToString();
    }

    private static string Page1()
    {
        var c = new Canvas();
        c.Rect(0, H - 96, W, 96, 0.93);
        c.Text("F2", 22, 56, H - 56, "Northgate Family Clinic");
        c.Text("F1", 11, 56, H - 76, "Patient Registration and Consent Form");

        double y = H - 140;
        c.Text("F2", 13, 56, y, "1.  Patient details");
        c.Line(56, y - 8, W - 56, y - 8);

        y -= 34;
        void Field(string label, string value)
        {
            c.Text("F1", 10.5, 56, y, label);
            c.Text("F1", 11.5, 190, y, value);
            c.Line(186, y - 5, W - 56, y - 5, 0.6, 0.82);
            y -= 30;
        }

        // Every identifier below is from a range reserved for fiction, so nothing here can
        // collide with a real person's details once this repository is public:
        //   987-65-4320..4329  reserved by the SSA for advertising and fiction
        //   555-0100..555-0199 reserved fictional telephone numbers
        //   example.com        reserved for documentation by RFC 2606
        Field("Full name", "Margaret Ellen Whitfield");
        Field("Date of birth", "14 March 1948");
        Field("Social Security No.", "987-65-4321");
        Field("Home address", "1187 Cedar Hollow Road, Apt 4B");
        Field("City / State / ZIP", "Brookfield, VT 05036");
        Field("Telephone", "(802) 555-0148");
        Field("Email", "m.whitfield48@example.com");

        y -= 8;
        c.Text("F2", 13, 56, y, "2.  Insurance");
        c.Line(56, y - 8, W - 56, y - 8);
        y -= 34;

        Field("Provider", "Green Mountain Health");
        Field("Member ID", "GMH-4471-88203");
        Field("Group number", "0091447");

        y -= 10;
        c.Text("F1", 10, 56, y, "Please check all that apply:");
        y -= 26;
        c.Box(58, y - 3, 12);
        c.Text("F1", 11, 78, y, "I consent to treatment");
        c.Box(258, y - 3, 12);
        c.Text("F1", 11, 278, y, "I have read the privacy notice");

        y -= 46;
        c.Text("F1", 10.5, 56, y, "Signature");
        c.Line(130, y - 4, 360, y - 4, 0.9, 0.4);
        c.Text("F1", 10.5, 380, y, "Date");
        c.Line(415, y - 4, W - 56, y - 4, 0.9, 0.4);

        c.Text("F1", 9, 56, 52, "Page 1 of 3   -Form NFC-118   -Revised January 2026");
        return c.ToString();
    }

    private static string Page2()
    {
        var c = new Canvas();
        c.Text("F2", 17, 56, H - 70, "3.  Medical history");
        c.Line(56, H - 80, W - 56, H - 80);

        double y = H - 112;
        string[] rows =
        {
            "Do you currently take any prescription medication?          Yes",
            "Have you been hospitalised in the last five years?          No",
            "Do you have any known allergies?                            Yes - penicillin",
            "Is there a family history of heart disease?                 Yes",
            "Do you use tobacco products?                                No",
        };

        foreach (var row in rows)
        {
            c.Text("F1", 11, 56, y, row);
            c.Line(56, y - 9, W - 56, y - 9, 0.5, 0.85);
            y -= 30;
        }

        y -= 16;
        c.Text("F2", 13, 56, y, "4.  Emergency contact");
        c.Line(56, y - 8, W - 56, y - 8);
        y -= 32;

        c.Text("F1", 10.5, 56, y, "Name");
        c.Text("F1", 11.5, 190, y, "Daniel Whitfield (son)");
        y -= 28;
        c.Text("F1", 10.5, 56, y, "Telephone");
        c.Text("F1", 11.5, 190, y, "(802) 555-0177");
        y -= 28;
        c.Text("F1", 10.5, 56, y, "Relationship");
        c.Text("F1", 11.5, 190, y, "Immediate family");

        y -= 56;
        c.Rect(56, y - 60, W - 112, 76, 0.95);
        c.Text("F2", 11, 72, y, "Notes for the clinician");
        c.Text("F1", 10.5, 72, y - 20, "Patient reports occasional dizziness on standing. Blood pressure");
        c.Text("F1", 10.5, 72, y - 36, "readings taken 2 Feb 2026: 148/92, 145/90, 151/94.");

        c.Text("F1", 9, 56, 52, "Page 2 of 3");
        return c.ToString();
    }

    private static string Page3()
    {
        var c = new Canvas();
        c.Text("F2", 17, 56, H - 70, "5.  Privacy notice and consent");
        c.Line(56, H - 80, W - 56, H - 80);

        double y = H - 110;
        string[] paragraph =
        {
            "Northgate Family Clinic collects the information on this form in order to provide",
            "care, to bill your insurance provider, and to contact you about appointments.",
            "",
            "We do not share your records with third parties except where required by law, or",
            "where you have given written permission. You may ask to see the information we",
            "hold about you at any time, and you may ask us to correct anything inaccurate.",
            "",
            "If you are sending this form to another practice, please remove any details you do",
            "not wish to share before you send it.",
        };

        foreach (var line in paragraph)
        {
            if (line.Length > 0) c.Text("F1", 11, 56, y, line);
            y -= 19;
        }

        y -= 26;
        c.Text("F2", 12, 56, y, "Declaration");
        y -= 26;
        c.Text("F1", 11, 56, y, "I confirm the details above are correct to the best of my knowledge.");

        y -= 60;
        c.Text("F1", 10.5, 56, y, "Signed");
        c.Line(120, y - 4, 340, y - 4, 0.9, 0.4);
        c.Text("F1", 10.5, 370, y, "Date");
        c.Line(410, y - 4, W - 56, y - 4, 0.9, 0.4);

        y -= 44;
        c.Text("F1", 10.5, 56, y, "Print name");
        c.Line(130, y - 4, 340, y - 4, 0.9, 0.4);

        c.Text("F1", 9, 56, 52, "Page 3 of 3   -Retain a copy for your records");
        return c.ToString();
    }

    private static byte[] Assemble(string[] contents)
    {
        var objects = new List<string>();
        int n = contents.Length;
        int fontRegular = 3 + n * 2;
        int fontBold = fontRegular + 1;

        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + i * 2} 0 R"));
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {n} >>");

        for (int i = 0; i < n; i++)
        {
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] " +
                $"/Resources << /Font << /F1 {fontRegular} 0 R /F2 {fontBold} 0 R >> >> /Contents {4 + i * 2} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(contents[i])} >>\nstream\n{contents[i]}endstream");
        }

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>");

        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xref = ms.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
