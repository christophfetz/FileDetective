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

    /// <summary>
    /// Puts a local image on the clipboard (stage 11): as a picture, so it can be pasted into a mail or a ticket, and as a
    /// file for programs that take files. Returns an error text, or null on success. Only for local images of the image text.
    /// </summary>
    public static string? CopyImage(string fullPath)
    {
        System.Windows.Media.Imaging.BitmapSource image;
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or UriFormatException
            or InvalidOperationException or ArgumentException or COMException)
        {
            return ex.Message;
        }

        var data = new System.Windows.DataObject();
        data.SetImage(image);
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { fullPath });
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(data, copy: true);
                return null;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
        }

        return Strings.Get("Footer_ClipboardBusy");
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
