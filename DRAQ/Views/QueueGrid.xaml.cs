using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DRAQ.Models;

namespace DRAQ.Views
{
    /// <summary>The one grid used by both the Queue tab and the Torrents tab. It always shows items of the same shared queue.</summary>
    public partial class QueueGrid : UserControl
    {
        public QueueGrid()
        {
            InitializeComponent();
        }

        public IEnumerable ItemsSource
        {
            get { return Grid.ItemsSource; }
            set { Grid.ItemsSource = value; }
        }

        public List<QueueItem> Selected()
        {
            return Grid.SelectedItems.Cast<QueueItem>().ToList();
        }

        void OpenLocation_Click(object sender, RoutedEventArgs e)
        {
            QueueItem q = Selected().FirstOrDefault();
            if (q == null) return;
            UiUtil.OpenFolder(!string.IsNullOrEmpty(q.FinalPath) && (System.IO.File.Exists(q.FinalPath) || System.IO.Directory.Exists(q.FinalPath)) ? q.FinalPath : q.DestDir);
        }

        void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            QueueItem q = Selected().FirstOrDefault();
            if (q != null && !string.IsNullOrEmpty(q.FinalPath)) UiUtil.OpenFile(q.FinalPath);
        }

        void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            QueueItem q = Selected().FirstOrDefault();
            if (q != null && !string.IsNullOrEmpty(q.Url))
            {
                try { Clipboard.SetText(q.Url); } catch { }
            }
        }
    }
}

