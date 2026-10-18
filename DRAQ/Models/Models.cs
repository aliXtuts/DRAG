using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DRAQ.Utilities;

namespace DRAQ.Models
{
    // 62742
    public abstract class Notifier : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    public enum DownloadKind { Direct, Video, Torrent }
    public enum QueueStatus { Queued, Downloading, Paused, Completed, Failed, Cancelled }
    public enum EpisodeStatus { NotDownloaded, Queued, Downloading, Downloaded, Failed }

    /// <summary>One entry of THE queue. Single downloads, dramas, series, torrents and new episodes all use this same class.</summary>
    public sealed class QueueItem : Notifier
    {
        public long Id { get; set; }
        public DownloadKind Kind { get; set; }
        public string Url { get; set; }
        public string DestDir { get; set; }
        /// <summary>Direct: wanted file name. Video: chosen base name (no extension) once started.</summary>
        public string FileName { get; set; }
        public string FinalPath { get; set; }
        public string Format { get; set; }
        public string ExtraArgs { get; set; }
        public string Quality { get; set; }
        public long ShowId { get; set; }
        public long EpisodeId { get; set; }
        public string VideoId { get; set; }
        public string TorrentFile { get; set; }
        public string Gid { get; set; }
        public int RetriesDone { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
        public DateTime NotBefore { get; set; } = DateTime.MinValue;

        string _title;
        QueueStatus _status;
        long _downloaded;
        long _total;
        long _speed;
        long _eta;
        int _priority = 1;
        string _error;
        string _note;

        public string Title { get { return _title; } set { Set(ref _title, value); } }

        public QueueStatus Status
        {
            get { return _status; }
            set
            {
                if (Set(ref _status, value))
                {
                    Raise(nameof(StatusText)); Raise(nameof(Progress)); Raise(nameof(ProgressText));
                    Raise(nameof(SpeedText)); Raise(nameof(EtaText)); Raise(nameof(Details));
                }
            }
        }

        public long Downloaded
        {
            get { return _downloaded; }
            set { if (Set(ref _downloaded, value)) { Raise(nameof(DownloadedText)); Raise(nameof(Progress)); Raise(nameof(ProgressText)); } }
        }

        public long Total
        {
            get { return _total; }
            set { if (Set(ref _total, value)) { Raise(nameof(TotalText)); Raise(nameof(Progress)); Raise(nameof(ProgressText)); } }
        }

        public long Speed
        {
            get { return _speed; }
            set { if (Set(ref _speed, value)) Raise(nameof(SpeedText)); }
        }

        public long EtaSeconds
        {
            get { return _eta; }
            set { if (Set(ref _eta, value)) Raise(nameof(EtaText)); }
        }

        public int Priority
        {
            get { return _priority; }
            set { if (Set(ref _priority, value)) Raise(nameof(PriorityText)); }
        }

        public string Error { get { return _error; } set { if (Set(ref _error, value)) Raise(nameof(Details)); } }
        public string Note { get { return _note; } set { if (Set(ref _note, value)) Raise(nameof(Details)); } }

        public string StatusText { get { return _status.ToString(); } }

        public string TypeText
        {
            get { return Kind == DownloadKind.Direct ? "File" : Kind == DownloadKind.Video ? "Video" : "Torrent"; }
        }

        public double Progress
        {
            get
            {
                if (_total > 0) return Math.Min(100.0, _downloaded * 100.0 / _total);
                return _status == QueueStatus.Completed ? 100.0 : 0.0;
            }
        }

        public string ProgressText
        {
            get
            {
                if (_total > 0 || _status == QueueStatus.Completed) return Progress.ToString("0.0", Fmt.Inv) + "%";
                return _status == QueueStatus.Downloading ? "..." : "";
            }
        }

        public string DownloadedText { get { return Fmt.Bytes(_downloaded); } }
        public string TotalText { get { return _total > 0 ? Fmt.Bytes(_total) : "?"; } }
        public string SpeedText { get { return _status == QueueStatus.Downloading ? Fmt.Speed(_speed) : ""; } }
        public string EtaText { get { return _status == QueueStatus.Downloading ? Fmt.Eta(_eta) : ""; } }
        public string PriorityText { get { return _priority >= 2 ? "High" : _priority == 1 ? "Normal" : "Low"; } }

        public string Details
        {
            get
            {
                if (_status == QueueStatus.Failed && !string.IsNullOrEmpty(_error)) return _error;
                return _note ?? "";
            }
        }
    }

    public sealed class ShowRecord : Notifier
    {
        public long Id { get; set; }
        /// <summary>"drama" or "series"</summary>
        public string Kind { get; set; }
        public string Name { get; set; }
        public string Folder { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;

        bool _auto;
        bool _complete;
        DateTime? _lastChecked;

        public bool AutoQueue { get { return _auto; } set { Set(ref _auto, value); } }
        public bool Complete { get { return _complete; } set { Set(ref _complete, value); Raise(nameof(Display)); } }
        public DateTime? LastChecked { get { return _lastChecked; } set { if (Set(ref _lastChecked, value)) Raise(nameof(LastCheckedText)); } }

        public string LastCheckedText { get { return Fmt.Date(_lastChecked); } }
        public string KindText { get { return Kind == "series" ? "Series" : "Drama"; } }
        public string Display { get { return _complete ? Name + "  (complete)" : Name; } }
    }

    public sealed class PlaylistRecord
    {
        public long Id { get; set; }
        public long ShowId { get; set; }
        public int? Season { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public int ItemCount { get; set; }
        public DateTime? LastChecked { get; set; }
    }

    public sealed class EpisodeRecord
    {
        public long Id { get; set; }
        public long ShowId { get; set; }
        public long PlaylistId { get; set; }
        public int? Season { get; set; }
        public int? EpisodeNumber { get; set; }
        public int Position { get; set; }
        public string VideoId { get; set; }
        public string Key { get; set; }
        public string Url { get; set; }
        public string Title { get; set; }
        public EpisodeStatus Status { get; set; }
        public string LocalPath { get; set; }
        public DateTime DiscoveredAt { get; set; } = DateTime.Now;
    }

    public sealed class LocalFile
    {
        public long Id { get; set; }
        public long ShowId { get; set; }
        public int? Season { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public int? EpisodeNumber { get; set; }
        public string Source { get; set; }
    }

    public sealed class HistoryRow
    {
        public long Id { get; set; }
        public string FileName { get; set; }
        public string Url { get; set; }
        public DateTime Date { get; set; }
        public long Size { get; set; }
        public string Quality { get; set; }
        public string Destination { get; set; }
        public string Type { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public string SizeText { get { return Size > 0 ? Fmt.Bytes(Size) : ""; } }
        public string DateText { get { return Date.ToString("yyyy-MM-dd HH:mm", Fmt.Inv); } }
    }

    /// <summary>One item of a parsed playlist. Playlist position and episode number are kept SEPARATELY.</summary>
    public sealed class PlaylistItem : Notifier
    {
        bool _selected;
        int? _episode;
        string _status = "New";
        string _title;

        public int Position { get; set; }
        public string VideoId { get; set; }
        public string Url { get; set; }
        public int? Season { get; set; }
        public bool Unavailable { get; set; }

        public string Title { get { return _title; } set { Set(ref _title, value); } }
        public bool IsSelected { get { return _selected; } set { Set(ref _selected, value); } }
        public string Status { get { return _status; } set { if (Set(ref _status, value)) Raise(nameof(CanSelect)); } }

        public int? EpisodeNumber
        {
            get { return _episode; }
            set { if (Set(ref _episode, value)) Raise(nameof(EpisodeText)); }
        }

        public string EpisodeText
        {
            get { return _episode.HasValue ? _episode.Value.ToString(CultureInfo.InvariantCulture) : ""; }
            set
            {
                int n;
                EpisodeNumber = int.TryParse((value ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0 ? (int?)n : null;
            }
        }

        /// <summary>Stable identity: the video id when known, otherwise the URL.</summary>
        public string Key { get { return string.IsNullOrEmpty(VideoId) ? Url : VideoId; } }

        /// <summary>Only items that are not already downloaded / queued can be selected.</summary>
        public bool CanSelect
        {
            get { return !Unavailable && Status != "Downloaded" && Status != "Queued" && Status != "Downloading"; }
        }
    }

    public sealed class NewEpisodeRow : Notifier
    {
        bool _selected;
        public bool IsSelected { get { return _selected; } set { Set(ref _selected, value); } }
        public ShowRecord Show { get; set; }
        public string ShowName { get { return Show.Name; } }
        public int? Season { get; set; }
        public PlaylistItem Item { get; set; }
        public long PlaylistId { get; set; }
        public string DestFolder { get; set; }
        public string Status { get { return "New"; } }
        public string EpisodeText { get { return Item.EpisodeText; } }
        public string SeasonText { get { return Season.HasValue ? Season.Value.ToString(CultureInfo.InvariantCulture) : ""; } }
        public string Title { get { return Item.Title; } }
        public string Url { get { return Item.Url; } }
    }

    public sealed class AppSettings
    {
        public string DownloadFolder { get; set; }
        public string DramasFolder { get; set; }
        public string SeriesFolder { get; set; }
        public string TorrentsFolder { get; set; }
        public int MaxConcurrent { get; set; } = 3;
        public int SpeedLimitKBps { get; set; } = 0;
        public int RetryCount { get; set; } = 3;
        public string DefaultQuality { get; set; } = "Best Available";
        public bool DarkTheme { get; set; } = false;
        public bool NotifyCompleted { get; set; } = true;
        public bool NotifyFailed { get; set; } = true;
        public bool NotifyNewEpisodes { get; set; } = true;
        public bool AutoCheck { get; set; } = false;
        public int AutoCheckHours { get; set; } = 6;
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool ShutdownWhenDone { get; set; } = false;
        public bool NeverOverwrite { get; set; } = true;
        /// <summary>Ask, Skip or Allow</summary>
        public string DuplicateHandling { get; set; } = "Skip";

        public AppSettings()
        {
            string b = AppPaths.DefaultBase();
            DownloadFolder = b;
            DramasFolder = System.IO.Path.Combine(b, "Dramas");
            SeriesFolder = System.IO.Path.Combine(b, "Series");
            TorrentsFolder = System.IO.Path.Combine(b, "Torrents");
        }

        public void Load(Dictionary<string, string> d)
        {
            DownloadFolder = Str(d, "DownloadFolder", DownloadFolder);
            DramasFolder = Str(d, "DramasFolder", DramasFolder);
            SeriesFolder = Str(d, "SeriesFolder", SeriesFolder);
            TorrentsFolder = Str(d, "TorrentsFolder", TorrentsFolder);
            MaxConcurrent = Math.Max(1, Math.Min(20, Int(d, "MaxConcurrent", MaxConcurrent)));
            SpeedLimitKBps = Math.Max(0, Int(d, "SpeedLimitKBps", SpeedLimitKBps));
            RetryCount = Math.Max(0, Math.Min(20, Int(d, "RetryCount", RetryCount)));
            DefaultQuality = Str(d, "DefaultQuality", DefaultQuality);
            DarkTheme = Bool(d, "DarkTheme", DarkTheme);
            NotifyCompleted = Bool(d, "NotifyCompleted", NotifyCompleted);
            NotifyFailed = Bool(d, "NotifyFailed", NotifyFailed);
            NotifyNewEpisodes = Bool(d, "NotifyNewEpisodes", NotifyNewEpisodes);
            AutoCheck = Bool(d, "AutoCheck", AutoCheck);
            AutoCheckHours = Math.Max(1, Math.Min(168, Int(d, "AutoCheckHours", AutoCheckHours)));
            StartWithWindows = Bool(d, "StartWithWindows", StartWithWindows);
            StartMinimized = Bool(d, "StartMinimized", StartMinimized);
            MinimizeToTray = Bool(d, "MinimizeToTray", MinimizeToTray);
            ShutdownWhenDone = Bool(d, "ShutdownWhenDone", ShutdownWhenDone);
            NeverOverwrite = Bool(d, "NeverOverwrite", NeverOverwrite);
            DuplicateHandling = Str(d, "DuplicateHandling", DuplicateHandling);
        }

        public Dictionary<string, string> ToDictionary()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            d["DownloadFolder"] = DownloadFolder;
            d["DramasFolder"] = DramasFolder;
            d["SeriesFolder"] = SeriesFolder;
            d["TorrentsFolder"] = TorrentsFolder;
            d["MaxConcurrent"] = MaxConcurrent.ToString(Fmt.Inv);
            d["SpeedLimitKBps"] = SpeedLimitKBps.ToString(Fmt.Inv);
            d["RetryCount"] = RetryCount.ToString(Fmt.Inv);
            d["DefaultQuality"] = DefaultQuality;
            d["DarkTheme"] = B(DarkTheme);
            d["NotifyCompleted"] = B(NotifyCompleted);
            d["NotifyFailed"] = B(NotifyFailed);
            d["NotifyNewEpisodes"] = B(NotifyNewEpisodes);
            d["AutoCheck"] = B(AutoCheck);
            d["AutoCheckHours"] = AutoCheckHours.ToString(Fmt.Inv);
            d["StartWithWindows"] = B(StartWithWindows);
            d["StartMinimized"] = B(StartMinimized);
            d["MinimizeToTray"] = B(MinimizeToTray);
            d["ShutdownWhenDone"] = B(ShutdownWhenDone);
            d["NeverOverwrite"] = B(NeverOverwrite);
            d["DuplicateHandling"] = DuplicateHandling;
            return d;
        }

        static string B(bool b) { return b ? "1" : "0"; }
        static string Str(Dictionary<string, string> d, string k, string def)
        {
            string v;
            return d.TryGetValue(k, out v) && !string.IsNullOrWhiteSpace(v) ? v : def;
        }
        static int Int(Dictionary<string, string> d, string k, int def)
        {
            string v; int n;
            return d.TryGetValue(k, out v) && int.TryParse(v, NumberStyles.Integer, Fmt.Inv, out n) ? n : def;
        }
        static bool Bool(Dictionary<string, string> d, string k, bool def)
        {
            string v;
            return d.TryGetValue(k, out v) ? v == "1" : def;
        }
    }
}

