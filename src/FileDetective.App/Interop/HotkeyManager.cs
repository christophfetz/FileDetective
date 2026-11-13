using System.ComponentModel;
using System.Windows.Interop;

namespace FileDetective.App.Interop;

/// <summary>
/// Registers the global hotkey with <c>RegisterHotKey</c> on an invisible message-only window (PLAN.md 8.3).
/// Must be created on the UI thread.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 1;

    private readonly HwndSource _source;
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

    /// <summary>
    /// Registers Alt+Space with <c>MOD_NOREPEAT</c> (decision: fixed in stage 4, configurable in stage 5).
    /// Returns false and the Win32 error text if another program owns the combination.
    /// </summary>
    public bool TryRegisterAltSpace(out string error)
    {
        if (NativeMethods.RegisterHotKey(
                _source.Handle, HotkeyId, NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SPACE))
        {
            _registered = true;
            error = string.Empty;
            return true;
        }

        error = new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message;
        return false;
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
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
