namespace FileDetective.Core;

/// <summary>
/// Debounces and cancels searches while the user types (PLAN.md 6.4, "Ablauf in der Oberfläche"): every
/// <see cref="Request"/> cancels the previous search, waits for the quiet time and then runs the search on the thread
/// pool. Only the result of the newest request is ever delivered. The callbacks run on a pool thread; the UI
/// marshals them itself.
/// </summary>
public sealed class SearchCoordinator
{
    /// <summary>Quiet time before a search starts (PLAN.md 6.4: 30 ms).</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(30);

    private readonly Func<string, SearchMode, CancellationToken, SearchResult> _search;
    private readonly Action<long, SearchResult> _completed;
    private readonly Action<Exception>? _failed;
    private readonly TimeSpan _debounce;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private long _generation;

    /// <param name="search">The search to run; must honour the cancellation token.</param>
    /// <param name="completed">Receives the generation of the request and its result. Never called for superseded requests.</param>
    /// <param name="failed">Receives unexpected exceptions of the search (not cancellations).</param>
    /// <param name="debounce">Quiet time; default <see cref="DefaultDebounce"/>.</param>
    /// <param name="delay">Replaceable wait for tests; default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public SearchCoordinator(
        Func<string, SearchMode, CancellationToken, SearchResult> search,
        Action<long, SearchResult> completed,
        Action<Exception>? failed = null,
        TimeSpan? debounce = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(completed);
        _search = search;
        _completed = completed;
        _failed = failed;
        _debounce = debounce ?? DefaultDebounce;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The generation of the newest request (0 before the first one).</summary>
    public long CurrentGeneration => Volatile.Read(ref _generation);

    /// <summary>Starts a search after the quiet time and cancels the previous one. Returns the generation of this request.</summary>
    public long Request(string input, SearchMode mode)
    {
        // The token sources are deliberately not disposed: they have no timer or registration that needs cleanup,
        // and disposing one while its search still checks the token would be a needless race.
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous;
        long generation;
        lock (_gate)
        {
            previous = _current;
            _current = cts;
            generation = ++_generation;
        }

        previous?.Cancel();
        _ = Task.Run(() => RunAsync(input, mode, generation, cts.Token));
        return generation;
    }

    /// <summary>Cancels the running or waiting search; its result is never delivered.</summary>
    public void CancelPending()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _current;
            _current = null;
            _generation++;
        }

        previous?.Cancel();
    }

    private async Task RunAsync(string input, SearchMode mode, long generation, CancellationToken token)
    {
        try
        {
            if (_debounce > TimeSpan.Zero)
            {
                await _delay(_debounce, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            var result = _search(input, mode, token);
            token.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }
            }

            _completed(generation, result);
        }
        catch (OperationCanceledException)
        {
            // Superseded or cancelled: nothing to deliver.
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (generation != _generation)
                {
                    return;
                }
            }

            _failed?.Invoke(ex);
        }
    }
}
