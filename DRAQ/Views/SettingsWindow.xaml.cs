using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using DRAQ.Models;
using DRAQ.Services;
using DRAQ.Utilities;

namespace DRAQ.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
            AppSettings s = App.Settings;
            DownloadFolderBox.Text = s.DownloadFolder;
            DramasFolderBox.Text = s.DramasFolder;
            SeriesFolderBox.Text = s.SeriesFolder;
            TorrentsFolderBox.Text = s.TorrentsFolder;
            MaxBox.Text = s.MaxConcurrent.ToString(CultureInfo.InvariantCulture);
            SpeedBox.Text = s.SpeedLimitKBps.ToString(CultureInfo.InvariantCulture);
            RetryBox.Text = s.RetryCount.ToString(CultureInfo.InvariantCulture);
            List<string> q = new List<string> { "Best Available", "1080p", "720p", "480p", "360p" };
            QualityBox.ItemsSource = q;
            QualityBox.SelectedItem = q.Contains(s.DefaultQuality) ? s.DefaultQuality : q[0];
            NotifyDoneBox.IsChecked = s.NotifyCompleted;
            NotifyFailBox.IsChecked = s.NotifyFailed;
            NotifyNewBox.IsChecked = s.NotifyNewEpisodes;
            AutoCheckBox.IsChecked = s.AutoCheck;
            AutoHoursBox.Text = s.AutoCheckHours.ToString(CultureInfo.InvariantCulture);
            StartWinBox.IsChecked = s.StartWithWindows;
            StartMinBox.IsChecked = s.StartMinimized;
            TrayBox.IsChecked = s.MinimizeToTray;
            ShutdownBox.IsChecked = s.ShutdownWhenDone;
            NoOverwriteBox.IsChecked = s.NeverOverwrite;
            List<string> d = new List<string> { "Skip", "Ask", "Allow" };
            DupBox.ItemsSource = d;
            DupBox.SelectedItem = d.Contains(s.DuplicateHandling) ? s.DuplicateHandling : "Skip";
        }

        void Pick(System.Windows.Controls.TextBox box)
        {
            string p = UiUtil.PickFolder(box.Text);
            if (p != null) box.Text = p;
        }

        void BrowseDownload_Click(object s, RoutedEventArgs e) { Pick(DownloadFolderBox); }
        void BrowseDramas_Click(object s, RoutedEventArgs e) { Pick(DramasFolderBox); }
        void BrowseSeries_Click(object s, RoutedEventArgs e) { Pick(SeriesFolderBox); }
        void BrowseTorrents_Click(object s, RoutedEventArgs e) { Pick(TorrentsFolderBox); }

        static bool ParseInt(string text, int min, int max, string label, out int value)
        {
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max)
            {
                UiUtil.Warn(label + " must be a whole number between " + min + " and " + max + ".");
                return false;
            }
            return true;
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            int max, speed, retry, hours;
            if (!ParseInt(MaxBox.Text, 1, 20, "Max simultaneous downloads", out max)) return;
            if (!ParseInt(SpeedBox.Text, 0, 10000000, "Speed limit", out speed)) return;
            if (!ParseInt(RetryBox.Text, 0, 20, "Retry count", out retry)) return;
            if (!ParseInt(AutoHoursBox.Text, 1, 168, "Hours between checks", out hours)) return;
            string err;
            foreach (string p in new[] { DownloadFolderBox.Text, DramasFolderBox.Text, SeriesFolderBox.Text, TorrentsFolderBox.Text })
            {
                if (!UiUtil.IsUsableFolder(p, out err)) { UiUtil.Warn(err); return; }
            }
            if (NoOverwriteBox.IsChecked != true && App.Settings.NeverOverwrite &&
                !UiUtil.Ask("Turning off \"Never overwrite\" means a download can replace an existing file with the same name.\n\nAre you sure?")) return;

            AppSettings s = App.Settings;
            s.DownloadFolder = DownloadFolderBox.Text.Trim();
            s.DramasFolder = DramasFolderBox.Text.Trim();
            s.SeriesFolder = SeriesFolderBox.Text.Trim();
            s.TorrentsFolder = TorrentsFolderBox.Text.Trim();
            s.MaxConcurrent = max;
            s.SpeedLimitKBps = speed;
            s.RetryCount = retry;
            s.DefaultQuality = QualityBox.SelectedItem as string ?? "Best Available";
            s.NotifyCompleted = NotifyDoneBox.IsChecked == true;
            s.NotifyFailed = NotifyFailBox.IsChecked == true;
            s.NotifyNewEpisodes = NotifyNewBox.IsChecked == true;
            s.AutoCheck = AutoCheckBox.IsChecked == true;
            s.AutoCheckHours = hours;
            s.StartWithWindows = StartWinBox.IsChecked == true;
            s.StartMinimized = StartMinBox.IsChecked == true;
            s.MinimizeToTray = TrayBox.IsChecked == true;
            s.ShutdownWhenDone = ShutdownBox.IsChecked == true;
            s.NeverOverwrite = NoOverwriteBox.IsChecked == true;
            s.DuplicateHandling = DupBox.SelectedItem as string ?? "Skip";

            try
            {
                App.Db.SaveSettings(s.ToDictionary());
                App.Engine.ApplySettings();
                App.ApplyStartupSetting();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Log.Error("Saving settings failed", ex);
                UiUtil.Error("Could not save the settings: " + ex.Message);
            }
        }

        void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }

        void Backup_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Title = "Back up the DRAQ database";
            dlg.InitialDirectory = AppPaths.BackupDir;
            dlg.FileName = "draq-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".db";
            dlg.Filter = "DRAQ database backup (*.db)|*.db";
            if (dlg.ShowDialog() != true) return;
            try
            {
                App.Db.Backup(dlg.FileName);
                UiUtil.Info("Backup saved:\n" + dlg.FileName);
            }
            catch (Exception ex)
            {
                Log.Error("Backup failed", ex);
                UiUtil.Error("Backup failed: " + ex.Message);
            }
        }

        void Restore_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Title = "Choose a DRAQ database backup";
            dlg.InitialDirectory = AppPaths.BackupDir;
            dlg.Filter = "DRAQ database backup (*.db)|*.db|All files (*.*)|*.*";
            if (dlg.ShowDialog() != true) return;
            if (!UiUtil.Ask("Restoring replaces ALL current DRAQ data (queue, dramas, series, history, settings) with the backup.\n\nDRAQ will restart. Continue?")) return;
            try
            {
                // Make a safety copy of the current database first.
                string safety = Path.Combine(AppPaths.BackupDir, "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".db");
                App.Db.Backup(safety);
                Database.Db.StageRestore(dlg.FileName, AppPaths.DbPath);

                string exe = Environment.ProcessPath;
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 4 /nobreak >nul & start \"\" \"" + exe + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
                App.RequestExit();
            }
            catch (Exception ex)
            {
                Log.Error("Restore failed", ex);
                UiUtil.Error("Restore failed: " + ex.Message);
            }
        }

        void OpenData_Click(object sender, RoutedEventArgs e) { UiUtil.OpenFolder(AppPaths.DataDir); }
        void OpenLogs_Click(object sender, RoutedEventArgs e) { UiUtil.OpenFolder(AppPaths.LogDir); }

        async void Versions_Click(object sender, RoutedEventArgs e)
        {
            ToolText.Text = "Reading versions...";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Tools folder: " + AppPaths.ToolsDir);
            sb.AppendLine("yt-dlp: " + await VersionOf(ToolPaths.YtDlp, "--version"));
            sb.AppendLine("ffmpeg: " + await VersionOf(ToolPaths.Ffmpeg, "-version"));
            sb.AppendLine("aria2c: " + await VersionOf(ToolPaths.Aria2, "--version"));
            ToolText.Text = sb.ToString();
        }

        static async System.Threading.Tasks.Task<string> VersionOf(string exe, string arg)
        {
            if (!File.Exists(exe)) return "MISSING";
            ProcResult r = await Proc.RunAsync(exe, new[] { arg }, CancellationToken.None, 20000);
            string first = (r.Out ?? "").Trim().Split('\n')[0].Trim();
            return string.IsNullOrEmpty(first) ? "unknown (" + (r.Err ?? "").Trim() + ")" : first;
        }

        async void UpdateYt_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(ToolPaths.YtDlp)) { UiUtil.Warn("yt-dlp.exe is missing from the tools folder."); return; }
            ToolText.Text = "Updating yt-dlp... please wait.";
            ProcResult r = await Proc.RunAsync(ToolPaths.YtDlp, new[] { "-U" }, CancellationToken.None, 180000);
            ToolText.Text = ((r.Out ?? "") + "\n" + (r.Err ?? "")).Trim();
        }
    }
}

