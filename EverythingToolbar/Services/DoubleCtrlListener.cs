using System;
using System.Diagnostics;
using System.Windows.Threading;
using EverythingToolbar.Helpers;
using NLog;
using Windows.Win32;

namespace EverythingToolbar.Services
{
    /// <summary>
    /// Double-Ctrl open listener. Uses the same dedicated-thread LowLevelKeyboardHook pattern
    /// as GlobalShortcutListener so EcoQoS does not drop taps after long idle.
    /// Mouse buttons are sampled via GetAsyncKeyState to detect chords without a second hook type.
    /// </summary>
    public sealed class DoubleCtrlListener : IDisposable
    {
        private const long ThresholdMilliseconds = 350;
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
        private readonly DoubleCtrlDetector _detector = new(ThresholdMilliseconds);
        private readonly LowLevelKeyboardHook _keyboardHook;

        private Action? _handler;
        private Dispatcher? _dispatcher;
        private bool _mouseWasDown;
        private bool _installed;

        public DoubleCtrlListener(ISettings settings)
        {
            _settings = settings;
            _keyboardHook = new LowLevelKeyboardHook(OnKeyEvent);
            _settings.PropertyChanged += OnSettingsChanged;
        }

        public void Initialize(Action handler)
        {
            _handler = handler;
            _dispatcher = Dispatcher.CurrentDispatcher;
            UpdateHook();
        }

        /// <summary>Re-evaluate install state without replacing the open-search handler.</summary>
        public void Refresh()
        {
            if (_dispatcher == null)
                _dispatcher = Dispatcher.CurrentDispatcher;
            UpdateHook();
        }

        public void Disable()
        {
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
            Disable();
            _keyboardHook.Dispose();
        }

        private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ISettings.IsDoubleCtrlOpenSearchWindow) or nameof(ISettings.DoubleCtrlProcessBlacklist))
                _dispatcher?.BeginInvoke(UpdateHook);
        }

        private void UpdateHook()
        {
            if (_handler == null)
                return;

            if (_settings.IsDoubleCtrlOpenSearchWindow)
            {
                if (_installed)
                    return;

                _detector.Reset();
                _keyboardHook.Install();
                _installed = true;
            }
            else
            {
                Disable();
            }
        }

        private bool OnKeyEvent(int vk, bool isDown, bool isInjected)
        {
            try
            {
                if (!_settings.IsDoubleCtrlOpenSearchWindow || isInjected)
                    return false;

                SyncMouseButtons();

                var now = Environment.TickCount64;

                if (vk is VkLcontrol or VkRcontrol or VkControl)
                {
                    var isLeft = vk is VkLcontrol or VkControl;
                    if (vk == VkControl)
                    {
                        // Generic VK_CONTROL: prefer left if either side reports down on key-up sampling.
                        isLeft = true;
                    }

                    if (isDown)
                    {
                        _detector.ResetIfAnomalous();
                        var triggered = _detector.RegisterCtrlDownWithCleanup(now, isLeft, enableAutoCleanup: true);
                        if (triggered && CanTrigger())
                        {
                            _dispatcher?.BeginInvoke(() => _handler?.Invoke());
                        }
                    }
                    else
                    {
                        _detector.RegisterCtrlUp(now);
                    }

                    return false; // never swallow Ctrl
                }

                if (isDown)
                    _detector.RegisterNonCtrlKeyDown((uint)vk);
                else
                    _detector.RegisterNonCtrlKeyUp((uint)vk);

                return false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in DoubleCtrl keyboard hook.");
                return false;
            }
        }

        private void SyncMouseButtons()
        {
            var mouseDown =
                IsKeyDown(VkLbutton)
                || IsKeyDown(VkRbutton)
                || IsKeyDown(VkMbutton)
                || IsKeyDown(VkXbutton1)
                || IsKeyDown(VkXbutton2);

            if (mouseDown && !_mouseWasDown)
                _detector.RegisterMouseButtonDown();
            else if (!mouseDown && _mouseWasDown)
                _detector.RegisterMouseButtonUp();
            else if (mouseDown)
                _detector.RegisterMouseInput();

            _mouseWasDown = mouseDown;
        }

        private bool CanTrigger()
        {
            var blacklist = _settings.DoubleCtrlProcessBlacklist;
            if (string.IsNullOrWhiteSpace(blacklist))
                return true;

            try
            {
                var foreground = NativeMethods.GetForegroundWindow();
                if (foreground == IntPtr.Zero)
                    return true;

                NativeMethods.GetWindowThreadProcessId(foreground, out var pid);
                if (pid == 0)
                    return true;

                using var process = Process.GetProcessById((int)pid);
                var name = process.ProcessName;
                foreach (var entry in blacklist.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var candidate = entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? entry[..^4]
                        : entry;
                    if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to evaluate DoubleCtrl process blacklist.");
            }

            return true;
        }

        private static bool IsKeyDown(int vk) => (PInvoke.GetAsyncKeyState(vk) & 0x8000) != 0;
    }
}
