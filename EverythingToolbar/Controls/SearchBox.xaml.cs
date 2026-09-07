using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using EverythingToolbar.ViewModels;

namespace EverythingToolbar.Controls
{
    public partial class SearchBox
    {
        public static readonly DependencyProperty SearchTermProperty = DependencyProperty.Register(
            nameof(SearchTerm),
            typeof(string),
            typeof(SearchBox),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSearchTermPropertyChanged
            )
        );

        public string SearchTerm
        {
            get => (string)GetValue(SearchTermProperty);
            set => SetValue(SearchTermProperty, value);
        }

        private static void OnSearchTermPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not SearchBox searchBox || e.NewValue is not string newValue)
                return;

            // Keep the visible text in sync when SearchState pushes a new term (history, reset, etc.).
            if (searchBox.TextBox.Text == newValue)
                return;

            searchBox._isInternalTextChange = true;
            try
            {
                searchBox.TextBox.Text = newValue;
                searchBox.TextBox.CaretIndex = searchBox.TextBox.Text.Length;
            }
            finally
            {
                searchBox._isInternalTextChange = false;
            }
        }

        private bool _isInternalTextChange;
        private bool _syncPosted;
        private readonly SearchBoxViewModel _viewModel = Ioc.Default.GetRequiredService<SearchBoxViewModel>();

        public SearchBox()
        {
            InitializeComponent();
            DataContext = _viewModel;

            InputMethod.SetPreferredImeState(this, InputMethodState.DoNotCare);

            _viewModel.Settings.PropertyChanged += OnSettingsChanged;

            // IME composition can update the text without a reliable intermediate TextChanged in some hosts;
            // also re-sync after the input pipeline settles.
            TextBox.AddHandler(
                TextCompositionManager.TextInputStartEvent,
                new TextCompositionEventHandler(OnTextComposition),
                true
            );
            TextBox.AddHandler(
                TextCompositionManager.TextInputUpdateEvent,
                new TextCompositionEventHandler(OnTextComposition),
                true
            );
            TextBox.AddHandler(
                TextCompositionManager.TextInputEvent,
                new TextCompositionEventHandler(OnTextComposition),
                true
            );
        }

        private void OnTextComposition(object sender, TextCompositionEventArgs e) => QueueSyncFromTextBox();

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInternalTextChange)
                return;

            QueueSyncFromTextBox();
        }

        /// <summary>
        /// Coalesce rapid key/IME events onto the dispatcher so SearchState always sees the final TextBox.Text.
        /// </summary>
        private void QueueSyncFromTextBox()
        {
            if (!_viewModel.Settings.IsSearchAsYouType)
                return;

            if (_syncPosted)
                return;

            _syncPosted = true;
            Dispatcher.BeginInvoke(
                () =>
                {
                    _syncPosted = false;
                    PushTextToSearchState();
                },
                DispatcherPriority.Input
            );
        }

        private void PushTextToSearchState()
        {
            if (!_viewModel.Settings.IsSearchAsYouType)
                return;

            var text = TextBox.Text ?? string.Empty;

            // Keep the DP in sync for any TwoWay binding to SearchState on the parent.
            if (!string.Equals(SearchTerm, text, System.StringComparison.Ordinal))
                SetCurrentValue(SearchTermProperty, text);

            // Always go through SearchState so SearchSession rebuilds (force notify).
            _viewModel.SearchState.SetSearchTermFromUi(text);
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Up)
            {
                UpdateSearchTerm(_viewModel.PreviousHistoryTerm());
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Down)
            {
                UpdateSearchTerm(_viewModel.NextHistoryTerm());
                e.Handled = true;
                return;
            }
            if (
                Keyboard.Modifiers == ModifierKeys.None
                && e.Key is Key.Enter or Key.Return
                && !_viewModel.Settings.IsSearchAsYouType
            )
            {
                // Commit the box text as the query when search-as-you-type is off.
                var text = TextBox.Text ?? string.Empty;
                SetCurrentValue(SearchTermProperty, text);
                _viewModel.SearchState.SetSearchTermFromUi(text);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                _viewModel.Dismiss();
                e.Handled = true;
                return;
            }

            if (_viewModel.TryHandleResultsGesture(e.Key, e.SystemKey, Keyboard.Modifiers))
                e.Handled = true;
        }

        private void UpdateSearchTerm(string newSearchTerm)
        {
            _isInternalTextChange = true;
            try
            {
                TextBox.Text = newSearchTerm;
                TextBox.CaretIndex = TextBox.Text.Length;
                SetCurrentValue(SearchTermProperty, newSearchTerm);
                _viewModel.SearchState.SetSearchTermFromUi(newSearchTerm);
            }
            finally
            {
                _isInternalTextChange = false;
            }
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ISettings.IsShowQuickToggles))
                UpdateQuickTogglesVisibility();

            // Turning search-as-you-type on should immediately apply the current box text.
            if (e.PropertyName == nameof(ISettings.IsSearchAsYouType) && _viewModel.Settings.IsSearchAsYouType)
                PushTextToSearchState();
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            UpdateQuickTogglesVisibility();
        }

        private void UpdateQuickTogglesVisibility()
        {
            if (_viewModel.Settings.IsShowQuickToggles && ActualWidth > 200)
            {
                QuickToggleButtons.Visibility = Visibility.Visible;
                TextBox.Padding = new Thickness(37, 0, 130, 0);
            }
            else
            {
                QuickToggleButtons.Visibility = Visibility.Collapsed;
                TextBox.Padding = new Thickness(37, 0, 10, 0);
            }
        }

        public new void Focus()
        {
            if (PresentationSource.FromVisual(TextBox) is HwndSource hwnd)
            {
                NativeMethods.ForciblySetForegroundWindow(hwnd.Handle);
            }

            TextBox.Focus();
            Keyboard.Focus(TextBox);
        }

        private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            TextBox.SelectAll();
            _viewModel.NotifySearchBoxFocused();
        }

        private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Commit whatever is in the box when leaving, if search-as-you-type is on.
            if (_viewModel.Settings.IsSearchAsYouType)
                PushTextToSearchState();

            if (e.NewFocus == null) // New focus outside application
            {
                _viewModel.NotifyFocusLostToOutside();
            }
        }

        private void SelectivelyIgnoreMouseButton(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox { IsKeyboardFocusWithin: false } textBox)
            {
                e.Handled = true;
                textBox.Focus();
            }
        }

        private void OnPasteClicked(object sender, RoutedEventArgs args)
        {
            TextBox.Paste();
        }

        private void OnCopyClicked(object sender, RoutedEventArgs args)
        {
            TextBox.Copy();
        }

        private void OnCutClicked(object sender, RoutedEventArgs args)
        {
            TextBox.Cut();
        }
    }
}
