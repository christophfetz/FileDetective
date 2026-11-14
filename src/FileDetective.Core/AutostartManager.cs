using Microsoft.Win32;

namespace FileDetective.Core;

/// <summary>
/// Start with Windows through <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (PLAN.md 8.1). The registry is
/// the only truth: nothing about the autostart is stored in <c>settings.json</c>. The value is
/// <c>"&lt;exe&gt;" --autostart</c>. No admin rights are needed (current user only).
/// </summary>
public sealed class AutostartManager
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "FileDetective";
    public const string Argument = "--autostart";

    private readonly string _keyPath;
    private readonly string _valueName;

    /// <param name="keyPath">Key below HKCU; tests use their own key, never the real <c>Run</c> key.</param>
    /// <param name="valueName">Name of the value.</param>
    public AutostartManager(string keyPath = DefaultKeyPath, string valueName = DefaultValueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        _keyPath = keyPath;
        _valueName = valueName;
    }

    /// <summary>The command line written to the registry: the quoted exe path and <c>--autostart</c>.</summary>
    public static string BuildCommand(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        return $"\"{exePath}\" {Argument}";
    }

    /// <summary>The registered command line, or null if there is none.</summary>
    public string? GetCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        return key?.GetValue(_valueName) as string;
    }

    /// <summary>True if the value exists and is not empty.</summary>
    public bool IsEnabled() => !string.IsNullOrWhiteSpace(GetCommand());

    /// <summary>Registers <paramref name="exePath"/> for the start with Windows.</summary>
    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        key.SetValue(_valueName, BuildCommand(exePath), RegistryValueKind.String);
    }

    /// <summary>Removes the registration; fine if there is none.</summary>
    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
