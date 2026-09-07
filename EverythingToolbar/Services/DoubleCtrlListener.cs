using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using EverythingToolbar.Helpers;
using NLog;
using Windows.Win32;

namespace EverythingToolbar.Services
{
    /// <summary>
    /// Double-Ctrl open listener. Uses the dedicated-thread <see cref="LowLevelKeyboardHook"/>
    /// and a SwiftList-style double-tap detector (release required, 100–500 ms window).
    /// </summary>
    public sealed class DoubleCtrlListener : IDisposable
    {
        private const int VkLbutton = 0x01;
        private const int VkRbutton = 0x02;
        private const int VkMbutton = 0x04;
        private const int VkXbutton1 = 0x05;
        private const int VkXbutton2 = 0x06;
        private const int VkLcontrol = 0xA2;
        private const int VkRcontrol = 0xA3;
        private const int VkControl = 0x11;

        private static readonly ILogger Logger = ToolbarLogger.GetLogger<DoubleCtrlListener>();

        private readonly ISettings _settings;
        private readonly DoubleCtrlDetector _detector = new();
        private readonly LowLevelKeyboardHook _keyboardHook;

        private Action? _handler;
        private Dispatcher? _dispatcher;
        private bool _mouseWasDown;
        private bool _installed;
        private Timer? _debugHeartbeatTimer;

        public DoubleCtrlListener(ISettings settings)
        {
            _settings = settings;
            _keyboardHook = new LowLevelKeyboardHook(OnKeyEvent, CheckForMissingCtrlRelease);
            _settings.PropertyChanged += OnSettingsChanged;
        }

        public void Initialize(Action handler)
        {
            _handler = handler;
            _dispatcher = Dispatcher.CurrentDispatcher;
            Logger.Debug(
                "DoubleCtrl initialized: enabled={0}, uiThread={1}.",
                _settings.IsDoubleCtrlOpenSearchWindow,
                _dispatcher.Thread.ManagedThreadId
            );
            UpdateHook();
            UpdateDebugDiagnostics();
        }

        /// <summary>Re-evaluate install state without replacing the open-search handler.</summary>
        public void Refresh(string reason = "requested")
        {
            Logger.Debug("DoubleCtrl refresh: reason={0}, installed={1}.", reason, _installed);
            if (_dispatcher == null)
                _dispatcher = Dispatcher.CurrentDispatcher;
            UpdateHook();
            UpdateDebugDiagnostics();
        }

        public void Disable(string reason = "requested")
        {
            Logger.Debug("DoubleCtrl disable: reason={0}, installed={1}.", reason, _installed);
            if (!_installed)
                return;

            _keyboardHook.Uninstall();
            _detector.Reset();
            _mouseWasDown = false;
            _installed = false;
        }

        public void Dispose()
        {
            _settings.PropertyChanged -= OnSettingsChanged;
            _debugHeartbeatTimer?.Dispose();
            _debugHeartbeatTimer = null;
            Disable("disposed");
            _keyboardHook.Dispose();
        }

        private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ISettings.IsDebugLoggingEnabled))
            {
                _dispatcher?.BeginInvoke(UpdateDebugDiagnostics);
                return;
            }

            if (
                e.PropertyName
                is nameof(ISettings.IsDoubleCtrlOpenSearchWindow)
                    or nameof(ISettings.DoubleCtrlProcessBlacklist)
            )
            {
                Logger.Debug("DoubleCtrl setting changed: {0}; scheduling hook update.", e.PropertyName);
                _dispatcher?.BeginInvoke(UpdateHook);
            }
        }

        private void UpdateHook()
        {
            if (_handler == null)
            {
                Logger.Debug("DoubleCtrl hook update skipped: search handler not initialized.");
                return;
            }

            if (_settings.IsDoubleCtrlOpenSearchWindow)
            {
                if (_installed)
                {
                    Logger.Debug(
                        "DoubleCtrl hook update skipped: listener already installed, hookTracked={0}.",
                        _keyboardHook.IsInstalled
                    );
                    return;
                }

                _detector.Reset();
                _keyboardHook.Install();
                _installed = _keyboardHook.IsInstalled;
                Logger.Debug("DoubleCtrl hook install result: installed={0}.", _installed);
            }
            else
            {
                Disable("double Ctrl setting disabled");
            }
        }

        private void UpdateDebugDiagnostics()
        {
            if (!Logger.IsDebugEnabled)
            {
                _debugHeartbeatTimer?.Dispose();
                _debugHeartbeatTimer = null;
                return;
            }

            if (_debugHeartbeatTimer != null)
                return;

            Logger.Debug(
                "DoubleCtrl diagnostics enabled: requiredIntervalMs=({0}, {1}) exclusive; injected Ctrl is ignored; ordinary key codes/text are not recorded. Heartbeat every 30 seconds distinguishes a disabled listener from missing hook callbacks; other software can suppress events before this hook sees them.",
                DoubleCtrlDetector.MinIntervalMs,
                DoubleCtrlDetector.MaxIntervalMs
            );
            Logger.Debug(
                "DoubleCtrl missing-release recovery: active independently of debug logging; checks run outside hook callbacks after {0} ms without a Ctrl event, require two released-state observations at least {1} ms apart, and discard the expired sequence.",
                DoubleCtrlDetector.MaxIntervalMs,
                DoubleCtrlDetector.ReleaseConfirmationMs
            );
            // Independent of both the hook's message loop and the UI dispatcher. The heartbeat
            // must still be written when either of those threads stops making progress.
            _debugHeartbeatTimer = new Timer(_ => LogHeartbeat(), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }

        private void LogHeartbeat()
        {
            if (!Logger.IsDebugEnabled)
                return;

            try
            {
                Logger.Debug(
                    "DoubleCtrl heartbeat: enabled={0}, listenerInstalled={1}, handlerAttached={2}, dispatcherAttached={3}, uiThreadAlive={4}, shutdownStarted={5}, shutdownFinished={6}, detectedTriggers={7}, droppedDebugEvents={8}, waitingForCtrlRelease={9}, systemLeftCtrlDown={10}, systemRightCtrlDown={11}.",
                    _settings.IsDoubleCtrlOpenSearchWindow,
                    _installed,
                    _handler != null,
                    _dispatcher != null,
                    _dispatcher?.Thread.IsAlive,
                    _dispatcher?.HasShutdownStarted,
                    _dispatcher?.HasShutdownFinished,
                    _detector.TriggerCount,
                    ToolbarLogger.DroppedDebugEvents,
                    _detector.IsWaitingForRelease,
                    IsKeyDown(VkLcontrol),
                    IsKeyDown(VkRcontrol)
                );
                _keyboardHook.LogDiagnostics();
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "DoubleCtrl heartbeat failed.");
            }
        }

        private bool OnKeyEvent(int vk, bool isDown, bool isInjected)
        {
            try
            {
                if (!_settings.IsDoubleCtrlOpenSearchWindow || isInjected)
                {
                    if (isInjected && IsCtrlVk(vk))
                        _detector.OnInjectedCtrlEvent(Environment.TickCount64);

                    if (IsCtrlVk(vk) && Logger.IsDebugEnabled)
                    {
                        Logger.Debug(
                            "DoubleCtrl Ctrl event ignored: vk=0x{0:X2}, down={1}, enabled={2}, injected={3}, pendingTap={4}. Injected releases do not release the detector.",
                            vk,
                            isDown,
                            _settings.IsDoubleCtrlOpenSearchWindow,
                            isInjected,
                            _detector.HasPendingTap
                        );
                    }
                    return false;
                }

                // Mouse chord while double-tapping Ctrl should cancel the sequence (same idea as
                // SwiftList ResetOnOtherKey).
                if (SyncMouseButtonsAndDetectChange())
                {
                    if (_detector.HasPendingTap || IsCtrlVk(vk))
                        Logger.Debug(
                            "DoubleCtrl sequence cancelled by mouse button state change: mouseDown={0}, pendingTap={1}.",
                            _mouseWasDown,
                            _detector.HasPendingTap
                        );
                    _detector.ResetOnOtherInput();
                }

                if (IsCtrlVk(vk))
                {
                    if (isDown)
                    {
                        var now = Environment.TickCount64;
                        var triggered = _detector.OnCtrlKeyDown(now);
                        _keyboardHook.ScheduleStateCheck(DoubleCtrlDetector.MaxIntervalMs);
                        if (triggered && CanTrigger(_detector.TriggerCount))
                            QueueTrigger(_detector.TriggerCount);
                    }
                    else
                    {
                        _keyboardHook.CancelStateCheck();
                        _detector.OnCtrlKeyUp();
                    }

                    return false; // never swallow Ctrl
                }

                // Any other key breaks an in-progress double-tap (SwiftList ResetOnOtherKey).
                if (isDown)
                {
                    if (_detector.HasPendingTap)
                        Logger.Debug("DoubleCtrl sequence cancelled by a non-Ctrl key down (key not recorded).");
                    _detector.ResetOnOtherInput();
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in DoubleCtrl keyboard hook.");
                return false;
            }
        }

        private static bool IsCtrlVk(int vk) => vk is VkLcontrol or VkRcontrol or VkControl;

        private void CheckForMissingCtrlRelease()
        {
            if (!_settings.IsDoubleCtrlOpenSearchWindow || !_detector.IsWaitingForRelease)
                return;

            // Runs on the hook thread's message loop, not inside WH_KEYBOARD_LL: the current
            // key event has not updated GetAsyncKeyState while that hook callback is running.
            // Keeping both paths on the same thread also serializes detector state changes.
            if (
                !_detector.TryRecoverMissingRelease(
                    Environment.TickCount64,
                    IsKeyDown(VkLcontrol),
                    IsKeyDown(VkRcontrol)
                )
            )
                _keyboardHook.ScheduleStateCheck(_detector.ReleaseCheckDelayMs);
        }

        private void QueueTrigger(int triggerId)
        {
            var dispatcher = _dispatcher;
            if (dispatcher == null)
            {
                Logger.Debug("DoubleCtrl trigger={0} dropped: UI dispatcher is missing.", triggerId);
                return;
            }

            var queuedAt = Environment.TickCount64;
            Logger.Debug(
                "DoubleCtrl trigger={0} queueing UI handler: shutdownStarted={1}, shutdownFinished={2}.",
                triggerId,
                dispatcher.HasShutdownStarted,
                dispatcher.HasShutdownFinished
            );
            var operation = dispatcher.BeginInvoke(() =>
            {
                using var scope = Logger.IsDebugEnabled
                    ? ScopeContext.PushProperty("DoubleCtrlTrigger", triggerId)
                    : null;
                var startedAt = Environment.TickCount64;
                Logger.Debug(
                    "DoubleCtrl trigger={0} UI handler started: queueDelayMs={1}, handlerAttached={2}, enabledNow={3}, installedNow={4}.",
                    triggerId,
                    startedAt - queuedAt,
                    _handler != null,
                    _settings.IsDoubleCtrlOpenSearchWindow,
                    _installed
                );
                if (_handler == null)
                {
                    Logger.Debug("DoubleCtrl trigger={0} dropped: search handler is missing.", triggerId);
                    return;
                }

                try
                {
                    _handler.Invoke();
                    Logger.Debug(
                        "DoubleCtrl trigger={0} UI handler returned: durationMs={1} (window visibility is logged separately).",
                        triggerId,
                        Environment.TickCount64 - startedAt
                    );
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "DoubleCtrl trigger={0} UI handler failed.", triggerId);
                    throw;
                }
            });

            if (Logger.IsDebugEnabled)
            {
                operation.Aborted += (_, _) => Logger.Debug("DoubleCtrl trigger={0} UI dispatch aborted.", triggerId);
                Logger.Debug("DoubleCtrl trigger={0} UI dispatch submitted: status={1}.", triggerId, operation.Status);
            }
        }

        /// <returns>True when mouse button pressed-state changed (down edge or up edge).</returns>
        private bool SyncMouseButtonsAndDetectChange()
        {
            var mouseDown =
                IsKeyDown(VkLbutton)
                || IsKeyDown(VkRbutton)
                || IsKeyDown(VkMbutton)
                || IsKeyDown(VkXbutton1)
                || IsKeyDown(VkXbutton2);

            var changed = mouseDown != _mouseWasDown;
            _mouseWasDown = mouseDown;
            return changed;
        }

        private bool CanTrigger(int triggerId)
        {
            var blacklist = _settings.DoubleCtrlProcessBlacklist;
            if (string.IsNullOrWhiteSpace(blacklist))
            {
                Logger.Debug("DoubleCtrl trigger={0} allowed: process blacklist is empty.", triggerId);
                return true;
            }

            try
            {
                var foreground = NativeMethods.GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                {
                    Logger.Debug(
                        "DoubleCtrl trigger={0} allowed: foreground window unavailable for blacklist check.",
                        triggerId
                    );
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
                if (pid == 0)
                {
                    Logger.Debug(
                        "DoubleCtrl trigger={0} allowed: foreground process ID unavailable, hwnd={1}.",
                        triggerId,
                        foreground
                    );
                    return true;
                }

                using var process = Process.GetProcessById((int)pid);
                var name = process.ProcessName;
                foreach (
                    var entry in blacklist.Split(
                        new char[] { ',', ';', ' ' },
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                    )
                )
                {
                    var candidate = entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? entry[..^4] : entry;
                    if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug(
                            "DoubleCtrl trigger={0} blocked by process blacklist: process={1}, pid={2}, hwnd={3}, matchedEntry={4}.",
                            triggerId,
                            name,
                            pid,
                            foreground,
                            entry
                        );
                        return false;
                    }
                }
                Logger.Debug(
                    "DoubleCtrl trigger={0} allowed by process blacklist: process={1}, pid={2}, hwnd={3}.",
                    triggerId,
                    name,
                    pid,
                    foreground
                );
            }
            catch (Exception ex)
            {
                Logger.Debug(
                    ex,
                    "DoubleCtrl trigger={0} allowed after process blacklist evaluation failed.",
                    triggerId
                );
            }

            return true;
        }

        private static bool IsKeyDown(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;
    }
}
