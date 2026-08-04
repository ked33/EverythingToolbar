namespace EverythingToolbar.Core.Platform
{
    public interface IShellDialogs
    {
        void OpenWith(string filePath);
        void OpenParentFolderAndSelect(string filePath);
        void ShowFileProperties(string filePath);

        void ShowWindowsContextMenu(string filePath);

        string? BrowseForFile(string filterLabel, string filterPattern, string? initialDirectory);

        /// <summary>Send a file or folder to the Recycle Bin. Returns false if the path is missing or the user cancels.</summary>
        bool DeleteToRecycleBin(string path);

        /// <summary>Resolve a .lnk shortcut to its target path, or null if it cannot be resolved.</summary>
        string? ResolveShortcutTargetPath(string shortcutPath);
    }
}
