using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace PrimePdf.Shell;

/// <summary>
/// Opens Explorer with a saved file selected.
///
/// Done through the shell API rather than by building an "explorer.exe /select,..."
/// command line. Splicing a path into a process argument string is the shape of an
/// argument-injection bug even when a particular input cannot trigger it, and this
/// avoids the question entirely — the path is passed as a parsed shell item, never as
/// text a command-line parser has to split.
/// </summary>
public static class ExplorerReveal
{
    public static void Select(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
            if (!File.Exists(full)) return;
        }
        catch
        {
            return;
        }

        if (TrySelectViaShell(full)) return;
        TryOpenContainingFolder(full);
    }

    private static bool TrySelectViaShell(string fullPath)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(fullPath, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return false;

            return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    private static void TryOpenContainingFolder(string fullPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            // A directory path handed to the shell as the item to open — still no
            // command line of our own construction.
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch
        {
            // Being unable to open a window is not worth interrupting the user over.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHParseDisplayName(
        string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);
}
