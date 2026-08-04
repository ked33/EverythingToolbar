using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EverythingToolbar.Controls;
using EverythingToolbar.ViewModels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace EverythingToolbar
{
    public sealed record ShowOptions(bool Activate, bool AtCursor);

    public sealed class ShowingEventArgs : EventArgs
    {
        public ShowingEventArgs(bool atCursor) => AtCursor = atCursor;

        public bool AtCursor { get; }
    }

    public partial class SearchWindow
    {
        public event EventHandler<EventArgs>? Hiding;
        public event EventHandler<EventArgs>? Hidden;
        public event EventHandler<ShowingEventArgs>? Showing;

        private bool _isFirstShow = true;
        private bool _isHiding;
        private int _pendingSuppressedAltSystemChars;
        private readonly SearchWindowViewModel _viewModel;
        private readonly SearchWindowController _controller;
        private readonly SearchWindowAnimator _animator;

        public SearchWindow(SearchWindowViewModel viewModel, SearchWindowController controller)
            : base(viewModel.ThemeService, viewModel.WindowsPolicy)
        {
            _viewModel = viewModel;
            _controller = controller;
            InitializeComponent();

            SourceInitialized += OnSearchWindowSourceInitialized;

            _animator = new SearchWindowAnimator(
                this,
                ContentGrid,
                SetTopmostBelowTaskbar,
                OnHidden,
                () => _viewModel.AnimationsDisabled,
                () => _viewModel.IsWindows11OrGreater
            );
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Alt+1..9,0 selects filter tabs; same shortcut again returns to "All".
            if (key is >= Key.D0 and <= Key.D9 && Keyboard.Modifiers == ModifierKeys.Alt)
            {
                var index = key == Key.D0 ? 9 : key - Key.D1;
                _viewModel.SelectFilterFromIndex(index);
                SuppressNextAltSystemChar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Keyboard.ClearFocus();
                _controller.Dismiss();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Space)
            {
                e.Handled = true;
            }
        }

        private void SuppressNextAltSystemChar() => _pendingSuppressedAltSystemChars++;

        private void OnSearchWindowSourceInitialized(object? sender, EventArgs e)
        {
            if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
                hwndSource.AddHook(SuppressHandledAltShortcutSystemChar);
        }

        private IntPtr SuppressHandledAltShortcutSystemChar(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled
        )
        {
            const int wmSyschar = 0x0106;

            if (msg == wmSyschar && _pendingSuppressedAltSystemChars > 0)
            {
                _pendingSuppressedAltSystemChars--;
                handled = true;
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        private void OnLostKeyboardFocus(object? sender, KeyboardFocusChangedEventArgs e)
        {
            _pendingSuppressedAltSystemChars = 0;

            if (e.NewFocus == null) // New focus outside application
            {
                _controller.NotifyFocusLostToOutside();
            }
        }

        private void OpenSearchInEverything(object? sender, RoutedEventArgs e)
        {
            _viewModel.OpenSearchInEverything();
        }

        internal void Show(ShowOptions options)
        {
            if (Visibility == Visibility.Visible && !_isHiding)
            {
                if (options.Activate)
                    ActivateAndBringToFront();

                return;
            }

            _isHiding = false;

            if (_isFirstShow)
                PreWarm();

            ShowActivated = options.Activate;
            base.Show();

            if (options.Activate)
            {
                Dispatcher.BeginInvoke(new Action(ActivateAndBringToFront), DispatcherPriority.Input);
            }

            Showing?.Invoke(this, new ShowingEventArgs(options.AtCursor));
        }

        internal void HideAnimated()
        {
            if (Visibility != Visibility.Visible || _isHiding)
                return;

            _isHiding = true;
            Hiding?.Invoke(this, EventArgs.Empty);
        }

        internal void FocusSearchBox()
        {
            SearchBox.Focus();
        }

        private void OnHidden()
        {
            _isHiding = false;
            _viewModel.SavePopupSize((int)Width, (int)Height);

            // Push outside of screens to hide Windows' closing animation
            _animator.ClearAnimations();
            Top = 100000;
            Left = 100000;

            base.Hide();

            _animator.UnhookRendering();

            Dispatcher.BeginInvoke(
                () =>
                {
                    if (Visibility != Visibility.Visible)
                        _viewModel.ResetSearch();
                },
                DispatcherPriority.ApplicationIdle
            );

            Hidden?.Invoke(this, EventArgs.Empty);
        }

        internal void PreWarm()
        {
            if (!_isFirstShow || Visibility == Visibility.Visible)
                return;

            _isFirstShow = false;

            // Park off-screen so the warm-up show can never flash on screen.
            Top = 100000;
            Left = 100000;

            Width = Math.Max(_viewModel.PopupWidth, MinWidth);
            Height = Math.Max(_viewModel.PopupHeight, MinHeight);

            ShowActivated = false;
            base.Show(); // Intentionally without firing Showing
            UpdateLayout();
            base.Hide(); // Intentionally without firing Hiding
        }

        private void ActivateAndBringToFront()
        {
            var hwnd = new WindowInteropHelper(this).Handle;

            Activate();
            NativeMethods.ForciblySetForegroundWindow(hwnd);
        }

        public void AnimateShow(double left, double top, double width, double height, Edge taskbarEdge)
        {
            _animator.AnimateShow(left, top, width, height, taskbarEdge);
        }

        public void AnimateHide(Edge taskbarEdge)
        {
            _animator.AnimateHide(taskbarEdge);
        }

        private void SetTopmostBelowTaskbar()
        {
            const int hwndTopmost = -1;

            const SET_WINDOW_POS_FLAGS flags =
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
                | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;

            var hwnd = new WindowInteropHelper(this).Handle;
            var taskbarHwnd = NativeMethods.FindTaskbarHandle();

            PInvoke.SetWindowPos((HWND)hwnd, (HWND)(IntPtr)hwndTopmost, 0, 0, 0, 0, flags);

            // The taskbar should always be above the search window
            if (taskbarHwnd != IntPtr.Zero)
                PInvoke.SetWindowPos((HWND)taskbarHwnd, (HWND)(IntPtr)hwndTopmost, 0, 0, 0, 0, flags);
        }
    }
}
