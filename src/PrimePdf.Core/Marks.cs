namespace PrimePdf.Core;

/// <summary>
/// One user edit on one page. Coordinates are page points in BASE space
/// (top-left origin) — see <see cref="PageTransform"/>.
/// </summary>
public abstract class Mark
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Area the mark occupies, used for hit-testing and for the eraser.</summary>
    public abstract PtRect Bounds { get; }

    /// <summary>
    /// True when this mark's whole purpose is to make content underneath unreadable.
    /// Any page carrying one of these must be flattened to pixels on export, otherwise
    /// the original text would still sit in the file underneath the covering box.
    /// </summary>
    public virtual bool RequiresFlatten => false;

    public abstract Mark Clone();
}

/// <summary>Permanently removes what is underneath. Drawn as a solid black bar.</summary>
public sealed class RedactMark : Mark
{
    public PtRect Rect { get; set; }

    public override PtRect Bounds => Rect;
    public override bool RequiresFlatten => true;

    public override Mark Clone() => new RedactMark { Rect = Rect };
}

public enum TextAlign { Left, Center, Right }

/// <summary>
/// Text placed on the page. When <see cref="CoverBehind"/> is set it first paints an
/// opaque box, which is how "change the wording" works: hide the old text, draw the new.
/// </summary>
public sealed class TextMark : Mark
{
    public PtRect Rect { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 12;
    public string FontFamily { get; set; } = "Segoe UI";
    public uint Color { get; set; } = 0xFF000000;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public TextAlign Align { get; set; } = TextAlign.Left;

    /// <summary>Paint an opaque rectangle behind the text before drawing it.</summary>
    public bool CoverBehind { get; set; }

    public uint CoverColor { get; set; } = 0xFFFFFFFF;

    public override PtRect Bounds => Rect;

    /// <summary>
    /// Covering old wording only hides it visually — the original glyphs are still in the
    /// content stream, so the page has to be rasterised for the change to be real.
    /// </summary>
    public override bool RequiresFlatten => CoverBehind;

    public override Mark Clone() => new TextMark
    {
        Rect = Rect,
        Text = Text,
        FontSize = FontSize,
        FontFamily = FontFamily,
        Color = Color,
        Bold = Bold,
        Italic = Italic,
        Align = Align,
        CoverBehind = CoverBehind,
        CoverColor = CoverColor,
    };
}

public enum InkStyle { Pen, Highlighter }

/// <summary>A freehand stroke: pen, highlighter, or the lines of a hand-drawn signature.</summary>
public sealed class InkMark : Mark
{
    public List<PtPoint> Points { get; set; } = new();
    public uint Color { get; set; } = 0xFF1D4ED8;
    public double Width { get; set; } = 2;
    public InkStyle Style { get; set; } = InkStyle.Pen;

    public override PtRect Bounds
    {
        get
        {
            if (Points.Count == 0) return default;
            double minX = Points[0].X, maxX = minX, minY = Points[0].Y, maxY = minY;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new PtRect(minX - Width, minY - Width, maxX - minX + 2 * Width, maxY - minY + 2 * Width);
        }
    }

    public override Mark Clone() => new InkMark
    {
        Points = new List<PtPoint>(Points),
        Color = Color,
        Width = Width,
        Style = Style,
    };
}

/// <summary>A PNG stamped onto the page — a scanned or drawn signature, or initials.</summary>
public sealed class ImageMark : Mark
{
    public byte[] Png { get; set; } = Array.Empty<byte>();
    public PtRect Rect { get; set; }

    public override PtRect Bounds => Rect;

    public override Mark Clone() => new ImageMark { Png = Png, Rect = Rect };
}

public enum StampKind { Check, Cross, Dot }

/// <summary>The tick / cross / filled dot used to complete checkbox-style forms.</summary>
public sealed class StampMark : Mark
{
    public PtRect Rect { get; set; }
    public StampKind Kind { get; set; } = StampKind.Check;
    public uint Color { get; set; } = 0xFF111827;

    public override PtRect Bounds => Rect;

    public override Mark Clone() => new StampMark { Rect = Rect, Kind = Kind, Color = Color };
}
