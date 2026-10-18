using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DRAQ.Models;
using DRAQ.Services;
using DRAQ.Utilities;

namespace DRAQ.Views
{
    public partial class NewEpisodesView : UserControl
    {
        readonly ObservableCollection<ShowRecord> _shows = new ObservableCollection<ShowRecord>();
        readonly ObservableCollection<NewEpisodeRow> _rows = new ObservableCollection<NewEpisodeRow>();
        bool _loading;
        bool _checking;
        DateTime _lastAuto = DateTime.Now;

        public NewEpisodesView()
        {
            InitializeComponent();
            ShowsGrid.ItemsSource = _shows;
            ResultsGrid.ItemsSource = _rows;
            Loaded += delegate { ReloadShows(); };
            App.LibraryChanged += delegate { ReloadShows(); };
        }

        void ReloadShows()
        {
            _loading = true;
            try
            {
                bool all = ShowCompletedBox.IsChecked == true;
                foreach (ShowRecord s in _shows) s.PropertyChanged -= OnShowChanged;
                _shows.Clear();
                foreach (ShowRecord s in App.Db.GetShows().Where(x => all || !x.Complete))
                {
                    s.PropertyChanged += OnShowChanged;
                    _shows.Add(s);
                }
            }
            finally { _loading = false; }
        }

        void OnShowChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_loading) return;
            ShowRecord s = sender as ShowRecord;
            if (s == null) return;
            if (e.PropertyName == "AutoQueue" || e.PropertyName == "Complete")
            {
                try { App.Db.UpdateShow(s); }
                catch (Exception ex) { Log.Error("Could not save show flags", ex); }
                if (e.PropertyName == "Complete")
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        // Completed items leave the default list; other tabs refresh as well.
                        App.RaiseLibraryChanged();
                    }));
                }
            }
        }

        void ShowCompleted_Click(object sender, RoutedEventArgs e) { ReloadShows(); }

        async void Check_Click(object sender, RoutedEventArgs e)
        {
            await CheckAllAsync(false);
        }

        public void AutoCheckIfDue()
        {
            if (!App.Settings.AutoCheck || _checking) return;
            if ((DateTime.Now - _lastAuto).TotalHours < App.Settings.AutoCheckHours) return;
            _lastAuto = DateTime.Now;
            Dispatcher.BeginInvoke(new Action(async delegate { await CheckAllAsync(true); }));
        }

        async Task CheckAllAsync(bool silent)
        {
            if (_checking) return;
            _checking = true;
            CheckButton.IsEnabled = false;
            try
            {
                bool all = ShowCompletedBox.IsChecked == true && !silent;
                List<ShowRecord> targets = App.Db.GetShows().Where(s => all || !s.Complete).ToList();
                if (targets.Count == 0)
                {
                    StatusText.Text = "Nothing to check yet. Queue some episodes from the Dramas or Series tab first (that saves their playlist).";
                    return;
                }

                // Keep results of earlier checks for shows that are not being re-checked now (none) - rebuild from scratch.
                _rows.Clear();
                int totalNew = 0, totalAuto = 0, failed = 0;
                StringBuilder errors = new StringBuilder();
                int index = 0;

                foreach (ShowRecord show in targets)
                {
                    index++;
                    StatusText.Text = "Checking " + show.Name + " (" + index + " of " + targets.Count + ")...";
                    CheckOutcome o;
                    using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(12)))
                    {
                        o = await App.Library.CheckShowAsync(show, cts.Token, true);
                    }
                    if (o.Error != null)
                    {
                        failed++;
                        errors.AppendLine(show.Name + ": " + o.Error);
                        continue;
                    }
                    foreach (NewEpisodeRow r in o.Rows) _rows.Add(r);
                    totalNew += o.Rows.Count;
                    totalAuto += o.QueuedAutomatically;

                    int count = o.Rows.Count + o.QueuedAutomatically;
                    if (count > 0)
                    {
                        string msg = show.Name + ": " + count + " new episode" + (count == 1 ? "" : "s") + " found" +
                                     (o.QueuedAutomatically > 0 ? " (added to the queue)" : "");
                        if (App.Settings.NotifyNewEpisodes) App.Notify("New episodes", msg);
                    }
                }

                ReloadShows();
                string summary = totalNew + " new episode(s) waiting for you";
                if (totalAuto > 0) summary += ", " + totalAuto + " queued automatically";
                if (totalNew + totalAuto == 0) summary = "No new episodes found";
                if (failed > 0) summary += ". " + failed + " could not be checked (see the log or details below)";
                StatusText.Text = summary + ".  Last check: " + DateTime.Now.ToString("HH:mm");
                if (failed > 0 && !silent) UiUtil.Warn("Some items could not be checked:\n\n" + errors);
            }
            catch (Exception ex)
            {
                Log.Error("Check failed", ex);
                StatusText.Text = "Check failed: " + ex.Message;
            }
            finally
            {
                _checking = false;
                CheckButton.IsEnabled = true;
            }
        }

        void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (NewEpisodeRow r in _rows) r.IsSelected = true;
        }

        void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (NewEpisodeRow r in _rows) r.IsSelected = false;
        }

        void AddSelected_Click(object sender, RoutedEventArgs e)
        {
            List<NewEpisodeRow> sel = _rows.Where(r => r.IsSelected).ToList();
            if (sel.Count == 0) { UiUtil.Info("Tick the episodes you want, or press Select All."); return; }
            try
            {
                AddResult res = App.Library.QueueNewRows(sel, App.Settings.DefaultQuality, true);
                foreach (NewEpisodeRow r in sel) _rows.Remove(r);
                StatusText.Text = "Added " + res.Added.Count + " episode(s) to the queue.";
                App.RaiseLibraryChanged();
            }
            catch (Exception ex)
            {
                Log.Error("Queue new episodes failed", ex);
                UiUtil.Error("Could not add the episodes: " + ex.Message);
            }
        }

        void Ignore_Click(object sender, RoutedEventArgs e)
        {
            List<NewEpisodeRow> sel = _rows.Where(r => r.IsSelected).ToList();
            if (sel.Count == 0) return;
            App.Library.IgnoreRows(sel);
            foreach (NewEpisodeRow r in sel) _rows.Remove(r);
            StatusText.Text = sel.Count + " episode(s) ignored.";
        }
    }
}

