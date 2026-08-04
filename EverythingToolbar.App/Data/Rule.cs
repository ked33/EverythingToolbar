using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EverythingToolbar.App.Data
{
    public enum FileType
    {
        Any,
        File,
        Folder,
    }

    [Serializable]
    public partial class Rule : ObservableObject
    {
        [ObservableProperty]
        private string _name = "";

        [ObservableProperty]
        private FileType _type;

        [ObservableProperty]
        private string _expression = "";

        [XmlIgnore]
        private Regex? _compiledRegex;

        [XmlIgnore]
        private bool _expressionValid = true;

        partial void OnExpressionChanged(string? oldValue, string newValue)
        {
            try
            {
                _compiledRegex = new Regex(newValue ?? "", RegexOptions.Compiled);
                _expressionValid = true;
            }
            catch (ArgumentException)
            {
                _compiledRegex = null;
                _expressionValid = false;
            }

            OnPropertyChanged(nameof(ExpressionValid));
        }

        public bool ExpressionValid => _expressionValid;

        public bool IsExpressionMatch(string input)
        {
            if (string.IsNullOrEmpty(Expression) || input == null)
                return false;

            return _compiledRegex?.IsMatch(input) ?? false;
        }

        [ObservableProperty]
        private string _command = "";

        /// <summary>
        /// Optional keyboard shortcut (e.g. "Ctrl+Shift+E"). Empty means no shortcut.
        /// Must include at least one modifier when non-empty (validated in the settings UI).
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShortcutValid))]
        private string _shortcut = "";

        /// <summary>
        /// Lightweight validity for the settings grid. Full parse/normalize happens on save via ShortcutUtils.
        /// </summary>
        [XmlIgnore]
        public bool ShortcutValid
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Shortcut))
                    return true;

                // Require at least one modifier token and one non-modifier key token (Mod+Key).
                var parts = Shortcut
                    .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2)
                    return false;

                static bool IsMod(string t) =>
                    t.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("Control", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("Alt", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("Shift", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("Win", StringComparison.OrdinalIgnoreCase)
                    || t.Equals("Windows", StringComparison.OrdinalIgnoreCase);

                var hasMod = parts.Any(IsMod);
                var hasKey = parts.Any(p => !IsMod(p));
                return hasMod && hasKey;
            }
        }
    }
}
