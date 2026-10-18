using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DRAQ.Models;
using DRAQ.Services;
using DRAQ.Utilities;

namespace DRAQ.Views
{
    public partial class TorrentsView : UserControl
    {
        string _torrentPath;

        public TorrentsView()
        {
            InitializeComponent();
            DestBox.Text = App.Settings.TorrentsFolder;
            // Same shared queue as everywhere else - this is only a filtered view of it.
            CollectionViewSource cvs = new CollectionViewSource { Source = App.Engine.Items };
            cvs.View.Filter = delegate (object o) { QueueItem q = o as QueueItem; return q != null && q.Kind == DownloadKind.Torrent; };
            TGrid.ItemsSource = cvs.View;
        }

        void Paste_Click(object sender, RoutedEventArgs e)
        {
            try { if (Clipboard.ContainsText()) MagnetBox.Text = Clipboard.GetText().Trim(); }
            catch (Exception ex) { Log.Warn("Clipboard: " + ex.Message); }
        }

        void ChooseFile_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*";
            dlg.Title = "Choose a .torrent file";
            if (dlg.ShowDialog() == true)
            {
                _torrentPath = dlg.FileName;
                FileBox.Text = dlg.FileName;
            }
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            string p = UiUtil.PickFolder(DestBox.Text, "Choose the folder for the torrent");
            if (p != null) DestBox.Text = p;
        }

        static bool LooksLikeTorrentFile(string path, out string error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 20 || data[0] != (byte)'d') { error = "This does not look like a valid .torrent file."; return false; }
                string text = Encoding.Latin1.GetString(data);
                if (text.IndexOf("4:info", StringComparison.Ordinal) < 0) { error = "This .torrent file has no 'info' section, so it is invalid or damaged."; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not read the .torrent file: " + ex.Message;
                return false;
            }
        }

        void Start_Click(object sender, RoutedEventArgs e)
        {
            string magnet = MagnetBox.Text.Trim();
            string err;
            if (!UiUtil.IsUsableFolder(DestBox.Text, out err)) { UiUtil.Warn(err); return; }
            string dest = Path.GetFullPath(DestBox.Text.Trim());

            QueueItem item = new QueueItem { Kind = DownloadKind.Torrent, DestDir = dest, Priority = 1 };
            if (magnet.Length > 0)
            {
                if (!UrlUtil.IsMagnet(magnet))
                {
                    UiUtil.Warn("That is not a valid magnet link.\nIt should look like  magnet:?xt=urn:btih:...");
                    return;
                }
                item.Url = magnet;
                item.Title = UrlUtil.MagnetName(magnet);
            }
            else if (!string.IsNullOrEmpty(_torrentPath))
            {
                if (!LooksLikeTorrentFile(_torrentPath, out err)) { UiUtil.Warn(err); return; }
                try
                {
                    string copy = Path.Combine(AppPaths.TorrentDir, Guid.NewGuid().ToString("N") + ".torrent");
                    File.Copy(_torrentPath, copy, true);
                    item.TorrentFile = copy;
                    item.Url = new Uri(_torrentPath).AbsoluteUri;
                    item.Title = Path.GetFileNameWithoutExtension(_torrentPath);
                }
                catch (Exception ex)
                {
                    UiUtil.Error("Could not use that .torrent file: " + ex.Message);
                    return;
                }
            }
            else
            {
                UiUtil.Info("Paste a magnet link or choose a .torrent file first.");
                return;
            }

            AddResult res = App.Engine.AddMany(new[] { item }, true);
            if (res.Added.Count > 0)
            {
                Msg.Text = "Added to the queue.";
                MagnetBox.Text = "";
                FileBox.Text = "";
                _torrentPath = null;
            }
            else Msg.Text = "That torrent is already in the queue.";
        }

        List<QueueItem> Sel() { return TGrid.Selected(); }
        void Pause_Click(object s, RoutedEventArgs e) { foreach (QueueItem q in Sel()) App.Engine.Pause(q); }
        void Resume_Click(object s, RoutedEventArgs e) { foreach (QueueItem q in Sel()) { App.Engine.Resume(q); App.Engine.Retry(q); } }
        void Cancel_Click(object s, RoutedEventArgs e)
        {
            List<QueueItem> sel = Sel().Where(q => q.Status != QueueStatus.Completed).ToList();
            if (sel.Count == 0) return;
            if (!UiUtil.Ask("Cancel " + sel.Count + " torrent(s)? Files already downloaded are kept.")) return;
            foreach (QueueItem q in sel) App.Engine.Cancel(q);
        }
        void Remove_Click(object s, RoutedEventArgs e) { foreach (QueueItem q in Sel()) App.Engine.Remove(q); }
    }
}

