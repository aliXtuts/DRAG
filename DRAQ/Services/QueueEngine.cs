using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using DRAQ.Database;
using DRAQ.Models;
using DRAQ.Utilities;

namespace DRAQ.Services
{
    // 15122003
    public enum StopReason { None, Pause, Cancel, Remove, Shutdown }

    public sealed class Aria2DownloadException : DownloadException
    {
        public int Code { get; private set; }
        public Aria2DownloadException(string message, int code, bool noRetry) : base(message, noRetry) { Code = code; }
    }

    sealed class Job
    {
        public QueueItem Item;
        public CancellationTokenSource Cts = new CancellationTokenSource();
        public StopReason Reason = StopReason.None;
        public Task Task;
    }

    public sealed class AddResult
    {
        public List<QueueItem> Added = new List<QueueItem>();
        public int SkippedDuplicates;
    }

    /// <summary>THE queue. Every download type (single files, videos, dramas, series, torrents, new episodes) goes through this class.</summary>
    public sealed class QueueEngine : IDisposable
    {
        readonly Db _db;
        readonly AppSettings _s;
        readonly Aria2 _aria = new Aria2();
        readonly Dispatcher _ui;
        readonly object _lock = new object();
        readonly List<QueueItem> _all = new List<QueueItem>();
        readonly Dictionary<long, Job> _running = new Dictionary<long, Job>();
        Timer _timer;
        int _ticking;
        bool _hadActivity;
        DateTime _lastSave = DateTime.MinValue;
        bool _disposed;

        public ObservableCollection<QueueItem> Items { get; private set; }

        /// <summary>kind: done | fail | disk | queue. Raised on a background thread.</summary>
        public event Action<string, string, string> Notification;
        /// <summary>Raised whenever an episode's status was changed by the queue.</summary>
        public event Action EpisodesChanged;
        /// <summary>Raised once when everything is finished and nothing is paused/queued (used for "shut down when done").</summary>
        public event Action QueueIdle;
        /// <summary>Asked when duplicates are found and the setting is "Ask". Argument = number of duplicates. Runs on the UI thread.</summary>
        public Func<int, bool> ConfirmDuplicates;

        public QueueEngine(Db db, AppSettings settings, Dispatcher ui)
        {
            _db = db;
            _s = settings;
            _ui = ui;
            Items = new ObservableCollection<QueueItem>();
        }

        // ------------------------------------------------------------------ start / stop
        public void Start()
        {
            List<QueueItem> loaded = _db.LoadQueue();
            foreach (QueueItem q in loaded)
            {
                if (q.Status == QueueStatus.Downloading) q.Status = QueueStatus.Queued; // interrupted by exit/power loss: resume
                q.Speed = 0;
                q.EtaSeconds = 0;
                _all.Add(q);
                Items.Add(q);
            }
            ApplySettings();
            _timer = new Timer(delegate { Tick(); }, null, 1000, 1000);
        }

        public void ApplySettings()
        {
            long limit = Math.Max(0, _s.SpeedLimitKBps) * 1024L;
            Task t = _aria.SetLimitAsync(limit);
        }

        public void Shutdown()
        {
            _disposed = true;
            if (_timer != null) { _timer.Dispose(); _timer = null; }
            List<Job> jobs;
            lock (_lock)
            {
                jobs = _running.Values.ToList();
                foreach (Job j in jobs) { j.Reason = StopReason.Shutdown; j.Cts.Cancel(); }
            }
            try { Task.WaitAll(jobs.Where(j => j.Task != null).Select(j => j.Task).ToArray(), 8000); } catch { }
            lock (_lock)
            {
                foreach (QueueItem q in _all) Persist(q);
            }
        }

        public void Dispose()
        {
            if (!_disposed) Shutdown();
            _aria.Dispose();
        }

        void Ui(Action a)
        {
            if (_ui == null || _ui.CheckAccess()) a();
            else _ui.BeginInvoke(a);
        }

        void Persist(QueueItem q)
        {
            try { _db.UpdateQueue(q); }
            catch (Exception ex) { Log.Error("Could not save queue item " + q.Id.ToString(CultureInfo.InvariantCulture), ex); }
        }

        void Notify(string kind, string title, string msg)
        {
            Action<string, string, string> h = Notification;
            if (h != null) { try { h(kind, title, msg); } catch (Exception ex) { Log.Error("Notification handler failed", ex); } }
        }

        // ------------------------------------------------------------------ adding
        public bool IsDuplicate(QueueItem q)
        {
            lock (_lock)
            {
                foreach (QueueItem x in _all)
                {
                    if (x.Status == QueueStatus.Failed || x.Status == QueueStatus.Cancelled) continue;
                    if (!string.IsNullOrEmpty(q.VideoId) && !string.IsNullOrEmpty(x.VideoId) && q.VideoId == x.VideoId && x.Kind == q.Kind) return true;
                    if (string.Equals(x.Url, q.Url, StringComparison.OrdinalIgnoreCase) && x.Kind == q.Kind
                        && string.Equals(x.DestDir, q.DestDir, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>Adds items to the queue, applying the duplicate setting (Ask / Skip / Allow). Call from the UI thread when interactive.</summary>
        public AddResult AddMany(IEnumerable<QueueItem> items, bool interactive)
        {
            AddResult res = new AddResult();
            List<QueueItem> list = items.ToList();
            List<QueueItem> dups = list.Where(IsDuplicate).ToList();
            bool addDuplicates = _s.DuplicateHandling == "Allow";
            if (dups.Count > 0 && _s.DuplicateHandling == "Ask" && interactive && ConfirmDuplicates != null)
                addDuplicates = ConfirmDuplicates(dups.Count);

            foreach (QueueItem q in list)
            {
                if (!addDuplicates && dups.Contains(q)) { res.SkippedDuplicates++; continue; }
                Add(q);
                res.Added.Add(q);
            }
            return res;
        }

        public void Add(QueueItem q)
        {
            lock (_lock)
            {
                q.SortOrder = (_all.Count == 0 ? 0 : _all.Max(x => x.SortOrder)) + 1;
                q.Status = QueueStatus.Queued;
                q.CreatedAt = DateTime.Now;
                _db.InsertQueue(q);
                _all.Add(q);
            }
            Ui(delegate { Items.Add(q); });
            SyncEpisode(q);
        }

        // ------------------------------------------------------------------ user commands
        public void Pause(QueueItem q)
        {
            lock (_lock)
            {
                Job j;
                if (q.Status == QueueStatus.Downloading && _running.TryGetValue(q.Id, out j))
                {
                    j.Reason = StopReason.Pause;
                    j.Cts.Cancel();
                }
                else if (q.Status == QueueStatus.Queued)
                {
                    q.Status = QueueStatus.Paused;
                    Persist(q);
                    SyncEpisode(q);
                }
            }
        }

        public void Resume(QueueItem q)
        {
            lock (_lock)
            {
                if (q.Status != QueueStatus.Paused) return;
                q.Status = QueueStatus.Queued;
                q.NotBefore = DateTime.MinValue;
                q.Error = null;
                Persist(q);
            }
            SyncEpisode(q);
        }

        public void Cancel(QueueItem q)
        {
            bool cleanup = false;
            lock (_lock)
            {
                Job j;
                if (q.Status == QueueStatus.Downloading && _running.TryGetValue(q.Id, out j))
                {
                    j.Reason = StopReason.Cancel;
                    j.Cts.Cancel();
                }
                else if (q.Status == QueueStatus.Queued || q.Status == QueueStatus.Paused || q.Status == QueueStatus.Failed)
                {
                    q.Status = QueueStatus.Cancelled;
                    q.Note = null;
                    Persist(q);
                    cleanup = true;
                }
            }
            if (cleanup)
            {
                CleanupPartials(q);
                SyncEpisode(q);
                AddHistory(q, "Cancelled");
            }
        }

        public void Retry(QueueItem q)
        {
            lock (_lock)
            {
                if (q.Status != QueueStatus.Failed && q.Status != QueueStatus.Cancelled) return;
                q.Status = QueueStatus.Queued;
                q.RetriesDone = 0;
                q.Error = null;
                q.Note = null;
                q.NotBefore = DateTime.MinValue;
                Persist(q);
            }
            SyncEpisode(q);
        }

        public void Remove(QueueItem q)
        {
            bool wasDone = q.Status == QueueStatus.Completed;
            lock (_lock)
            {
                Job j;
                if (_running.TryGetValue(q.Id, out j))
                {
                    j.Reason = StopReason.Remove;
                    j.Cts.Cancel();
                }
                _all.Remove(q);
                try { _db.DeleteQueue(q.Id); } catch (Exception ex) { Log.Error("Delete queue item failed", ex); }
            }
            Ui(delegate { Items.Remove(q); });
            if (!wasDone && q.EpisodeId > 0)
            {
                try { _db.SetEpisodeStatus(q.EpisodeId, EpisodeStatus.NotDownloaded, null); } catch { }
                Action h = EpisodesChanged; if (h != null) h();
            }
        }

        public void SetPriority(QueueItem q, int priority)
        {
            lock (_lock) { q.Priority = priority; Persist(q); }
        }

        public void MoveUp(QueueItem q) { Move(q, -1); }
        public void MoveDown(QueueItem q) { Move(q, 1); }

        void Move(QueueItem q, int delta)
        {
            // Must run on the UI thread (it reorders the visible collection).
            int idx = Items.IndexOf(q);
            int to = idx + delta;
            if (idx < 0 || to < 0 || to >= Items.Count) return;
            QueueItem other = Items[to];
            lock (_lock)
            {
                int tmp = q.SortOrder;
                q.SortOrder = other.SortOrder;
                other.SortOrder = tmp;
                Persist(q);
                Persist(other);
            }
            Items.Move(idx, to);
        }

        public void PauseAll()
        {
            foreach (QueueItem q in Snapshot()) if (q.Status == QueueStatus.Queued || q.Status == QueueStatus.Downloading) Pause(q);
        }

        public void ResumeAll()
        {
            foreach (QueueItem q in Snapshot()) if (q.Status == QueueStatus.Paused) Resume(q);
        }

        public void RetryFailed()
        {
            foreach (QueueItem q in Snapshot()) if (q.Status == QueueStatus.Failed) Retry(q);
        }

        public List<QueueItem> Snapshot()
        {
            lock (_lock) { return _all.ToList(); }
        }

        public void GetCounts(out int downloading, out int queued, out int paused, out long speed)
        {
            lock (_lock)
            {
                downloading = _all.Count(i => i.Status == QueueStatus.Downloading);
                queued = _all.Count(i => i.Status == QueueStatus.Queued);
                paused = _all.Count(i => i.Status == QueueStatus.Paused);
                speed = _all.Where(i => i.Status == QueueStatus.Downloading).Sum(i => i.Speed);
            }
        }

        // ------------------------------------------------------------------ scheduler
        void Tick()
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
            try
            {
                lock (_lock)
                {
                    int slots = Math.Max(1, _s.MaxConcurrent) - _running.Count;
                    if (slots > 0)
                    {
                        DateTime now = DateTime.Now;
                        List<QueueItem> next = _all
                            .Where(i => i.Status == QueueStatus.Queued && i.NotBefore <= now && !_running.ContainsKey(i.Id))
                            .OrderByDescending(i => i.Priority).ThenBy(i => i.SortOrder)
                            .Take(slots).ToList();
                        foreach (QueueItem it in next) StartJobLocked(it);
                    }
                }
                if ((DateTime.Now - _lastSave).TotalSeconds >= 5)
                {
                    _lastSave = DateTime.Now;
                    List<QueueItem> active;
                    lock (_lock) { active = _all.Where(i => i.Status == QueueStatus.Downloading).ToList(); }
                    foreach (QueueItem q in active) Persist(q);
                }
                CheckIdle();
            }
            catch (Exception ex) { Log.Error("Scheduler tick failed", ex); }
            finally { Interlocked.Exchange(ref _ticking, 0); }
        }

        void StartJobLocked(QueueItem it)
        {
            it.Status = QueueStatus.Downloading;
            it.Error = null;
            it.Speed = 0;
            it.EtaSeconds = 0;
            _hadActivity = true;
            Job job = new Job { Item = it };
            _running[it.Id] = job;
            Persist(it);
            job.Task = Task.Run(() => RunJob(job));
            SyncEpisode(it);
        }

        void CheckIdle()
        {
            bool active, paused, fire = false;
            lock (_lock)
            {
                active = _all.Any(i => i.Status == QueueStatus.Queued || i.Status == QueueStatus.Downloading);
                paused = _all.Any(i => i.Status == QueueStatus.Paused);
                if (!active && _hadActivity)
                {
                    _hadActivity = false;
                    fire = true;
                }
            }
            if (!fire) return;
            Notify("queue", "DRAQ", "Queue completed. All downloads have finished.");
            if (_s.ShutdownWhenDone && !paused)
            {
                Action h = QueueIdle;
                if (h != null) h();
            }
        }

        async Task RunJob(Job job)
        {
            QueueItem it = job.Item;
            try
            {
                PreflightDisk(it);
                switch (it.Kind)
                {
                    case DownloadKind.Direct: await RunDirectAsync(job); break;
                    case DownloadKind.Video: await RunVideoAsync(job); break;
                    default: await RunTorrentAsync(job); break;
                }
                FinishCompleted(it);
            }
            catch (OperationCanceledException ex)
            {
                if (job.Cts.IsCancellationRequested) HandleStopped(job);
                else HandleFailure(it, Describe(ex, it), false); // e.g. an HTTP timeout, not a user action
            }
            catch (Exception ex)
            {
                if (job.Cts.IsCancellationRequested) HandleStopped(job);
                else HandleFailure(it, Describe(ex, it), ex is DownloadException && ((DownloadException)ex).NoRetry);
                if (!(ex is DownloadException)) Log.Error("Download crashed: " + it.Url, ex);
            }
            finally
            {
                lock (_lock) { _running.Remove(it.Id); }
            }
        }

        static string Describe(Exception ex, QueueItem it)
        {
            if (ex is DownloadException) return ex.Message;
            if (ex is UnauthorizedAccessException) return "Permission denied: DRAQ cannot write to " + it.DestDir;
            if (ex is DirectoryNotFoundException) return "The destination folder is not available: " + it.DestDir;
            IOException io = ex as IOException;
            if (io != null)
            {
                int code = io.HResult & 0xFFFF;
                if (code == 112 || code == 39) return "The disk is full.";
                return "File error: " + ex.Message;
            }
            if (ex is HttpRequestException || ex is TaskCanceledException) return HttpProbe.FriendlyNetError(ex);
            if (ex is Aria2Exception) return "Download engine error: " + ex.Message;
            return "Unexpected error: " + ex.Message;
        }

        void PreflightDisk(QueueItem it)
        {
            try { Directory.CreateDirectory(it.DestDir); }
            catch (UnauthorizedAccessException) { throw new DownloadException("Permission denied: DRAQ cannot create the folder " + it.DestDir, true); }
            catch (IOException ex) { throw new DownloadException("Cannot use the destination folder: " + ex.Message, true); }

            string root = Path.GetPathRoot(Path.GetFullPath(it.DestDir));
            if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\")) return;
            long free;
            try { free = new DriveInfo(root).AvailableFreeSpace; }
            catch { return; }
            long remaining = it.Total > it.Downloaded ? it.Total - it.Downloaded : 0;
            long need = remaining > 0 ? remaining + 64L * 1024 * 1024 : 300L * 1024 * 1024;
            if (free < need)
            {
                string msg = "Not enough free disk space on " + root + " (need about " + Fmt.Bytes(need) + ", free " + Fmt.Bytes(free) + ").";
                Notify("disk", "Low disk space", msg);
                throw new DownloadException(msg, true);
            }
        }

        // ------------------------------------------------------------------ job outcomes
        void FinishCompleted(QueueItem it)
        {
            it.Status = QueueStatus.Completed;
            it.Speed = 0;
            it.EtaSeconds = 0;
            it.Error = null;
            it.Note = null;
            it.CompletedAt = DateTime.Now;
            if (it.Total > 0) it.Downloaded = it.Total;
            Persist(it);
            RegisterFile(it);
            SyncEpisode(it);
            AddHistory(it, "Completed");
            Notify("done", "Download finished", it.Title);
        }

        void HandleStopped(Job job)
        {
            QueueItem it = job.Item;
            it.Speed = 0;
            it.EtaSeconds = 0;
            switch (job.Reason)
            {
                case StopReason.Pause:
                    it.Status = QueueStatus.Paused;
                    it.Note = null;
                    break;
                case StopReason.Cancel:
                    it.Status = QueueStatus.Cancelled;
                    it.Note = null;
                    CleanupPartials(it);
                    AddHistory(it, "Cancelled");
                    break;
                case StopReason.Remove:
                    return;
                default:
                    it.Status = QueueStatus.Queued; // app is closing: resume on next start
                    break;
            }
            Persist(it);
            SyncEpisode(it);
        }

        void HandleFailure(QueueItem it, string message, bool noRetry)
        {
            Log.Warn("Download failed (" + it.Url + "): " + message);
            it.Speed = 0;
            it.EtaSeconds = 0;
            if (!noRetry && it.RetriesDone < _s.RetryCount)
            {
                it.RetriesDone++;
                it.NotBefore = DateTime.Now.AddSeconds(5 * it.RetriesDone);
                it.Status = QueueStatus.Queued;
                it.Note = "Retry " + it.RetriesDone.ToString(CultureInfo.InvariantCulture) + "/" + _s.RetryCount.ToString(CultureInfo.InvariantCulture) + " - " + message;
            }
            else
            {
                it.Status = QueueStatus.Failed;
                it.Error = message;
                it.Note = null;
                AddHistory(it, "Failed");
                Notify("fail", "Download failed", (it.Title ?? it.Url) + ": " + message);
            }
            Persist(it);
            SyncEpisode(it);
        }

        void AddHistory(QueueItem it, string status)
        {
            try
            {
                long size = it.Total;
                if (status == "Completed" && !string.IsNullOrEmpty(it.FinalPath))
                {
                    try
                    {
                        if (File.Exists(it.FinalPath)) size = new FileInfo(it.FinalPath).Length;
                    }
                    catch { }
                }
                _db.AddHistory(new HistoryRow
                {
                    FileName = it.Title,
                    Url = it.Url,
                    Date = DateTime.Now,
                    Size = size,
                    Quality = it.Quality,
                    Destination = it.DestDir,
                    Type = it.TypeText,
                    Status = status,
                    Error = it.Error
                });
            }
            catch (Exception ex) { Log.Error("History write failed", ex); }
        }

        void RegisterFile(QueueItem it)
        {
            if (it.ShowId <= 0 || string.IsNullOrEmpty(it.FinalPath) || it.Kind == DownloadKind.Torrent) return;
            try
            {
                int? season = null, number = null;
                if (it.EpisodeId > 0)
                {
                    EpisodeRecord ep = _db.GetEpisode(it.EpisodeId);
                    if (ep != null) { season = ep.Season; number = ep.EpisodeNumber; }
                }
                long size = 0;
                try { size = new FileInfo(it.FinalPath).Length; } catch { }
                _db.UpsertFile(new LocalFile { ShowId = it.ShowId, Season = season, Path = it.FinalPath, Size = size, EpisodeNumber = number, Source = "download" });
            }
            catch (Exception ex) { Log.Error("Could not register downloaded file", ex); }
        }

        void SyncEpisode(QueueItem it)
        {
            if (it.EpisodeId <= 0) return;
            EpisodeStatus st;
            switch (it.Status)
            {
                case QueueStatus.Downloading: st = EpisodeStatus.Downloading; break;
                case QueueStatus.Completed: st = EpisodeStatus.Downloaded; break;
                case QueueStatus.Failed: st = EpisodeStatus.Failed; break;
                case QueueStatus.Cancelled: st = EpisodeStatus.NotDownloaded; break;
                default: st = EpisodeStatus.Queued; break;
            }
            try { _db.SetEpisodeStatus(it.EpisodeId, st, it.Status == QueueStatus.Completed ? it.FinalPath : null); }
            catch (Exception ex) { Log.Error("Episode status update failed", ex); }
            Action h = EpisodesChanged;
            if (h != null) h();
        }

        void CleanupPartials(QueueItem it)
        {
            try
            {
                if (it.Kind == DownloadKind.Direct && !string.IsNullOrEmpty(it.FinalPath))
                {
                    FileNames.TryDelete(it.FinalPath + ".part");
                    FileNames.TryDelete(it.FinalPath + ".part.aria2");
                }
                else if (it.Kind == DownloadKind.Video && !string.IsNullOrEmpty(it.FileName) && Directory.Exists(it.DestDir))
                {
                    foreach (string f in Directory.EnumerateFiles(it.DestDir))
                    {
                        string n = Path.GetFileName(f);
                        if (n.StartsWith(it.FileName + ".", StringComparison.OrdinalIgnoreCase) &&
                            (n.Contains(".part") || n.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)))
                            FileNames.TryDelete(f);
                    }
                }
            }
            catch (Exception ex) { Log.Warn("Cleanup failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------ direct downloads (aria2)
        async Task RunDirectAsync(Job job)
        {
            QueueItem it = job.Item;
            if (string.IsNullOrEmpty(it.FinalPath))
            {
                string name = FileNames.Sanitize(!string.IsNullOrWhiteSpace(it.FileName) ? it.FileName : FileNames.FromUrl(it.Url), 150);
                it.FinalPath = FileNames.Unique(Path.Combine(it.DestDir, name), !_s.NeverOverwrite, true);
                it.Title = Path.GetFileName(it.FinalPath);
                Persist(it);
            }
            string part = it.FinalPath + ".part";
            Dictionary<string, string> opts = new Dictionary<string, string>();
            opts["dir"] = Path.GetDirectoryName(it.FinalPath);
            opts["out"] = Path.GetFileName(part);
            opts["allow-overwrite"] = "true";
            opts["continue"] = "true";

            try
            {
                await RunAria2Async(job, delegate { return _aria.AddUriAsync(it.Url, opts); });
            }
            catch (Aria2DownloadException ex)
            {
                if (ex.Code == 8)
                {
                    // Server cannot resume: drop the partial data so the retry starts cleanly.
                    FileNames.TryDelete(part);
                    FileNames.TryDelete(part + ".aria2");
                    it.Downloaded = 0;
                }
                throw;
            }

            if (!File.Exists(part))
                throw new DownloadException("The download finished but the file was not found on disk.");
            string target = it.FinalPath;
            if (File.Exists(target)) target = FileNames.Unique(target, false);
            File.Move(part, target);
            FileNames.TryDelete(part + ".aria2");
            it.FinalPath = target;
            it.Title = Path.GetFileName(target);
            try { long len = new FileInfo(target).Length; it.Total = len; it.Downloaded = len; } catch { }
        }

        // ------------------------------------------------------------------ torrents (aria2)
        async Task RunTorrentAsync(Job job)
        {
            QueueItem it = job.Item;
            Dictionary<string, string> opts = new Dictionary<string, string>();
            opts["dir"] = it.DestDir;
            opts["seed-time"] = "0";
            opts["allow-overwrite"] = "true";

            Func<Task<string>> add;
            if (!string.IsNullOrEmpty(it.TorrentFile))
            {
                if (!File.Exists(it.TorrentFile))
                    throw new DownloadException("The saved .torrent file is missing: " + it.TorrentFile, true);
                add = delegate { return _aria.AddTorrentAsync(it.TorrentFile, opts); };
            }
            else
            {
                add = delegate { return _aria.AddUriAsync(it.Url, opts); };
            }

            Aria2Status st = await RunAria2Async(job, add);
            string name = !string.IsNullOrWhiteSpace(st.BtName) ? st.BtName : it.Title;
            it.Title = name;
            it.FinalPath = Path.Combine(it.DestDir, FileNames.Sanitize(name, 200));
        }

        async Task<Aria2Status> RunAria2Async(Job job, Func<Task<string>> add)
        {
            QueueItem it = job.Item;
            CancellationToken ct = job.Cts.Token;
            await _aria.EnsureRunningAsync();
            it.Gid = await add();
            Persist(it);
            int rpcFails = 0;

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    await _aria.RemoveAsync(it.Gid);
                    throw new OperationCanceledException(ct);
                }

                Aria2Status st;
                try
                {
                    st = await _aria.TellStatusAsync(it.Gid);
                    rpcFails = 0;
                }
                catch (Exception ex)
                {
                    rpcFails++;
                    if (rpcFails > 6) throw new DownloadException("Lost contact with the download engine: " + ex.Message);
                    bool restarted = false;
                    try { restarted = await _aria.EnsureRunningAsync(); } catch (Exception e2) { Log.Error("aria2 restart failed", e2); }
                    if (restarted || ex is Aria2Exception)
                    {
                        try { it.Gid = await add(); } catch (Exception e3) { Log.Warn("Re-adding download failed: " + e3.Message); }
                    }
                    await DelayCt(ct, 1000);
                    continue;
                }

                if (it.Kind == DownloadKind.Torrent && st.IsBt && !string.IsNullOrWhiteSpace(st.BtName) && it.Title != st.BtName)
                    it.Title = st.BtName;

                if (st.Status == "complete")
                {
                    if (!string.IsNullOrEmpty(st.FollowedBy))
                    {
                        it.Gid = st.FollowedBy; // magnet link / .torrent URL: metadata finished, the real download continues
                        it.Note = "Starting download...";
                        Persist(it);
                        continue;
                    }
                    if (st.Total > 0) it.Total = st.Total;
                    it.Downloaded = st.Completed > 0 ? st.Completed : it.Downloaded;
                    return st;
                }
                if (st.Status == "error")
                {
                    int c = st.ErrorCode;
                    bool noRetry = c == 9 || c == 13 || c == 25 || c == 26 || c == 27 || c == 24;
                    throw new Aria2DownloadException(Aria2.ErrorText(c, st.ErrorMessage), c, noRetry);
                }
                if (st.Status == "removed")
                {
                    throw new DownloadException("The download was removed from the download engine.");
                }

                if (st.Total > 0) it.Total = st.Total;
                it.Downloaded = st.Completed;
                it.Speed = st.Speed;
                it.EtaSeconds = (st.Speed > 0 && st.Total > st.Completed) ? (st.Total - st.Completed) / st.Speed : 0;

                if (st.IsBt)
                {
                    if (st.Total == 0) it.Note = "Fetching torrent information...";
                    else if (st.Connections == 0 && st.Speed == 0) it.Note = "Looking for peers...";
                    else it.Note = "Connections: " + st.Connections.ToString(CultureInfo.InvariantCulture) + ", seeders: " + st.Seeders.ToString(CultureInfo.InvariantCulture);

                    if (st.Total > 0 && st.Completed >= st.Total)
                    {
                        await _aria.RemoveAsync(it.Gid); // finished; do not keep seeding
                        return st;
                    }
                }
                else
                {
                    it.Note = st.Status == "waiting" ? "Waiting..." : null;
                }

                await DelayCt(ct, 1000);
            }
        }

        static async Task DelayCt(CancellationToken ct, int ms)
        {
            try { await Task.Delay(ms, ct); } catch (OperationCanceledException) { }
        }

        // ------------------------------------------------------------------ video downloads (yt-dlp)
        async Task RunVideoAsync(Job job)
        {
            QueueItem it = job.Item;
            ToolPaths.Require(ToolPaths.YtDlp);
            if (string.IsNullOrEmpty(it.FileName))
            {
                it.FileName = FileNames.UniqueBase(it.DestDir, FileNames.Sanitize(it.Title, 110), !_s.NeverOverwrite);
                Persist(it);
            }
            string template = Path.Combine(it.DestDir, it.FileName).Replace("%", "%%") + ".%(ext)s";
            string format = string.IsNullOrWhiteSpace(it.Format) ? YtDlp.BuildFormat("Best Available", null) : it.Format;

            List<string> args = YtDlp.Common(
                "--newline", "--no-playlist",
                "--ffmpeg-location", AppPaths.ToolsDir,
                "-f", format,
                "--merge-output-format", "mp4/mkv",
                "-o", template,
                _s.NeverOverwrite ? "--no-overwrites" : "--force-overwrites",
                "--continue", "--retries", "5", "--fragment-retries", "10", "--concurrent-fragments", "4",
                "--progress-template", "download:DRAQPROG|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s",
                "--print", "after_move:DRAQFILE|%(filepath)s",
                "--no-quiet");
            args.Remove("--no-warnings"); // keep yt-dlp warnings so problems are visible in the log
            if (_s.SpeedLimitKBps > 0)
            {
                int per = Math.Max(1, _s.SpeedLimitKBps / Math.Max(1, _s.MaxConcurrent));
                args.Add("--limit-rate");
                args.Add(per.ToString(CultureInfo.InvariantCulture) + "K");
            }
            if (!string.IsNullOrEmpty(it.ExtraArgs))
            {
                foreach (string a in it.ExtraArgs.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(a)) args.Add(a.Trim());
            }
            args.Add(it.Url);

            string finalFile = null;
            int streams = 0;
            Action<string> onOut = delegate (string line)
            {
                if (line.StartsWith("DRAQPROG|", StringComparison.Ordinal))
                {
                    string[] p = line.Split('|');
                    if (p.Length >= 7)
                    {
                        long down = (long)Num(p[2]);
                        long total = (long)Num(p[3]);
                        long est = (long)Num(p[4]);
                        long speed = (long)Num(p[5]);
                        long eta = (long)Num(p[6]);
                        if (total <= 0) total = est;
                        if (total > 0) it.Total = total;
                        it.Downloaded = down;
                        it.Speed = speed;
                        it.EtaSeconds = eta;
                    }
                }
                else if (line.StartsWith("DRAQFILE|", StringComparison.Ordinal))
                {
                    finalFile = line.Substring(9).Trim();
                }
                else if (line.StartsWith("[download] Destination:", StringComparison.Ordinal))
                {
                    streams++;
                    it.Note = "Downloading stream " + streams.ToString(CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("[Merger]", StringComparison.Ordinal) || line.StartsWith("[VideoRemuxer]", StringComparison.Ordinal)
                         || line.StartsWith("[EmbedSubtitle]", StringComparison.Ordinal) || line.StartsWith("[ExtractAudio]", StringComparison.Ordinal))
                {
                    it.Note = "Processing with FFmpeg...";
                    it.Speed = 0;
                    it.EtaSeconds = 0;
                }
            };

            Log.Info("yt-dlp format for \"" + it.Title + "\": " + format);
            Action<string> onErr = delegate (string l)
            {
                if (l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) || l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                    Log.Warn("yt-dlp [" + it.Title + "] " + l);
            };
            ProcResult res = await Proc.RunAsync(ToolPaths.YtDlp, args, job.Cts.Token, 0, onOut, onErr);
            if (res.Canceled) throw new OperationCanceledException(job.Cts.Token);
            if (res.StartFailed) throw new DownloadException(res.Err, true);
            if (res.ExitCode != 0) throw new DownloadException(YtDlp.FriendlyError(res.Err));

            if (string.IsNullOrEmpty(finalFile) || !File.Exists(finalFile))
                finalFile = FindOutput(it.DestDir, it.FileName);
            if (finalFile == null)
                throw new DownloadException("yt-dlp finished but the output file was not found in " + it.DestDir);
            ToolPaths.Require(ToolPaths.Ffprobe);
            ProcResult probe = await Proc.RunAsync(ToolPaths.Ffprobe,
                new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_type", "-of", "default=noprint_wrappers=1:nokey=1", finalFile },
                job.Cts.Token, 30000);
            if (probe.Canceled) throw new OperationCanceledException(job.Cts.Token);
            if (probe.StartFailed || probe.ExitCode != 0 || !probe.Out.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(x => x.Trim() == "video"))
                throw new DownloadException("The downloaded file has no video stream. Check the selected quality and update yt-dlp in Settings.", true);

            it.FinalPath = finalFile;
            try { long len = new FileInfo(finalFile).Length; it.Total = len; it.Downloaded = len; } catch { }
        }

        static double Num(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : 0;
        }

        static string FindOutput(string dir, string baseName)
        {
            try
            {
                return Directory.EnumerateFiles(dir)
                    .Where(f =>
                    {
                        string n = Path.GetFileName(f);
                        if (!n.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)) return false;
                        string l = n.ToLowerInvariant();
                        return !(l.Contains(".part") || l.EndsWith(".ytdl") || l.EndsWith(".vtt") || l.EndsWith(".srt") || l.EndsWith(".json") || l.EndsWith(".aria2"));
                    })
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .FirstOrDefault();
            }
            catch { return null; }
        }
    }
}

