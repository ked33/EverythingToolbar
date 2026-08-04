using System.Collections.Generic;

namespace EverythingToolbar.Helpers
{
    /// <summary>
    /// Pure double-Ctrl tap detector. Tracks chords, mouse buttons, and stale state.
    /// </summary>
    public sealed class DoubleCtrlDetector
    {
        private const long StandaloneCtrlTapThresholdMilliseconds = 200;
        private const long StateTimeoutMilliseconds = 5000;
        private readonly long _thresholdMilliseconds;
        private readonly HashSet<uint> _pressedNonCtrlKeys = new();
        private readonly Dictionary<uint, long> _keyPressTimestamps = new();
        private bool _isCtrlPressed;
        private bool _ctrlChordUsed;
        private bool _suppressTapArmOnCurrentCtrlRelease;
        private bool _isMouseButtonPressed;
        private long _mouseButtonPressTimestamp;
        private long _currentCtrlDownTicks;
        private long _lastCtrlTapTicks;
        private bool _currentCtrlIsLeft;
        private bool _lastCtrlTapIsLeft;

        private int _triggerCount;
        private int _autoCleanupCount;
        private int _staleKeyCleanupCount;
        private int _staleMouseCleanupCount;

        public DoubleCtrlDetector(long thresholdMilliseconds)
        {
            _thresholdMilliseconds = thresholdMilliseconds;
        }

        public bool HasPendingSecondPress => _lastCtrlTapTicks > 0;

        public int TriggerCount => _triggerCount;
        public int AutoCleanupCount => _autoCleanupCount;
        public int StaleKeyCleanupCount => _staleKeyCleanupCount;
        public int StaleMouseCleanupCount => _staleMouseCleanupCount;

        public void Reset()
        {
            _isCtrlPressed = false;
            _ctrlChordUsed = false;
            _suppressTapArmOnCurrentCtrlRelease = false;
            _isMouseButtonPressed = false;
            _mouseButtonPressTimestamp = 0;
            _currentCtrlDownTicks = 0;
            _lastCtrlTapTicks = 0;
            _currentCtrlIsLeft = false;
            _lastCtrlTapIsLeft = false;
            _pressedNonCtrlKeys.Clear();
            _keyPressTimestamps.Clear();
        }

        private bool CleanupStaleState(long now)
        {
            var hadStaleState = false;

            var staleKeys = new List<uint>();
            foreach (var kvp in _keyPressTimestamps)
            {
                if (now - kvp.Value > StateTimeoutMilliseconds)
                {
                    staleKeys.Add(kvp.Key);
                    hadStaleState = true;
                }
            }

            foreach (var key in staleKeys)
            {
                _pressedNonCtrlKeys.Remove(key);
                _keyPressTimestamps.Remove(key);
                _staleKeyCleanupCount++;
            }

            if (
                _isMouseButtonPressed
                && _mouseButtonPressTimestamp > 0
                && now - _mouseButtonPressTimestamp > StateTimeoutMilliseconds
            )
            {
                _isMouseButtonPressed = false;
                _mouseButtonPressTimestamp = 0;
                _staleMouseCleanupCount++;
                hadStaleState = true;
            }

            if (hadStaleState)
                _autoCleanupCount++;

            return hadStaleState;
        }

        private bool DetectAnomalousState()
        {
            if (_pressedNonCtrlKeys.Count != _keyPressTimestamps.Count)
                return true;

            if (_isMouseButtonPressed && _mouseButtonPressTimestamp == 0)
                return true;

            if (!_isMouseButtonPressed && _mouseButtonPressTimestamp != 0)
                return true;

            return false;
        }

        public void ResetIfAnomalous()
        {
            if (DetectAnomalousState())
                Reset();
        }

        public bool RegisterCtrlDown(long now, bool isLeftCtrl)
        {
            if (_isCtrlPressed)
                return false;

            var hasOtherInputHeld = _pressedNonCtrlKeys.Count > 0 || _isMouseButtonPressed;

            _currentCtrlDownTicks = now;
            _isCtrlPressed = true;
            _ctrlChordUsed = hasOtherInputHeld;
            _currentCtrlIsLeft = isLeftCtrl;

            if (
                !hasOtherInputHeld
                && _lastCtrlTapTicks > 0
                && now - _lastCtrlTapTicks <= _thresholdMilliseconds
                && _lastCtrlTapIsLeft == isLeftCtrl
            )
            {
                _lastCtrlTapTicks = 0;
                _suppressTapArmOnCurrentCtrlRelease = true;
                _triggerCount++;
                return true;
            }

            _lastCtrlTapTicks = 0;
            _suppressTapArmOnCurrentCtrlRelease = false;
            return false;
        }

        public bool RegisterCtrlDownWithCleanup(long now, bool isLeftCtrl, bool enableAutoCleanup)
        {
            if (enableAutoCleanup)
                CleanupStaleState(now);

            return RegisterCtrlDown(now, isLeftCtrl);
        }

        public void RegisterCtrlUp(long now)
        {
            if (!_isCtrlPressed)
                return;

            _isCtrlPressed = false;
            var pressDuration = now - _currentCtrlDownTicks;
            var wasLeftCtrl = _currentCtrlIsLeft;
            _currentCtrlDownTicks = 0;

            if (_ctrlChordUsed)
            {
                _ctrlChordUsed = false;
                _suppressTapArmOnCurrentCtrlRelease = false;
                _lastCtrlTapTicks = 0;
                return;
            }

            if (_suppressTapArmOnCurrentCtrlRelease)
            {
                _suppressTapArmOnCurrentCtrlRelease = false;
                return;
            }

            if (pressDuration <= StandaloneCtrlTapThresholdMilliseconds)
            {
                _lastCtrlTapTicks = now;
                _lastCtrlTapIsLeft = wasLeftCtrl;
            }
            else
            {
                _lastCtrlTapTicks = 0;
            }
        }

        public void RegisterNonCtrlKeyDown(uint virtualKey)
        {
            _pressedNonCtrlKeys.Add(virtualKey);
            _keyPressTimestamps[virtualKey] = System.Environment.TickCount64;
            RegisterInterruptingInput();
        }

        public void RegisterNonCtrlKeyUp(uint virtualKey)
        {
            _pressedNonCtrlKeys.Remove(virtualKey);
            _keyPressTimestamps.Remove(virtualKey);
            RegisterInterruptingInput();
        }

        public void RegisterMouseButtonDown()
        {
            _isMouseButtonPressed = true;
            _mouseButtonPressTimestamp = System.Environment.TickCount64;
            RegisterInterruptingInput();
        }

        public void RegisterMouseButtonUp()
        {
            _isMouseButtonPressed = false;
            _mouseButtonPressTimestamp = 0;
            RegisterInterruptingInput();
        }

        public void RegisterMouseInput() => RegisterInterruptingInput();

        private void RegisterInterruptingInput()
        {
            if (_isCtrlPressed)
                _ctrlChordUsed = true;

            _lastCtrlTapTicks = 0;
        }
    }
}
