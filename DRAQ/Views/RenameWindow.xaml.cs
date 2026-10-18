using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using DRAQ.Models;
using DRAQ.Services;
using DRAQ.Utilities;

namespace DRAQ.Views
{
    public partial class RenameWindow : Window
    {
        readonly ShowRecord _show;
        readonly ObservableCollection<RenameEntry> _entries = new ObservableCollection<RenameEntry>();

        public RenameWindow(ShowRecord show, string folder)
        {
            InitializeComponent();
            _show = show;
            Title = "Rename episodes - " + show.Name;
            FolderBox.Text = folder;
            ItemsGrid.ItemsSource = _entries;
            LoadFiles();
        }

        void LoadFiles()
        {
            _entries.Clear();
            string folder = FolderBox.Text.Trim();
            if (!Directory.Exists(folder))
            {
                ResultText.Text = "That folder does not exist: " + folder;
                return;
            }
            try
            {
                foreach (RenameEntry e in App.Library.LoadRenameEntries(folder, SubBox.IsChecked == true)) _entries.Add(e);
                ResultText.Text = _entries.Count == 0
                    ? "No video files were found in this folder."
                    : _entries.Count + " video file(s) found. Type the episode number of each file in the \"Episode\" column.";
            }
            catch (Exception ex)
            {
                Log.Error("Rename: listing files failed", ex);
                ResultText.Text = "Could not read the folder: " + ex.Message;
            }
        }

        void Resort()
        {
            List<RenameEntry> sorted = Library.SortByEpisode(_entries);
            _entries.Clear();
            foreach (RenameEntry e in sorted) _entries.Add(e);
            ItemsGrid.Items.Refresh();
        }

        void Commit()
        {
            ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            string p = UiUtil.PickFolder(FolderBox.Text, "Choose the folder with the episode files");
            if (p != null) { FolderBox.Text = p; LoadFiles(); }
        }

        void Reload_Click(object sender, RoutedEventArgs e)
        {
            if (_entries.Any(x => x.Episode.HasValue) && !UiUtil.Ask("Reloading forgets the numbers you typed. Continue?")) return;
            LoadFiles();
        }

        void FillHints_Click(object sender, RoutedEventArgs e)
        {
            Commit();
            foreach (RenameEntry x in _entries) if (x.Suggested.HasValue) x.SetEpisodeDirect(x.Suggested);
            Resort();
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            Commit();
            foreach (RenameEntry x in _entries) { x.SetEpisodeDirect(null); x.NewName = ""; }
            ItemsGrid.Items.Refresh();
        }

        void Sort_Click(object sender, RoutedEventArgs e)
        {
            Commit();
            Resort();
        }

        RenamePlan MakePlan()
        {
            Commit();
            Resort();
            RenamePlan plan = Library.BuildPlan(_entries, NameBox.Text);
            ItemsGrid.Items.Refresh();
            if (plan.Errors.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Problems found - nothing was changed:");
                foreach (string s in plan.Errors) sb.AppendLine("  - " + s);
                ResultText.Text = sb.ToString();
            }
            else
            {
                int changing = plan.Ops.Count(o => !o.Unchanged);
                ResultText.Text = "The plan is valid: " + changing + " file(s) will be renamed" +
                                  (plan.Ops.Count - changing > 0 ? ", " + (plan.Ops.Count - changing) + " already have the right name" : "") +
                                  (plan.SkippedWithoutNumber > 0 ? ", " + plan.SkippedWithoutNumber + " file(s) without a number will not be touched" : "") +
                                  ". See the \"New name\" column.";
            }
            return plan;
        }

        void Check_Click(object sender, RoutedEventArgs e)
        {
            MakePlan();
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            RenamePlan plan = MakePlan();
            if (plan.Errors.Count > 0) return;
            int changing = plan.Ops.Count(o => !o.Unchanged);
            if (changing == 0) { UiUtil.Info("Every numbered file already has its final name. Nothing to do."); return; }
            if (!UiUtil.Ask("Rename " + changing + " file(s)?\n\nExample:  " + plan.Ops[0].Entry.Name + "\n   becomes:  " + plan.Ops[0].Entry.NewName)) return;
            try
            {
                App.Library.ExecutePlan(plan, _show);
                Resort();
                ResultText.Text = "Done. " + changing + " file(s) renamed.";
                UiUtil.Info(changing + " file(s) renamed.");
            }
            catch (Exception ex)
            {
                Log.Error("Rename failed", ex);
                ResultText.Text = "Renaming failed and was undone: " + ex.Message;
                UiUtil.Error("Renaming failed. " + ex.Message);
                LoadFiles();
            }
        }

        void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

