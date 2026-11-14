using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using FileDetective.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace FileDetective.App;

/// <summary>
/// Read-only box that records the next key combination while it has the focus ("press the new shortcut"). Shows the
/// combination in the language of the interface. Tab and Shift+Tab still move the focus.
/// </summary>
public sealed class HotkeyCaptureBox : TextBox
{
    public HotkeyCaptureBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = System.Windows.Input.Cursors.Hand;
        IsUndoEnabled = false;
        ContextMenu = null;
    }

    /// <summary>A valid combination was pressed.</summary>
    internal event Action<HotkeyGesture>? Captured;

    /// <summary>A combination was pressed that cannot be a hotkey (no Alt/Ctrl/Win, or an unsupported key).</summary>
    internal event Action? Rejected;

    internal void Show(HotkeyGesture gesture) => Text = gesture.Format(CultureInfo.CurrentUICulture);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };

        var modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && (modifiers & (ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Windows)) == 0)
        {
            return; // leave the box
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None)
        {
            return; // only modifiers so far
        }

        var mapped = HotkeyModifiers.None;
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            mapped |= HotkeyModifiers.Alt;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            mapped |= HotkeyModifiers.Control;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            mapped |= HotkeyModifiers.Shift;
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            mapped |= HotkeyModifiers.Win;
        }

        var gesture = new HotkeyGesture(mapped, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (gesture.IsValid)
        {
            Captured?.Invoke(gesture);
        }
        else
        {
            Rejected?.Invoke();
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e) => e.Handled = true;
}
