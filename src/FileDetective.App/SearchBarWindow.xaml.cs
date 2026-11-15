using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileDetective.App.Interop;
using FileDetective.App.ViewModels;
using FileDetective.Core;

namespace FileDetective.App;

/// <summary>
/// The search bar (PLAN.md 8.4). Created once at program start and then only shown and hidden. Handles keys and
/// window behaviour; everything else is in <see cref="SearchBarViewModel"/>.
/// </summary>
public partial class SearchBarWindow : Window
{
    private const int PageSize = 8;

    private readonly SearchBarViewModel _viewModel;
    private readonly DispatcherTimer _progressTimer;
    private bool _menuOpen;

    internal SearchBarWindow(SearchBarViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Title = Strings.Get("Bar_Title");
        OpenMenuItem.Header = Strings.Get("Ctx_Open");
        OpenWithMenuItem.Header = Strings.Get("Ctx_OpenWith");
        RevealMenuItem.Header = Strings.Get("Ctx_Reveal");
        CopyMenuItem.Header = Strings.Get("Ctx_CopyPath");
        CopyImageMenuItem.Header = Strings.Get("Ctx_CopyImage");
        CopyTextMenuItem.Header = Strings.Get("Ctx_CopyText");

        // Create the window handle now so that showing the bar later is fast (no handle creation on first use).
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _progressTimer.Tick += (_, _) => _viewModel.Tick();

        viewModel.CloseRequested += HideBar;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Deactivated += OnDeactivated;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _progressTimer.Start();
            }
            else
            {
                _progressTimer.Stop();
            }
        };

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseUp += (_, _) => FocusInput();
        HitList.MouseDoubleClick += OnHitDoubleClick;
        HitList.PreviewMouseRightButtonDown += OnHitRightButtonDown;
        HitList.ContextMenuOpening += (_, e) => OnHitListContextMenuOpening(e);
        HitMenu.Opened += (_, _) => _menuOpen = true;
        HitMenu.Closed += (_, _) =>
        {
            _menuOpen = false;
            FocusInput();
        };
        OpenMenuItem.Click += async (_, _) => await _viewModel.OpenSelectedAsync();
        OpenWithMenuItem.Click += async (_, _) => await _viewModel.OpenSelectedWithAsync();
        RevealMenuItem.Click += async (_, _) => await _viewModel.RevealSelectedAsync();
        CopyMenuItem.Click += (_, _) => _viewModel.CopySelectedPath();
        CopyImageMenuItem.Click += (_, _) => _viewModel.CopySelectedImage();
        CopyTextMenuItem.Click += async (_, _) => await _viewModel.CopySelectedTextAsync();
        HintEnableButton.Click += (_, _) =>
        {
            _viewModel.EnableImageText();
            FocusInput();
        };
        HintDismissButton.Click += (_, _) =>
        {
            _viewModel.DismissImageTextHint();
            FocusInput();
        };
        DpiChanged += (_, e) => _viewModel.PixelsPerDip = e.NewDpi.PixelsPerDip;
        Loaded += (_, _) => _viewModel.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
    }

    /// <summary>Toggles the bar: shown on the monitor under the mouse pointer with the focus in the input box, or hidden.</summary>
    public void ToggleBar()
    {
        if (IsVisible)
        {
            HideBar();
        }
        else
        {
            ShowBar();
        }
    }

    public void ShowBar()
    {
        if (IsVisible)
        {
            Activate();
            FocusInput();
            return;
        }

        try
        {
            WindowPositioner.PlaceOnMonitorUnderCursor(this);
        }
        catch (Exception ex)
        {
            // Placement is cosmetic; the bar still has to appear.
            FileLog.Shared.Error("Placing the search bar failed", ex);
        }

        _viewModel.OnShown();
        Show();

        // Show may rescale the window (different monitor DPI): center once more with the final pixel size.
        try
        {
            WindowPositioner.PlaceOnMonitorUnderCursor(this);
        }
        catch (Exception ex)
        {
            FileLog.Shared.Error("Placing the search bar failed", ex);
        }

        // Windows often denies the foreground to a program that is not in front: Activate alone is not reliable.
        Activate();
        WindowPositioner.ForceForeground(this);
        FocusInput();
        InputBox.SelectAll();

        // Focus once more after the window has finished appearing.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsVisible)
            {
                FocusInput();
                InputBox.SelectAll();
            }
        });
    }

    public void HideBar()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    private void FocusInput()
    {
        if (!IsVisible || _menuOpen)
        {
            return;
        }

        InputBox.Focus();
        Keyboard.Focus(InputBox);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // A context menu is a popup of this window and does not count as losing the focus.
        if (!_menuOpen)
        {
            HideBar();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchBarViewModel.SelectedIndex) && _viewModel.SelectedItem is { } item)
        {
            HitList.ScrollIntoView(item);
        }
    }

    private async void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;

        // With Alt held, the key arrives as Key.System; the real key is in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                HideBar();
                break;

            case Key.Down:
                e.Handled = true;
                _viewModel.MoveSelection(1);
                break;

            case Key.Up:
                e.Handled = true;
                _viewModel.MoveSelection(-1);
                break;

            case Key.PageDown:
                e.Handled = true;
                _viewModel.MoveSelection(PageSize);
                break;

            case Key.PageUp:
                e.Handled = true;
                _viewModel.MoveSelection(-PageSize);
                break;

            case Key.Enter when !alt:
                e.Handled = true;
                if (ctrl)
                {
                    await _viewModel.RevealSelectedAsync();
                }
                else
                {
                    await _viewModel.OpenSelectedAsync();
                }

                break;

            case Key.Tab:
                e.Handled = true;
                _viewModel.CycleMode(reverse: shift);
                break;

            case Key.C when ctrl && shift && !alt && _viewModel.SelectedIsImage:
                // Mode Image text: Ctrl+Shift+C copies the recognized text (PLAN.md 11.3).
                e.Handled = true;
                await _viewModel.CopySelectedTextAsync();
                break;

            case Key.C when ctrl && !shift && !alt && InputBox.SelectionLength == 0 && _viewModel.SelectedItem is not null:
                // Only when no text is selected in the input box; otherwise the box copies its selection as usual.
                // Mode Image text copies the image itself.
                e.Handled = true;
                if (_viewModel.SelectedIsImage)
                {
                    _viewModel.CopySelectedImage();
                }
                else
                {
                    _viewModel.CopySelectedPath();
                }

                break;
        }
    }

    private async void OnHitDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource as DependencyObject) is not null && e.ChangedButton == MouseButton.Left)
        {
            await _viewModel.OpenSelectedAsync();
        }
    }

    private void OnHitRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right click selects the row under the pointer so that the menu acts on it.
        if (ItemUnder(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
        }
    }

    private void OnHitListContextMenuOpening(ContextMenuEventArgs e)
    {
        if (_viewModel.SelectedItem is null)
        {
            e.Handled = true; // nothing under the pointer: no menu
            return;
        }

        // PLAN.md 8.4: hits of an unreachable location can only have their path copied.
        bool canOpen = _viewModel.CanOpenSelected;
        OpenMenuItem.IsEnabled = canOpen;
        OpenWithMenuItem.IsEnabled = canOpen;
        OpenWithMenuItem.Visibility = _viewModel.SelectedItem.IsDirectory ? Visibility.Collapsed : Visibility.Visible;
        RevealMenuItem.IsEnabled = canOpen;
        var imageItems = _viewModel.SelectedIsImage ? Visibility.Visible : Visibility.Collapsed;
        CopyImageMenuItem.Visibility = imageItems;
        CopyTextMenuItem.Visibility = imageItems;
    }

    private static ListBoxItem? ItemUnder(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem)
        {
            source = VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
        }

        return source as ListBoxItem;
    }
}
