using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileDetective.App.Interop;

/// <summary>
/// File type icons for the hit list. Icons come from the file name alone (<c>SHGFI_USEFILEATTRIBUTES</c>), so no file
/// is ever opened or even looked at, also not on network shares (PLAN.md 7.4). One icon per extension is cached.
/// Use on the UI thread only.
/// </summary>
internal static class FileIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon for a folder or for the extension of <paramref name="name"/>; null if the shell gives none.</summary>
    public static ImageSource? For(string name, bool isDirectory)
    {
        string key = isDirectory ? "<dir>" : Path.GetExtension(name);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // Only the extension goes to the shell, never the real name or path.
        string probe = isDirectory ? "folder" : "file" + key;
        var icon = Load(probe, isDirectory);
        Cache[key] = icon;
        return icon;
    }

    private static ImageSource? Load(string probeName, bool isDirectory)
    {
        // Decision: always the 32 px icon, drawn at 20 DIP. That is sharp at 150 % and 200 % scaling and still fine
        // at 100 %, without having to pick a size per monitor.
        var handle = NativeMethods.GetTypeIcon(probeName, isDirectory, large: true);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }
}
