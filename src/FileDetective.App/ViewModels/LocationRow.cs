using CommunityToolkit.Mvvm.ComponentModel;
using FileDetective.Core;

namespace FileDetective.App.ViewModels;

/// <summary>One line of the set-up list: a place with a check box.</summary>
internal sealed partial class LocationRow : ObservableObject
{
    public LocationRow(LocationCandidate candidate, string title, string detail, RootInfo? existing = null)
    {
        Candidate = candidate;
        Title = title;
        Detail = detail;
        Existing = existing;
    }

    public LocationCandidate Candidate { get; }

    public string Title { get; }

    public string Detail { get; }

    /// <summary>The location that already exists in the database (shown checked and fixed), or null.</summary>
    public RootInfo? Existing { get; }

    public bool IsExisting => Existing is not null;

    public bool CanToggle => Existing is null;

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Another checked location contains this one (or is the same place): it is not added.</summary>
    [ObservableProperty]
    private bool _isShadowed;

    [ObservableProperty]
    private string _note = string.Empty;

    /// <summary>
    /// Works out which rows are redundant (PLAN.md 8.5: of nested locations only the outer one stays) and writes the
    /// notes. Existing locations come first in the list, so they win over a new row that names the same place.
    /// </summary>
    public static void Recompute(IReadOnlyList<LocationRow> rows)
    {
        var active = rows.Where(r => r.IsExisting || r.IsChecked).ToList();
        var candidates = active.Select(r => r.Candidate).ToList();
        foreach (var row in rows)
        {
            row.IsShadowed = false;
            row.Note = row.IsExisting ? Strings.Get("Row_AlreadyAdded") : string.Empty;
        }

        for (int i = 0; i < active.Count; i++)
        {
            if (LocationRules.FindOuter(candidates, i) is { } outer)
            {
                active[i].IsShadowed = true;
                active[i].Note = LocationUi.Format(
                    active[i].IsExisting ? "Row_ReplacedBy" : "Row_ContainedIn", outer.Path);
            }
        }
    }
}
