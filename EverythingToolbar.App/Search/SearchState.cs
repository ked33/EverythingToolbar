using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EverythingToolbar.Core.Data;
using EverythingToolbar.Core.Search;

namespace EverythingToolbar.App.Search
{
    public sealed partial class SearchState : ObservableObject
    {
        [ObservableProperty]
        private string _searchTerm = "";

        // Session-scoped quick toggles: reset on each search session, do not write settings.ini.
        private bool _isMatchCase;
        private bool _isMatchPath;
        private bool _isMatchWholeWord;
        private bool _isRegExEnabled;

        public SortBy SortBy => (SortBy)_settings.SortBy;
        public bool IsSortDescending => _settings.IsSortDescending;

        public bool IsMatchCase
        {
            get => _isMatchCase;
            set
            {
                if (SetProperty(ref _isMatchCase, value))
                {
                    // Also notify bindings that still watch the settings name shape.
                }
            }
        }

        public bool IsMatchPath
        {
            get => _isMatchPath;
            set => SetProperty(ref _isMatchPath, value);
        }

        public bool IsMatchWholeWord
        {
            get => _isMatchWholeWord;
            set => SetProperty(ref _isMatchWholeWord, value);
        }

        public bool IsRegExEnabled
        {
            get => _isRegExEnabled;
            set => SetProperty(ref _isRegExEnabled, value);
        }

        private Filter _currentFilter;
        public Filter Filter
        {
            get => _currentFilter;
            set
            {
                if (SetProperty(ref _currentFilter, value))
                {
                    _settings.LastFilter = value.Name;
                }
            }
        }

        private readonly SearchHistory _history;
        private readonly FilterProvider _filterProvider;
        private readonly ISettings _settings;

        public SearchState(SearchHistory history, FilterProvider filterProvider, ISettings settings)
        {
            _history = history;
            _filterProvider = filterProvider;
            _settings = settings;

            _currentFilter = _filterProvider.GetInitialFilter();

            _settings.PropertyChanged += OnSettingsChanged;
        }

        public void Reset()
        {
            if (_settings.IsEnableHistory)
                _history.AddToHistory(SearchTerm);
            else
                SearchTerm = "";

            Filter = _filterProvider.GetInitialFilter();

            // Local behavior: clear quick toggles per search session without touching persisted settings.
            IsMatchCase = false;
            IsMatchPath = false;
            IsMatchWholeWord = false;
            IsRegExEnabled = false;
        }

        public string GetPreviousSearchTerm() => _history.GetPreviousItem();

        public string GetNextSearchTerm() => _history.GetNextItem();

        public void ClearHistory() => _history.ClearHistory();

        public void CycleFilters(int offset = 1)
        {
            var filterCount = _filterProvider.Filters.Count;
            if (filterCount == 0)
                return;

            var currentIndex = _filterProvider.Filters.IndexOf(Filter);
            var newIndex = (currentIndex + offset + filterCount) % filterCount;
            Filter = _filterProvider.Filters[newIndex];
        }

        public void SelectFilterFromIndex(int index)
        {
            if (index < 0 || index >= _filterProvider.Filters.Count)
                return;

            Filter = _filterProvider.Filters[index];
        }

        private string ApplyMacros(string searchTerm)
        {
            var result = searchTerm;

            foreach (var f in _filterProvider.Filters)
            {
                if (string.IsNullOrEmpty(f.Macro))
                    continue;

                result = result.Replace(f.Macro + ":", f.Search + " ");
            }

            var defaultMacros = new Dictionary<string, string>
            {
                { "apos:", "'" },
                { "amp:", "&" },
            };
            foreach (var defaultMacro in defaultMacros)
            {
                result = result.Replace(defaultMacro.Key, defaultMacro.Value);
            }

            return result;
        }

        public string BuildSearchTerm()
        {
            var rawSearchTerm =
                Filter.GetSearchPrefix(IsMatchCase, IsMatchWholeWord, IsMatchPath, IsRegExEnabled) + SearchTerm;
            var searchTermWithAppliedMacros = ApplyMacros(rawSearchTerm);

            // Default directory: when the user has not typed a term, scope results to the configured path.
            if (string.IsNullOrEmpty(SearchTerm))
            {
                var defaultPath = _settings.DefaultSearchPath;
                if (!string.IsNullOrWhiteSpace(defaultPath))
                {
                    var trimmedPath = defaultPath.Trim();
                    searchTermWithAppliedMacros = $"path:\"{trimmedPath}\" {searchTermWithAppliedMacros}";
                }
            }

            return searchTermWithAppliedMacros;
        }

        public SearchQuery BuildSearchQuery()
        {
            return new SearchQuery(
                BuildSearchTerm(),
                SortBy,
                IsSortDescending,
                IsMatchCase,
                IsMatchPath,
                IsMatchWholeWord,
                IsRegExEnabled
            );
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ISettings.SortBy):
                    OnPropertyChanged(nameof(SortBy));
                    break;
                case nameof(ISettings.IsSortDescending):
                    OnPropertyChanged(nameof(IsSortDescending));
                    break;
                case nameof(ISettings.IsHideEmptySearchResults):
                    SearchTerm = "";
                    OnPropertyChanged(nameof(SearchTerm));
                    break;
                case nameof(ISettings.DefaultSearchPath):
                    // Rebuild queries that depend on the default path prefix.
                    OnPropertyChanged(nameof(SearchTerm));
                    break;
            }
        }
    }
}
