using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DRAQ.Models;
using DRAQ.Services;
using DRAQ.Utilities;

namespace DRAQ.Views
{
    /// <summary>Used for BOTH the Dramas tab (Kind="drama", no seasons) and the Series tab (Kind="series", with seasons).</summary>
    public partial class ShowsView : UserControl
    {
        public string Kind { get; set; } = "drama";

        bool _init;
        bool _destTouched;
        bool _settingFields;
        bool _busy;
        bool _refreshPending;
        ShowRecord _current;
        string _parsedUrl;
        int? _parsedSeason;
        readonly ObservableCollection<ShowRecord> _shows = new ObservableCollection<ShowRecord>();
        readonly ObservableCollection<PlaylistItem> _items = new ObservableCollection<PlaylistItem>();

        public ShowsView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        bool IsSeries { get { return Kind == "series"; } }
        string BaseFolder { get { return IsSeries ? App.Settings.SeriesFolder : App.Settings.DramasFolder; } }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_init) return;
            _init = true;

            ListTitle.Text = IsSeries ? "Saved series" : "Saved dramas";
            NameLabel.Text = IsSeries ? "Series name" : "Drama name";
            SeasonPanel.Visibility = IsSeries ? Visibility.Visible : Visibility.Collapsed;
            SeasonColumn.Visibility = IsSeries ? Visibility.Visible : Visibility.Collapsed;
            ShowList.ItemsSource = _shows;
            ItemsGrid.ItemsSource = _items;

            List<string> q = new List<string> { "Best Available", "1080p", "720p", "480p", "360p" };
            QualityBox.ItemsSource = q;
            QualityBox.SelectedItem = q.Contains(App.Settings.DefaultQuality) ? App.Settings.DefaultQuality : q[0];

            SetFields(null, "", BaseFolder, "");
            ReloadShows(null);
            App.LibraryChanged += delegate { ReloadShows(_current != null ? _current.Id : (long?)null); };
            App.Engine.EpisodesChanged += delegate { ScheduleRefresh(); };
        }

        void ScheduleRefresh()
        {
            if (_refreshPending) return;
            _refreshPending = true;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                _refreshPending = false;
                RefreshStatuses();
            }), DispatcherPriority.Background);
        }

        void RefreshStatuses()
        {
            if (_current == null || _items.Count == 0) return;
            try { App.Library.ApplyStatuses(_current, _items); }
            catch (Exception ex) { Log.Warn("Status refresh failed: " + ex.Message); }
        }

        void ReloadShows(long? selectId)
        {
            bool all = ShowCompletedBox.IsChecked == true;
            List<ShowRecord> list = App.Db.GetShows(Kind).Where(s => all || !s.Complete || (selectId.HasValue && s.Id == selectId.Value)).ToList();
            _shows.Clear();
            foreach (ShowRecord s in list) _shows.Add(s);
            if (selectId.HasValue)
            {
                ShowRecord sel = _shows.FirstOrDefault(s => s.Id == selectId.Value);
                if (sel != null) ShowList.SelectedItem = sel;
            }
        }

        void SetFields(string name, string nameText, string dest, string url)
        {
            _settingFields = true;
            NameBox.Text = nameText;
            DestBox.Text = dest;
            UrlBox.Text = url;
            _settingFields = false;
        }

        // ------------------------------------------------------------------ selection
        void ShowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowRecord s = ShowList.SelectedItem as ShowRecord;
            _current = s;
            _parsedUrl = null;
            _items.Clear();
            if (s == null)
            {
                AutoQueueBox.IsChecked = false;
                AutoQueueBox.IsEnabled = false;
                CompleteButton.IsEnabled = false;
                return;
            }
            AutoQueueBox.IsEnabled = true;
            CompleteButton.IsEnabled = true;
            AutoQueueBox.IsChecked = s.AutoQueue;
            CompleteButton.Content = s.Complete ? "Reopen (not complete)" : "Mark as Complete";

            List<PlaylistRecord> pls = App.Db.GetPlaylists(s.Id);
            PlaylistRecord pl = pls.FirstOrDefault();
            _settingFields = true;
            if (IsSeries) SeasonBox.Text = (pl != null && pl.Season.HasValue ? pl.Season.Value : 1).ToString(CultureInfo.InvariantCulture);
            _settingFields = false;
            SetFields(s.Name, s.Name, s.Folder, pl != null ? pl.Url : "");
            _destTouched = true;

            foreach (PlaylistItem it in App.Library.SavedItems(s)) _items.Add(it);
            StatusText.Text = _items.Count + " saved episode(s). Press \"Parse Playlist\" to read the playlist again.";
            UpdateCount();
        }

        void SeasonBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settingFields || _current == null || !IsSeries) return;
            int n;
            if (int.TryParse(SeasonBox.Text.Trim(), out n))
            {
                PlaylistRecord pl = App.Db.GetPlaylist(_current.Id, n);
                if (pl != null) { _settingFields = true; UrlBox.Text = pl.Url; _settingFields = false; }
            }
        }

        void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_settingFields || _destTouched) return;
            string n = NameBox.Text.Trim();
            _settingFields = true;
            DestBox.Text = n.Length == 0 ? BaseFolder : Path.Combine(BaseFolder, FileNames.Folder(n));
            _settingFields = false;
        }

        void DestBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_settingFields) _destTouched = true;
        }

        void Paste_Click(object sender, RoutedEventArgs e)
        {
            try { if (Clipboard.ContainsText()) UrlBox.Text = Clipboard.GetText().Trim(); }
            catch (Exception ex) { Log.Warn("Clipboard: " + ex.Message); }
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            string p = UiUtil.PickFolder(DestBox.Text, "Choose the folder for this " + (IsSeries ? "series" : "drama"));
            if (p != null) { DestBox.Text = p; _destTouched = true; }
        }

        // ------------------------------------------------------------------ parse
        int? SeasonValue()
        {
            if (!IsSeries) return null;
            int n;
            return int.TryParse(SeasonBox.Text.Trim(), out n) && n >= 0 ? (int?)n : null;
        }

        async void Parse_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string name = NameBox.Text.Trim();
            string url = UrlBox.Text.Trim();
            string err;
            if (name.Length == 0) { UiUtil.Info("Enter the " + (IsSeries ? "series" : "drama") + " name first."); return; }
            if (!UrlUtil.IsHttp(url)) { UiUtil.Info("Paste the playlist link first (it must start with http:// or https://)."); return; }
            if (!UiUtil.IsUsableFolder(DestBox.Text, out err)) { UiUtil.Warn(err); return; }
            int? season = SeasonValue();
            if (IsSeries && !season.HasValue) { UiUtil.Info("Enter the season number (0 or higher)."); return; }

            _busy = true;
            ParseButton.IsEnabled = false;
            StatusText.Text = "Reading the entire playlist... this can take a minute for long playlists. Nothing is downloaded.";
            try
            {
                List<PlaylistItem> parsed;
                using (CancellationTokenSource cts = new CancellationTokenSource())
                {
                    parsed = await YtDlp.ParsePlaylistAsync(url, cts.Token);
                }
                foreach (PlaylistItem it in parsed) it.Season = IsSeries ? season : null;

                ShowRecord existing = App.Db.FindShow(Kind, name);
                _current = existing;
                if (existing != null) App.Library.ApplyStatuses(existing, parsed);

                _items.Clear();
                foreach (PlaylistItem it in parsed) _items.Add(it);
                _parsedUrl = url;
                _parsedSeason = season;

                int avail = parsed.Count(i => !i.Unavailable);
                int known = parsed.Count(i => i.EpisodeNumber.HasValue);
                StatusText.Text = "Found " + parsed.Count + " video(s) in the playlist" +
                                  (parsed.Count != avail ? " (" + (parsed.Count - avail) + " unavailable)" : "") +
                                  ". Episode number detected for " + known + ". Playlist order is kept separately and is NOT used as the episode number.";
                UpdateCount();
            }
            catch (YtDlpException ex)
            {
                StatusText.Text = "Could not read the playlist: " + ex.Message;
            }
            catch (DownloadException ex)
            {
                StatusText.Text = ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error("Parse failed", ex);
                StatusText.Text = "Could not read the playlist: " + ex.Message;
            }
            finally
            {
                _busy = false;
                ParseButton.IsEnabled = true;
            }
        }

        void UpdateCount()
        {
            CountText.Text = _items.Count == 0 ? "" : _items.Count(i => i.IsSelected) + " selected of " + _items.Count;
        }

        void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (PlaylistItem it in _items) it.IsSelected = it.CanSelect;
            UpdateCount();
        }

        void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (PlaylistItem it in _items) it.IsSelected = false;
            UpdateCount();
        }

        void GoQueue_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWin != null) App.MainWin.GoToQueue();
        }

        // ------------------------------------------------------------------ queue
        PlaylistRecord PlaylistFor(ShowRecord show, int? season)
        {
            if (_parsedUrl != null && season == _parsedSeason)
                return App.Db.UpsertPlaylist(show.Id, season, _parsedUrl, show.Name, _items.Count(i => i.Season == season));
            return App.Db.GetPlaylist(show.Id, season);
        }

        void AddSelected_Click(object sender, RoutedEventArgs e)
        {
            ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            List<PlaylistItem> selected = _items.Where(i => i.IsSelected && i.CanSelect).ToList();
            if (selected.Count == 0) { UiUtil.Info("Tick at least one episode first (or press Select All)."); return; }

            string name = NameBox.Text.Trim();
            string err;
            if (name.Length == 0) { UiUtil.Info("Enter the name first."); return; }
            if (!UiUtil.IsUsableFolder(DestBox.Text, out err)) { UiUtil.Warn(err); return; }
            string folder = Path.GetFullPath(DestBox.Text.Trim());
            string quality = QualityBox.SelectedItem as string ?? "Best Available";

            try
            {
                ShowRecord show = App.Library.EnsureShow(Kind, name, folder);
                _current = show;
                int added = 0, skipped = 0;
                foreach (IGrouping<int?, PlaylistItem> g in _items.GroupBy(i => i.Season))
                {
                    PlaylistRecord pl = PlaylistFor(show, g.Key);
                    App.Library.SaveParsed(show, pl, g.Key, g);
                    AddResult r = App.Library.QueueEpisodes(show, pl, g.Key, g.Where(i => i.IsSelected && i.CanSelect).ToList(), quality, true);
                    added += r.Added.Count;
                    skipped += r.SkippedDuplicates;
                }
                foreach (PlaylistItem it in _items) it.IsSelected = false;
                App.Library.ApplyStatuses(show, _items);
                string msg = "Added " + added + " episode(s) to the queue" + (skipped > 0 ? " (" + skipped + " skipped: already queued/downloaded)" : "") +
                             ". Files keep their original names until you use \"Rename Episodes\".";
                ReloadShows(show.Id);
                App.RaiseLibraryChanged();
                StatusText.Text = msg;
                UpdateCount();
            }
            catch (Exception ex)
            {
                Log.Error("Add to queue failed", ex);
                UiUtil.Error("Could not add the episodes: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------ show actions
        void ShowCompleted_Click(object sender, RoutedEventArgs e)
        {
            ReloadShows(_current != null ? _current.Id : (long?)null);
        }

        void AutoQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _current.AutoQueue = AutoQueueBox.IsChecked == true;
            App.Db.UpdateShow(_current);
            App.RaiseLibraryChanged();
        }

        void Complete_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            _current.Complete = !_current.Complete;
            App.Db.UpdateShow(_current);
            long id = _current.Id;
            ReloadShows(id);
            App.RaiseLibraryChanged();
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string f = _current != null ? _current.Folder : DestBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(f)) UiUtil.OpenFolder(f);
        }

        void RemoveShow_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            if (!UiUtil.Ask("Remove \"" + _current.Name + "\" from DRAQ?\n\nYour downloaded files are NOT deleted. Only DRAQ's list of episodes for it is forgotten.")) return;
            App.Db.DeleteShow(_current.Id);
            _current = null;
            _items.Clear();
            ReloadShows(null);
            App.RaiseLibraryChanged();
        }

        void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) { UiUtil.Info("Select a saved " + (IsSeries ? "series" : "drama") + " in the list first (queue some episodes or import a folder to create one)."); return; }
            string folder = _current.Folder;
            if (IsSeries)
            {
                int? season = SeasonValue();
                if (season.HasValue)
                {
                    string sf = App.Library.TargetFolder(_current, season);
                    if (Directory.Exists(sf)) folder = sf;
                }
            }
            RenameWindow w = new RenameWindow(_current, folder) { Owner = Window.GetWindow(this) };
            w.ShowDialog();
            App.RaiseLibraryChanged();
        }

        void Import_Click(object sender, RoutedEventArgs e)
        {
            ImportWindow w = new ImportWindow(Kind) { Owner = Window.GetWindow(this) };
            if (w.ShowDialog() == true && w.ImportedShow != null)
            {
                ReloadShows(w.ImportedShow.Id);
                App.RaiseLibraryChanged();
            }
        }
    }
}

