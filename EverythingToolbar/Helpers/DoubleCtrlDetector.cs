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

        private const int DoubleTapClickCount = 2;

        // Canonical id for any Ctrl vk (LCONTROL / RCONTROL / CONTROL).
        private const int CtrlKeyId = 0x11;

        private long _lastDownTime;
        private int _lastKeyId;
        private int _clickCount;
        private bool _wasReleased = true;

        public int TriggerCount { get; private set; }

        public void Reset()
        {
            _lastDownTime = 0;
            _lastKeyId = 0;
            _clickCount = 0;
            _wasReleased = true;
        }

        /// <summary>Call on WM_KEYUP / WM_SYSKEYUP for Ctrl so the next down can count as a new tap.</summary>
        public void OnCtrlKeyUp() => _wasReleased = true;

        /// <summary>
        /// Feed a Ctrl key-down. Returns true when a double-tap is completed.
        /// </summary>
        public bool OnCtrlKeyDown(long nowMs)
        {
            // Key-repeat: never released since last press — ignore (SwiftList _wasReleased guard).
            if (!_wasReleased)
                return false;

            _wasReleased = false;

            var keyId = CtrlKeyId;
            var elapsed = nowMs - _lastDownTime;

            if (keyId == _lastKeyId && elapsed > MinIntervalMs && elapsed < MaxIntervalMs)
            {
                _clickCount++;
                if (_clickCount >= DoubleTapClickCount)
                {
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
            _clickCount = 1;
            _lastDownTime = nowMs;
            _lastKeyId = keyId;
            return false;
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
