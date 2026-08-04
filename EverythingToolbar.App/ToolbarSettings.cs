using Config.Net;
using EverythingToolbar.Core.Data;

namespace EverythingToolbar.App
{
    public interface IToolbarSettings
    {
        [Option(DefaultValue = false)]
        bool IsMatchCase { get; set; }

        [Option(DefaultValue = false)]
        bool IsRegExEnabled { get; set; }

        [Option(DefaultValue = FocusBehavior.Repeat)]
        FocusBehavior ListFocusBehavior { get; set; }

        [Option(DefaultValue = false)]
        bool IsMatchPath { get; set; }

        [Option(DefaultValue = 1)]
        int SortBy { get; set; }

        [Option(DefaultValue = false)]
        bool IsSortDescending { get; set; }

        [Option(DefaultValue = false)]
        bool IsMatchWholeWord { get; set; }

        [Option(DefaultValue = 700)]
        int PopupHeight { get; set; }

        [Option(DefaultValue = 700)]
        int PopupWidth { get; set; }

        [Option(DefaultValue = "C:\\Program Files\\Everything\\Everything.exe")]
        string EverythingPath { get; set; }

        [Option(DefaultValue = "Normal")]
        string ItemTemplate { get; set; }

        [Option(DefaultValue = false)]
        bool IsAutoApplyCustomActions { get; set; }

        [Option(DefaultValue = 3)]
        int MaxTabItems { get; set; }

        [Option(DefaultValue = "")]
        string FilterOrder { get; set; }

        [Option(DefaultValue = "")]
        string FiltersPath { get; set; }

        [Option(DefaultValue = false)]
        bool IsImportFilters { get; set; }

        [Option(DefaultValue = 9)]
        int ShortcutModifiers { get; set; }

        [Option(DefaultValue = 62)]
        int ShortcutKey { get; set; }

        [Option(DefaultValue = false)]
        bool IsDoubleCtrlOpenSearchWindow { get; set; }

        [Option(DefaultValue = "")]
        string DoubleCtrlProcessBlacklist { get; set; }

        [Option(DefaultValue = "")]
        string DefaultSearchPath { get; set; }

        [Option(DefaultValue = "Ctrl+I")]
        string ToggleMatchCaseShortcut { get; set; }

        [Option(DefaultValue = "Ctrl+B")]
        string ToggleMatchWholeWordShortcut { get; set; }

        [Option(DefaultValue = "Ctrl+U")]
        string ToggleMatchPathShortcut { get; set; }

        [Option(DefaultValue = "Ctrl+R")]
        string ToggleRegexShortcut { get; set; }

        [Option(DefaultValue = "Enter")]
        string OpenResultShortcut { get; set; }

        [Option(DefaultValue = "Ctrl+Enter")]
        string OpenPathShortcut { get; set; }

        [Option(DefaultValue = "Shift+Enter")]
        string OpenInEverythingShortcut { get; set; }

        [Option(DefaultValue = "Ctrl+Shift+Enter")]
        string RunAsAdminShortcut { get; set; }

        [Option(DefaultValue = "Alt+Enter")]
        string ShowFilePropertiesShortcut { get; set; }

        [Option(DefaultValue = "Ctrl+C")]
        string CopyFileShortcut { get; set; }

        [Option(DefaultValue = "Alt+C")]
        string CopyNameShortcut { get; set; }

        [Option(DefaultValue = "Shift+Alt+C")]
        string CopyFullPathShortcut { get; set; }

        [Option(DefaultValue = false)]
        bool IsDebugLoggingEnabled { get; set; }

        [Option(DefaultValue = 60)]
        int KeepaliveIntervalSeconds { get; set; }

        [Option(DefaultValue = false)]
        bool IsAnimationsDisabled { get; set; }

        [Option(DefaultValue = false)]
        bool IsHideEmptySearchResults { get; set; }

        [Option(DefaultValue = false)]
        bool IsShowResultsCount { get; set; }

        [Option(DefaultValue = false)]
        bool IsShowQuickToggles { get; set; }

        [Option(DefaultValue = false)]
        bool IsEnableHistory { get; set; }

        [Option(DefaultValue = false)]
        bool IsReplaceStartMenuSearch { get; set; }

        [Option(DefaultValue = false)]
        bool IsRememberFilter { get; set; }

        [Option(DefaultValue = "")]
        string LastFilter { get; set; }

        [Option(DefaultValue = false)]
        bool IsThumbnailsEnabled { get; set; }

        [Option(DefaultValue = false)]
        bool IsSystemContextMenuDefault { get; set; }

        [Option(DefaultValue = false)]
        bool IsPreviewPaneEnabled { get; set; }

        [Option(DefaultValue = "")]
        string InstanceName { get; set; }

        [Option(DefaultValue = "")]
        string IconName { get; set; }

        [Option(DefaultValue = "0")]
        string SkippedUpdate { get; set; }

        [Option(DefaultValue = true)]
        bool IsUpdateNotificationsEnabled { get; set; }

        [Option(DefaultValue = false)]
        bool IsSetupAssistantDisabled { get; set; }

        [Option(DefaultValue = true)]
        bool IsTrayIconEnabled { get; set; }

        [Option(DefaultValue = true)]
        bool IsAutoSelectFirstResult { get; set; }

        [Option(DefaultValue = true)]
        bool IsHomeEndNavigateResults { get; set; }

        [Option(DefaultValue = true)]
        bool IsSearchAsYouType { get; set; }

        [Option(DefaultValue = false)]
        bool IsForceCenterAlignment { get; set; }

        [Option(DefaultValue = false)]
        bool IsDoubleClickToOpen { get; set; }

        [Option(Alias = "ForceWin10Theme", DefaultValue = false)]
        bool ForceWin10Behavior { get; set; }

        [Option(DefaultValue = "")]
        string ThemeOverride { get; set; }

        [Option(DefaultValue = "")]
        string VersionBeforeUpdate { get; set; }

        [Option(DefaultValue = "")]
        string UILanguage { get; set; }

        [Option(DefaultValue = false)]
        bool TaskbarWindowEnabled { get; set; }

        [Option(DefaultValue = "Left")]
        string TaskbarWindowAlignment { get; set; }
    }
}
