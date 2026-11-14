using System.IO;
namespace FileDetective.App;

/// <summary>Where the program keeps its own data (PLAN.md 2.2: only below %LOCALAPPDATA%\FileDetective).</summary>
internal static class AppPaths
{
    /// <summary>
    /// Decision: the environment variable <c>FILEDETECTIVE_DATA_DIR</c> replaces the data folder, so that tests and
    /// trials never touch the real database.
    /// </summary>
    public const string DataDirVariable = "FILEDETECTIVE_DATA_DIR";

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string DatabasePath => Path.Combine(DataDirectory, "index.db");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string LogPath => Path.Combine(DataDirectory, "filedetective.log");

    private static string ResolveDataDirectory()
    {
        string? custom = Environment.GetEnvironmentVariable(DataDirVariable);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return Path.GetFullPath(custom);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileDetective");
    }
}
