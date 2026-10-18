using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DRAQ.Database;
using DRAQ.Models;
using DRAQ.Utilities;

namespace DRAQ.Services
{
    public sealed class CheckOutcome
    {
        public ShowRecord Show;
        public List<NewEpisodeRow> Rows = new List<NewEpisodeRow>();
        public int QueuedAutomatically;
        public int AlreadyOwned;
        public string Error;
    }

    /// <summary>A video file found when importing an existing folder.</summary>
    public sealed class ImportEntry : Notifier
    {
        bool _selected = true;
        int? _episode;
        public bool IsSelected { get { return _selected; } set { Set(ref _selected, value); } }
        public string Path { get; set; }
        public string RelPath { get; set; }
        public long Size { get; set; }
        public string SizeText { get { return Fmt.Bytes(Size); } }
        public int? Episode { get { return _episode; } set { if (Set(ref _episode, value)) Raise(nameof(EpisodeText)); } }
        public string EpisodeText
        {
            get { return _episode.HasValue ? _episode.Value.ToString(CultureInfo.InvariantCulture) : ""; }
            set
            {
                int n;
                Episode = int.TryParse((value ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0 ? (int?)n : null;
            }
        }
    }

    /// <summary>One file in the rename table. Episode numbers are typed in by the user.</summary>
    public sealed class RenameEntry : Notifier
    {
        int? _episode;
        string _newName = "";
        public string FullPath { get; set; }
        public string Name { get { return System.IO.Path.GetFileName(FullPath); } }
        public string RelDir { get; set; }
        public long Size { get; set; }
        public int? Suggested { get; set; }
        public string SizeText { get { return Fmt.Bytes(Size); } }
        public string SuggestedText { get { return Suggested.HasValue ? Suggested.Value.ToString(CultureInfo.InvariantCulture) : ""; } }
        public int? Episode { get { return _episode; } set { if (Set(ref _episode, value)) Raise(nameof(EpisodeText)); } }
        public string RawText { get; private set; }
        public string EpisodeText
        {
            get { return RawText != null ? RawText : (_episode.HasValue ? _episode.Value.ToString(CultureInfo.InvariantCulture) : ""); }
            set
            {
                RawText = (value ?? "").Trim();
                int n;
                if (RawText.Length == 0) _episode = null;
                else if (int.TryParse(RawText, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0) _episode = n;
                else _episode = null;
                Raise(nameof(Episode));
                Raise(nameof(EpisodeText));
            }
        }
        /// <summary>True when something was typed but it is not a valid positive number.</summary>
        public bool InvalidNumber { get { return !string.IsNullOrEmpty(RawText) && !_episode.HasValue; } }
        public string NewName { get { return _newName; } set { Set(ref _newName, value); } }
        public void SetEpisodeDirect(int? n) { RawText = null; Episode = n; }
    }

    public sealed class RenameOp
    {
        public RenameEntry Entry;
        public string From;
        public string To;
        public string Temp;
        public int Episode;
        public bool Unchanged { get { return string.Equals(From, To, StringComparison.Ordinal); } }
    }

    public sealed class RenamePlan
    {
        public List<string> Errors = new List<string>();
        public List<RenameOp> Ops = new List<RenameOp>();
        public int SkippedWithoutNumber;
    }

    public sealed class Library
    {
        readonly Db _db;
        readonly QueueEngine _engine;
        readonly AppSettings _s;

        public Library(Db db, QueueEngine engine, AppSettings settings)
        {
            _db = db;
            _engine = engine;
            _s = settings;
        }

        // ------------------------------------------------------------------ shows & folders
        public ShowRecord EnsureShow(string kind, string name, string folder)
        {
            name = (name ?? "").Trim();
            ShowRecord s = _db.FindShow(kind, name);
            if (s == null)
            {
                s = new ShowRecord { Kind = kind, Name = name, Folder = folder };
                _db.InsertShow(s);
            }
            else if (!string.Equals(s.Folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                s.Folder = folder; // remember the newest folder for this show
                _db.UpdateShow(s);
            }
            return s;
        }

        public string TargetFolder(ShowRecord show, int? season)
        {
            if (show.Kind == "series" && season.HasValue)
                return Path.Combine(show.Folder, "Season " + season.Value.ToString(CultureInfo.InvariantCulture));
            return show.Folder;
        }

        // ------------------------------------------------------------------ episodes <-> parsed items
        static EpisodeRecord ToRecord(ShowRecord show, PlaylistRecord pl, int? season, PlaylistItem it, EpisodeStatus status)
        {
            return new EpisodeRecord
            {
                ShowId = show.Id,
                PlaylistId = pl != null ? pl.Id : 0,
                Season = season,
                EpisodeNumber = it.EpisodeNumber,
                Position = it.Position,
                VideoId = it.VideoId,
                Key = it.Key,
                Url = it.Url,
                Title = it.Title,
                Status = status
            };
        }

        public static string StatusText(EpisodeStatus s)
        {
            switch (s)
            {
                case EpisodeStatus.Downloaded: return "Downloaded";
                case EpisodeStatus.Queued: return "Queued";
                case EpisodeStatus.Downloading: return "Downloading";
                case EpisodeStatus.Failed: return "Failed";
                default: return "Not downloaded";
            }
        }

        /// <summary>Shows what DRAQ already knows (Downloaded / Queued / ...) next to freshly parsed items.</summary>
        public void ApplyStatuses(ShowRecord show, IEnumerable<PlaylistItem> items)
        {
            Dictionary<string, EpisodeRecord> known = new Dictionary<string, EpisodeRecord>();
            foreach (EpisodeRecord e in _db.GetEpisodes(show.Id)) known[e.Key] = e;
            foreach (PlaylistItem it in items)
            {
                EpisodeRecord e;
                if (it.Unavailable) { it.Status = "Unavailable"; continue; }
                if (known.TryGetValue(it.Key, out e))
                {
                    it.Status = StatusText(e.Status);
                    if (!it.EpisodeNumber.HasValue && e.EpisodeNumber.HasValue) it.EpisodeNumber = e.EpisodeNumber;
                }
                else it.Status = "New";
            }
        }

        public List<PlaylistItem> SavedItems(ShowRecord show)
        {
            List<PlaylistItem> list = new List<PlaylistItem>();
            foreach (EpisodeRecord e in _db.GetEpisodes(show.Id))
            {
                list.Add(new PlaylistItem
                {
                    Position = e.Position,
                    VideoId = e.VideoId,
                    Url = e.Url,
                    Title = e.Title,
                    EpisodeNumber = e.EpisodeNumber,
                    Season = e.Season,
                    Status = StatusText(e.Status)
                });
            }
            return list;
        }

        /// <summary>Remembers every parsed item (so later checks know what has already been seen) without queuing anything.</summary>
        public void SaveParsed(ShowRecord show, PlaylistRecord pl, int? season, IEnumerable<PlaylistItem> items)
        {
            foreach (PlaylistItem it in items)
            {
                if (it.Unavailable || string.IsNullOrEmpty(it.Key)) continue;
                _db.UpsertEpisode(ToRecord(show, pl, season, it, EpisodeStatus.NotDownloaded), false);
            }
        }

        public AddResult QueueEpisodes(ShowRecord show, PlaylistRecord pl, int? season, IEnumerable<PlaylistItem> items, string quality, bool interactive)
        {
            List<QueueItem> toAdd = new List<QueueItem>();
            List<QueueItem> downloadedAgain = new List<QueueItem>();
            int skipped = 0;
            string folder = TargetFolder(show, season);
            string fmt = YtDlp.BuildFormat(quality, null);

            foreach (PlaylistItem it in items)
            {
                if (it.Unavailable || string.IsNullOrEmpty(it.Url)) continue;
                EpisodeRecord rec = ToRecord(show, pl, season, it, EpisodeStatus.NotDownloaded);
                EpisodeRecord existing = _db.FindEpisode(show.Id, rec.Key);
                if (existing != null && (existing.Status == EpisodeStatus.Queued || existing.Status == EpisodeStatus.Downloading))
                {
                    skipped++;
                    continue;
                }
                long epId = _db.UpsertEpisode(rec, false);
                QueueItem q = new QueueItem
                {
                    Kind = DownloadKind.Video,
                    Url = it.Url,
                    Title = it.Title,
                    DestDir = folder,
                    Format = fmt,
                    Quality = quality,
                    ShowId = show.Id,
                    EpisodeId = epId,
                    VideoId = it.VideoId,
                    Priority = 1
                };
                if (existing != null && existing.Status == EpisodeStatus.Downloaded) downloadedAgain.Add(q);
                else toAdd.Add(q);
            }

            if (downloadedAgain.Count > 0)
            {
                bool again = _s.DuplicateHandling == "Allow";
                if (_s.DuplicateHandling == "Ask" && interactive && _engine.ConfirmDuplicates != null)
                    again = _engine.ConfirmDuplicates(downloadedAgain.Count);
                if (again) toAdd.AddRange(downloadedAgain);
                else skipped += downloadedAgain.Count;
            }

            AddResult res = _engine.AddMany(toAdd, interactive);
            res.SkippedDuplicates += skipped;
            return res;
        }

        // ------------------------------------------------------------------ new episode detection
        public async Task<CheckOutcome> CheckShowAsync(ShowRecord show, CancellationToken ct, bool allowAutoQueue)
        {
            CheckOutcome outcome = new CheckOutcome { Show = show };
            try
            {
                List<PlaylistRecord> playlists = _db.GetPlaylists(show.Id);
                if (playlists.Count == 0)
                {
                    outcome.Error = "No playlist address is saved for this " + show.KindText.ToLowerInvariant() + ". Parse its playlist once in the " + show.KindText + "s tab.";
                    return outcome;
                }
                Dictionary<string, EpisodeRecord> known = new Dictionary<string, EpisodeRecord>();
                foreach (EpisodeRecord e in _db.GetEpisodes(show.Id)) known[e.Key] = e;
                List<LocalFile> files = _db.GetFiles(show.Id);

                foreach (PlaylistRecord pl in playlists)
                {
                    List<PlaylistItem> items = await YtDlp.ParsePlaylistAsync(pl.Url, ct);
                    List<NewEpisodeRow> rows = new List<NewEpisodeRow>();
                    foreach (PlaylistItem it in items)
                    {
                        if (it.Unavailable) continue;
                        it.Season = pl.Season;
                        if (known.ContainsKey(it.Key)) continue;

                        if (it.EpisodeNumber.HasValue)
                        {
                            LocalFile owned = files.FirstOrDefault(f => f.EpisodeNumber == it.EpisodeNumber &&
                                                                         f.Season.GetValueOrDefault() == pl.Season.GetValueOrDefault());
                            if (owned != null)
                            {
                                // A local file with this episode number already exists: remember it as downloaded instead of calling it new.
                                EpisodeRecord rec = ToRecord(show, pl, pl.Season, it, EpisodeStatus.Downloaded);
                                rec.LocalPath = owned.Path;
                                _db.UpsertEpisode(rec, true);
                                outcome.AlreadyOwned++;
                                continue;
                            }
                        }
                        rows.Add(new NewEpisodeRow
                        {
                            Show = show,
                            Item = it,
                            Season = pl.Season,
                            PlaylistId = pl.Id,
                            DestFolder = TargetFolder(show, pl.Season)
                        });
                    }
                    _db.TouchPlaylist(pl.Id, items.Count);

                    if (rows.Count > 0 && show.AutoQueue && allowAutoQueue)
                    {
                        AddResult r = QueueEpisodes(show, pl, pl.Season, rows.Select(x => x.Item), _s.DefaultQuality, false);
                        outcome.QueuedAutomatically += r.Added.Count;
                    }
                    else outcome.Rows.AddRange(rows);
                }
                show.LastChecked = DateTime.Now;
                _db.UpdateShow(show);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error("New-episode check failed for " + show.Name, ex);
                outcome.Error = ex.Message;
            }
            return outcome;
        }

        public AddResult QueueNewRows(IEnumerable<NewEpisodeRow> rows, string quality, bool interactive)
        {
            AddResult total = new AddResult();
            foreach (var group in rows.GroupBy(r => new { r.Show.Id, r.PlaylistId, r.Season }))
            {
                NewEpisodeRow first = group.First();
                PlaylistRecord pl = _db.GetPlaylists(first.Show.Id).FirstOrDefault(p => p.Id == first.PlaylistId);
                AddResult r = QueueEpisodes(first.Show, pl, first.Season, group.Select(x => x.Item), quality, interactive);
                total.Added.AddRange(r.Added);
                total.SkippedDuplicates += r.SkippedDuplicates;
            }
            return total;
        }

        /// <summary>"Ignore": remember the episodes as seen-but-not-downloaded so they are no longer reported as new.</summary>
        public void IgnoreRows(IEnumerable<NewEpisodeRow> rows)
        {
            foreach (NewEpisodeRow r in rows)
            {
                PlaylistRecord pl = _db.GetPlaylists(r.Show.Id).FirstOrDefault(p => p.Id == r.PlaylistId);
                _db.UpsertEpisode(ToRecord(r.Show, pl, r.Season, r.Item, EpisodeStatus.NotDownloaded), false);
            }
        }

        // ------------------------------------------------------------------ import existing folder (read-only scan)
        public List<ImportEntry> ScanFolder(string folder)
        {
            List<ImportEntry> list = new List<ImportEntry>();
            foreach (string f in VideoFiles.Scan(folder, true))
            {
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                string rel = f.Length > folder.Length ? f.Substring(folder.Length).TrimStart('\\', '/') : f;
                list.Add(new ImportEntry
                {
                    Path = f,
                    RelPath = rel,
                    Size = size,
                    Episode = EpisodeDetector.DetectFromFile(f),
                    IsSelected = true
                });
            }
            return list;
        }

        /// <summary>Registers the files in the database only. Nothing on disk is renamed, moved or modified.</summary>
        public ShowRecord ImportFiles(string kind, string name, string folder, int? season, IEnumerable<ImportEntry> entries)
        {
            name = (name ?? "").Trim();
            ShowRecord show = _db.FindShow(kind, name) ?? EnsureShow(kind, name, folder);
            foreach (ImportEntry e in entries)
            {
                int? fileSeason = null;
                if (kind == "series")
                {
                    Match sm = Regex.Match(e.RelPath ?? "", @"Season\s*(\d+)", RegexOptions.IgnoreCase);
                    int sn;
                    fileSeason = sm.Success && int.TryParse(sm.Groups[1].Value, out sn) ? (int?)sn : season;
                }
                _db.UpsertFile(new LocalFile
                {
                    ShowId = show.Id,
                    Season = fileSeason,
                    Path = e.Path,
                    Size = e.Size,
                    EpisodeNumber = e.Episode,
                    Source = "import"
                });
            }
            return show;
        }

        // ------------------------------------------------------------------ rename
        public List<RenameEntry> LoadRenameEntries(string folder, bool recursive)
        {
            List<RenameEntry> list = new List<RenameEntry>();
            foreach (string f in VideoFiles.Scan(folder, recursive))
            {
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                string dir = Path.GetDirectoryName(f) ?? "";
                string rel = dir.Length > folder.Length ? dir.Substring(folder.Length).TrimStart('\\', '/') : "";
                list.Add(new RenameEntry
                {
                    FullPath = f,
                    RelDir = rel,
                    Size = size,
                    Suggested = EpisodeDetector.DetectFromFile(f)
                });
            }
            return SortByEpisode(list);
        }

        public static List<RenameEntry> SortByEpisode(IEnumerable<RenameEntry> entries)
        {
            List<RenameEntry> l = entries.ToList();
            List<RenameEntry> assigned = l.Where(e => e.Episode.HasValue).OrderBy(e => e.Episode.Value)
                .ThenBy(e => e.FullPath, NaturalComparer.Instance).ToList();
            List<RenameEntry> rest = l.Where(e => !e.Episode.HasValue).OrderBy(e => e.FullPath, NaturalComparer.Instance).ToList();
            assigned.AddRange(rest);
            return assigned;
        }

        /// <summary>Checks the whole plan and returns every problem. Nothing is changed on disk.</summary>
        public static RenamePlan BuildPlan(IEnumerable<RenameEntry> entries, string commonName)
        {
            RenamePlan plan = new RenamePlan();
            List<RenameEntry> all = entries.ToList();
            string name = (commonName ?? "").Trim();

            foreach (RenameEntry e in all) e.NewName = "";

            if (name.Length == 0) plan.Errors.Add("Enter the common name (for example: PARIZAD - DRAMA - 480P - ALI MOBILE).");
            else if (FileNames.HasInvalidChars(name)) plan.Errors.Add("The name contains characters Windows does not allow:  \\ / : * ? \" < > |");
            else if (name.EndsWith(".", StringComparison.Ordinal)) plan.Errors.Add("The name must not end with a dot.");

            foreach (RenameEntry e in all.Where(x => x.InvalidNumber))
                plan.Errors.Add("\"" + e.Name + "\": the episode number \"" + e.RawText + "\" is not a whole number above zero.");

            List<RenameEntry> assigned = all.Where(e => e.Episode.HasValue).ToList();
            plan.SkippedWithoutNumber = all.Count - assigned.Count;
            if (assigned.Count == 0) plan.Errors.Add("No episode numbers have been assigned yet.");

            foreach (var g in assigned.GroupBy(e => (Path.GetDirectoryName(e.FullPath) ?? "").ToLowerInvariant() + "|" + e.Episode.Value.ToString(CultureInfo.InvariantCulture)))
            {
                if (g.Count() > 1)
                    plan.Errors.Add("Episode " + g.First().Episode.Value.ToString(CultureInfo.InvariantCulture) + " is assigned to more than one file: " +
                                    string.Join(", ", g.Select(x => "\"" + x.Name + "\"")));
            }

            if (plan.Errors.Count > 0) return plan;

            HashSet<string> sources = new HashSet<string>(assigned.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
            HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RenameEntry e in assigned.OrderBy(x => x.Episode.Value))
            {
                string dir = Path.GetDirectoryName(e.FullPath);
                string ext = Path.GetExtension(e.FullPath);
                string target = Path.Combine(dir, e.Episode.Value.ToString(CultureInfo.InvariantCulture) + ". " + name + ext);
                e.NewName = Path.GetFileName(target);

                if (!File.Exists(e.FullPath))
                    plan.Errors.Add("The file no longer exists: \"" + e.FullPath + "\"");
                if (target.Length > 255)
                    plan.Errors.Add("The new path is too long (" + target.Length.ToString(CultureInfo.InvariantCulture) + " characters): " + target);
                if (!targets.Add(target))
                    plan.Errors.Add("Two files would get the same name: \"" + Path.GetFileName(target) + "\"");
                if ((File.Exists(target) || Directory.Exists(target)) && !sources.Contains(target))
                    plan.Errors.Add("A different file already has the name \"" + Path.GetFileName(target) + "\" in " + dir + " (it would be overwritten).");

                plan.Ops.Add(new RenameOp
                {
                    Entry = e,
                    From = e.FullPath,
                    To = target,
                    Episode = e.Episode.Value,
                    Temp = Path.Combine(dir, ".draq-tmp-" + Guid.NewGuid().ToString("N") + ext)
                });
            }
            return plan;
        }

        /// <summary>Two-step rename (source -> temporary -> final) so files can never collide. Rolls back if anything fails.</summary>
        public void ExecutePlan(RenamePlan plan, ShowRecord show)
        {
            if (plan.Errors.Count > 0) throw new InvalidOperationException("The rename plan has errors.");
            List<RenameOp> work = plan.Ops.Where(o => !o.Unchanged).ToList();
            List<RenameOp> toTemp = new List<RenameOp>();
            List<RenameOp> toFinal = new List<RenameOp>();
            try
            {
                foreach (RenameOp o in work) { File.Move(o.From, o.Temp); toTemp.Add(o); }
                foreach (RenameOp o in work) { File.Move(o.Temp, o.To); toFinal.Add(o); }
            }
            catch (Exception ex)
            {
                Log.Error("Rename failed, rolling back", ex);
                for (int i = toFinal.Count - 1; i >= 0; i--)
                {
                    try { File.Move(toFinal[i].To, toFinal[i].Temp); } catch (Exception e2) { Log.Error("Rollback step 1 failed", e2); }
                }
                for (int i = toTemp.Count - 1; i >= 0; i--)
                {
                    try { File.Move(toTemp[i].Temp, toTemp[i].From); } catch (Exception e2) { Log.Error("Rollback step 2 failed", e2); }
                }
                throw new IOException("Renaming stopped because of an error and every change was undone: " + ex.Message, ex);
            }

            foreach (RenameOp o in plan.Ops)
            {
                try
                {
                    Match m = Regex.Match(Path.GetDirectoryName(o.To) ?? "", @"Season (\d+)", RegexOptions.IgnoreCase);
                    int? season = null;
                    int sn;
                    if (m.Success && int.TryParse(m.Groups[1].Value, out sn)) season = sn;
                    _db.UpdatePathReferences(o.From, o.To, o.Episode, show != null ? show.Id : 0, season);
                    o.Entry.FullPathChanged(o.To);
                }
                catch (Exception ex) { Log.Error("Database update after rename failed", ex); }
            }
        }
    }

    public static class RenameEntryExtensions
    {
        public static void FullPathChanged(this RenameEntry e, string newPath)
        {
            e.FullPath = newPath;
            e.NewName = "";
        }
    }
}

