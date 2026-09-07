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
    /// Double-Ctrl open listener. Observes complete physical taps and intervening input
    /// on the dedicated <see cref="LowLevelKeyboardHook"/> thread.
    /// </summary>
    public sealed class DoubleCtrlListener : IDisposable
    {
        private const int VkLcontrol = 0xA2;
        private const int VkRcontrol = 0xA3;
        private const int VkControl = 0x11;

        private static readonly ILogger Logger = ToolbarLogger.GetLogger<DoubleCtrlListener>();

        private readonly ISettings _settings;
        private readonly DoubleCtrlDetector _detector = new();
        private readonly LowLevelKeyboardHook _keyboardHook;

        private Action? _handler;
        private Dispatcher? _dispatcher;
        private bool _installed;
        private bool _suspended;
        private int _generation;
        private Timer? _debugHeartbeatTimer;

        public DoubleCtrlListener(ISettings settings)
        {
            _settings = settings;
            _keyboardHook = new LowLevelKeyboardHook(OnKeyEvent, CheckForMissingCtrlRelease, OnMouseInput);
            _settings.PropertyChanged += OnSettingsChanged;
        }

        public void Initialize(Action handler)
        {
            _handler = handler;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _suspended = false;
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
            _suspended = false;
            UpdateHook();
            UpdateDebugDiagnostics();
        }

        public void Disable(string reason = "requested")
        {
            _suspended = true;
            UninstallHook(reason);
        }

        private void UninstallHook(string reason)
        {
            Logger.Debug("DoubleCtrl disable: reason={0}, installed={1}.", reason, _installed);
            if (!_installed)
                return;

            _keyboardHook.Uninstall();
            Interlocked.Increment(ref _generation);
            _detector.Reset();
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
                    or nameof(ISettings.DoubleCtrlKeySide)
                    or nameof(ISettings.DoubleCtrlProcessBlacklist)
            )
            {
                Interlocked.Increment(ref _generation);
                Logger.Debug("DoubleCtrl setting changed: {0}; scheduling hook update.", e.PropertyName);
                _dispatcher?.BeginInvoke(() =>
                {
                    // Discard gestures and queued triggers made with the previous settings.
                    // A settings page suspension stays in effect until Refresh on unload.
                    UninstallHook("double Ctrl settings changed");
                    UpdateHook();
                });
            }
        }

        private void UpdateHook()
        {
            if (_handler == null)
            {
                Logger.Debug("DoubleCtrl hook update skipped: search handler not initialized.");
                return;
            }

            if (_suspended)
            {
                Logger.Debug("DoubleCtrl hook update skipped: listener temporarily suspended.");
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
                UninstallHook("double Ctrl setting disabled");
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
                "DoubleCtrl diagnostics enabled: same-side physical taps trigger on second release; native down intervalMs=({0}, {1}) exclusive, minimumReleaseIntervalMs={2}, maximumHoldMs={3}, keySide={4}. Other keyboard input and mouse buttons/wheels cancel; injected input never completes a tap. Ordinary key codes/text are not recorded. Heartbeat every 30 seconds; other software can suppress events before this hook sees them.",
                DoubleCtrlDetector.MinIntervalMs,
                DoubleCtrlDetector.MaxIntervalMs,
                DoubleCtrlDetector.MinReleaseIntervalMs,
                DoubleCtrlDetector.MaxHoldMs,
                _settings.DoubleCtrlKeySide
            );
            Logger.Debug(
                "DoubleCtrl missing-release recovery: active independently of debug logging; checks run outside hook callbacks after {0} ms without a Ctrl event, require two released-state observations at least {1} ms apart, and discard the expired sequence.",
                DoubleCtrlDetector.ReleaseRecoveryIdleMs,
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

        private bool OnKeyEvent(int vk, bool isDown, bool isInjected, uint eventTime)
        {
            try
            {
                if (!_settings.IsDoubleCtrlOpenSearchWindow)
                    return false;

                if (isInjected)
                {
                    if (IsCtrlVk(vk))
                    {
                        _detector.OnInjectedCtrlEvent(Environment.TickCount64);
                        Logger.Debug(
                            "DoubleCtrl injected Ctrl ignored: vk=0x{0:X2}, down={1}; gesture cancelled, physical release still required.",
                            vk,
                            isDown
                        );
                    }
                    else
                    {
                        _detector.ResetOnOtherInput("injected keyboard input");
                    }
                    return false;
                }

                if (!IsCtrlVk(vk))
                {
                    // Key-up also cancels: its down might predate hook installation or have
                    // been suppressed by another hook. Do not record ordinary key identities.
                    _detector.ResetOnOtherInput(isDown ? "non-Ctrl key down" : "non-Ctrl key up");
                    return false;
                }

                var now = Environment.TickCount64;
                var triggered = false;
                if (isDown)
                {
                    _detector.OnCtrlKeyDown(vk, eventTime, now, IsAllowedCtrlSide(vk) && !IsOtherInputDown(vk));
                }
                else
                {
                    if (_detector.HasPendingTap && IsOtherInputDown(vk))
                        _detector.ResetOnOtherInput("another input is held at Ctrl release");
                    triggered = _detector.OnCtrlKeyUp(vk, eventTime, now);
                }

                if (_detector.IsWaitingForRelease)
                    _keyboardHook.ScheduleStateCheck(DoubleCtrlDetector.ReleaseRecoveryIdleMs);
                else
                    _keyboardHook.CancelStateCheck();

                if (triggered)
                    QueueTrigger(_detector.TriggerCount, NativeMethods.GetForegroundWindow());

                return false; // never swallow Ctrl
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in DoubleCtrl keyboard hook.");
                return false;
            }
        }

        private static bool IsCtrlVk(int vk) => vk is VkLcontrol or VkRcontrol or VkControl;

        private bool IsAllowedCtrlSide(int vk) =>
            _settings.DoubleCtrlKeySide switch
            {
                "Left" => vk == VkLcontrol,
                "Right" => vk == VkRcontrol,
                _ => true,
            };

        private void OnMouseInput() => _detector.ResetOnOtherInput("mouse button or wheel");

        private static bool IsOtherInputDown(int ctrlVk)
        {
            // A key/button can already be held when the hook is installed, or another hook
            // can hide its down event. Sample only at Ctrl edges, without an idle polling loop.
            // WH_KEYBOARD_LL runs before the current event updates asynchronous key state;
            // therefore ignore this Ctrl and generic VK_CONTROL, but include the other side.
            for (var vk = 1; vk < 0xFF; vk++)
            {
                if (vk != ctrlVk && vk != VkControl && IsKeyDown(vk))
                    return true;
            }
            return false;
        }

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

        private void QueueTrigger(int triggerId, IntPtr foreground)
        {
            var dispatcher = _dispatcher;
            if (dispatcher == null)
            {
                Logger.Debug("DoubleCtrl trigger={0} dropped: UI dispatcher is missing.", triggerId);
                return;
            }

            var queuedAt = Environment.TickCount64;
            var generation = Volatile.Read(ref _generation);
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
                if (
                    _handler == null
                    || !_settings.IsDoubleCtrlOpenSearchWindow
                    || !_installed
                    || generation != _generation
                    || foreground == IntPtr.Zero
                    || NativeMethods.GetForegroundWindow() != foreground
                )
                {
                    Logger.Debug(
                        "DoubleCtrl trigger={0} dropped: handler, listener settings or foreground window changed before dispatch.",
                        triggerId
                    );
                    return;
                }

                // Process inspection runs on the UI thread, outside the time-critical hook.
                if (!CanTrigger(triggerId, foreground))
                    return;

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

        private bool CanTrigger(int triggerId, IntPtr foreground)
        {
            var blacklist = _settings.DoubleCtrlProcessBlacklist;
            if (string.IsNullOrWhiteSpace(blacklist))
            {
                Logger.Debug("DoubleCtrl trigger={0} allowed: process blacklist is empty.", triggerId);
                return true;
            }

            try
            {
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
