using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using DRAQ.Utilities;

namespace DRAQ.Views
{
    public static class UiUtil
    {
        public static string PickFolder(string initial, string title = "Choose a folder")
        {
            Microsoft.Win32.OpenFolderDialog dlg = new Microsoft.Win32.OpenFolderDialog();
            dlg.Title = title;
            try
            {
                if (!string.IsNullOrWhiteSpace(initial))
                {
                    string probe = initial;
                    while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe)) probe = Path.GetDirectoryName(probe);
                    if (!string.IsNullOrEmpty(probe)) dlg.InitialDirectory = probe;
                }
            }
            catch { }
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }

        public static void Info(string text) { MessageBox.Show(text, "DRAQ", MessageBoxButton.OK, MessageBoxImage.Information); }
        public static void Warn(string text) { MessageBox.Show(text, "DRAQ", MessageBoxButton.OK, MessageBoxImage.Warning); }
        public static void Error(string text) { MessageBox.Show(text, "DRAQ", MessageBoxButton.OK, MessageBoxImage.Error); }
        public static bool Ask(string text)
        {
            return MessageBox.Show(text, "DRAQ", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        public static void OpenFolder(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else if (Directory.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                else
                    Warn("That folder does not exist yet:\n" + path);
            }
            catch (Exception ex) { Log.Warn("OpenFolder: " + ex.Message); Warn("Could not open the folder: " + ex.Message); }
        }

        public static void OpenFile(string path)
        {
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                    Warn("The file is not there (yet):\n" + path);
            }
            catch (Exception ex) { Warn("Could not open it: " + ex.Message); }
        }

        /// <summary>Returns true when it is fine to continue (enough space, or the user chose to continue anyway).</summary>
        public static bool CheckDiskSpace(string destDir, long neededBytes)
        {
            if (neededBytes <= 0 || string.IsNullOrWhiteSpace(destDir)) return true;
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(destDir));
                if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\")) return true;
                long free = new DriveInfo(root).AvailableFreeSpace;
                if (free >= neededBytes + 64L * 1024 * 1024) return true;
                return Ask("There may not be enough free space on " + root + ".\n\nNeeded: about " + Fmt.Bytes(neededBytes) +
                           "\nFree: " + Fmt.Bytes(free) + "\n\nAdd it to the queue anyway?");
            }
            catch { return true; }
        }

        public static bool IsUsableFolder(string path, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path)) { error = "Choose a destination folder first."; return false; }
            try
            {
                string full = Path.GetFullPath(path.Trim());
                if (!Path.IsPathRooted(full)) { error = "Please enter a full folder path such as D:\\Downloads."; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = "That is not a valid folder path: " + ex.Message;
                return false;
            }
        }
    }
}

