using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileDetective.Core;

/// <summary>
/// One running instance per user (PLAN.md 8.1): a named mutex decides who is first, a named event lets a second start
/// wake the first one. Names live in the <c>Local\</c> namespace, so they are per Windows session, and contain the
/// user and a hash of the data folder.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// Fixed name of a mutex that every running instance holds, whatever the user or data folder (the guard's own names
    /// contain a hash of the data folder, too unstable for outsiders). The installer (<c>AppMutex</c> in
    /// <c>installer/FileDetective.iss</c>) uses it to see that the program runs and asks to close it.
    /// </summary>
    public const string RunningMarkerName = @"Local\FileDetective.Running";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _wake;
    private bool _owner;
    private RegisteredWaitHandle? _registration;

    /// <param name="baseName">Name from <see cref="BuildName"/> (or any unique text in tests).</param>
    public SingleInstanceGuard(string baseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);

        // The event is created first: a second start that comes right after the mutex was taken can already signal it,
        // and the signal stays set until the first instance has started listening.
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + baseName + ".wake");
        _mutex = new Mutex(false, @"Local\" + baseName);
    }

    /// <summary>Raised on a pool thread when another start asked this instance to show itself.</summary>
    public event Action? WakeRequested;

    /// <summary>The same user and the same data folder always give the same name; other users or folders give other names.</summary>
    public static string BuildName(string appName, string userId, string dataFolder)
    {
        string folder = Path.GetFullPath(dataFolder).TrimEnd('\\', '/').ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(folder));
        return string.Create(CultureInfo.InvariantCulture, $"{appName}-{userId}-{Convert.ToHexString(hash, 0, 4)}");
    }

    /// <summary>True if this is the first instance. Call once; the instance stays first until <see cref="Dispose"/>.</summary>
    public bool TryBecomePrimary()
    {
        try
        {
            _owner = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance died without releasing: this one is the first now.
            _owner = true;
        }

        return _owner;
    }

    /// <summary>Asks the running instance to show itself. Used by a second start.</summary>
    public void SignalPrimary() => _wake.Set();

    /// <summary>Starts raising <see cref="WakeRequested"/>. Only the first instance should listen.</summary>
    public void StartListening()
    {
        _registration ??= ThreadPool.RegisterWaitForSingleObject(
            _wake, (_, _) => WakeRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _registration = null;
        if (_owner)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Disposed on another thread than the one that took it; process exit releases it anyway.
            }

            _owner = false;
        }

        _mutex.Dispose();
        _wake.Dispose();
    }
}
