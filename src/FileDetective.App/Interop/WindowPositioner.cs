using System.Windows;
using System.Windows.Interop;
using FileDetective.Core;

namespace FileDetective.App.Interop;

/// <summary>Places the search bar on the monitor under the mouse pointer and brings it to the foreground.</summary>
internal static class WindowPositioner
{
    /// <summary>Tallest the bar can become, in device-independent units (search row + 8 list rows + footer + margins).</summary>
    public const double ReservedHeightDip = 520;

    /// <summary>
    /// Moves the window to the monitor under the mouse pointer and centers it in the upper part of the work area.
    /// All arithmetic is in device pixels, so mixed-DPI setups work: the window is first moved onto the target
    /// monitor (WPF then rescales it to that monitor's DPI) and its pixel size is read afterwards.
    /// </summary>
    public static void PlaceOnMonitorUnderCursor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var (work, dpi) = NativeMethods.GetMonitorAt(cursor);

        // Step 1: bring the window onto the target monitor so that its DPI (and with it its pixel size) is right.
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, work.Left + 40, work.Top + 40, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // Step 2: compute the final position from the real pixel width.
        NativeMethods.GetWindowRect(hwnd, out var rect);
        int widthPx = rect.Right - rect.Left;
        int reservedPx = (int)Math.Ceiling(ReservedHeightDip * dpi / 96.0);
        var (x, y) = BarPlacement.Compute(
            new PixelRect(work.Left, work.Top, work.Right, work.Bottom), widthPx, reservedPx);
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Windows often refuses <c>SetForegroundWindow</c> for a process that is not in the foreground. Attaching the
    /// input queue of the current foreground thread for a moment lifts that restriction (known technique).
    /// </summary>
    public static void ForceForeground(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == hwnd)
        {
            return;
        }

        uint foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        uint currentThread = NativeMethods.GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread
            && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }
}
