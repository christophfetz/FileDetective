using System.Buffers;

namespace FileDetective.Core;

public sealed partial class MemoryIndex
{
    /// <summary>Size of the best list each thread keeps (PLAN.md 6.4).</summary>
    public const int PerThreadBestCount = 200;

    // Entries per work item. Divides BlockSize, so a chunk never spans two blocks.
    private const int ChunkSize = 16 * 1024;

    // Per-folder state of the inherited mask (step 2). The low 8 bits hold the mask.
    private const ushort StateUnknown = 0;
    private const ushort StateAlive = 0x100;
    private const ushort StateDead = 0x200;
    private const ushort StateInProgress = 0x400;

    /// <summary>
    /// Finds all entries for which every term occurs in the own name or in the name of an ancestor folder,
    /// filtered by type filters and mode, and returns the best <paramref name="maxResults"/> in rank order
    /// plus the total number of matches (PLAN.md 6.4). Deterministic: the same index and query always give the same order.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public IndexSearchResult Search(
        SearchQuery query, IUsageLookup? usage = null, int maxResults = SearchEngine.MaxResults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.IsEmpty)
        {
            return IndexSearchResult.Empty;
        }

        _lock.EnterReadLock();
        try
        {
            if (_maxId == 0)
            {
                return IndexSearchResult.Empty;
            }

            int length = _maxId + 1;
            byte[] dirOwn = ArrayPool<byte>.Shared.Rent(length);
            ushort[] state = ArrayPool<ushort>.Shared.Rent(length);
            try
            {
                Array.Clear(dirOwn, 0, length);
                Array.Clear(state, 0, length);
                var ctx = new SearchContext(
                    _blocks, _segments, _maxId, query, usage ?? NoUsage.Instance, dirOwn, state,
                    Math.Max(PerThreadBestCount, maxResults));

                // Worker threads read the structure without taking the lock themselves: this thread holds the
                // read lock for the whole search, so no writer can run until all workers are done.
                ForEachChunk(length, cancellationToken, () => 0, (start, end, local) =>
                {
                    ctx.ComputeFolderMasks(start, end);
                    return local;
                }, _ => { });

                ctx.ComputeInheritedMasks(cancellationToken);

                var merged = new List<RankKey>();
                int total = 0;
                var mergeLock = new object();
                ForEachChunk(length, cancellationToken, () => new LocalResult(ctx.Comparer, ctx.PerThreadCapacity),
                    (start, end, local) =>
                    {
                        ctx.Match(start, end, local);
                        return local;
                    },
                    local =>
                    {
                        lock (mergeLock)
                        {
                            local.Best.CopyTo(merged);
                            total += local.Count;
                        }
                    });

                cancellationToken.ThrowIfCancellationRequested();

                // The order is total (entry id is the last tie-breaker), so the result does not depend on how
                // the work was split between threads.
                merged.Sort(ctx.Comparer);
                int take = Math.Min(maxResults, merged.Count);
                var matches = new IndexMatch[take];
                for (int i = 0; i < take; i++)
                {
                    var k = merged[i];
                    matches[i] = new IndexMatch(k.Id, k.Level, k.Quality);
                }

                return new IndexSearchResult(matches, total);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(dirOwn);
                ArrayPool<ushort>.Shared.Return(state);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private void ForEachChunk<TLocal>(
        int length, CancellationToken ct, Func<TLocal> init, Func<int, int, TLocal, TLocal> body, Action<TLocal> final)
    {
        int chunks = (length + ChunkSize - 1) / ChunkSize;
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = _maxDegreeOfParallelism };
        try
        {
            Parallel.For(0, chunks, options, init, (chunk, _, local) =>
            {
                ct.ThrowIfCancellationRequested();
                int start = chunk * ChunkSize;
                return body(start, Math.Min(length, start + ChunkSize), local);
            }, final);
        }
        catch (AggregateException ex) when (ct.IsCancellationRequested
            && ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ct);
        }
    }

    private sealed class LocalResult(RankComparer comparer, int capacity)
    {
        public BestList Best { get; } = new(comparer, capacity);

        public int Count { get; set; }
    }

    /// <summary>Ranking key of one match (PLAN.md 6.4, "Rangfolge").</summary>
    internal readonly record struct RankKey(
        int Id, byte Level, byte Quality, bool Opened, long OpenedAt, uint MTime, uint NameOffset, ushort NameLength);

    /// <summary>Orders better matches first. Total order: the entry id decides the last tie.</summary>
    internal sealed class RankComparer(byte[][] segments) : IComparer<RankKey>
    {
        public int Compare(RankKey a, RankKey b)
        {
            // 1. Name level, higher first.
            if (a.Level != b.Level)
            {
                return b.Level.CompareTo(a.Level);
            }

            // 2. Name quality, higher first (0 for levels below 2).
            if (a.Quality != b.Quality)
            {
                return b.Quality.CompareTo(a.Quality);
            }

            // 3. Opened through the tool before not opened, then most recent opening first.
            if (a.Opened != b.Opened)
            {
                return a.Opened ? -1 : 1;
            }

            if (a.OpenedAt != b.OpenedAt)
            {
                return b.OpenedAt.CompareTo(a.OpenedAt);
            }

            // 4. Last write time, newest first.
            if (a.MTime != b.MTime)
            {
                return b.MTime.CompareTo(a.MTime);
            }

            // 5. Shorter name first, then alphabetical (ordinal on the search form).
            if (a.NameLength != b.NameLength)
            {
                return a.NameLength.CompareTo(b.NameLength);
            }

            int c = Name(a).SequenceCompareTo(Name(b));
            if (c != 0)
            {
                return c;
            }

            // Not in the plan: the entry id makes the order total and therefore deterministic.
            return a.Id.CompareTo(b.Id);
        }

        private ReadOnlySpan<byte> Name(RankKey k)
            => segments[k.NameOffset >> SegmentShift].AsSpan((int)(k.NameOffset & SegmentPositionMask), k.NameLength);
    }

    /// <summary>Bounded list of the best keys: a heap with the worst kept key at the root.</summary>
    internal sealed class BestList(IComparer<RankKey> comparer, int capacity)
    {
        private readonly RankKey[] _items = new RankKey[capacity];
        private int _count;

        public int Count => _count;

        public void Offer(in RankKey key)
        {
            if (_count < _items.Length)
            {
                int i = _count++;
                _items[i] = key;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (comparer.Compare(_items[i], _items[parent]) <= 0)
                    {
                        break;
                    }

                    (_items[i], _items[parent]) = (_items[parent], _items[i]);
                    i = parent;
                }

                return;
            }

            if (comparer.Compare(key, _items[0]) >= 0)
            {
                return; // not better than the worst kept key
            }

            _items[0] = key;
            int j = 0;
            while (true)
            {
                int left = (2 * j) + 1;
                if (left >= _count)
                {
                    break;
                }

                int worse = left;
                int right = left + 1;
                if (right < _count && comparer.Compare(_items[right], _items[left]) > 0)
                {
                    worse = right;
                }

                if (comparer.Compare(_items[worse], _items[j]) <= 0)
                {
                    break;
                }

                (_items[j], _items[worse]) = (_items[worse], _items[j]);
                j = worse;
            }
        }

        public void CopyTo(List<RankKey> target)
        {
            for (int i = 0; i < _count; i++)
            {
                target.Add(_items[i]);
            }
        }
    }

    /// <summary>State of one search run; read-only access to the index while the read lock is held.</summary>
    private sealed class SearchContext
    {
        private readonly EntryRec[]?[] _blocks;
        private readonly byte[][] _segments;
        private readonly int _maxId;
        private readonly byte[][] _terms;
        private readonly byte[][] _filters;
        private readonly byte[] _exact;
        private readonly SearchMode _mode;
        private readonly IUsageLookup _usage;
        private readonly byte[] _dirOwn;
        private readonly ushort[] _state;
        private readonly int _fullMask;

        public SearchContext(
            EntryRec[]?[] blocks, byte[][] segments, int maxId, SearchQuery query, IUsageLookup usage,
            byte[] dirOwn, ushort[] state, int perThreadCapacity)
        {
            _blocks = blocks;
            _segments = segments;
            _maxId = maxId;
            _terms = query.TermBytes;
            _filters = query.TypeFilterBytes;
            _exact = query.ExactBytes;
            _mode = query.Mode;
            _usage = usage;
            _dirOwn = dirOwn;
            _state = state;
            _fullMask = (1 << _terms.Length) - 1;
            PerThreadCapacity = perThreadCapacity;
            Comparer = new RankComparer(segments);
        }

        public RankComparer Comparer { get; }

        public int PerThreadCapacity { get; }

        /// <summary>Step 1: which terms each folder's own name contains.</summary>
        public void ComputeFolderMasks(int start, int end)
        {
            var block = _blocks[start >> BlockShift];
            if (block is null)
            {
                return;
            }

            for (int id = start; id < end; id++)
            {
                ref readonly EntryRec r = ref block[id & BlockMask];
                if ((r.Flags & (EntryFlags.Present | EntryFlags.Deleted | EntryFlags.IsDir)) == (EntryFlags.Present | EntryFlags.IsDir))
                {
                    _dirOwn[id] = (byte)OwnMask(Name(r), _fullMask);
                }
            }
        }

        /// <summary>
        /// Step 2: inherited mask of every folder = own mask OR inherited mask of the parent. Walks up the parent
        /// chain with an explicit stack and memoizes each result, so the order of ids does not matter, each folder
        /// is resolved once, and deep paths cannot overflow the call stack. A folder whose chain does not end in a
        /// root entry (missing, deleted or non-folder ancestor, or a cycle) is marked dead and hides its subtree.
        /// </summary>
        public void ComputeInheritedMasks(CancellationToken ct)
        {
            var stack = new int[64];
            for (int id = 1; id <= _maxId; id++)
            {
                if ((id & BlockMask) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var block = _blocks[id >> BlockShift];
                if (block is null)
                {
                    // Skip the rest of the missing block (written so that it cannot overflow).
                    int last = id | BlockMask;
                    if (last >= _maxId)
                    {
                        break;
                    }

                    id = last;
                    continue;
                }

                if (_state[id] == StateUnknown && IsLiveDir(block[id & BlockMask].Flags))
                {
                    Resolve(id, ref stack);
                }
            }
        }

        private void Resolve(int start, ref int[] stack)
        {
            int depth = 0;
            int current = start;
            int result;
            while (true)
            {
                ushort s = _state[current];
                if (s != StateUnknown)
                {
                    // In progress means we came back to a folder on the current chain: a cycle.
                    result = s == StateInProgress ? StateDead : s;
                    break;
                }

                var block = _blocks[current >> BlockShift];
                if (block is null || !IsLiveDir(block[current & BlockMask].Flags))
                {
                    result = StateDead;
                    break;
                }

                _state[current] = StateInProgress;
                if (depth == stack.Length)
                {
                    Array.Resize(ref stack, stack.Length * 2);
                }

                stack[depth++] = current;
                int parent = block[current & BlockMask].Parent;
                if (parent == 0)
                {
                    result = StateAlive;
                    break;
                }

                if (parent > _maxId)
                {
                    result = StateDead;
                    break;
                }

                current = parent;
            }

            // Unwind from the topmost folder down, adding each folder's own mask.
            while (depth > 0)
            {
                int id = stack[--depth];
                if (result != StateDead)
                {
                    result |= _dirOwn[id];
                }

                _state[id] = (ushort)result;
            }
        }

        /// <summary>Step 3: test every entry and offer matches to the thread's best list.</summary>
        public void Match(int start, int end, LocalResult local)
        {
            var block = _blocks[start >> BlockShift];
            if (block is null)
            {
                return;
            }

            for (int id = start; id < end; id++)
            {
                ref readonly EntryRec r = ref block[id & BlockMask];
                byte flags = r.Flags;
                if (!IsLive(flags))
                {
                    continue;
                }

                bool isDir = (flags & EntryFlags.IsDir) != 0;
                if ((_mode == SearchMode.Files && isDir) || (_mode == SearchMode.Folders && !isDir))
                {
                    continue;
                }

                int parentState = r.Parent == 0 ? StateAlive : r.Parent <= _maxId ? _state[r.Parent] : StateUnknown;
                if ((parentState & StateAlive) == 0)
                {
                    continue; // parent chain broken (deleted, missing, cycle)
                }

                var name = Name(r);
                if (_filters.Length > 0 && !EndsWithAny(name, _filters))
                {
                    continue;
                }

                int parentMask = parentState & 0xFF;
                int own;
                if (isDir)
                {
                    own = _dirOwn[id];
                    if ((own | parentMask) != _fullMask)
                    {
                        continue;
                    }
                }
                else
                {
                    // First only the terms the path does not provide; stop at the first missing one.
                    int needed = _fullMask & ~parentMask;
                    own = OwnMask(name, needed, stopAtFirstMiss: true);
                    if (own != needed)
                    {
                        continue;
                    }

                    // Then the rest, which only matters for the name level.
                    own |= OwnMask(name, _fullMask & parentMask);
                }

                local.Count++;
                byte level = own == _fullMask ? (byte)2 : own != 0 ? (byte)1 : (byte)0;
                byte quality = level == 2 ? Quality(name, isDir) : (byte)0;
                bool opened = _usage.TryGetLastOpened(id, out long openedAt);
                local.Best.Offer(new RankKey(id, level, quality, opened, opened ? openedAt : 0, r.MTime, r.NameOffset, r.NameLength));
            }
        }

        /// <summary>
        /// Bits of the terms in <paramref name="which"/> that occur in <paramref name="name"/>.
        /// With <paramref name="stopAtFirstMiss"/> it returns early, because one missing term already excludes the entry.
        /// </summary>
        private int OwnMask(ReadOnlySpan<byte> name, int which, bool stopAtFirstMiss = false)
        {
            int mask = 0;
            for (int t = 0; t < _terms.Length; t++)
            {
                int bit = 1 << t;
                if ((which & bit) == 0)
                {
                    continue;
                }

                if (name.IndexOf(_terms[t]) >= 0)
                {
                    mask |= bit;
                }
                else if (stopAtFirstMiss)
                {
                    return mask;
                }
            }

            return mask;
        }

        private byte Quality(ReadOnlySpan<byte> name, bool isDir)
        {
            // 3: the name (without extension) is exactly the input. Decision: folders have no extension,
            // and the full name counts too, so "bericht.pdf" typed as one term matches "bericht.pdf".
            if (_exact.Length > 0)
            {
                if (name.SequenceEqual(_exact))
                {
                    return 3;
                }

                if (!isDir)
                {
                    int dot = name.LastIndexOf((byte)'.');
                    if (dot > 0 && name[..dot].SequenceEqual(_exact))
                    {
                        return 3;
                    }
                }
            }

            // 2: the name starts with the first term.
            if (_terms.Length > 0 && name.StartsWith(_terms[0]))
            {
                return 2;
            }

            // 1: some term starts at a word boundary.
            foreach (var term in _terms)
            {
                if (StartsAtWordBoundary(name, term))
                {
                    return 1;
                }
            }

            return 0;
        }

        private static bool StartsAtWordBoundary(ReadOnlySpan<byte> name, ReadOnlySpan<byte> term)
        {
            int from = 0;
            while (from < name.Length)
            {
                int pos = name[from..].IndexOf(term);
                if (pos < 0)
                {
                    return false;
                }

                int at = from + pos;
                if (at == 0 || !IsWordByte(name[at - 1]))
                {
                    return true;
                }

                from = at + 1;
            }

            return false;
        }

        /// <summary>Letters and digits of the search form; bytes of multi-byte UTF-8 characters count as letters.</summary>
        private static bool IsWordByte(byte b) => (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') || b >= 0x80;

        private static bool EndsWithAny(ReadOnlySpan<byte> name, byte[][] filters)
        {
            foreach (var f in filters)
            {
                if (name.EndsWith(f))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLiveDir(byte flags)
            => (flags & (EntryFlags.Present | EntryFlags.Deleted | EntryFlags.IsDir)) == (EntryFlags.Present | EntryFlags.IsDir);

        private ReadOnlySpan<byte> Name(in EntryRec r)
            => _segments[r.NameOffset >> SegmentShift].AsSpan((int)(r.NameOffset & SegmentPositionMask), r.NameLength);
    }
}
