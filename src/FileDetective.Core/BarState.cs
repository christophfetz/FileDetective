namespace FileDetective.Core;

/// <summary>What the footer of the search bar reports besides hits (PLAN.md 8.4).</summary>
public enum IndexPhase
{
    /// <summary>The directory is being loaded into memory ("Verzeichnis wird geladen").</summary>
    Loading,

    /// <summary>The directory is loaded, but the very first scan is still running ("Erster Durchlauf läuft: N Einträge").</summary>
    FirstScan,

    /// <summary>Normal operation.</summary>
    Ready,
}

/// <summary>Small decisions of the search bar that need no WPF.</summary>
public static class BarState
{
    /// <summary>
    /// Decides the phase. A scan counts as "first scan" while it is pending or running and no location has ever been
    /// scanned completely.
    /// </summary>
    public static IndexPhase Resolve(bool loaded, bool scanPendingOrRunning, bool anyScanEverCompleted)
    {
        if (!loaded)
        {
            return IndexPhase.Loading;
        }

        return scanPendingOrRunning && !anyScanEverCompleted ? IndexPhase.FirstScan : IndexPhase.Ready;
    }

    /// <summary>Next mode for Tab (<paramref name="reverse"/> false) or Shift+Tab: All, Files, Folders, then around.</summary>
    public static SearchMode CycleMode(SearchMode mode, bool reverse)
    {
        int count = Enum.GetValues<SearchMode>().Length;
        int next = ((int)mode + (reverse ? count - 1 : 1)) % count;
        return (SearchMode)next;
    }
}
