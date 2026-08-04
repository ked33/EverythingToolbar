using System.Windows.Media;

namespace EverythingToolbar.Helpers
{
    /// <summary>
    /// Resolves UI color schemes and exposes theme-dependent brushes used outside DynamicResource scopes.
    /// </summary>
    public static class ThemeManager
    {
        public const string System = "";
        public const string Light = "light";
        public const string Dark = "dark";
        public const string Nord = "nord";
        public const string OneDark = "onedark";

        private static readonly Color DefaultHighlightColor = Color.FromRgb(0x86, 0xa7, 0xdd);
        private static readonly Color NordHighlightColor = Color.FromRgb(0x88, 0xc0, 0xd0);
        private static readonly Color OneDarkHighlightColor = Color.FromRgb(0x61, 0xaf, 0xef);
        private static readonly Color NordAccentColor = Color.FromRgb(0x5e, 0x81, 0xac);
        private static readonly Color OneDarkAccentColor = Color.FromRgb(0x61, 0xaf, 0xef);
        private static readonly Color NordWindowBackground = Color.FromRgb(0x2e, 0x34, 0x40);
        private static readonly Color OneDarkWindowBackground = Color.FromRgb(0x28, 0x2c, 0x34);

        private static SolidColorBrush _highlightBrush = CreateFrozenBrush(DefaultHighlightColor);

        public static SolidColorBrush HighlightBrush => _highlightBrush;

        public static string Normalize(string? themeOverride)
        {
            if (string.IsNullOrWhiteSpace(themeOverride))
                return System;

            return themeOverride.Trim().ToLowerInvariant() switch
            {
                "system" or "auto" => System,
                "light" => Light,
                "dark" => Dark,
                "nord" => Nord,
                "onedark" or "one-dark" or "one_dark" => OneDark,
                _ => System,
            };
        }

        public static bool IsCustomColorScheme(string? themeOverride)
        {
            var scheme = Normalize(themeOverride);
            return scheme is Nord or OneDark;
        }

        public static bool IsEffectivelyLight(string? themeOverride, bool systemIsLight)
        {
            return Normalize(themeOverride) switch
            {
                Light => true,
                Dark or Nord or OneDark => false,
                _ => systemIsLight,
            };
        }

        public static string GetColorSchemeRelativePath(string? themeOverride, bool systemIsLight, bool isWindows11)
        {
            var scheme = Normalize(themeOverride);
            var platformFolder = isWindows11 ? "Win11" : "Win10";

            return scheme switch
            {
                Light => $"{platformFolder}/Light.xaml",
                Dark => $"{platformFolder}/Dark.xaml",
                Nord => "ColorSchemes/Nord.xaml",
                OneDark => "ColorSchemes/OneDark.xaml",
                _ => systemIsLight ? $"{platformFolder}/Light.xaml" : $"{platformFolder}/Dark.xaml",
            };
        }

        public static bool TryGetBuiltInAccent(string? themeOverride, out SolidColorBrush brush)
        {
            switch (Normalize(themeOverride))
            {
                case Nord:
                    brush = CreateFrozenBrush(NordAccentColor);
                    return true;
                case OneDark:
                    brush = CreateFrozenBrush(OneDarkAccentColor);
                    return true;
                default:
                    brush = null!;
                    return false;
            }
        }

        public static Color GetWindowBackgroundColor(string? themeOverride, bool systemIsLight, bool isWindows11)
        {
            return Normalize(themeOverride) switch
            {
                Nord => NordWindowBackground,
                OneDark => OneDarkWindowBackground,
                Light => isWindows11 ? Color.FromRgb(0xf0, 0xf0, 0xf0) : Color.FromRgb(0xee, 0xee, 0xee),
                Dark => isWindows11 ? Color.FromRgb(0x25, 0x25, 0x25) : Color.FromRgb(0x22, 0x22, 0x22),
                _ when systemIsLight => isWindows11
                    ? Color.FromRgb(0xf0, 0xf0, 0xf0)
                    : Color.FromRgb(0xee, 0xee, 0xee),
                _ => isWindows11 ? Color.FromRgb(0x25, 0x25, 0x25) : Color.FromRgb(0x22, 0x22, 0x22),
            };
        }

        public static void UpdateHighlightBrush(string? themeOverride)
        {
            var color = Normalize(themeOverride) switch
            {
                Nord => NordHighlightColor,
                OneDark => OneDarkHighlightColor,
                _ => DefaultHighlightColor,
            };

            _highlightBrush = CreateFrozenBrush(color);
        }

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            if (brush.CanFreeze)
                brush.Freeze();
            return brush;
        }
    }
}
