using System.IO;
using System.Globalization;

namespace FileDetective.App;

/// <summary>
/// Minimal text log in the data folder (stop-gap until <c>FileLog</c> with size limit and scan statistics arrives in
/// stage 7). Never throws. The file is started anew when it exceeds 1 MiB at program start.
/// </summary>
internal static class DiagnosticLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var info = new FileInfo(AppPaths.LogPath);
            if (info.Exists && info.Length > MaxBytes)
            {
                info.Delete();
            }
        }
        catch (Exception)
        {
            // Logging must never break the program.
        }
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? exception = null)
        => Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            string line = string.Create(
                CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            lock (Gate)
            {
                File.AppendAllText(AppPaths.LogPath, line);
            }
        }
        catch (Exception)
        {
            // Logging must never break the program.
        }
    }
}
