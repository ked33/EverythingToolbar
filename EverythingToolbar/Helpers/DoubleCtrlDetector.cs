using NLog;

namespace EverythingToolbar.Helpers
{
    /// <summary>
    /// Double-tap detector for a bare modifier (Ctrl), aligned with SwiftList's
    /// <c>ModifierDoubleTapDetector</c>:
    /// <list type="bullet">
    /// <item>Must release between taps (ignores OS key-repeat).</item>
    /// <item>Second tap must fall in (MinInterval, MaxInterval) ms after the first down.</item>
    /// <item>Min interval rejects contact bounce that would look like a single physical press.</item>
    /// <item>Any non-modifier key (or mouse) resets the in-progress sequence.</item>
    /// </list>
    /// </summary>
    public sealed class DoubleCtrlDetector
    {
        /// <summary>Reject second downs closer than this (bounce / chatter).</summary>
        public const int MinIntervalMs = 100;

        /// <summary>Second down must arrive before this after the first down.</summary>
        public const int MaxIntervalMs = 500;

        internal const int ReleaseConfirmationMs = 100;

        private const int DoubleTapClickCount = 2;

        // Canonical id for any Ctrl vk (LCONTROL / RCONTROL / CONTROL).
        private const int CtrlKeyId = 0x11;
        private static readonly ILogger Logger = ToolbarLogger.GetLogger<DoubleCtrlDetector>();

        private long _lastDownTime;
        private int _lastKeyId;
        private int _clickCount;
        private bool _wasReleased = true;
        private long _lastCtrlEventTime;
        private long? _releasedStateObservedAt;

        public int TriggerCount { get; private set; }
        internal bool HasPendingTap => _clickCount != 0;
        internal bool IsWaitingForRelease => !_wasReleased;
        internal int ReleaseCheckDelayMs => _releasedStateObservedAt.HasValue ? ReleaseConfirmationMs : MaxIntervalMs;

        public void Reset()
        {
            Logger.Debug("DoubleCtrl detector reset: pendingTaps={0}, wasReleased={1}.", _clickCount, _wasReleased);
            _lastDownTime = 0;
            _lastKeyId = 0;
            _clickCount = 0;
            _wasReleased = true;
            _lastCtrlEventTime = 0;
            _releasedStateObservedAt = null;
        }

        /// <summary>Call on WM_KEYUP / WM_SYSKEYUP for Ctrl so the next down can count as a new tap.</summary>
        public void OnCtrlKeyUp()
        {
            Logger.Debug("DoubleCtrl Ctrl up: wasReleased={0}, pendingTaps={1}.", _wasReleased, _clickCount);
            _wasReleased = true;
            _releasedStateObservedAt = null;
        }

        /// <summary>
        /// Feed a Ctrl key-down. Returns true when a double-tap is completed.
        /// </summary>
        public bool OnCtrlKeyDown(long nowMs)
        {
            // Include repeats: recovery must not turn a recently observed held key into a new tap.
            _lastCtrlEventTime = nowMs;
            _releasedStateObservedAt = null;

            // Key-repeat: never released since last press — ignore (SwiftList _wasReleased guard).
            if (!_wasReleased)
            {
                Logger.Debug(
                    "DoubleCtrl down ignored: no accepted Ctrl up since previous down (repeat or missing release). pendingTaps={0}.",
                    _clickCount
                );
                return false;
            }

            _wasReleased = false;

            var keyId = CtrlKeyId;
            var elapsed = nowMs - _lastDownTime;

            if (keyId == _lastKeyId && elapsed > MinIntervalMs && elapsed < MaxIntervalMs)
            {
                _clickCount++;
                if (_clickCount >= DoubleTapClickCount)
                {
                    Logger.Debug("DoubleCtrl detected: trigger={0}, intervalMs={1}.", TriggerCount + 1, elapsed);
                    _clickCount = 0;
                    _lastDownTime = 0;
                    _lastKeyId = 0;
                    TriggerCount++;
                    return true;
                }

                _lastDownTime = nowMs;
                return false;
            }

            // First tap of a new sequence (or second tap outside the window / different key).
            if (Logger.IsDebugEnabled)
            {
                var reason =
                    _lastKeyId != keyId ? "first tap"
                    : elapsed <= MinIntervalMs ? "too fast"
                    : "too slow";
                Logger.Debug(
                    "DoubleCtrl starting sequence: reason={0}, intervalMs={1}, previousTaps={2}, requiredIntervalMs=({3}, {4}) exclusive.",
                    reason,
                    _lastKeyId == keyId ? elapsed : -1,
                    _clickCount,
                    MinIntervalMs,
                    MaxIntervalMs
                );
            }
            _clickCount = 1;
            _lastDownTime = nowMs;
            _lastKeyId = keyId;
            return false;
        }

        internal void OnInjectedCtrlEvent(long nowMs)
        {
            // Synthetic modifier changes can temporarily change GetAsyncKeyState. They do not
            // release the detector or count as taps, and invalidate any recovery observation.
            if (!IsWaitingForRelease)
                return;

            _lastCtrlEventTime = nowMs;
            _releasedStateObservedAt = null;
        }

        /// <summary>
        /// Discard stale state after two released-state observations outside the hook callback.
        /// Never preserve a tap or synthesize a trigger from system key-state observations.
        /// </summary>
        internal bool TryRecoverMissingRelease(long nowMs, bool leftCtrlDown, bool rightCtrlDown)
        {
            if (!IsWaitingForRelease)
                return false;

            var eventAge = nowMs - _lastCtrlEventTime;
            if (leftCtrlDown || rightCtrlDown || eventAge < MaxIntervalMs)
            {
                if (_releasedStateObservedAt.HasValue)
                    Logger.Debug(
                        "DoubleCtrl release confirmation cancelled: leftCtrlDown={0}, rightCtrlDown={1}, lastCtrlEventAgeMs={2}.",
                        leftCtrlDown,
                        rightCtrlDown,
                        eventAge
                    );
                _releasedStateObservedAt = null;
                return false;
            }

            if (!_releasedStateObservedAt.HasValue)
            {
                _releasedStateObservedAt = nowMs;
                Logger.Debug(
                    "DoubleCtrl missing release suspected: both Ctrl keys are up in system state, lastCtrlEventAgeMs={0}, pendingTaps={1}; confirming after {2} ms.",
                    eventAge,
                    _clickCount,
                    ReleaseConfirmationMs
                );
                return false;
            }

            var sampleInterval = nowMs - _releasedStateObservedAt.Value;
            if (sampleInterval < ReleaseConfirmationMs)
                return false;

            Logger.Debug(
                "DoubleCtrl recovered missing Ctrl release: both Ctrl keys up in two system state checks, lastCtrlEventAgeMs={0}, upSampleIntervalMs={1}, discardedTaps={2}. A new double tap is required.",
                eventAge,
                sampleInterval,
                _clickCount
            );
            Reset();
            return true;
        }

        /// <summary>Any non-Ctrl key or mouse activity clears an in-progress double-tap sequence.</summary>
        public void ResetOnOtherInput()
        {
            _clickCount = 0;
            _lastDownTime = 0;
            _lastKeyId = 0;
            // Do not force _wasReleased here: if Ctrl is still physically held, key-repeat
            // must remain suppressed until a real key-up arrives.
        }
    }
}
