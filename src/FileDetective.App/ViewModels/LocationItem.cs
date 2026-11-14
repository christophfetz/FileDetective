using System.Globalization;
using System.Windows;
using FileDetective.Core;

namespace FileDetective.App.ViewModels;

/// <summary>One line of the location list in the settings: a location with state, last update and entry count.</summary>
internal sealed class LocationItem
{
    public LocationItem(RootInfo root, QueuedScanState scanState, int? entryCount, bool unreachable)
    {
        Id = root.Id;
        Root = root;
        Path = root.Path;
        UncPath = root.UncPath ?? string.Empty;
        KindText = LocationUi.KindText(root.Kind);
        StatusText = scanState == QueuedScanState.Idle && unreachable
            ? Strings.Get("Status_NotConnected")
            : StatusOf(root, scanState);
        UpdatedText = UpdatedOf(root);
        EntriesText = entryCount is { } n ? n.ToString("N0", CultureInfo.CurrentCulture) : "…";
    }

    public byte Id { get; }

    public RootInfo Root { get; }

    public string Path { get; }

    public string UncPath { get; }

    public Visibility UncVisibility => UncPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string KindText { get; }

    public string StatusText { get; }

    public string UpdatedText { get; }

    public string EntriesText { get; }

    private static string StatusOf(RootInfo root, QueuedScanState scanState)
    {
        if (scanState == QueuedScanState.Running)
        {
            return Strings.Get("Status_Running");
        }

        if (scanState == QueuedScanState.Queued)
        {
            return Strings.Get("Status_Queued");
        }

        return root.LastScanStatus switch
        {
            ScanStatus.Complete => Strings.Get("Status_Complete"),
            ScanStatus.CompleteWithErrors => LocationUi.Format("Status_CompleteErrors", root.LastScanFailedDirs),
            ScanStatus.Aborted => Strings.Get("Status_Aborted"),
            ScanStatus.Running => Strings.Get("Status_Aborted"), // "running" in the database but no scan: the program was closed
            _ => Strings.Get("Status_Never"),
        };
    }

    private static string UpdatedOf(RootInfo root)
    {
        if (root.LastScanCompletedUtc is not { } unix)
        {
            return "–";
        }

        var local = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        return local.Date == DateTime.Today
            ? LocationUi.Format("Updated_Today", local)
            : local.ToString("g", CultureInfo.CurrentCulture);
    }
}
