using NLog;

namespace EverythingToolbar.Helpers
{
    /// <summary>
    /// Detects two complete, short presses of the same Ctrl key. Only the second
    /// physical key-up can trigger, so a Ctrl chord can cancel before it opens search.
    /// </summary>
    public sealed class DoubleCtrlDetector
    {
        /// <summary>Reject second downs closer than this (bounce / chatter).</summary>
        public const int MinIntervalMs = 100;

        /// <summary>Second down must arrive before this after the first down.</summary>
        public const int MaxIntervalMs = 350;

        /// <summary>Require a real pause between the first release and second press.</summary>
        public const int MinReleaseIntervalMs = 50;

        /// <summary>Each press must be a tap, not a held modifier.</summary>
        public const int MaxHoldMs = 250;

        internal const int ReleaseConfirmationMs = 100;
        internal const int ReleaseRecoveryIdleMs = 500;

        private static readonly ILogger Logger = ToolbarLogger.GetLogger<DoubleCtrlDetector>();

        private int _pressedCtrlKeys;
        private int _sequenceKey;
        private int _completedTaps;
        private uint _firstDownTime;
        private uint _firstUpTime;
        private uint _currentDownTime;
        private long _lastCtrlEventTime;
        private long? _releasedStateObservedAt;

        public int TriggerCount { get; private set; }
        internal bool HasPendingTap => _sequenceKey != 0;
        internal bool IsWaitingForRelease => _pressedCtrlKeys != 0;
        internal int ReleaseCheckDelayMs =>
            _releasedStateObservedAt.HasValue ? ReleaseConfirmationMs : ReleaseRecoveryIdleMs;

        public void Reset()
        {
            Logger.Debug(
                "DoubleCtrl detector reset: completedTaps={0}, pressedCtrlKeys={1}.",
                _completedTaps,
                _pressedCtrlKeys
            );
            ResetOnOtherInput("detector reset");
            _pressedCtrlKeys = 0;
            _lastCtrlEventTime = 0;
            _releasedStateObservedAt = null;
        }

        /// <summary>
        /// Track a physical Ctrl down, using the native event time for gesture timing and
        /// the observation time only for missing-release recovery. A down never triggers.
        /// </summary>
        public void OnCtrlKeyDown(int vk, uint eventTime, long nowMs, bool allowTap)
        {
            var keyMask = CtrlKeyMask(vk);
            if (keyMask == 0)
            {
                ResetOnOtherInput("unknown Ctrl side");
                return;
            }

            // Include repeats: recovery must not turn a held key into a new tap.
            _lastCtrlEventTime = nowMs;
            _releasedStateObservedAt = null;
            var wasDown = (_pressedCtrlKeys & keyMask) != 0;
            _pressedCtrlKeys |= keyMask;
            if (wasDown || _pressedCtrlKeys != keyMask || !allowTap)
            {
                ResetOnOtherInput(
                    wasDown ? "repeat or missing release"
                    : _pressedCtrlKeys != keyMask ? "overlapping Ctrl keys"
                    : "Ctrl side disabled or another input is held"
                );
                return;
            }

            if (_sequenceKey == vk && _completedTaps == 1)
            {
                var interval = Elapsed(eventTime, _firstDownTime);
                var releaseInterval = Elapsed(eventTime, _firstUpTime);
                if (interval < MaxIntervalMs)
                {
                    if (interval <= MinIntervalMs || releaseInterval < MinReleaseIntervalMs)
                    {
                        Logger.Debug(
                            "DoubleCtrl tap rejected: intervalMs={0}, releaseIntervalMs={1}; sequence discarded.",
                            interval,
                            releaseInterval
                        );
                        ResetOnOtherInput("taps too close together");
                        return;
                    }

                    _currentDownTime = eventTime;
                    Logger.Debug(
                        "DoubleCtrl second down awaiting release: vk=0x{0:X2}, intervalMs={1}, releaseIntervalMs={2}.",
                        vk,
                        interval,
                        releaseInterval
                    );
                    return;
                }
            }

            // An expired first tap or a different side starts a fresh sequence.
            ResetOnOtherInput("new sequence");
            _sequenceKey = vk;
            _firstDownTime = eventTime;
            _currentDownTime = eventTime;
            Logger.Debug("DoubleCtrl first down awaiting release: vk=0x{0:X2}, nativeTime={1}.", vk, eventTime);
        }

        /// <summary>
        /// Returns true only after two matching physical down/up pairs. Unsigned
        /// subtraction keeps native event timing valid across the 32-bit tick rollover.
        /// </summary>
        public bool OnCtrlKeyUp(int vk, uint eventTime, long nowMs)
        {
            _lastCtrlEventTime = nowMs;
            _releasedStateObservedAt = null;
            var keyMask = CtrlKeyMask(vk);
            var wasDown = (_pressedCtrlKeys & keyMask) != 0;
            _pressedCtrlKeys &= ~keyMask;
            if (!wasDown || _pressedCtrlKeys != 0 || _sequenceKey != vk)
            {
                ResetOnOtherInput("unmatched, overlapping or cancelled Ctrl release");
                return false;
            }

            var heldMs = Elapsed(eventTime, _currentDownTime);
            if (heldMs == 0 || heldMs > MaxHoldMs)
            {
                Logger.Debug("DoubleCtrl tap rejected: vk=0x{0:X2}, heldMs={1}.", vk, heldMs);
                ResetOnOtherInput("press was not a short tap");
                return false;
            }

            if (_completedTaps == 0)
            {
                _completedTaps = 1;
                _firstUpTime = eventTime;
                Logger.Debug(
                    "DoubleCtrl first tap complete: vk=0x{0:X2}, heldMs={1}; awaiting same-side second tap.",
                    vk,
                    heldMs
                );
                return false;
            }

            TriggerCount++;
            Logger.Debug(
                "DoubleCtrl detected on second release: trigger={0}, vk=0x{1:X2}, intervalMs={2}, releaseIntervalMs={3}, firstHeldMs={4}, secondHeldMs={5}.",
                TriggerCount,
                vk,
                Elapsed(_currentDownTime, _firstDownTime),
                Elapsed(_currentDownTime, _firstUpTime),
                Elapsed(_firstUpTime, _firstDownTime),
                heldMs
            );
            // Consume both taps. A third tap cannot reuse the second to toggle again.
            ResetOnOtherInput("double tap consumed");
            return true;
        }

        internal void OnInjectedCtrlEvent(long nowMs)
        {
            // Synthetic modifier changes can temporarily change GetAsyncKeyState. They do not
            // release physical keys or count as taps, and invalidate both gesture and recovery.
            ResetOnOtherInput("injected Ctrl input");
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
            if (leftCtrlDown || rightCtrlDown || eventAge < ReleaseRecoveryIdleMs)
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
                    _completedTaps,
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
                _completedTaps
            );
            Reset();
            return true;
        }

        /// <summary>Any non-Ctrl key or mouse activity clears an in-progress double-tap sequence.</summary>
        public void ResetOnOtherInput(string reason = "other input")
        {
            if (HasPendingTap)
                Logger.Debug(
                    "DoubleCtrl sequence cancelled: reason={0}, completedTaps={1}, pressedCtrlKeys={2}.",
                    reason,
                    _completedTaps,
                    _pressedCtrlKeys
                );
            _sequenceKey = 0;
            _completedTaps = 0;
            _firstDownTime = 0;
            _firstUpTime = 0;
            _currentDownTime = 0;
            // Do not release physical keys here: if Ctrl is still physically held, key-repeat
            // must remain suppressed until a real key-up arrives.
        }

        private static int CtrlKeyMask(int vk) =>
            vk switch
            {
                0xA2 => 1,
                0xA3 => 2,
                _ => 0,
            };

        private static uint Elapsed(uint now, uint then) => unchecked(now - then);
    }
}
