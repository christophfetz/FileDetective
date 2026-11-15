namespace FileDetective.Core;

/// <summary>
/// The notes of the user for images (stage 13, PLAN.md 11.4). A note is stored by the full path of the image in the table
/// <c>notes</c>, never in the image or its name (rule 2). In memory it is attached to the entry of that path in the
/// <see cref="ImageTextIndex"/>, so the image text search finds it; notes work whether the text recognition is on or off.
/// </summary>
/// <remarks>
/// Follows the directory through <see cref="IndexStore.Changed"/>:
/// <list type="bullet">
/// <item>Rebuild: all notes are let go in memory and attached again as their images come back.</item>
/// <item>New files: notes without an entry are attached when an entry with their path appears.</item>
/// <item>Move or rename reported by the directory: the note moves with the image.</item>
/// <item>Removed image: the note stays while no location contains the path (the location was removed) or the file is still
/// in the listing of its folder (local only, nothing is opened); otherwise the image is gone for good and so is the note.</item>
/// </list>
/// Thread-safe.
/// </remarks>
public sealed class ImageNoteService
{
    /// <summary>Longest note kept; a longer text is cut.</summary>
    public const int MaxLength = 2000;

    private readonly object _gate = new();
    private readonly IndexStore _store;
    private readonly ImageTextIndex _texts;
    private readonly IEntryResolver _resolver;
    private readonly ILocalFileAccess _files;
    private readonly Func<string, bool> _isLocalPath;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Note> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Note> _byId = [];

    public ImageNoteService(
        IndexStore store,
        ImageTextIndex texts,
        IEntryResolver resolver,
        ILocalFileAccess files,
        Func<string, bool> isLocalPath,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _texts = texts ?? throw new ArgumentNullException(nameof(texts));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _isLocalPath = isLocalPath ?? throw new ArgumentNullException(nameof(isLocalPath));
        _clock = clock ?? TimeProvider.System;
        _store.Changed += OnStoreChanged;
    }

    /// <summary>Raised after a note was saved, removed or moved, on the thread that did it.</summary>
    public event Action? Changed;

    /// <summary>Number of stored notes (attached or not).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byPath.Count;
            }
        }
    }

    /// <summary>Reads the stored notes and attaches them to their entries. Call once the directory is in memory.</summary>
    public void Load()
    {
        var stored = _store.ReadImageNotes();
        lock (_gate)
        {
            foreach (var note in _byId.Values)
            {
                _texts.RemoveNote(note.EntryId!.Value);
            }

            _byPath.Clear();
            _byId.Clear();
            foreach (var row in stored)
            {
                _byPath[row.Path] = new Note(row.Path, row.Text);
            }

            AttachAll();
        }
    }

    /// <summary>The note of an entry, or null.</summary>
    public string? Get(int entryId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(entryId, out var note) ? note.Text : null;
        }
    }

    /// <summary>The note stored for a full path, or null.</summary>
    public string? GetByPath(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        lock (_gate)
        {
            return _byPath.TryGetValue(fullPath, out var note) ? note.Text : null;
        }
    }

    /// <summary>
    /// Stores the note of the image <paramref name="entryId"/> at <paramref name="fullPath"/>. Blank text removes it; the
    /// text is trimmed and cut at <see cref="MaxLength"/>. Does database work.
    /// </summary>
    /// <returns>True if something changed.</returns>
    public bool Save(int entryId, string fullPath, string? text)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        string clean = Clean(text);
        bool changed;
        lock (_gate)
        {
            if (clean.Length == 0)
            {
                changed = _store.DeleteImageNote(fullPath) | _byPath.ContainsKey(fullPath);
                Forget(fullPath);
            }
            else
            {
                changed = !(_byPath.TryGetValue(fullPath, out var old) && old.Text == clean);
                if (changed)
                {
                    _store.SaveImageNote(new StoredImageNote(fullPath, clean, _clock.GetUtcNow().ToUnixTimeSeconds()));
                    Forget(fullPath);
                    var note = new Note(fullPath, clean);
                    _byPath[fullPath] = note;
                    Attach(note, entryId);
                }
            }
        }

        if (changed)
        {
            RaiseChanged();
        }

        return changed;
    }

    /// <summary>Trims the text and cuts it at <see cref="MaxLength"/>; line breaks become <c>\n</c>.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string clean = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return clean.Length > MaxLength ? clean[..MaxLength].TrimEnd() : clean;
    }

    // ---------------------------------------------------------------- following the directory

    private void OnStoreChanged(IndexChange change)
    {
        bool changed = false;
        try
        {
            lock (_gate)
            {
                if (_byPath.Count == 0)
                {
                    return;
                }

                if (change.Reset)
                {
                    foreach (int id in _byId.Keys)
                    {
                        _texts.RemoveNote(id);
                    }

                    foreach (var note in _byId.Values)
                    {
                        note.EntryId = null;
                    }

                    _byId.Clear();
                    return; // the rebuild brings the entries back as new files
                }

                foreach (int id in change.Deleted)
                {
                    if (_byId.Remove(id, out var note))
                    {
                        _texts.RemoveNote(id);
                        note.EntryId = null;
                        changed |= HandleRemoved(note);
                    }
                }

                if (change.Relocated)
                {
                    changed |= FollowMoves();
                }

                foreach (var written in change.Written)
                {
                    if (_byId.TryGetValue(written.Id, out var note))
                    {
                        Attach(note, written.Id); // new time
                    }
                }

                if (change.Relocated || change.Written.Any(w => w.Name is not null))
                {
                    AttachAll(change.Relocated ? null : change.Written.Where(w => w.Name is not null).Select(w => w.Name!));
                }
            }
        }
        catch (Exception)
        {
            // A listener must never break a scan; the notes are attached again at the next start.
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>The entry of a note was removed: is the image gone for good? Then the note goes too.</summary>
    private bool HandleRemoved(Note note)
    {
        if (_store.FindEntryId(note.Path) is { } id)
        {
            Attach(note, id); // replaced by a new entry of the same path
            return false;
        }

        if (!UnderAnyLocation(note.Path))
        {
            return false; // the location was removed: that says nothing about the image
        }

        if (!_isLocalPath(note.Path) || _files.Probe(note.Path) is not null)
        {
            return false; // still there (or not checkable): attached again when the directory has it
        }

        _store.DeleteImageNote(note.Path);
        _byPath.Remove(note.Path);
        return true;
    }

    /// <summary>Moves notes whose entry now has another path (moved or renamed in the directory).</summary>
    private bool FollowMoves()
    {
        if (_byId.Count == 0)
        {
            return false;
        }

        var ids = _byId.Keys.ToList();
        var details = _resolver.Resolve(ids, CancellationToken.None);
        bool changed = false;
        for (int i = 0; i < ids.Count; i++)
        {
            var note = _byId[ids[i]];
            if (details[i] is not { IsDirectory: false } d)
            {
                continue;
            }

            if (!string.Equals(d.FullPath, note.Path, StringComparison.Ordinal))
            {
                _store.MoveImageNote(note.Path, d.FullPath);
                _byPath.Remove(note.Path);
                if (_byPath.Remove(d.FullPath, out var replaced) && replaced.EntryId is { } replacedId && replacedId != ids[i])
                {
                    _byId.Remove(replacedId);
                    _texts.RemoveNote(replacedId);
                }

                note.Path = d.FullPath;
                _byPath[note.Path] = note;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Attaches notes without an entry; with <paramref name="names"/> only those whose file name is among them.</summary>
    private void AttachAll(IEnumerable<string>? names = null)
    {
        HashSet<string>? wanted = names is null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (var note in _byPath.Values)
        {
            if (note.EntryId is not null || (wanted is not null && !wanted.Contains(Path.GetFileName(note.Path))))
            {
                continue;
            }

            if (_store.FindEntryId(note.Path) is { } id)
            {
                Attach(note, id);
            }
        }
    }

    private void Attach(Note note, int entryId)
    {
        if (_store.GetEntry(entryId) is not { IsDirectory: false } entry)
        {
            return;
        }

        if (note.EntryId is { } old && old != entryId)
        {
            _byId.Remove(old);
            _texts.RemoveNote(old);
        }

        note.EntryId = entryId;
        _byId[entryId] = note;
        _texts.SetNote(entryId, note.Text, DateTime.UnixEpoch.AddSeconds(entry.MTimeUnixSeconds).Ticks);
    }

    private void Forget(string fullPath)
    {
        if (_byPath.Remove(fullPath, out var note) && note.EntryId is { } id)
        {
            _byId.Remove(id);
            _texts.RemoveNote(id);
        }
    }

    private bool UnderAnyLocation(string path)
        => _store.GetRoots().Any(r =>
        {
            string root = r.Path.TrimEnd('\\', '/');
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (path.Length == root.Length || path[root.Length] is '\\' or '/');
        });

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A listener must not break saving.
        }
    }

    private sealed class Note(string path, string text)
    {
        public string Path { get; set; } = path;

        public string Text { get; } = text;

        public int? EntryId { get; set; }
    }
}
