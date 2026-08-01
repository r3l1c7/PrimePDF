// SkiaSharp 4 marks the mutating SKPath methods obsolete in favour of SKPathBuilder.
// The paths built here are short-lived and thrown away after one draw, so the older
// direct API stays clearer than routing every stroke through a builder.
#pragma warning disable CS0618

using SkiaSharp;

namespace PrimePdf.Core;

/// <summary>
/// Draws edit marks onto a rendered page.
///
/// The on-screen preview and the exported file both go through this one method, so what
/// the user sees while editing is exactly what lands in the saved PDF.
/// </summary>
public static class MarkPainter
{
    private static readonly Dictionary<(string, bool, bool), SKTypeface> TypefaceCache = new();

    public static SKTypeface GetTypeface(string family, bool bold, bool italic)
    {
        var key = (family, bold, italic);
        lock (TypefaceCache)
        {
            if (TypefaceCache.TryGetValue(key, out var cached)) return cached;
            var style = new SKFontStyle(
                bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            var tf = SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
            TypefaceCache[key] = tf;
            return tf;
        }
    }

    /// <param name="scale">Pixels per point of the target surface.</param>
    /// <param name="showGuides">Draw the dashed outlines that only make sense while editing.</param>
    public static void Paint(SKCanvas canvas, PageEntry page, double scale, bool showGuides)
    {
        var t = page.Transform;
        foreach (var mark in page.Marks)
            PaintMark(canvas, mark, t, scale, showGuides);
    }

    public static void PaintMark(SKCanvas canvas, Mark mark, PageTransform t, double scale, bool showGuides)
    {
        switch (mark)
        {
            case RedactMark r: PaintRedact(canvas, r, t, scale); break;
            case TextMark tm: PaintText(canvas, tm, t, scale, showGuides); break;
            case InkMark ink: PaintInk(canvas, ink, t, scale); break;
            case ImageMark im: PaintImage(canvas, im, t, scale); break;
            case StampMark st: PaintStamp(canvas, st, t, scale); break;
        }
    }

    private static SKRect ToPixels(PtRect rect, PageTransform t, double scale)
    {
        var d = t.ToDisplay(rect);
        return new SKRect(
            (float)(d.X * scale), (float)(d.Y * scale),
            (float)(d.Right * scale), (float)(d.Bottom * scale));
    }

    private static void PaintRedact(SKCanvas canvas, RedactMark mark, PageTransform t, double scale)
    {
        using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false };
        canvas.DrawRect(ToPixels(mark.Rect, t, scale), paint);
    }

    private static void PaintImage(SKCanvas canvas, ImageMark mark, PageTransform t, double scale)
    {
        if (mark.Png.Length == 0) return;
        using var image = SKImage.FromEncodedData(mark.Png);
        if (image is null) return;

        var dest = ToPixels(mark.Rect, t, scale);
        canvas.Save();
        // Rotate the stamp with the page so a signature stays upright when the page turns.
        ApplyRotation(canvas, t, dest);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Restore();
    }

    /// <summary>
    /// Marks are authored in base space; when the page is displayed rotated, content that
    /// has an "up" direction (text, stamps, images) must turn with it. Rotates about the
    /// centre of the already-transformed destination rectangle.
    /// </summary>
    private static void ApplyRotation(SKCanvas canvas, PageTransform t, SKRect dest)
    {
        if (t.Rotation == 0) return;
        canvas.RotateDegrees(t.Rotation, dest.MidX, dest.MidY);
    }

    /// <summary>The destination box as it was authored, i.e. before the page rotation swaps w/h.</summary>
    private static SKRect UnrotatedBox(SKRect dest, PageTransform t)
    {
        if (t.Rotation is 90 or 270)
            return new SKRect(
                dest.MidX - dest.Height / 2, dest.MidY - dest.Width / 2,
                dest.MidX + dest.Height / 2, dest.MidY + dest.Width / 2);
        return dest;
    }

    private static void PaintStamp(SKCanvas canvas, StampMark mark, PageTransform t, double scale)
    {
        var dest = ToPixels(mark.Rect, t, scale);
        canvas.Save();
        ApplyRotation(canvas, t, dest);
        var box = UnrotatedBox(dest, t);

        var color = new SKColor(mark.Color);
        float w = box.Width, h = box.Height;

        switch (mark.Kind)
        {
            case StampKind.Dot:
            {
                using var fill = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
                canvas.DrawCircle(box.MidX, box.MidY, Math.Min(w, h) * 0.32f, fill);
                break;
            }
            case StampKind.Cross:
            {
                using var pen = new SKPaint
                {
                    Color = color,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(1f, Math.Min(w, h) * 0.16f),
                    StrokeCap = SKStrokeCap.Round,
                    IsAntialias = true,
                };
                float m = Math.Min(w, h) * 0.22f;
                canvas.DrawLine(box.Left + m, box.Top + m, box.Right - m, box.Bottom - m, pen);
                canvas.DrawLine(box.Right - m, box.Top + m, box.Left + m, box.Bottom - m, pen);
                break;
            }
            default:
            {
                using var pen = new SKPaint
                {
                    Color = color,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(1f, Math.Min(w, h) * 0.16f),
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                    IsAntialias = true,
                };
                using var path = new SKPath();
                path.MoveTo(box.Left + w * 0.16f, box.Top + h * 0.52f);
                path.LineTo(box.Left + w * 0.40f, box.Top + h * 0.78f);
                path.LineTo(box.Left + w * 0.86f, box.Top + h * 0.20f);
                canvas.DrawPath(path, pen);
                break;
            }
        }
        canvas.Restore();
    }

    private static void PaintInk(SKCanvas canvas, InkMark mark, PageTransform t, double scale)
    {
        if (mark.Points.Count == 0) return;

        using var path = new SKPath();
        for (int i = 0; i < mark.Points.Count; i++)
        {
            var d = t.ToDisplay(mark.Points[i].X, mark.Points[i].Y);
            var pt = new SKPoint((float)(d.X * scale), (float)(d.Y * scale));
            if (i == 0) path.MoveTo(pt);
            else path.LineTo(pt);
        }

        // A single tap should still leave a dot rather than nothing.
        if (mark.Points.Count == 1)
        {
            var d = t.ToDisplay(mark.Points[0].X, mark.Points[0].Y);
            using var dot = new SKPaint { Color = new SKColor(mark.Color), Style = SKPaintStyle.Fill, IsAntialias = true };
            canvas.DrawCircle((float)(d.X * scale), (float)(d.Y * scale), (float)(mark.Width * scale / 2), dot);
            return;
        }

        using var paint = new SKPaint
        {
            Color = new SKColor(mark.Color),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)(mark.Width * scale),
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
            BlendMode = mark.Style == InkStyle.Highlighter ? SKBlendMode.Multiply : SKBlendMode.SrcOver,
        };
        canvas.DrawPath(path, paint);
    }

    private static void PaintText(SKCanvas canvas, TextMark mark, PageTransform t, double scale, bool showGuides)
    {
        var dest = ToPixels(mark.Rect, t, scale);
        canvas.Save();
        ApplyRotation(canvas, t, dest);
        var box = UnrotatedBox(dest, t);

        if (mark.CoverBehind)
        {
            using var cover = new SKPaint { Color = new SKColor(mark.CoverColor), Style = SKPaintStyle.Fill };
            canvas.DrawRect(box, cover);
        }

        if (!string.IsNullOrEmpty(mark.Text))
        {
            var tf = GetTypeface(mark.FontFamily, mark.Bold, mark.Italic);
            using var font = new SKFont(tf, (float)(mark.FontSize * scale)) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
            using var paint = new SKPaint { Color = new SKColor(mark.Color), IsAntialias = true };

            var lines = WrapLines(mark.Text, font, paint, box.Width);
            var metrics = font.Metrics;
            float lineHeight = font.Size * 1.25f;
            float y = box.Top - metrics.Ascent;

            foreach (var line in lines)
            {
                float x = mark.Align switch
                {
                    TextAlign.Center => box.MidX,
                    TextAlign.Right => box.Right,
                    _ => box.Left,
                };
                var align = mark.Align switch
                {
                    TextAlign.Center => SKTextAlign.Center,
                    TextAlign.Right => SKTextAlign.Right,
                    _ => SKTextAlign.Left,
                };
                canvas.DrawText(line, x, y, align, font, paint);
                y += lineHeight;
            }
        }

        if (showGuides && !mark.CoverBehind)
        {
            using var guide = new SKPaint
            {
                Color = new SKColor(0x552563EB),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0),
            };
            canvas.DrawRect(box, guide);
        }

        canvas.Restore();
    }

    /// <summary>Honours explicit newlines and soft-wraps anything wider than the box.</summary>
    private static List<string> WrapLines(string text, SKFont font, SKPaint paint, float maxWidth)
    {
        var result = new List<string>();
        foreach (var hard in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (maxWidth <= 1 || font.MeasureText(hard, paint) <= maxWidth)
            {
                result.Add(hard);
                continue;
            }

            var current = "";
            foreach (var word in hard.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (font.MeasureText(candidate, paint) <= maxWidth || current.Length == 0)
                {
                    current = candidate;
                }
                else
                {
                    result.Add(current);
                    current = word;
                }
            }
            result.Add(current);
        }
        return result;
    }
}
