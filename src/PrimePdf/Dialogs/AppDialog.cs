using System.IO;
using System.Windows;
using System.Windows.Controls;
using PrimePdf.Core;

namespace PrimePdf.Dialogs;

/// <summary>Plain-language confirmations and notices.</summary>
public sealed class AppDialog : DialogShell
{
    private AppDialog(Window? owner, string heading, string body, AppDialogKind kind)
        : base(owner, heading, kind)
    {
        foreach (var para in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            AddParagraph(para);
    }

    public static void Info(Window? owner, string heading, string body, AppDialogKind kind = AppDialogKind.Info)
    {
        if (App.Headless) { Console.Error.WriteLine($"[{kind}] {heading}: {body}"); return; }

        var d = new AppDialog(owner, heading, body, kind);
        d.AddButton("OK", primary: true, () => d.DialogResult = true, isDefault: true);
        d.ShowDialog();
    }

    public static bool Ask(Window? owner, string heading, string body, string confirmText, string cancelText,
        AppDialogKind kind = AppDialogKind.Info)
    {
        if (App.Headless) { Console.Error.WriteLine($"[{kind}] {heading}: {body}"); return false; }

        var d = new AppDialog(owner, heading, body, kind);
        d.AddButton(cancelText, primary: false, () => d.DialogResult = false);
        d.AddButton(confirmText, primary: true, () => d.DialogResult = true, isDefault: true);
        return d.ShowDialog() == true;
    }
}

/// <summary>Asks for the password of a protected PDF.</summary>
public sealed class PasswordWindow : DialogShell
{
    private readonly PasswordBox _box = new()
    {
        FontSize = 17,
        Padding = new Thickness(12, 10, 12, 10),
        MinWidth = 320,
    };

    private PasswordWindow(Window? owner, string fileName)
        : base(owner, "This PDF needs a password", AppDialogKind.Warning)
    {
        AddParagraph($"'{fileName}' is protected. Type the password to open it.");
        BodyPanel.Children.Add(_box);

        AddButton("Cancel", primary: false, () => DialogResult = false);
        AddButton("Open", primary: true, () => DialogResult = true, isDefault: true);

        Loaded += (_, _) => _box.Focus();
    }

    /// <returns>The password, or null if the user gave up.</returns>
    public static string? Prompt(Window? owner, string fileName)
    {
        var w = new PasswordWindow(owner, fileName);
        return w.ShowDialog() == true ? w._box.Password : null;
    }
}

/// <summary>Confirms a successful save and says plainly what happened to the file.</summary>
public sealed class SaveResultWindow : DialogShell
{
    private SaveResultWindow(Window? owner, ExportResult result, bool hadRedactions)
        : base(owner, "Saved", AppDialogKind.Success, width: 560)
    {
        AddParagraph($"Your PDF was saved as “{Path.GetFileName(result.Path)}”.");
        AddParagraph($"{result.PageCount} page(s) · {FormatSize(result.Bytes)}", muted: true);

        if (result.SearchablePages > 0)
        {
            AddCallout(
                $"{result.SearchablePages} scanned page(s) can now be searched. The words were added invisibly "
                + "behind the picture, so the page looks the same but any PDF reader can find and copy the text.",
                "#EFF4FF", "#BBD3F8");
        }

        if (hadRedactions)
        {
            AddCallout(
                "The text you blacked out has been deleted from this copy. It cannot be selected, "
                + "copied or searched for — not even by someone opening the file in another program.",
                "#DFF5EA", "#9BDCC0");
        }

        AddCallout("Your original file has not been changed.", "#F1F4F8", "#DDE3EA");

        AddButton("Show me the file", primary: false, () => Shell.ExplorerReveal.Select(result.Path));
        AddButton("Done", primary: true, () => DialogResult = true, isDefault: true);
    }

    public static void ShowResult(Window? owner, ExportResult result, bool hadRedactions) =>
        new SaveResultWindow(owner, result, hadRedactions).ShowDialog();

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

}
