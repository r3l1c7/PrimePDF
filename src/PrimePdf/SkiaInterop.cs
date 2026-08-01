using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace PrimePdf;

/// <summary>Bridges SkiaSharp bitmaps (what the PDF engine produces) into WPF images.</summary>
public static class SkiaInterop
{
    /// <summary>
    /// Copies an SKBitmap into a frozen WPF bitmap. Freezing matters: it lets the image be
    /// produced on a background thread and handed to the UI without further marshalling.
    /// </summary>
    public static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        var source = bitmap.ColorType == SKColorType.Bgra8888
            ? bitmap
            : ConvertToBgra(bitmap);

        try
        {
            var pixels = source.Bytes;
            var bmp = BitmapSource.Create(
                source.Width,
                source.Height,
                96, 96,
                PixelFormats.Pbgra32,
                null,
                pixels,
                source.RowBytes);
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            if (!ReferenceEquals(source, bitmap)) source.Dispose();
        }
    }

    private static SKBitmap ConvertToBgra(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), null);
        return dst;
    }

    public static Color ToWpf(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static uint ToArgb(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}
