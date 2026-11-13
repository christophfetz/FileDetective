using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace FileDetective.App.Interop;

/// <summary>Opening, showing in Explorer and copying a path (PLAN.md 8.4). UI thread only.</summary>
internal static class ShellActions
{
    /// <summary>Opens the file or folder with its default program. Returns an error text, or null on success.</summary>
    public static string? Open(string fullPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return ex.Message;
        }
    }

    /// <summary>Shows the item selected in an Explorer window. Returns an error text, or null on success.</summary>
    public static string? RevealInExplorer(string fullPath)
    {
        if (NativeMethods.RevealInExplorer(fullPath))
        {
            return null;
        }

        // Fallback: explorer.exe /select.
        try
        {
            using var process = Process.Start(
                new ProcessStartInfo("explorer.exe") { UseShellExecute = false, Arguments = $"/select,\"{fullPath}\"" });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>Puts the text on the clipboard; the clipboard can be locked by another program, so it retries briefly.</summary>
    public static bool CopyText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
        }

        return false;
    }
}
