using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Support;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.App.UI;

public sealed partial class MainForm
{
    /// <summary>
    /// The Contact IT button: pictures the window as the employee sees it now (before the dialog covers it), then lets them
    /// choose the IT Department member to send it and the logs to on WhatsApp.
    /// </summary>
    internal void ContactIt()
    {
        var contacts = Runtime.Environment.Config.ItContacts;
        if (contacts.Count == 0)
            return;

        using var screenshot = CaptureWindow();
        Runtime.Log.Info("ui.contact_it.opened", "The employee opened Contact IT.");
        using var dialog = new ContactItDialog(T, contacts, (owner, contact) => SendToIt(owner, contact, screenshot), ShowSupportFiles);
        dialog.ShowDialog(this);
    }

    /// <summary>The window's content (header, steps, current view and footer) as it is on screen.</summary>
    internal Bitmap CaptureWindow()
    {
        var bitmap = new Bitmap(Math.Max(1, _root.Width), Math.Max(1, _root.Height));
        _root.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    /// <summary>
    /// Writes the picture and this session's logs, puts both files on the clipboard (Ctrl+V in the chat attaches them) and opens
    /// the WhatsApp chat with <paramref name="contact"/> with the request already typed. Null when the files could not be written.
    /// </summary>
    private SupportPackage? SendToIt(IWin32Window owner, ItContact contact, Image screenshot)
    {
        Runtime.Log.Info("ui.contact_it.requested", "The employee asked IT for help on WhatsApp.", new Dictionary<string, object?>
        {
            ["contact"] = contact.Name,
            ["mode"] = _mode.ToString(),
            ["outcome"] = _lastResult?.Outcome.ToString(),
        });

        string message = SupportMessage.Compose(Runtime, _mode, _lastResult, DateTimeOffset.Now);
        SupportPackage package;
        try
        {
            package = WriteSupportPackage(screenshot, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            Runtime.Log.Warn("ui.contact_it.prepare_failed", "The picture and logs for IT could not be written.", exception: ex);
            ShowMessage(T.Format("contact.prepareFailed", ex.Message), MessageBoxIcon.Error, owner);
            return null;
        }

        try
        {
            Clipboard.SetFileDropList(new StringCollection { package.ScreenshotPath, package.ArchivePath });
        }
        catch (ExternalException ex)
        {
            // Another program holds the clipboard; the dialog also says how to attach the files from their folder.
            Runtime.Log.Warn("ui.contact_it.clipboard_failed", "The files for IT could not be put on the clipboard.", exception: ex);
        }

        try
        {
            Process.Start(new ProcessStartInfo(SupportMessage.WhatsAppLink(contact, message)) { UseShellExecute = true })?.Dispose();
            Runtime.Log.Info("ui.contact_it.chat_opened", "The WhatsApp chat with IT was opened.", new Dictionary<string, object?> { ["contact"] = contact.Name });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Runtime.Log.Warn("ui.contact_it.chat_failed", "The WhatsApp chat with IT could not be opened.", new Dictionary<string, object?> { ["contact"] = contact.Name }, ex);
            ShowMessage(T.Format("contact.openFailed", ContactItDialog.NameOf(contact, T), "+" + contact.WhatsApp), MessageBoxIcon.Warning, owner);
        }

        return package;
    }

    private SupportPackage WriteSupportPackage(Image screenshot, string message)
    {
        string? directory = null;
        foreach (var (baseDirectory, candidate) in AppPaths.SupportLocations)
        {
            if (AppPaths.TryPrepareDirectory(baseDirectory, candidate))
            {
                directory = candidate;
                break;
            }
        }
        if (directory is null)
            throw new IOException("No folder for the files could be created.");

        var logs = SupportPackage.SessionLogs(Runtime.Log.SessionId, Runtime.Log.FilePath, AppPaths.SessionLogDirectories);
        string? report = _lastResult is { } result ? FormatReport(result) : null;

        var package = SupportPackage.Write(directory, DateTime.Now, screenshot, logs, report, message);
        Runtime.Log.Info("ui.contact_it.prepared", "The picture and logs for IT were written.", new Dictionary<string, object?>
        {
            ["screenshot"] = package.ScreenshotPath,
            ["archive"] = package.ArchivePath,
            ["logs"] = logs,
            ["report"] = report is not null,
        });
        return package;
    }

    /// <summary>Opens the files' folder with the picture selected, for dragging them into the chat.</summary>
    private void ShowSupportFiles(SupportPackage package)
    {
        try
        {
            // Full path: a bare "explorer.exe" would also be looked for in the current folder first.
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, "/select,\"" + package.ScreenshotPath + "\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Runtime.Log.Warn("ui.contact_it.show_files_failed", "The folder of the files for IT could not be opened.", new Dictionary<string, object?> { ["path"] = package.ScreenshotPath }, ex);
        }
    }
}
