using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PrimePdf;

/// <summary>
/// Declares the process per-monitor DPI aware in code, as well as in app.manifest.
///
/// The manifest alone is not enough: publishing as a single file builds a fresh host and
/// the custom manifest does not survive, so the shipped executable ends up DPI *unaware*.
/// Windows then renders the window at 96 DPI and bitmap-stretches it to fit a scaled
/// display, which makes every pixel of the interface soft — text, icons and page alike —
/// while looking perfect on a 100% monitor where nothing is being stretched.
///
/// A module initializer runs before the application class is touched, which is early
/// enough: the setting can only be applied before the first window exists.
/// </summary>
internal static class DpiAwareness
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        }
        catch
        {
            // Older Windows, or awareness already fixed by a manifest that did survive.
            // Both are fine; this is a belt-and-braces call.
        }
    }

    /// <summary>Human-readable awareness of the running process, for the self-test.</summary>
    public static string Describe()
    {
        try
        {
            var context = GetThreadDpiAwarenessContext();

            if (AreDpiAwarenessContextsEqual(context, new IntPtr(-4))) return "PerMonitorV2";
            if (AreDpiAwarenessContextsEqual(context, new IntPtr(-3))) return "PerMonitor";
            if (AreDpiAwarenessContextsEqual(context, new IntPtr(-2))) return "System";
            if (AreDpiAwarenessContextsEqual(context, new IntPtr(-1))) return "Unaware";
            if (AreDpiAwarenessContextsEqual(context, new IntPtr(-5))) return "UnawareGdiScaled";
            return "Unknown";
        }
        catch
        {
            return "Unavailable";
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
}
