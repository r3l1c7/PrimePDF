using System.Windows;
using System.Windows.Interop;
using PrimePdf.Shell;

namespace PrimePdf.Dialogs;

/// <summary>
/// The first-run offer to become the computer's PDF reader, and the follow-up that
/// explains what Windows is about to ask.
/// </summary>
public sealed class DefaultAppWindow : DialogShell
{
    private DefaultAppWindow(Window? owner) : base(owner, "Open PDF files with Prime PDF?", AppDialogKind.Info, width: 560)
    {
        AddParagraph(
            "At the moment, PDF files on this computer open in a different program. "
            + "Would you like them to open here instead?");

        AddCallout(
            "Windows will ask you to confirm. When the list appears, choose Prime PDF.",
            "#E7F0FE", "#BBD3F8");

        AddParagraph("You can change this back at any time in Windows settings.", muted: true);

        AddButton("No thanks", primary: false, () => DialogResult = false);
        AddButton("Yes, please", primary: true, () => DialogResult = true, isDefault: true);
    }

    /// <summary>Builds the dialog without showing it, for layout review.</summary>
    internal static Window CreateForPreview() => new DefaultAppWindow(null);

    /// <summary>
    /// Asks, and if the user agrees, registers the app and opens the Windows chooser.
    /// </summary>
    /// <returns>True if the user said yes.</returns>
    public static bool Offer(Window owner)
    {
        var dialog = new DefaultAppWindow(owner);
        if (dialog.ShowDialog() != true) return false;

        MakeDefault(owner);
        return true;
    }

    /// <summary>Registers this app and hands the final choice to Windows.</summary>
    public static void MakeDefault(Window owner)
    {
        try
        {
            FileAssociation.RegisterForCurrentUser();
        }
        catch (Exception ex)
        {
            AppDialog.Info(owner, "Could not set this up",
                "Prime PDF could not register itself with Windows.\n\nDetails: " + ex.Message,
                AppDialogKind.Error);
            return;
        }

        var handle = new WindowInteropHelper(owner).Handle;
        bool opened = FileAssociation.PromptWindowsToSetDefault(handle);

        if (!opened)
        {
            AppDialog.Info(owner, "Nearly there",
                "Prime PDF is now in the list of programs that can open PDF files.\n\n"
                + "To finish, right-click any PDF, choose \"Open with\", then \"Choose another app\", "
                + "pick Prime PDF and tick \"Always use this app\".",
                AppDialogKind.Info);
            return;
        }

        AppDialog.Info(owner, "Choose Prime PDF in the Windows window",
            "Windows has opened its own window so you can confirm the change.\n\n"
            + "Find Prime PDF in the list, select it, then close that window. "
            + "PDF files will open here from then on.",
            AppDialogKind.Info);
    }
}
