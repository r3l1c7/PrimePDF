using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PrimePdf.Shell;

/// <summary>
/// Registers the app with Windows as something that can open PDFs, and hands off to the
/// system chooser so the user can make it the default.
///
/// Worth being clear about what is and is not possible: since Windows 8 an application
/// cannot make itself the default handler on its own. The association Windows honours
/// lives under a protected key that is validated with an undocumented hash, and writing
/// it directly gets detected and reset. So this class does the half that *is* legitimate
/// — registering the ProgId, icon and capabilities under HKEY_CURRENT_USER so the app
/// shows up properly in "Open with" and Default Apps — and then opens the Windows
/// dialog where the user confirms with one click. No administrator rights needed.
/// </summary>
public static class FileAssociation
{
    public const string ProgId = "PrimePdf.Document";
    private const string AppDisplayName = "Prime PDF";
    private const string CapabilitiesPath = @"Software\PrimePdf\Capabilities";

    public static string ExecutablePath =>
        // Environment.ProcessPath is the one that stays correct in a single-file build,
        // where Assembly.Location comes back empty.
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private static string ExecutableName => Path.GetFileName(ExecutablePath);

    // ============================================================== querying

    /// <summary>True when Windows currently opens .pdf files with this application.</summary>
    public static bool IsDefaultForPdf()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice");
            return string.Equals(key?.GetValue("ProgId") as string, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the app has been registered as a PDF handler at least once.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            return key?.GetValue(null) is string command && command.Contains(ExecutableName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // =========================================================== registering

    /// <summary>
    /// Writes the per-user registration. This alone does not change the default; it makes
    /// the app a legitimate candidate that Windows will offer in its chooser.
    /// </summary>
    public static void RegisterForCurrentUser()
    {
        var exe = ExecutablePath;
        if (string.IsNullOrEmpty(exe)) throw new InvalidOperationException("Could not determine the application path.");

        var command = $"\"{exe}\" \"%1\"";
        var icon = $"\"{exe}\",0";

        // The document type this app understands.
        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, "PDF Document");
            progId.SetValue("FriendlyTypeName", "PDF Document");
            using (var iconKey = progId.CreateSubKey("DefaultIcon")) iconKey.SetValue(null, icon);
            using (var cmd = progId.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, command);
        }

        // Offer this app in the "Open with" list for .pdf.
        using (var openWith = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids"))
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

        // The application entry itself, which is what "Open with" shows by name.
        using (var app = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{ExecutableName}"))
        {
            app.SetValue("FriendlyAppName", AppDisplayName);
            using (var iconKey = app.CreateSubKey("DefaultIcon")) iconKey.SetValue(null, icon);
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, command);
            using (var types = app.CreateSubKey("SupportedTypes")) types.SetValue(".pdf", "");
        }

        // Capabilities, which is what puts the app on the Default Apps page.
        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            caps.SetValue("ApplicationName", AppDisplayName);
            caps.SetValue("ApplicationDescription", "Open, read, redact, fill in and sign PDF documents.");
            using var assoc = caps.CreateSubKey("FileAssociations");
            assoc.SetValue(".pdf", ProgId);
        }

        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue(AppDisplayName, CapabilitiesPath);

        NotifyShell();
    }

    /// <summary>Removes the per-user registration again.</summary>
    public static void UnregisterForCurrentUser()
    {
        void Delete(string path)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch { /* already gone */ }
        }

        Delete($@"Software\Classes\{ProgId}");
        Delete($@"Software\Classes\Applications\{ExecutableName}");
        Delete(@"Software\PrimePdf");

        try
        {
            using var openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);

            using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            registered?.DeleteValue(AppDisplayName, throwOnMissingValue: false);
        }
        catch { /* nothing to undo */ }

        NotifyShell();
    }

    // ================================================== handing off to Windows

    /// <summary>
    /// Opens the Windows UI where the user actually makes the choice. Tries the shell's
    /// "How do you want to open this?" chooser first because it is a single click, and
    /// falls back to the Default Apps settings page.
    /// </summary>
    /// <returns>True if some Windows UI was opened.</returns>
    public static bool PromptWindowsToSetDefault(IntPtr ownerWindow)
    {
        if (TryOpenWithDialog(ownerWindow)) return true;
        return TryOpenSettings();
    }

    private static bool TryOpenWithDialog(IntPtr ownerWindow)
    {
        try
        {
            var info = new OpenAsInfo
            {
                FileName = null,
                FileClass = ".pdf",
                Flags = OpenAsInfoFlags.AllowRegistration
                        | OpenAsInfoFlags.RegisterExtension
                        | OpenAsInfoFlags.ForceOpenWithDialog,
            };
            return SHOpenWithDialog(ownerWindow, ref info) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryOpenSettings()
    {
        // Deep link straight to this app's Default Apps page where it is supported,
        // then progressively fall back to the plain page.
        string[] targets =
        {
            $"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppDisplayName)}",
            "ms-settings:defaultapps",
        };

        foreach (var target in targets)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }
            catch
            {
                // Try the next, less specific, entry point.
            }
        }
        return false;
    }

    // ============================================================== interop

    private static void NotifyShell()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch { /* cosmetic only: icons refresh on next sign-in */ }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    [Flags]
    private enum OpenAsInfoFlags
    {
        AllowRegistration = 0x00000001,
        RegisterExtension = 0x00000002,
        Execute = 0x00000004,
        ForceOpenWithDialog = 0x00000008,
        HideRegistration = 0x00000020,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? FileName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? FileClass;
        public OpenAsInfoFlags Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo info);
}
