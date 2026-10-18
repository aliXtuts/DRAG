using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DRAQ.Models;
using DRAQ.Utilities;

namespace DRAQ.Views
{
    public partial class HistoryWindow : Window
    {
        public HistoryWindow()
        {
            InitializeComponent();
            Reload();
        }

        void Reload()
        {
            try { Grid.ItemsSource = App.Db.GetHistory(SearchBox.Text); }
            catch (Exception ex) { Log.Error("History load failed", ex); UiUtil.Error("Could not read the history: " + ex.Message); }
        }

        void Search_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) Reload(); }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            HistoryRow r = Grid.SelectedItem as HistoryRow;
            if (r != null && !string.IsNullOrEmpty(r.Destination)) UiUtil.OpenFolder(r.Destination);
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (!UiUtil.Ask("Delete the whole download history? (Downloaded files are not touched.)")) return;
            App.Db.ClearHistory();
            Reload();
        }
    }
}

