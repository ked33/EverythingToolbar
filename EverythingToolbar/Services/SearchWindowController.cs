using System;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using NLog;

namespace EverythingToolbar.Services
{
    public sealed class SearchWindowController : ObservableObject, ISearchWindowController
    {
        private enum WindowState
        {
            Hidden,
            Visible,
            HidingAnimation,
        }

        private const double DebounceMs = 500;
        private static readonly ILogger Logger = ToolbarLogger.GetLogger<SearchWindowController>();

        private readonly SearchSession _session;
        private readonly ISettings _settings;

        private SearchWindow? _window;
        private WindowState _state = WindowState.Hidden;
        private bool _structuralIconMode;
        private bool _temporaryPopupMode;
        private DateTime _lastHideStart = DateTime.MinValue;
        private DispatcherTimer? _keepaliveTimer;
        private IntPtr _previousForegroundWindow;
        private uint _previousForegroundProcessId;
        private uint _previousForegroundThreadId;

        private Func<bool>? _toolbarBoxIsFocused;
        private Action? _toolbarBoxFocus;
        private Action? _focusSelectedResult;

        public event EventHandler? Showing;
        public event EventHandler? Hiding;
        public event EventHandler? Hidden;
        public event EventHandler<bool>? ActiveChanged;
        public event EventHandler? SearchBoxFocused;

        public bool IsIconMode => _structuralIconMode || _temporaryPopupMode;

        public SearchWindowController(SearchSession session, ISettings settings)
        {
            _session = session;
            _settings = settings;
            _settings.PropertyChanged += OnSettingsChanged;
        }

        private SearchWindow Window
        {
            get
            {
                if (_window == null)
                {
                    _window = Ioc.Default.GetRequiredService<SearchWindow>();
                    _window.Activated += OnWindowActivated;
                    _window.Deactivated += OnWindowDeactivated;
                    _window.Showing += OnWindowShowing;
                    _window.Hidden += OnWindowHidden;
                }

                return _window;
            }
        }

        public void SetIconMode(bool isIcon)
        {
            if (_structuralIconMode == isIcon)
                return;

            _structuralIconMode = isIcon;
            OnPropertyChanged(nameof(IsIconMode));
        }

        public void Show() => RunOnUi(() => ShowInternal(atCursor: false));

        public void Hide() => RunOnUi(HideInternal);

        public void Toggle() => RunOnUi(ToggleInternal);

        public void Dismiss() => RunOnUi(DismissInternal);

        public void ToggleSearchUi() =>
            RunOnUi(() =>
            {
                LogState("ToggleSearchUi requested");
                if (IsIconMode)
                {
                    Logger.Debug("Search UI toggle routed to popup window.");
                    ToggleInternal();
                }
                else if (_state != WindowState.Hidden || _toolbarBoxIsFocused?.Invoke() == true)
                {
                    Logger.Debug("Search UI toggle routed to dismiss: search is already open or focused.");
                    DismissInternal();
                }
                else
                {
                    RememberForegroundWindow();
                    Logger.Debug(
                        "Search UI toggle routed to toolbar focus: focusHandlerAttached={0}.",
                        _toolbarBoxFocus != null
                    );
                    _toolbarBoxFocus?.Invoke();
                    LogState("toolbar focus handler returned");
                }
            });

        public void TogglePopupAtCursor() =>
            RunOnUi(() =>
            {
                if (_state == WindowState.Visible)
                {
                    DismissInternal();
                    return;
                }

                // Ignore a toggle arriving right after a hide (e.g. clicking the icon to close reopens otherwise).
                if ((DateTime.Now - _lastHideStart).TotalMilliseconds < DebounceMs)
                {
                    LogState("cursor popup toggle ignored during hide debounce");
                    return;
                }

                ShowStandaloneInternal();
            });

        public void ShowStandalone() => RunOnUi(ShowStandaloneInternal);

        public void FocusSearchBox() =>
            RunOnUi(() =>
            {
                if (IsIconMode)
                    Window.FocusSearchBox();
                else
                    _toolbarBoxFocus?.Invoke();
            });

        public void FocusSelectedResult() => RunOnUi(() => _focusSelectedResult?.Invoke());

        public void PreWarm() => RunOnUi(() => Window.PreWarm());

        public void NotifyFocusLostToOutside() =>
            RunOnUi(() =>
            {
                LogState("keyboard focus lost to outside; scheduling focus check");
                // Keyboard focus leaving the window reports NewFocus == null even when it moves to our own
                // attached toolbar box, which lives in a separate top-level window. Defer the hide so focus
                // can settle (the box's GotKeyboardFocus runs right after this), then skip it if focus landed
                // in the toolbar box — the user is still interacting with us, so the window must stay open.
                Window.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (IsSearchUiFocused)
                        {
                            Logger.Debug("Search UI focus-loss hide skipped: focus remains in the search UI.");
                            return;
                        }

                        LogState("hiding after deferred outside-focus check");
                        HideInternal();
                    }),
                    DispatcherPriority.Input
                );
            });

        public void NotifySearchBoxFocused() => SearchBoxFocused?.Invoke(this, EventArgs.Empty);

        public void RegisterToolbarSearchBox(Func<bool> isFocused, Action focus)
        {
            if (_toolbarBoxFocus != null)
                Logger.Warn("A toolbar search box is already registered; overwriting.");

            _toolbarBoxIsFocused = isFocused;
            _toolbarBoxFocus = focus;
        }

        public void UnregisterToolbarSearchBox(Action focus)
        {
            if (!ReferenceEquals(_toolbarBoxFocus, focus))
                return;

            _toolbarBoxIsFocused = null;
            _toolbarBoxFocus = null;
        }

        public void RegisterResultsList(Action focusSelected)
        {
            if (_focusSelectedResult != null)
                Logger.Warn("A results list is already registered; overwriting.");

            _focusSelectedResult = focusSelected;
        }

        public void UnregisterResultsList(Action focusSelected)
        {
            if (ReferenceEquals(_focusSelectedResult, focusSelected))
                _focusSelectedResult = null;
        }

        private void ShowInternal(bool atCursor)
        {
            LogState("ShowInternal requested");
            RememberForegroundWindow();
            StopKeepaliveTimer();
            Window.Show(new ShowOptions(IsIconMode, atCursor));
            _state = WindowState.Visible;
            LogState("ShowInternal returned");

            // Restore auto-select-first when the window opens (and after results settle).
            // Clear first so a stale index from a previous session cannot linger, then select
            // index 0 when IsAutoSelectFirstResult is on and there are results.
            _session.ClearSelection();
            Window.Dispatcher.BeginInvoke(
                () => _session.AutoSelect(),
                System.Windows.Threading.DispatcherPriority.Loaded
            );
        }

        private void ShowStandaloneInternal()
        {
            if (!_structuralIconMode)
                SetTemporaryPopupMode(true);

            ShowInternal(atCursor: true);
        }

        private void HideInternal()
        {
            if (_state != WindowState.Visible)
            {
                LogState("HideInternal ignored: controller state is not Visible");
                return;
            }

            LogState("HideInternal starting");
            _state = WindowState.HidingAnimation;
            _lastHideStart = DateTime.Now;
            Window.HideAnimated();
            Hiding?.Invoke(this, EventArgs.Empty);
        }

        private bool IsSearchUiFocused =>
            _window?.IsActive == true
            || _window?.IsKeyboardFocusWithin == true
            || _toolbarBoxIsFocused?.Invoke() == true;

        private void DismissInternal()
        {
            var restoreFocus = IsSearchUiFocused;
            var previousWindow = _previousForegroundWindow;
            var previousProcessId = _previousForegroundProcessId;
            var previousThreadId = _previousForegroundThreadId;
            ForgetForegroundWindow();

            HideInternal();
            if (!restoreFocus)
                return;

            Keyboard.ClearFocus();
            // Restore while handling the dismissal, before the asynchronous hide completes.
            // A delayed restore could steal focus after the user switches to another window.
            RestoreForegroundWindow(previousWindow, previousProcessId, previousThreadId);
        }

        private void RememberForegroundWindow()
        {
            if (_state == WindowState.Visible || IsSearchUiFocused)
                return;

            var foreground = NativeMethods.GetForegroundWindow();
            if (
                foreground == IntPtr.Zero
                || foreground == new WindowInteropHelper(Window).Handle
                || foreground == NativeMethods.FindTaskbarHandle()
            )
                return;

            var threadId = NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
            if (threadId == 0 || processId == 0)
                return;

            _previousForegroundWindow = foreground;
            _previousForegroundProcessId = processId;
            _previousForegroundThreadId = threadId;
            Logger.Debug(
                "Search UI remembered foreground window: hwnd={0}, pid={1}, thread={2}.",
                foreground,
                processId,
                threadId
            );
        }

        private void ForgetForegroundWindow()
        {
            _previousForegroundWindow = IntPtr.Zero;
            _previousForegroundProcessId = 0;
            _previousForegroundThreadId = 0;
        }

        private static void RestoreForegroundWindow(IntPtr window, uint processId, uint threadId)
        {
            if (window == IntPtr.Zero)
            {
                Logger.Debug("Search UI foreground restore skipped: no previous window was captured.");
                return;
            }

            var currentThreadId = NativeMethods.GetWindowThreadProcessId(window, out var currentProcessId);
            if (currentThreadId == 0 || currentThreadId != threadId || currentProcessId != processId)
            {
                Logger.Debug(
                    "Search UI foreground restore skipped: previous window no longer matches, hwnd={0}.",
                    window
                );
                return;
            }

            NativeMethods.ForciblySetForegroundWindow(window);
            var foreground = NativeMethods.GetForegroundWindow();
            Logger.Debug(
                "Search UI foreground restore completed: target={0}, foreground={1}, restored={2}.",
                window,
                foreground,
                foreground == window
            );
        }

        private void ToggleInternal()
        {
            LogState("ToggleInternal deciding show or hide");
            if (_state == WindowState.Hidden)
                ShowInternal(atCursor: false);
            else
                DismissInternal();
        }

        private void SetTemporaryPopupMode(bool value)
        {
            if (_temporaryPopupMode == value)
                return;

            _temporaryPopupMode = value;
            OnPropertyChanged(nameof(IsIconMode));
        }

        private void OnWindowActivated(object? sender, EventArgs e)
        {
            LogState("window activated");
            ActiveChanged?.Invoke(this, true);

            if (IsIconMode)
                FocusSearchBox();
        }

        private void OnWindowDeactivated(object? sender, EventArgs e)
        {
            LogState("window deactivated");
            ActiveChanged?.Invoke(this, false);
        }

        private void OnWindowShowing(object? sender, ShowingEventArgs e)
        {
            LogState("window Showing event");
            Showing?.Invoke(this, EventArgs.Empty);
        }

        private void OnWindowHidden(object? sender, EventArgs e)
        {
            _state = WindowState.Hidden;
            ForgetForegroundWindow();
            LogState("window Hidden event");
            SetTemporaryPopupMode(false);
            StartKeepaliveTimer();
            Hidden?.Invoke(this, EventArgs.Empty);
        }

        private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ISettings.KeepaliveIntervalSeconds) && _state == WindowState.Hidden)
            {
                StartKeepaliveTimer();
            }
        }

        private void StartKeepaliveTimer()
        {
            StopKeepaliveTimer();

            var interval = Helpers.KeepaliveSettings.GetInterval(_settings);
            _keepaliveTimer = new DispatcherTimer { Interval = interval };
            _keepaliveTimer.Tick += OnKeepaliveTick;
            _keepaliveTimer.Start();
        }

        private void StopKeepaliveTimer()
        {
            if (_keepaliveTimer == null)
                return;

            _keepaliveTimer.Tick -= OnKeepaliveTick;
            _keepaliveTimer.Stop();
            _keepaliveTimer = null;
        }

        private void OnKeepaliveTick(object? sender, EventArgs e)
        {
            if (_state != WindowState.Hidden)
            {
                StopKeepaliveTimer();
                return;
            }

            // Light touch: re-assert Everything instance name so IPC stays warm after long idle.
            try
            {
                var client = Ioc.Default.GetRequiredService<IEverythingClient>();
                client.SetInstanceName(_settings.InstanceName);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Keepalive tick failed.");
            }
        }

        private void RunOnUi(Action action)
        {
            var dispatcher = Window.Dispatcher;
            if (dispatcher.CheckAccess())
                action();
            else
            {
                Logger.Debug(
                    "Search UI action queued from another thread: shutdownStarted={0}, shutdownFinished={1}.",
                    dispatcher.HasShutdownStarted,
                    dispatcher.HasShutdownFinished
                );
                dispatcher.BeginInvoke(action);
            }
        }

        private void LogState(string stage)
        {
            if (!Logger.IsDebugEnabled)
                return;

            Logger.Debug(
                "Search UI {0}: controllerState={1}, iconMode={2}, temporaryPopup={3}, toolbarFocusHandler={4}, windowCreated={5}, visible={6}, active={7}, keyboardFocusWithin={8}, sinceHideStartMs={9:F0}, foreground={10}.",
                stage,
                _state,
                IsIconMode,
                _temporaryPopupMode,
                _toolbarBoxFocus != null,
                _window != null,
                _window?.IsVisible,
                _window?.IsActive,
                _window?.IsKeyboardFocusWithin,
                _lastHideStart == DateTime.MinValue ? -1 : (DateTime.Now - _lastHideStart).TotalMilliseconds,
                NativeMethods.GetForegroundWindow()
            );
        }
    }
}
