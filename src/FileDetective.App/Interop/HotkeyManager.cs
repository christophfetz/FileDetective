using System.ComponentModel;
using System.Windows.Interop;
using FileDetective.Core;

namespace FileDetective.App.Interop;

/// <summary>
/// Registers the global hotkey with <c>RegisterHotKey</c> on an invisible message-only window (PLAN.md 8.3). The
/// combination can be changed at run time (<see cref="TryRegister"/>); if the new one is refused, the old one stays.
/// Must be created on the UI thread.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 1;

    private readonly HwndSource _source;
    private HotkeyGesture? _current;
    private bool _registered;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("FileDetectiveHotkey")
        {
            ParentWindow = NativeMethods.HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Raised on the UI thread when the hotkey is pressed.</summary>
    public event Action? Pressed;

    /// <summary>The combination in force, or null if none could be registered yet.</summary>
    public HotkeyGesture? Current => _current;

    /// <summary>
    /// Registers a combination with <c>MOD_NOREPEAT</c> and replaces the previous one. Returns false and the Win32 error
    /// text if another program owns the combination; the previous registration is then restored.
    /// </summary>
    public bool TryRegister(HotkeyGesture gesture, out string error)
    {
        var previous = _current;
        Unregister();
        if (TryRegisterCore(gesture, out error))
        {
            _current = gesture;
            return true;
        }

        if (previous is { } old && TryRegisterCore(old, out _))
        {
            _current = old;
        }
        else
        {
            _current = null;
        }

        return false;
    }

    /// <summary>
    /// Lets go of the combination for a moment (the settings window records a new one and the old combination must not
    /// open the search bar meanwhile). <see cref="Resume"/> takes it back.
    /// </summary>
    public void Suspend() => Unregister();

    public void Resume()
    {
        if (_current is { } gesture && !_registered)
        {
            TryRegisterCore(gesture, out _);
        }
    }

    private bool TryRegisterCore(HotkeyGesture gesture, out string error)
    {
        uint modifiers = (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT;
        if (NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers, gesture.VirtualKey))
        {
            _registered = true;
            error = string.Empty;
            return true;
        }

        error = new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message;
        return false;
    }

    private void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
