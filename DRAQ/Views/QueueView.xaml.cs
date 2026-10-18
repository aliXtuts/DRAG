using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DRAQ.Models;

namespace DRAQ.Views
{
    public partial class QueueView : UserControl
    {
        public QueueView()
        {
            InitializeComponent();
            QGrid.ItemsSource = App.Engine.Items;
        }

        List<QueueItem> Sel() { return QGrid.Selected(); }

        void Pause_Click(object s, RoutedEventArgs e) { foreach (QueueItem q in Sel()) App.Engine.Pause(q); }
        void Resume_Click(object s, RoutedEventArgs e) { foreach (QueueItem q in Sel()) App.Engine.Resume(q); }
        void Cancel_Click(object s, RoutedEventArgs e)
        {
            List<QueueItem> sel = Sel().Where(q => q.Status != QueueStatus.Completed).ToList();
            if (sel.Count == 0) return;
            if (!UiUtil.Ask("Cancel " + sel.Count + " download(s)? Partly downloaded data of cancelled files is deleted.")) return;
            foreach (QueueItem q in sel) App.Engine.Cancel(q);
        }
        void Retry_Click(object s, RoutedEventArgs e) { foreach (QueueItem q in Sel()) App.Engine.Retry(q); }

        void Remove_Click(object s, RoutedEventArgs e)
        {
            List<QueueItem> sel = Sel();
            if (sel.Count == 0) return;
            if (sel.Any(q => q.Status == QueueStatus.Downloading) &&
                !UiUtil.Ask("Some selected downloads are running. Stop and remove them from the list?\n(Files already on disk are kept.)")) return;
            foreach (QueueItem q in sel) App.Engine.Remove(q);
        }

        void Up_Click(object s, RoutedEventArgs e)
        {
            foreach (QueueItem q in Sel().OrderBy(i => App.Engine.Items.IndexOf(i))) App.Engine.MoveUp(q);
        }

        void Down_Click(object s, RoutedEventArgs e)
        {
            foreach (QueueItem q in Sel().OrderByDescending(i => App.Engine.Items.IndexOf(i))) App.Engine.MoveDown(q);
        }

        void PauseAll_Click(object s, RoutedEventArgs e) { App.Engine.PauseAll(); }
        void ResumeAll_Click(object s, RoutedEventArgs e) { App.Engine.ResumeAll(); }
        void RetryFailed_Click(object s, RoutedEventArgs e) { App.Engine.RetryFailed(); }

        void ClearFinished_Click(object s, RoutedEventArgs e)
        {
            foreach (QueueItem q in App.Engine.Snapshot().Where(i => i.Status == QueueStatus.Completed || i.Status == QueueStatus.Cancelled))
                App.Engine.Remove(q);
        }

        void Priority_Changed(object s, SelectionChangedEventArgs e)
        {
            if (PriorityBox.SelectedIndex < 0) return;
            foreach (QueueItem q in Sel()) App.Engine.SetPriority(q, PriorityBox.SelectedIndex);
            PriorityBox.SelectedIndex = -1;
        }
    }
}

