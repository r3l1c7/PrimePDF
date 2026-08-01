namespace PrimePdf.Core;

/// <summary>
/// A rectangle in "page points", origin at the TOP-LEFT of the page, y growing downward.
/// This is the space every edit mark is stored in, so marks are resolution independent
/// and survive zooming, re-rendering and export at a different DPI.
/// </summary>
public readonly record struct PtRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double CenterX => X + W / 2;
    public double CenterY => Y + H / 2;

    public static PtRect FromCorners(double x0, double y0, double x1, double y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public bool Intersects(PtRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public PtRect Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);

    public PtRect Union(PtRect o)
    {
        double x0 = Math.Min(X, o.X), y0 = Math.Min(Y, o.Y);
        double x1 = Math.Max(Right, o.Right), y1 = Math.Max(Bottom, o.Bottom);
        return new PtRect(x0, y0, x1 - x0, y1 - y0);
    }
}

public readonly record struct PtPoint(double X, double Y);

/// <summary>
/// Maps between a page's own "base" coordinate space (as the source PDF defines it, with
/// any /Rotate the file already carries baked in by the renderer) and the "display" space
/// the user sees after applying extra rotation they added in this app.
///
/// Marks are always stored in BASE space, so rotating a page never has to rewrite them.
/// </summary>
public readonly record struct PageTransform(double BaseWidth, double BaseHeight, int Rotation)
{
    /// <summary>Page size after the extra rotation, in points.</summary>
    public double DisplayWidth => Rotation is 90 or 270 ? BaseHeight : BaseWidth;

    public double DisplayHeight => Rotation is 90 or 270 ? BaseWidth : BaseHeight;

    public PtPoint ToDisplay(double x, double y) => Rotation switch
    {
        90 => new PtPoint(BaseHeight - y, x),
        180 => new PtPoint(BaseWidth - x, BaseHeight - y),
        270 => new PtPoint(y, BaseWidth - x),
        _ => new PtPoint(x, y),
    };

    public PtPoint ToBase(double x, double y) => Rotation switch
    {
        90 => new PtPoint(y, BaseHeight - x),
        180 => new PtPoint(BaseWidth - x, BaseHeight - y),
        270 => new PtPoint(BaseWidth - y, x),
        _ => new PtPoint(x, y),
    };

    public PtRect ToDisplay(PtRect r)
    {
        var a = ToDisplay(r.X, r.Y);
        var b = ToDisplay(r.Right, r.Bottom);
        return PtRect.FromCorners(a.X, a.Y, b.X, b.Y);
    }

    public PtRect ToBase(PtRect r)
    {
        var a = ToBase(r.X, r.Y);
        var b = ToBase(r.Right, r.Bottom);
        return PtRect.FromCorners(a.X, a.Y, b.X, b.Y);
    }

    public static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;
}
