using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;
using Windows.Win32;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace EverythingToolbar.Platform.Helpers
{
    public static class ShellUtils
    {
        public static void ShowFileProperties(string path)
        {
            unsafe
            {
                fixed (char* verb = "properties")
                fixed (char* file = path)
                {
                    var info = new SHELLEXECUTEINFOW
                    {
                        cbSize = (uint)sizeof(SHELLEXECUTEINFOW),
                        fMask = 12u, // SEE_MASK_INVOKEIDLIST
                        lpVerb = verb,
                        lpFile = file,
                        nShow = 5, // SW_SHOW
                    };
                    PInvoke.ShellExecuteEx(ref info);
                }
            }
        }

        public static unsafe void CreateProcessFromCommandLine(string commandLine, string? workingDirectory = null)
        {
            var startupInfo = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };

            Span<char> mutableCommandLine = new char[commandLine.Length + 1];
            commandLine.CopyTo(mutableCommandLine);

            if (
                !PInvoke.CreateProcess(
                    null,
                    ref mutableCommandLine,
                    null,
                    null,
                    false,
                    0,
                    null,
                    workingDirectory,
                    in startupInfo,
                    out var processInformation
                )
            )
                return;

            PInvoke.CloseHandle(processInformation.hProcess);
            PInvoke.CloseHandle(processInformation.hThread);
        }

        public static void OpenWithDialog(string path)
        {
            var args = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
            args += ",OpenAs_RunDLL " + path;
            Process.Start("rundll32.exe", args);
        }

        public static unsafe void OpenParentFolderAndSelect(string path)
        {
            var parentFolder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parentFolder))
                return;

            PInvoke.SHParseDisplayName(parentFolder, null, out ITEMIDLIST* nativeFolder, 0, out _);
            if (nativeFolder == null)
                return;

            var itemToSelect = Path.GetFileName(path);
            PInvoke.SHParseDisplayName(
                Path.Combine(parentFolder, itemToSelect),
                null,
                out ITEMIDLIST* nativeFile,
                0,
                out _
            );

            var fileToSelect = nativeFile != null ? nativeFile : nativeFolder;
            PInvoke.SHOpenFolderAndSelectItems(nativeFolder, 1, &fileToSelect, 0);

            Marshal.FreeCoTaskMem((IntPtr)nativeFolder);
            if (nativeFile != null)
                Marshal.FreeCoTaskMem((IntPtr)nativeFile);
        }

        public static void DeleteToRecycleBin(string path)
        {
            if (File.Exists(path))
            {
                FileSystem.DeleteFile(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException
                );
                return;
            }

            if (Directory.Exists(path))
            {
                FileSystem.DeleteDirectory(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException
                );
                return;
            }

            throw new FileNotFoundException("Path does not exist.", path);
        }

        public static string? ResolveShortcutTargetPath(string shortcutPath)
        {
            if (!File.Exists(shortcutPath))
                return null;

            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                    return null;

                dynamic? shell = null;
                dynamic? shortcut = null;
                try
                {
                    shell = Activator.CreateInstance(shellType);
                    shortcut = shell?.CreateShortcut(shortcutPath);
                    var targetPath = shortcut?.TargetPath as string;
                    if (!string.IsNullOrWhiteSpace(targetPath))
                        return Environment.ExpandEnvironmentVariables(targetPath);
                }
                finally
                {
                    if (shortcut != null && Marshal.IsComObject(shortcut))
                        Marshal.FinalReleaseComObject(shortcut);
                    if (shell != null && Marshal.IsComObject(shell))
                        Marshal.FinalReleaseComObject(shell);
                }
            }
            catch
            {
                return null;
            }

            return null;
        }
    }
}
