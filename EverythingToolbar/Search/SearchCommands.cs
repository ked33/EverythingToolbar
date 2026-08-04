using System;
using System.Windows.Input;
using EverythingToolbar.Core.Data;
using EverythingToolbar.Helpers;

namespace EverythingToolbar.Search
{
    public sealed class SearchCommands
    {
        private readonly SearchSession _session;
        private readonly SearchResultActions _actions;
        private readonly CustomActionService _customActions;
        private readonly SearchWindowController _controller;
        private readonly ISettings _settings;
        private readonly SearchState _searchState;

        public SearchCommands(
            SearchSession session,
            SearchResultActions actions,
            CustomActionService customActions,
            SearchWindowController controller,
            ISettings settings,
            SearchState searchState
        )
        {
            _session = session;
            _actions = actions;
            _customActions = customActions;
            _controller = controller;
            _settings = settings;
            _searchState = searchState;
        }

        public bool TranslateResultsGesture(Key key, Key systemKey, ModifierKeys modifiers, bool fromSearchBox)
        {
            var effectiveKey = key == Key.System ? systemKey : key;

            if (Matches(_settings.OpenResultShortcut, effectiveKey, modifiers, requireModifier: false))
            {
                if (modifiers == ModifierKeys.None && effectiveKey is Key.Enter or Key.Return)
                {
                    if (_session.SelectedResult == null)
                    {
                        _session.MoveDown();
                        SyncFocusToSelection();
                    }
                    else
                    {
                        OpenSelected();
                    }
                    return true;
                }

                if (modifiers != ModifierKeys.None || effectiveKey is not (Key.Enter or Key.Return))
                {
                    OpenSelected();
                    return true;
                }
            }

            if (Matches(_settings.OpenPathShortcut, effectiveKey, modifiers))
            {
                OpenSelectedPath();
                return true;
            }

            if (Matches(_settings.OpenInEverythingShortcut, effectiveKey, modifiers))
            {
                ShowSelectedInEverything();
                return true;
            }

            if (Matches(_settings.RunAsAdminShortcut, effectiveKey, modifiers))
            {
                RunSelectedAsAdmin();
                return true;
            }

            if (Matches(_settings.ShowFilePropertiesShortcut, effectiveKey, modifiers))
            {
                ShowSelectedProperties();
                return true;
            }

            if (Matches(_settings.CopyFileShortcut, effectiveKey, modifiers, requireModifier: true))
            {
                CopySelected();
                return true;
            }

            if (Matches(_settings.CopyFullPathShortcut, effectiveKey, modifiers, requireModifier: true))
            {
                CopySelectedPath();
                return true;
            }

            if (Matches(_settings.CopyNameShortcut, effectiveKey, modifiers, requireModifier: true))
            {
                if (_session.SelectedResult != null)
                {
                    try
                    {
                        System.Windows.Clipboard.SetText(_session.SelectedResult.FileName);
                    }
                    catch
                    {
                        // ignore clipboard failures
                    }

                    _controller.Hide();
                    _session.ClearSelection();
                }
                return true;
            }

            if (Matches(_settings.ToggleMatchCaseShortcut, effectiveKey, modifiers))
            {
                _searchState.IsMatchCase = !_searchState.IsMatchCase;
                return true;
            }

            if (Matches(_settings.ToggleMatchWholeWordShortcut, effectiveKey, modifiers))
            {
                _searchState.IsMatchWholeWord = !_searchState.IsMatchWholeWord;
                return true;
            }

            if (Matches(_settings.ToggleMatchPathShortcut, effectiveKey, modifiers))
            {
                _searchState.IsMatchPath = !_searchState.IsMatchPath;
                return true;
            }

            if (Matches(_settings.ToggleRegexShortcut, effectiveKey, modifiers))
            {
                _searchState.IsRegExEnabled = !_searchState.IsRegExEnabled;
                return true;
            }

            // Alt+1..9,0 selects filter tabs 1..10; pressing the same shortcut again returns to "All".
            if (modifiers == ModifierKeys.Alt && effectiveKey is >= Key.D0 and <= Key.D9)
            {
                var index = effectiveKey == Key.D0 ? 9 : effectiveKey - Key.D1;
                _searchState.SelectFilterFromIndex(index);
                return true;
            }

            // Custom action shortcuts (must include a modifier, same as settings validation).
            if (
                modifiers != ModifierKeys.None
                && !ShortcutUtils.IsModifierKey(effectiveKey)
                && TryRunCustomActionShortcut(effectiveKey, modifiers)
            )
            {
                return true;
            }

            if (effectiveKey == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
            {
                _searchState.CycleFilters(modifiers == ModifierKeys.Shift ? -1 : 1);
                return true;
            }

            switch (effectiveKey)
            {
                case Key.Up when CanArrowNavigate(modifiers, fromSearchBox):
                    _session.MoveUp();
                    break;
                case Key.Down when CanArrowNavigate(modifiers, fromSearchBox):
                    _session.MoveDown();
                    break;
                case Key.PageUp when CanArrowNavigate(modifiers, fromSearchBox):
                    _session.PageUp();
                    break;
                case Key.PageDown when CanArrowNavigate(modifiers, fromSearchBox):
                    _session.PageDown();
                    break;
                case Key.Home when CanHomeEndNavigate(modifiers, fromSearchBox):
                    _session.SelectFirst();
                    break;
                case Key.End when CanHomeEndNavigate(modifiers, fromSearchBox):
                    _session.SelectLast();
                    break;
                default:
                    return false;
            }

            SyncFocusToSelection();
            return true;
        }

        private static bool Matches(string? shortcut, Key key, ModifierKeys modifiers, bool requireModifier = false) =>
            ShortcutUtils.MatchesShortcut(key, modifiers, shortcut, requireModifier);

        private static bool CanArrowNavigate(ModifierKeys modifiers, bool fromSearchBox) =>
            !fromSearchBox || modifiers == ModifierKeys.None;

        private bool CanHomeEndNavigate(ModifierKeys modifiers, bool fromSearchBox) =>
            !fromSearchBox || (modifiers != ModifierKeys.Shift && _settings.IsHomeEndNavigateResults);

        private void SyncFocusToSelection()
        {
            if (_session.KeepSearchBoxFocused)
                return;

            if (_session.SelectedIndex < 0)
                _controller.FocusSearchBox();
            else
                _controller.FocusSelectedResult();
        }

        public void OpenSelected(SearchResult? target = null) =>
            Act(target, RunOrOpen, hide: true, clearSelection: true);

        public void OpenSelectedPath(SearchResult? target = null) =>
            Act(target, _actions.OpenPath, hide: true, clearSelection: true);

        public void RunSelectedAsAdmin(SearchResult? target = null) =>
            Act(target, _actions.RunAsAdmin, hide: true, clearSelection: true);

        public void ShowSelectedProperties(SearchResult? target = null) =>
            Act(target, _actions.ShowProperties, hide: true, clearSelection: true);

        public void OpenSelectedWith(SearchResult? target = null) =>
            Act(target, _actions.OpenWith, hide: true, clearSelection: true);

        public void ShowSelectedInEverything(SearchResult? target = null) =>
            Act(target, _actions.ShowInEverything, hide: true, clearSelection: true);

        // Local behavior: close the search window after copying so the user can paste immediately.
        public void CopySelected(SearchResult? target = null) =>
            Act(target, _actions.CopyToClipboard, hide: true, clearSelection: true);

        public void CopySelectedPath(SearchResult? target = null) =>
            Act(target, _actions.CopyPathToClipboard, hide: true, clearSelection: true);

        public void ShowSelectedWindowsContextMenu(SearchResult? target = null) =>
            Act(target, _actions.ShowWindowsContextMenu, hide: false, clearSelection: false);

        public void PreviewSelected(SearchResult? target = null) =>
            Act(target, _actions.Preview, hide: false, clearSelection: false);

        private void RunOrOpen(SearchResult item)
        {
            if (!_customActions.TryRun(item))
                _actions.Open(item);
        }

        /// <summary>
        /// Runs the first custom action whose shortcut matches. Hides the window on success (local behavior).
        /// </summary>
        private bool TryRunCustomActionShortcut(Key key, ModifierKeys modifiers)
        {
            var item = _session.SelectedResult;
            if (item == null)
                return false;

            foreach (var rule in _customActions.Load())
            {
                if (string.IsNullOrWhiteSpace(rule.Shortcut))
                    continue;

                if (!ShortcutUtils.MatchesShortcut(key, modifiers, rule.Shortcut, requireModifier: true))
                    continue;

                if (_customActions.TryRun(item, rule.Command))
                {
                    _controller.Hide();
                    _session.ClearSelection();
                    return true;
                }

                // Matched a shortcut but failed to run — still consume the key.
                return true;
            }

            return false;
        }

        private void Act(SearchResult? target, Action<SearchResult> action, bool hide, bool clearSelection)
        {
            var item = target ?? _session.SelectedResult;
            if (item != null)
                action(item);
            if (hide)
                _controller.Hide();
            if (clearSelection)
                _session.ClearSelection();
        }
    }
}
