using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace EverythingToolbar.Services
{
    public sealed class LowLevelKeyboardHook : IDisposable
    {
        private static readonly ILogger Logger = ToolbarLogger.GetLogger<LowLevelKeyboardHook>();

        private NativeMethods.LowLevelKeyboardProc? _callback;
        private IntPtr _hookId = IntPtr.Zero;
        private Thread? _hookThread;
        private uint _hookThreadId;
        private readonly string _owner;
        private readonly Action? _onStateCheck;
        private nuint _stateCheckTimerId;
        private long _debugCallbackCount;
        private long _debugPhysicalCtrlCount;
        private long _debugInjectedCtrlCount;
        private long _debugLastCallbackTick;
        private long _debugLastCtrlTick;

        private const int WhKeyboardLl = 13;
        private const int WmKeydown = 0x0100;
        private const int WmSyskeydown = 0x0104;
        private const int LlkhfInjected = 0x10;
        private const int LlkhfLowerIlInjected = 0x02;

        public LowLevelKeyboardHook(Func<int, bool, bool, bool> onKeyEvent, Action? onStateCheck = null)
        {
            OnKeyEvent = onKeyEvent ?? throw new ArgumentNullException(nameof(onKeyEvent));
            _owner = onKeyEvent.Method.DeclaringType?.Name ?? "unknown";
            _onStateCheck = onStateCheck;
        }

        public Func<int, bool, bool, bool> OnKeyEvent { get; }
        internal bool IsInstalled => _hookId != IntPtr.Zero && _hookThread?.IsAlive == true;

        public void Install()
        {
            if (_hookThread != null)
            {
                Logger.Debug(
                    "Keyboard hook install skipped: owner={0}, threadAlive={1}, handle={2}.",
                    _owner,
                    _hookThread.IsAlive,
                    _hookId
                );
                return;
            }

            Logger.Debug("Keyboard hook install requested: owner={0}.", _owner);
            Interlocked.Exchange(ref _debugCallbackCount, 0);
            Interlocked.Exchange(ref _debugPhysicalCtrlCount, 0);
            Interlocked.Exchange(ref _debugInjectedCtrlCount, 0);
            Interlocked.Exchange(ref _debugLastCallbackTick, 0);
            Interlocked.Exchange(ref _debugLastCtrlTick, 0);
            var ready = new ManualResetEventSlim(false);
            _hookThread = new Thread(() => HookThreadProc(ready))
            {
                Name = "LowLevelKeyboardHook",
                IsBackground = true,
                Priority = ThreadPriority.Highest,
            };
            _hookThread.Start();
            ready.Wait();

            if (_hookId == IntPtr.Zero)
            {
                _hookThread.Join();
                _hookThread = null;
                Logger.Error("Failed to install the low-level keyboard hook. Owner={0}.", _owner);
            }
            Logger.Debug(
                "Keyboard hook install completed: owner={0}, installed={1}, nativeThread={2}, handle={3}.",
                _owner,
                IsInstalled,
                _hookThreadId,
                _hookId
            );
        }

        public void Uninstall()
        {
            if (_hookThread == null)
                return;

            Logger.Debug(
                "Keyboard hook uninstall requested: owner={0}, nativeThread={1}, handle={2}.",
                _owner,
                _hookThreadId,
                _hookId
            );
            var posted = PInvoke.PostThreadMessage(_hookThreadId, PInvoke.WM_QUIT, default, default);
            var error = posted ? 0 : Marshal.GetLastWin32Error();
            Logger.Debug(
                "Keyboard hook quit posted: owner={0}, success={1}, win32Error={2}.",
                _owner,
                (bool)posted,
                error
            );
            _hookThread.Join();
            _hookThread = null;
            _hookThreadId = 0;
            Logger.Debug("Keyboard hook uninstall completed: owner={0}.", _owner);
        }

        public void Dispose()
        {
            Uninstall();
        }

        /// <summary>Schedule a one-shot state check. Call only on the hook thread.</summary>
        internal unsafe void ScheduleStateCheck(int delayMs)
        {
            if (_onStateCheck == null || _hookId == IntPtr.Zero)
                return;

            var timerId = PInvoke.SetTimer(HWND.Null, _stateCheckTimerId, (uint)delayMs, null);
            if (timerId == 0)
            {
                var error = Marshal.GetLastWin32Error();
                Logger.Error("Keyboard hook state check timer failed: owner={0}, win32Error={1}.", _owner, error);
                return;
            }
            _stateCheckTimerId = timerId;
        }

        /// <summary>Cancel a pending state check. Call only on the hook thread.</summary>
        internal void CancelStateCheck()
        {
            if (_stateCheckTimerId == 0)
                return;

            if (!PInvoke.KillTimer(HWND.Null, _stateCheckTimerId))
            {
                var error = Marshal.GetLastWin32Error();
                Logger.Error(
                    "Keyboard hook state check timer cancellation failed: owner={0}, win32Error={1}.",
                    _owner,
                    error
                );
            }
            _stateCheckTimerId = 0;
        }

        private void HookThreadProc(ManualResetEventSlim ready)
        {
            _hookThreadId = PInvoke.GetCurrentThreadId();

            // Ensure the thread has a message queue before Install() returns so that
            // Uninstall() can always reach it via PostThreadMessage.
            PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);

            OptOutOfPowerThrottling();

            _callback = HookCallback;
            _hookId = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _callback, IntPtr.Zero, 0);
            var installError = _hookId == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            Logger.Debug(
                "SetWindowsHookEx completed: owner={0}, nativeThread={1}, handle={2}, win32Error={3}.",
                _owner,
                _hookThreadId,
                _hookId,
                installError
            );

            ready.Set();

            if (_hookId == IntPtr.Zero)
            {
                _callback = null;
                return;
            }

            while (true)
            {
                var result = PInvoke.GetMessage(out var msg, HWND.Null, 0, 0);
                if (result.Value <= 0)
                {
                    var error = result.Value < 0 ? Marshal.GetLastWin32Error() : 0;
                    Logger.Debug(
                        "Keyboard hook message loop exited: owner={0}, result={1}, win32Error={2}.",
                        _owner,
                        result.Value,
                        error
                    );
                    break;
                }
                if (msg.message == PInvoke.WM_TIMER && msg.wParam.Value == _stateCheckTimerId)
                {
                    // Native timers repeat by default. Consume this one before the callback;
                    // the listener schedules another check only while awaiting a release.
                    CancelStateCheck();
                    try
                    {
                        _onStateCheck?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Keyboard hook state check failed: owner={0}.", _owner);
                    }
                    continue;
                }
                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }

            CancelStateCheck();
            var unhooked = NativeMethods.UnhookWindowsHookEx(_hookId);
            var unhookError = unhooked ? 0 : Marshal.GetLastWin32Error();
            Logger.Debug(
                "UnhookWindowsHookEx completed: owner={0}, handle={1}, success={2}, win32Error={3}.",
                _owner,
                _hookId,
                unhooked,
                unhookError
            );
            _hookId = IntPtr.Zero;
            _callback = null;
        }

        private unsafe void OptOutOfPowerThrottling()
        {
            // Windows 11 may put background processes into efficiency mode (EcoQoS). A
            // throttled hook thread risks missing the LowLevelHooksTimeout deadline after
            // the process has been idle, which makes Windows deliver the keystroke
            // un-swallowed as if the hook had not handled it.
            var state = new THREAD_POWER_THROTTLING_STATE
            {
                Version = PInvoke.THREAD_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PInvoke.THREAD_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0,
            };

            var applied = PInvoke.SetThreadInformation(
                PInvoke.GetCurrentThread(),
                THREAD_INFORMATION_CLASS.ThreadPowerThrottling,
                &state,
                (uint)sizeof(THREAD_POWER_THROTTLING_STATE)
            );
            var error = applied ? 0 : Marshal.GetLastWin32Error();
            Logger.Debug(
                "Keyboard hook power throttling opt-out: owner={0}, success={1}, win32Error={2}.",
                _owner,
                (bool)applied,
                error
            );
        }

        internal void LogDiagnostics()
        {
            if (!Logger.IsDebugEnabled)
                return;

            var now = Environment.TickCount64;
            var lastCallback = Interlocked.Read(ref _debugLastCallbackTick);
            var lastCtrl = Interlocked.Read(ref _debugLastCtrlTick);
            Logger.Debug(
                "Keyboard hook health: owner={0}, trackedHandle={1}, threadAlive={2}, nativeThread={3}, debugCallbacks={4}, physicalCtrlEvents={5}, injectedCtrlEvents={6}, lastCallbackAgeMs={7}, lastCtrlAgeMs={8}. A tracked handle does not prove Windows still delivers events; -1 means no event observed while debugging.",
                _owner,
                _hookId,
                _hookThread?.IsAlive == true,
                _hookThreadId,
                Interlocked.Read(ref _debugCallbackCount),
                Interlocked.Read(ref _debugPhysicalCtrlCount),
                Interlocked.Read(ref _debugInjectedCtrlCount),
                lastCallback == 0 ? -1 : now - lastCallback,
                lastCtrl == 0 ? -1 : now - lastCtrl
            );
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            var vk = Marshal.ReadInt32(lParam);
            var message = (int)wParam;
            var isDown = message is WmKeydown or WmSyskeydown;
            var flags = Marshal.ReadInt32(lParam, 8);
            var isInjected = (flags & LlkhfInjected) != 0;
            var isCtrl = vk is 0x11 or 0xA2 or 0xA3;
            var debugEnabled = Logger.IsDebugEnabled;
            var started = debugEnabled ? Stopwatch.GetTimestamp() : 0;
            uint eventTime = 0;

            if (debugEnabled)
            {
                Interlocked.Increment(ref _debugCallbackCount);
                Interlocked.Exchange(ref _debugLastCallbackTick, Environment.TickCount64);
                if (isCtrl)
                {
                    if (isInjected)
                        Interlocked.Increment(ref _debugInjectedCtrlCount);
                    else
                        Interlocked.Increment(ref _debugPhysicalCtrlCount);
                    Interlocked.Exchange(ref _debugLastCtrlTick, Environment.TickCount64);

                    eventTime = unchecked((uint)Marshal.ReadInt32(lParam, 12));
                    var deliveryDelay = unchecked((uint)Environment.TickCount - eventTime);
                    Logger.Debug(
                        "Keyboard hook Ctrl received: owner={0}, vk=0x{1:X2}, down={2}, scanCode=0x{3:X}, flags=0x{4:X}, injected={5}, lowerIntegrityInjected={6}, nativeTime={7}, deliveryDelayMs={8}.",
                        _owner,
                        vk,
                        isDown,
                        Marshal.ReadInt32(lParam, 4),
                        flags,
                        isInjected,
                        (flags & LlkhfLowerIlInjected) != 0,
                        eventTime,
                        deliveryDelay
                    );
                }
            }

            var swallow = OnKeyEvent(vk, isDown, isInjected);
            var handlerMs = debugEnabled ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0;
            var result = swallow ? (IntPtr)1 : NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            if (debugEnabled)
            {
                var totalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (isCtrl || totalMs >= 50)
                {
                    // Log no ordinary key codes or text. A nonzero downstream result can expose
                    // another hook suppressing Ctrl, but Windows does not identify that hook's process.
                    Logger.Debug(
                        "Keyboard hook completed: owner={0}, keyKind={1}, nativeTime={2}, swallowedHere={3}, downstreamSuppressed={4}, chainResult={5}, handlerMs={6:F2}, totalMs={7:F2}.",
                        _owner,
                        isCtrl ? "Ctrl" : "other (slow callback)",
                        eventTime,
                        swallow,
                        !swallow && result != IntPtr.Zero,
                        result,
                        handlerMs,
                        totalMs
                    );
                }
            }

            return result;
        }
    }
}
