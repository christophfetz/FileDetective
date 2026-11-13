namespace FileDetective.Core;

/// <summary>One entry in the <see cref="MemoryIndex"/> (PLAN.md 6.3), exactly 16 bytes.</summary>
internal struct EntryRec
{
    /// <summary>Id of the parent folder, 0 = root entry of a location.</summary>
    public int Parent;

    /// <summary>Segment (upper 8 bits) and position (lower 24 bits) in the name store.</summary>
    public uint NameOffset;

    /// <summary>Length of the search form in bytes.</summary>
    public ushort NameLength;

    public byte Flags;

    public byte RootId;

    /// <summary>Last write time, Unix seconds UTC.</summary>
    public uint MTime;
}

internal static class EntryFlags
{
    public const byte IsDir = 1;
    public const byte Deleted = 2;
    public const byte RootEntry = 4;

    /// <summary>
    /// Decision (addition to PLAN.md 6.3): marks a slot that has been set. Unused slots in a block are all zero
    /// and would otherwise be indistinguishable from a file with an empty name.
    /// </summary>
    public const byte Present = 8;
}

/// <summary>
/// The in-memory search structure (PLAN.md 6.3). Holds only the search form of each name plus parent,
/// flags, location and last write time. Searches hold the read lock, changes the write lock.
/// </summary>
public sealed partial class MemoryIndex : IDisposable
{
    internal const int BlockShift = 16;
    internal const int BlockSize = 1 << BlockShift;
    internal const int BlockMask = BlockSize - 1;
    internal const int SegmentShift = 24;
    internal const int SegmentSize = 1 << SegmentShift; // 16 MiB
    internal const int SegmentPositionMask = SegmentSize - 1;
    internal const int MaxSegments = 256;

    /// <summary>Highest allowed entry id, so that per-search arrays of size id + 1 can be allocated.</summary>
    public static readonly int MaxEntryId = Array.MaxLength - 1;

    // Decision: a segment starts small and doubles up to 16 MiB (copying only that one segment, under the
    // write lock), so that small indexes (tests, few entries) do not allocate 16 MiB up front.
    private const int InitialSegmentCapacity = 64 * 1024;

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly int _maxDegreeOfParallelism;
    private readonly byte[][] _segments = new byte[MaxSegments][];
    private EntryRec[]?[] _blocks = new EntryRec[]?[16];
    private int _segmentCount;
    private int _segmentUsed;
    private int _maxId;
    private int _count;

    /// <param name="maxDegreeOfParallelism">Threads used by a search; -1 = all cores.</param>
    public MemoryIndex(int maxDegreeOfParallelism = -1)
    {
        if (maxDegreeOfParallelism == 0 || maxDegreeOfParallelism < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        }

        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <summary>Number of entries that are set and not marked deleted.</summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>Highest entry id ever set.</summary>
    public int MaxId
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _maxId;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// Adds or replaces an entry. <paramref name="parentId"/> 0 makes it the root entry of a location
    /// (its name is then the whole path of the location). The parent does not need to exist yet.
    /// </summary>
    public void Set(int id, int parentId, byte rootId, string name, bool isDirectory, long lastWriteUnixSeconds)
    {
        var normalized = PrepareSet(id, parentId, name);
        _lock.EnterWriteLock();
        try
        {
            SetCore(id, parentId, rootId, normalized, isDirectory, lastWriteUnixSeconds);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Gives an entry a new name. The old search form stays as a gap until the next load.</summary>
    /// <returns>False if the entry does not exist or is deleted.</returns>
    public bool Rename(int id, string newName)
    {
        ArgumentNullException.ThrowIfNull(newName);
        var normalized = SearchNormalizer.Normalize(newName);
        _lock.EnterWriteLock();
        try
        {
            return RenameCore(id, normalized);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Moves an entry (with its subtree) below another parent.</summary>
    /// <returns>False if the entry does not exist or is deleted.</returns>
    public bool Move(int id, int newParentId)
    {
        ValidateParent(id, newParentId);
        _lock.EnterWriteLock();
        try
        {
            return MoveCore(id, newParentId);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Marks an entry as deleted. Descendants of a deleted folder no longer appear in searches either,
    /// because their parent chain is broken; the caller (IndexStore) is still expected to delete them explicitly.
    /// </summary>
    /// <returns>False if the entry does not exist or is already deleted.</returns>
    public bool Delete(int id)
    {
        _lock.EnterWriteLock();
        try
        {
            return DeleteCore(id);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Takes the write lock for a batch of changes; dispose the scope to release it.
    /// Must be disposed on the same thread (ReaderWriterLockSlim has thread affinity).
    /// </summary>
    public UpdateScope BeginUpdate()
    {
        _lock.EnterWriteLock();
        return new UpdateScope(this);
    }

    public void Dispose() => _lock.Dispose();

    /// <summary>A batch of changes under one write lock.</summary>
    public ref struct UpdateScope
    {
        private MemoryIndex? _index;

        internal UpdateScope(MemoryIndex index)
        {
            _index = index;
        }

        private readonly MemoryIndex Index => _index ?? throw new ObjectDisposedException(nameof(UpdateScope));

        /// <inheritdoc cref="MemoryIndex.Set"/>
        public readonly void Set(int id, int parentId, byte rootId, string name, bool isDirectory, long lastWriteUnixSeconds)
            => Index.SetCore(id, parentId, rootId, PrepareSet(id, parentId, name), isDirectory, lastWriteUnixSeconds);

        /// <inheritdoc cref="MemoryIndex.Rename"/>
        public readonly bool Rename(int id, string newName)
        {
            ArgumentNullException.ThrowIfNull(newName);
            return Index.RenameCore(id, SearchNormalizer.Normalize(newName));
        }

        /// <inheritdoc cref="MemoryIndex.Move"/>
        public readonly bool Move(int id, int newParentId)
        {
            ValidateParent(id, newParentId);
            return Index.MoveCore(id, newParentId);
        }

        /// <inheritdoc cref="MemoryIndex.Delete"/>
        public readonly bool Delete(int id) => Index.DeleteCore(id);

        public void Dispose()
        {
            if (_index is { } index)
            {
                _index = null;
                index._lock.ExitWriteLock();
            }
        }
    }

    private static byte[] PrepareSet(int id, int parentId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ValidateParent(id, parentId);
        var normalized = SearchNormalizer.Normalize(name);
        if (normalized.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The search form of the name is longer than 65535 bytes.", nameof(name));
        }

        return normalized;
    }

    private static void ValidateParent(int id, int parentId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(id, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(id, MaxEntryId);
        ArgumentOutOfRangeException.ThrowIfNegative(parentId);
        if (parentId == id)
        {
            throw new ArgumentException("An entry cannot be its own parent.", nameof(parentId));
        }
    }

    private void SetCore(int id, int parentId, byte rootId, byte[] normalized, bool isDirectory, long lastWriteUnixSeconds)
    {
        ref EntryRec slot = ref GetOrCreateSlot(id);
        bool wasLive = IsLive(slot.Flags);
        slot.NameOffset = AppendName(normalized);
        slot.NameLength = (ushort)normalized.Length;
        slot.Parent = parentId;
        slot.RootId = rootId;
        slot.MTime = (uint)Math.Clamp(lastWriteUnixSeconds, 0, uint.MaxValue);
        slot.Flags = (byte)(EntryFlags.Present
            | (isDirectory ? EntryFlags.IsDir : 0)
            | (parentId == 0 ? EntryFlags.RootEntry : 0));
        if (!wasLive)
        {
            _count++;
        }

        if (id > _maxId)
        {
            _maxId = id;
        }
    }

    private bool RenameCore(int id, byte[] normalized)
    {
        if (normalized.Length > ushort.MaxValue)
        {
            throw new ArgumentException("The search form of the name is longer than 65535 bytes.", nameof(normalized));
        }

        if (!TryGetLiveSlot(id, out var block))
        {
            return false;
        }

        ref EntryRec slot = ref block[id & BlockMask];
        slot.NameOffset = AppendName(normalized);
        slot.NameLength = (ushort)normalized.Length;
        return true;
    }

    private bool MoveCore(int id, int newParentId)
    {
        if (!TryGetLiveSlot(id, out var block))
        {
            return false;
        }

        ref EntryRec slot = ref block[id & BlockMask];
        slot.Parent = newParentId;
        slot.Flags = newParentId == 0
            ? (byte)(slot.Flags | EntryFlags.RootEntry)
            : (byte)(slot.Flags & ~EntryFlags.RootEntry);
        return true;
    }

    private bool DeleteCore(int id)
    {
        if (!TryGetLiveSlot(id, out var block))
        {
            return false;
        }

        block[id & BlockMask].Flags |= EntryFlags.Deleted;
        _count--;
        return true;
    }

    private static bool IsLive(byte flags) => (flags & (EntryFlags.Present | EntryFlags.Deleted)) == EntryFlags.Present;

    private bool TryGetLiveSlot(int id, out EntryRec[] block)
    {
        block = null!;
        if (id < 1 || id > _maxId)
        {
            return false;
        }

        var b = _blocks[id >> BlockShift];
        if (b is null || !IsLive(b[id & BlockMask].Flags))
        {
            return false;
        }

        block = b;
        return true;
    }

    private ref EntryRec GetOrCreateSlot(int id)
    {
        int blockIndex = id >> BlockShift;
        if (blockIndex >= _blocks.Length)
        {
            // Only the small outer array of block references is copied, never the blocks themselves.
            Array.Resize(ref _blocks, Math.Max(_blocks.Length * 2, blockIndex + 1));
        }

        var block = _blocks[blockIndex] ??= new EntryRec[BlockSize];
        return ref block[id & BlockMask];
    }

    private uint AppendName(byte[] name)
    {
        int length = name.Length;
        if (_segmentCount == 0 || _segmentUsed + length > SegmentSize)
        {
            // A name never spans two segments: start a new one.
            if (_segmentCount == MaxSegments)
            {
                throw new InvalidOperationException("The name store is full.");
            }

            _segments[_segmentCount++] = new byte[Math.Max(InitialSegmentCapacity, length)];
            _segmentUsed = 0;
        }

        int segmentIndex = _segmentCount - 1;
        var segment = _segments[segmentIndex];
        if (_segmentUsed + length > segment.Length)
        {
            int newLength = Math.Min(SegmentSize, Math.Max(segment.Length * 2, _segmentUsed + length));
            Array.Resize(ref segment, newLength);
            _segments[segmentIndex] = segment;
        }

        name.CopyTo(segment, _segmentUsed);
        uint offset = ((uint)segmentIndex << SegmentShift) | (uint)_segmentUsed;
        _segmentUsed += length;
        return offset;
    }
}
