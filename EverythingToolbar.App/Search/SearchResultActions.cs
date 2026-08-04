using System;
using EverythingToolbar.App.Helpers;
using EverythingToolbar.Core.Data;
using EverythingToolbar.Core.Platform;
using EverythingToolbar.Core.Search;
using NLog;

namespace EverythingToolbar.App.Search
{
    public sealed class SearchResultActions(
        IEverythingClient everything,
        IClipboard clipboard,
        IShellDialogs shellDialogs,
        INotifier notifier,
        IFileLauncher fileLauncher,
        IFilePreviewer previewer,
        SearchState searchState,
        EverythingSearchLauncher launcher
    )
    {
        private static readonly ILogger Logger = ToolbarLogger.GetLogger<SearchResultActions>();

        public void Open(SearchResult r)
        {
            try
            {
                fileLauncher.Open(r.FullPathAndFileName, r.Path);
                everything.IncrementRunCount(r.FullPathAndFileName);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to open search result.");
                notifier.ShowError("MessageBoxFailedToOpen", e.Message);
            }
        }

        public void RunAsAdmin(SearchResult r)
        {
            try
            {
                fileLauncher.OpenAsAdmin(r.FullPathAndFileName);
                everything.IncrementRunCount(r.FullPathAndFileName);
            }
            catch (OperationCanceledException)
            {
                // The user dismissed the UAC elevation prompt; nothing to run and nothing to report.
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to open search result.");
                notifier.ShowError("MessageBoxFailedToOpen", e.Message);
            }
        }

        public void OpenPath(SearchResult r)
        {
            try
            {
                shellDialogs.OpenParentFolderAndSelect(r.FullPathAndFileName);
                everything.IncrementRunCount(r.FullPathAndFileName);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to open path.");
                notifier.ShowError("MessageBoxFailedToOpenPath", e.Message);
            }
        }

        public void OpenWith(SearchResult r)
        {
            try
            {
                shellDialogs.OpenWith(r.FullPathAndFileName);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to open dialog.");
                notifier.ShowError("MessageBoxFailedToOpenDialog", e.Message);
            }
        }

        public void CopyToClipboard(SearchResult r)
        {
            try
            {
                clipboard.SetFileDropList(new[] { r.FullPathAndFileName });
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to copy file.");
                notifier.ShowError("MessageBoxFailedToCopyFile", e.Message);
            }
        }

        public void CopyPathToClipboard(SearchResult r)
        {
            try
            {
                clipboard.SetText(r.FullPathAndFileName);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to copy path.");
                notifier.ShowError("MessageBoxFailedToCopyPath", e.Message);
            }
        }

        public void ShowProperties(SearchResult r)
        {
            shellDialogs.ShowFileProperties(r.FullPathAndFileName);
        }

        public void ShowWindowsContextMenu(SearchResult r)
        {
            shellDialogs.ShowWindowsContextMenu(r.FullPathAndFileName);
        }

        public void ShowInEverything(SearchResult r)
        {
            launcher.OpenSearchInEverything(searchState, filenameToHighlight: r.FullPathAndFileName);
        }

        public void Preview(SearchResult r)
        {
            previewer.PreviewInQuickLook(r.FullPathAndFileName);
            previewer.PreviewInSeer(r.FullPathAndFileName);
        }

        public void DeleteToRecycleBin(SearchResult r)
        {
            try
            {
                if (!shellDialogs.DeleteToRecycleBin(r.FullPathAndFileName))
                    return;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to delete search result.");
                notifier.ShowError("MessageBoxFailedToOpen", e.Message);
            }
        }

        /// <summary>
        /// For .lnk files, open the target's parent folder and select the target.
        /// Returns false when the item is not a resolvable shortcut (caller may fall back to properties).
        /// </summary>
        public bool TryOpenShortcutTargetFolder(SearchResult r)
        {
            if (
                !r.IsFile
                || !string.Equals(System.IO.Path.GetExtension(r.FullPathAndFileName), ".lnk", StringComparison.OrdinalIgnoreCase)
            )
                return false;

            try
            {
                var target = shellDialogs.ResolveShortcutTargetPath(r.FullPathAndFileName);
                if (string.IsNullOrWhiteSpace(target))
                    return false;

                if (!System.IO.File.Exists(target) && !System.IO.Directory.Exists(target))
                    return false;

                shellDialogs.OpenParentFolderAndSelect(target);
                everything.IncrementRunCount(r.FullPathAndFileName);
                return true;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to open shortcut target.");
                return false;
            }
        }

        public void OpenShortcutTargetOrProperties(SearchResult r)
        {
            if (!TryOpenShortcutTargetFolder(r))
                ShowProperties(r);
        }
    }
}
