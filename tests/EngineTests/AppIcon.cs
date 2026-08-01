// Short-lived paths built and thrown away per icon size; the direct SKPath API stays
// clearer here than routing through SKPathBuilder.
#pragma warning disable CS0618

using SkiaSharp;

namespace EngineTests;

/// <summary>
/// Draws the application icon and packs it into a multi-resolution .ico.
///
/// This matters more than usual here: once the app is the default PDF handler, this
/// glyph appears on every PDF file on the machine, so it has to stay legible at 16px.
/// The mark is a document with a redaction bar across it — what the app is for.
/// </summary>
public static class AppIcon
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    public static byte[] BuildIco()
    {
        var images = Sizes.Select(RenderPng).ToArray();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // ICONDIR
        w.Write((ushort)0);           // reserved
        w.Write((ushort)1);           // type: icon
        w.Write((ushort)images.Length);

        int offset = 6 + 16 * images.Length;
        for (int i = 0; i < images.Length; i++)
        {
            int size = Sizes[i];
            w.Write((byte)(size >= 256 ? 0 : size));   // 0 means 256
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);         // palette size
            w.Write((byte)0);         // reserved
            w.Write((ushort)1);       // colour planes
            w.Write((ushort)32);      // bits per pixel
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }

        foreach (var png in images) w.Write(png);

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] RenderPng(int size)
    {
        using var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            Draw(canvas, size);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Draw(SKCanvas canvas, float s)
    {
        float unit = s / 100f;
        float inset = 8 * unit;

        // Rounded blue tile.
        var tile = new SKRect(inset * 0.6f, inset * 0.6f, s - inset * 0.6f, s - inset * 0.6f);
        using (var bg = new SKPaint { Color = new SKColor(0xFF, 0x1F, 0x6F, 0xEB), IsAntialias = true })
        {
            bg.Color = new SKColor(0x1F, 0x6F, 0xEB);
            canvas.DrawRoundRect(tile, 22 * unit, 22 * unit, bg);
        }

        // White page with a folded top-right corner.
        float pw = 44 * unit, ph = 56 * unit;
        float px = (s - pw) / 2f, py = (s - ph) / 2f;
        float fold = 13 * unit;

        using var page = new SKPath();
        page.MoveTo(px, py);
        page.LineTo(px + pw - fold, py);
        page.LineTo(px + pw, py + fold);
        page.LineTo(px + pw, py + ph);
        page.LineTo(px, py + ph);
        page.Close();

        using (var white = new SKPaint { Color = SKColors.White, IsAntialias = true })
            canvas.DrawPath(page, white);

        using (var foldPaint = new SKPaint { Color = new SKColor(0xC9, 0xDB, 0xF8), IsAntialias = true })
        using (var foldPath = new SKPath())
        {
            foldPath.MoveTo(px + pw - fold, py);
            foldPath.LineTo(px + pw, py + fold);
            foldPath.LineTo(px + pw - fold, py + fold);
            foldPath.Close();
            canvas.DrawPath(foldPath, foldPaint);
        }

        // Two faint text rules and one solid black redaction bar. Below about 24px the
        // rules turn to mush, so only the bar is drawn — it still reads as "PDF, edited".
        bool detailed = s >= 24;
        float barH = Math.Max(1.5f, 7 * unit);
        float leftPad = 7 * unit;

        if (detailed)
        {
            using var rule = new SKPaint { Color = new SKColor(0xB6, 0xC2, 0xD2), IsAntialias = true };
            canvas.DrawRect(px + leftPad, py + 15 * unit, pw - leftPad * 2, Math.Max(1f, 4 * unit), rule);
            canvas.DrawRect(px + leftPad, py + 25 * unit, (pw - leftPad * 2) * 0.72f, Math.Max(1f, 4 * unit), rule);
        }

        using var bar = new SKPaint { Color = new SKColor(0x11, 0x18, 0x27), IsAntialias = true };
        float barY = detailed ? py + 36 * unit : py + ph / 2 - barH / 2;
        canvas.DrawRect(px + leftPad, barY, pw - leftPad * 2, barH, bar);
    }
}
