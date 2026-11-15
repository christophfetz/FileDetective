using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileDetective.Core;

namespace FileDetective.App.Interop;

/// <summary>
/// Small previews of images for the rows of mode Image text (PLAN.md 11.3), made by Windows as in Explorer
/// (<c>IShellItemImageFactory</c>). Loaded one after the other on a background thread of their own, so the search bar never
/// waits; the newest request comes first. Only local files: a UNC path, a mapped network drive and a cloud-only file get no
/// preview (Windows would have to open or download them). Previews are kept for the paths shown last.
/// </summary>
internal sealed class Thumbnails : IDisposable
{
    /// <summary>Side of a preview in device-independent units.</summary>
    public const int Size = 64;

    private const int CacheSize = 300;

    private readonly Dispatcher _dispatcher;
    private readonly Func<string, bool> _isLocal;
    private readonly object _gate = new();
    private readonly LinkedList<(string Key, string Path, Action<BitmapSource> Done)> _requests = new();
    private readonly Dictionary<string, BitmapSource> _cache = [];
    private readonly Queue<string> _cacheOrder = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stopped;

    public Thumbnails(Dispatcher dispatcher, Func<string, bool> isLocal)
    {
        _dispatcher = dispatcher;
        _isLocal = isLocal;
        _thread = new Thread(Run) { IsBackground = true, Name = "FileDetective thumbnails", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// Asks for the preview of <paramref name="path"/> in <paramref name="pixels"/> device pixels; <paramref name="done"/> runs on
    /// the UI thread. A preview in the cache is handed over at once. Requests not started yet are dropped by <see cref="CancelPending"/>.
    /// </summary>
    public void Request(string path, DateTime lastWriteUtc, int pixels, Action<BitmapSource> done)
    {
        string key = $"{pixels}|{lastWriteUtc.Ticks}|{path}";
        BitmapSource? cached;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out cached))
            {
                _requests.AddLast((key, path, done));
            }
        }

        if (cached is not null)
        {
            done(cached);
            return;
        }

        _signal.Set();
    }

    /// <summary>Drops the requests that have not started (a new result list replaces the old one).</summary>
    public void CancelPending()
    {
        lock (_gate)
        {
            _requests.Clear();
        }
    }

    private void Run()
    {
        while (!_stopped)
        {
            _signal.WaitOne();
            while (!_stopped)
            {
                (string Key, string Path, Action<BitmapSource> Done) request;
                lock (_gate)
                {
                    if (_requests.First is not { } first)
                    {
                        break;
                    }

                    request = first.Value;
                    _requests.RemoveFirst();
                }

                int pixels = int.Parse(request.Key.AsSpan(0, request.Key.IndexOf('|')), System.Globalization.CultureInfo.InvariantCulture);
                BitmapSource? image = null;
                try
                {
                    image = Load(request.Path, pixels);
                }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // No preview: the row keeps its icon.
                }

                if (image is null)
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_cache.TryAdd(request.Key, image))
                    {
                        _cacheOrder.Enqueue(request.Key);
                        while (_cacheOrder.Count > CacheSize)
                        {
                            _cache.Remove(_cacheOrder.Dequeue());
                        }
                    }
                }

                var done = request.Done;
                _dispatcher.BeginInvoke(new Action(() => done(image)));
            }
        }
    }

    private BitmapSource? Load(string path, int pixels)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !_isLocal(path))
        {
            return null;
        }

        // Attributes come from the folder listing; a cloud-only file is never handed to the shell.
        var attributes = File.GetAttributes(path);
        if (DuplicateFinder.MustNotOpen(attributes))
        {
            return null;
        }

        var iid = typeof(IShellItemImageFactory).GUID;
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (hr != 0 || factory is null)
        {
            return null;
        }

        try
        {
            hr = factory.GetImage(new NativeSize { Width = pixels, Height = pixels }, ThumbnailOnly | BiggerSizeOk, out IntPtr bitmap);
            if (hr != 0 || bitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _signal.Set();
    }

    private const int BiggerSizeOk = 0x1;
    private const int ThumbnailOnly = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
