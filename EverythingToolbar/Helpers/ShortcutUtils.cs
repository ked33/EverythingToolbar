using System;
using System.Collections.Generic;
using System.Windows.Input;
using EverythingToolbar.Properties;

namespace EverythingToolbar.Helpers
{
    public static class ShortcutUtils
    {
        private static readonly KeyGestureConverter KeyGestureConverter = new();
        private const int EverythingShortcutKeyMask = 0x00FF;
        private const int EverythingShortcutControlMask = 0x0100;
        private const int EverythingShortcutAltMask = 0x0200;
        private const int EverythingShortcutShiftMask = 0x0400;
        private const int EverythingShortcutWindowsMask = 0x0800;

        public static bool TryParseShortcut(
            string? shortcut,
            out Key key,
            out ModifierKeys modifiers,
            bool requireModifier = false
        )
        {
            key = Key.None;
            modifiers = ModifierKeys.None;

            if (string.IsNullOrWhiteSpace(shortcut))
                return true;

            var normalizedInput = RemoveWhitespace(shortcut);

            try
            {
                if (
                    KeyGestureConverter.ConvertFromInvariantString(normalizedInput) is KeyGesture gesture
                    && gesture.Key != Key.None
                    && !IsModifierKey(gesture.Key)
                )
                {
                    if (requireModifier && gesture.Modifiers == ModifierKeys.None)
                        return false;

                    key = gesture.Key;
                    modifiers = gesture.Modifiers;
                    return true;
                }
            }
            catch (NotSupportedException) { }
            catch (InvalidCastException) { }
            catch (ArgumentException) { }

            if (!TryParseShortcutTokens(normalizedInput, out key, out modifiers))
            {
                key = Key.None;
                modifiers = ModifierKeys.None;
                return false;
            }

            if (requireModifier && modifiers == ModifierKeys.None)
            {
                key = Key.None;
                modifiers = ModifierKeys.None;
                return false;
            }

            return true;
        }

        private static bool TryParseShortcutTokens(string normalizedInput, out Key key, out ModifierKeys modifiers)
        {
            key = Key.None;
            modifiers = ModifierKeys.None;

            var tokens = normalizedInput.Split('+', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return false;

            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                var isLast = i == tokens.Length - 1;

                if (!isLast && TryParseModifierToken(token, out var mod))
                {
                    modifiers |= mod;
                    continue;
                }

                if (!isLast)
                    return false;

                if (TryParseKeyToken(token, out var parsedKey) && !IsModifierKey(parsedKey))
                {
                    key = parsedKey;
                    return true;
                }

                return false;
            }

            return false;
        }

        private static bool TryParseModifierToken(string token, out ModifierKeys modifier)
        {
            if (
                string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase)
            )
            {
                modifier = ModifierKeys.Control;
                return true;
            }

            if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifier = ModifierKeys.Shift;
                return true;
            }

            if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifier = ModifierKeys.Alt;
                return true;
            }

            if (
                string.Equals(token, "Win", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "Windows", StringComparison.OrdinalIgnoreCase)
            )
            {
                modifier = ModifierKeys.Windows;
                return true;
            }

            modifier = ModifierKeys.None;
            return false;
        }

        private static bool TryParseKeyToken(string token, out Key key)
        {
            switch (token)
            {
                case "Enter":
                    key = Key.Return;
                    return true;
                case "PageUp":
                    key = Key.Prior;
                    return true;
                case "PageDown":
                    key = Key.Next;
                    return true;
            }

            if (token.Length == 1 && token[0] >= '0' && token[0] <= '9')
            {
                key = Key.D0 + (token[0] - '0');
                return true;
            }

            return Enum.TryParse(token, ignoreCase: true, out key) && key != Key.None;
        }

        public static string FormatShortcut(Key key, ModifierKeys modifiers)
        {
            if (key == Key.None)
                return string.Empty;

            List<string> parts = [];

            if ((modifiers & ModifierKeys.Control) != 0)
                parts.Add(Resources.KeyCtrl);

            if ((modifiers & ModifierKeys.Windows) != 0)
                parts.Add(Resources.KeyWin);

            if ((modifiers & ModifierKeys.Alt) != 0)
                parts.Add(Resources.KeyAlt);

            if ((modifiers & ModifierKeys.Shift) != 0)
                parts.Add(Resources.KeyShift);

            parts.Add(FormatKey(key));
            return string.Join("+", parts);
        }

        public static bool MatchesShortcut(Key key, ModifierKeys modifiers, string? shortcut, bool requireModifier = false)
        {
            if (!TryParseShortcut(shortcut, out var expectedKey, out var expectedModifiers, requireModifier))
                return false;

            if (expectedKey == Key.None)
                return false;

            return key == expectedKey && modifiers == expectedModifiers;
        }

        public static bool MatchesShortcut(KeyEventArgs e, string? shortcut, bool requireModifier = false)
        {
            return MatchesShortcut(GetEffectiveKey(e), Keyboard.Modifiers, shortcut, requireModifier);
        }

        public static bool TryParseEverythingShortcut(string? shortcut, out Key key, out ModifierKeys modifiers)
        {
            key = Key.None;
            modifiers = ModifierKeys.None;

            if (string.IsNullOrWhiteSpace(shortcut))
                return false;

            var normalizedShortcut = RemoveWhitespace(shortcut);

            if (int.TryParse(normalizedShortcut, out var encodedShortcut) && encodedShortcut > 0)
            {
                var virtualKey = encodedShortcut & EverythingShortcutKeyMask;
                if (virtualKey == 0)
                    return false;

                if ((encodedShortcut & EverythingShortcutControlMask) != 0)
                    modifiers |= ModifierKeys.Control;

                if ((encodedShortcut & EverythingShortcutAltMask) != 0)
                    modifiers |= ModifierKeys.Alt;

                if ((encodedShortcut & EverythingShortcutShiftMask) != 0)
                    modifiers |= ModifierKeys.Shift;

                if ((encodedShortcut & EverythingShortcutWindowsMask) != 0)
                    modifiers |= ModifierKeys.Windows;

                key = KeyInterop.KeyFromVirtualKey(virtualKey);
                return key != Key.None && !IsModifierKey(key);
            }

            if (TryParseShortcut(normalizedShortcut, out key, out modifiers))
                return key != Key.None;

            return false;
        }

        public static Key GetEffectiveKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

        public static bool IsModifierKey(Key key)
        {
            return key
                is Key.LeftCtrl
                    or Key.RightCtrl
                    or Key.LeftAlt
                    or Key.RightAlt
                    or Key.LeftShift
                    or Key.RightShift
                    or Key.LWin
                    or Key.RWin;
        }

        private static string RemoveWhitespace(string value)
        {
            Span<char> buffer = stackalloc char[value.Length];
            var length = 0;

            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c))
                    continue;

                buffer[length++] = c;
            }

            return new string(buffer[..length]);
        }

        private static string FormatKey(Key key)
        {
            if (key is >= Key.D0 and <= Key.D9)
                return ((int)(key - Key.D0)).ToString();

            return key switch
            {
                Key.Return => "Enter",
                Key.Prior => "PageUp",
                Key.Next => "PageDown",
                _ => key.ToString(),
            };
        }
    }
}
