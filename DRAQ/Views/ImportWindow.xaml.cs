using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DRAQ.Models;
using DRAQ.Services;
using DRAQ.Utilities;

namespace DRAQ.Views
{
    public partial class ImportWindow : Window
    {
        readonly string _kind;
        readonly ObservableCollection<ImportEntry> _entries = new ObservableCollection<ImportEntry>();
        string _scannedFolder;

        public ShowRecord ImportedShow { get; private set; }

        public ImportWindow(string kind)
        {
            InitializeComponent();
            _kind = kind;
            Title = kind == "series" ? "Import an existing series folder" : "Import an existing drama folder";
            NameLabel.Text = kind == "series" ? "Series name" : "Drama name";
            SeasonPanel.Visibility = kind == "series" ? Visibility.Visible : Visibility.Collapsed;
            FilesGrid.ItemsSource = _entries;
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            string p = UiUtil.PickFolder(FolderBox.Text, "Choose the folder that contains the episodes");
            if (p == null) return;
            FolderBox.Text = p;
            if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = new DirectoryInfo(p).Name;
            Scan_Click(sender, e);
        }

        void Scan_Click(object sender, RoutedEventArgs e)
        {
            string folder = FolderBox.Text.Trim();
            if (!Directory.Exists(folder)) { UiUtil.Warn("That folder does not exist."); return; }
            if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = new DirectoryInfo(folder).Name;
            try
            {
                _entries.Clear();
                foreach (ImportEntry en in App.Library.ScanFolder(folder)) _entries.Add(en);
                _scannedFolder = folder;
                ImportButton.IsEnabled = _entries.Count > 0;
                int detected = _entries.Count(x => x.Episode.HasValue);
                CountText.Text = "Found " + _entries.Count + " video file(s); episode number detected for " + detected + ".";
                if (_entries.Count == 0) UiUtil.Info("No video files were found in that folder.");
            }
            catch (Exception ex)
            {
                Log.Error("Import scan failed", ex);
                UiUtil.Error("Could not scan the folder: " + ex.Message);
            }
        }

        void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (ImportEntry x in _entries) x.IsSelected = true; }
        void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (ImportEntry x in _entries) x.IsSelected = false; }

        void Import_Click(object sender, RoutedEventArgs e)
        {
            FilesGrid.CommitEdit(DataGridEditingUnit.Row, true);
            string name = NameBox.Text.Trim();
            if (name.Length == 0) { UiUtil.Info("Enter a name first."); return; }
            List<ImportEntry> sel = _entries.Where(x => x.IsSelected).ToList();
            if (sel.Count == 0) { UiUtil.Info("Select at least one file."); return; }
            int season = 1;
            if (_kind == "series" && !int.TryParse(SeasonBox.Text.Trim(), out season)) { UiUtil.Info("Enter the season number."); return; }
            try
            {
                ImportedShow = App.Library.ImportFiles(_kind, name, _scannedFolder, _kind == "series" ? (int?)season : null, sel);
                UiUtil.Info(sel.Count + " file(s) registered under \"" + name + "\". Nothing on disk was changed.\n\nUse \"Rename Episodes\" when you want to number them.");
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Log.Error("Import failed", ex);
                UiUtil.Error("Import failed: " + ex.Message);
            }
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}

