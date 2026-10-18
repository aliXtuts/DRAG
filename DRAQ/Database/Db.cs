using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using DRAQ.Models;
using DRAQ.Utilities;

namespace DRAQ.Database
{
    // ALIXTUTS
    /// <summary>The single SQLite database. All access is serialized through one lock/connection.</summary>
    public sealed class Db : IDisposable
    {
        readonly object _gate = new object();
        readonly string _path;
        SqliteConnection _c;

        public Db(string path)
        {
            _path = path;
            ApplyPendingRestore();
            try
            {
                OpenAndMigrate();
            }
            catch (SqliteException ex)
            {
                // Corrupt / unreadable database: keep the broken file for inspection and start fresh.
                Log.Error("Database could not be opened, moving it aside and creating a new one", ex);
                try { if (_c != null) _c.Dispose(); } catch { }
                SqliteConnection.ClearAllPools();
                string broken = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(path, broken);
                OpenAndMigrate();
            }
        }

        void ApplyPendingRestore()
        {
            string pending = _path + ".restore";
            if (!File.Exists(pending)) return;
            try
            {
                File.Copy(pending, _path, true);
                FileNames.TryDelete(_path + "-wal");
                FileNames.TryDelete(_path + "-shm");
                File.Delete(pending);
                Log.Info("Database restored from backup.");
            }
            catch (Exception ex) { Log.Error("Restoring database failed", ex); }
        }

        void OpenAndMigrate()
        {
            _c = new SqliteConnection("Data Source=" + _path + ";Pooling=False");
            _c.Open();
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA synchronous=FULL;");
            Migrate();
        }

        void Migrate()
        {
            Exec(@"CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);");
            Exec(@"CREATE TABLE IF NOT EXISTS shows(
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, name TEXT NOT NULL, folder TEXT NOT NULL,
                auto_queue INTEGER NOT NULL DEFAULT 0, complete INTEGER NOT NULL DEFAULT 0,
                last_checked TEXT, created_at TEXT NOT NULL);");
            Exec(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_shows ON shows(kind, name COLLATE NOCASE);");
            Exec(@"CREATE TABLE IF NOT EXISTS playlists(
                id INTEGER PRIMARY KEY AUTOINCREMENT, show_id INTEGER NOT NULL, season INTEGER,
                url TEXT NOT NULL, title TEXT, item_count INTEGER NOT NULL DEFAULT 0, last_checked TEXT);");
            Exec(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_playlists ON playlists(show_id, ifnull(season, 0));");
            Exec(@"CREATE TABLE IF NOT EXISTS episodes(
                id INTEGER PRIMARY KEY AUTOINCREMENT, show_id INTEGER NOT NULL, playlist_id INTEGER, season INTEGER,
                episode_number INTEGER, position INTEGER, video_id TEXT, ekey TEXT NOT NULL, url TEXT NOT NULL,
                title TEXT, status TEXT NOT NULL, local_path TEXT, discovered_at TEXT NOT NULL);");
            Exec(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_episodes ON episodes(show_id, ekey);");
            Exec(@"CREATE TABLE IF NOT EXISTS files(
                id INTEGER PRIMARY KEY AUTOINCREMENT, show_id INTEGER NOT NULL, season INTEGER, path TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0, episode_number INTEGER, source TEXT, added_at TEXT);");
            Exec(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_files ON files(path COLLATE NOCASE);");
            Exec(@"CREATE TABLE IF NOT EXISTS queue(
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, url TEXT NOT NULL, title TEXT, dest_dir TEXT,
                file_name TEXT, final_path TEXT, status TEXT NOT NULL, downloaded INTEGER NOT NULL DEFAULT 0,
                total INTEGER NOT NULL DEFAULT 0, priority INTEGER NOT NULL DEFAULT 1, sort_order INTEGER NOT NULL DEFAULT 0,
                retries INTEGER NOT NULL DEFAULT 0, error TEXT, format TEXT, extra_args TEXT, quality TEXT,
                show_id INTEGER NOT NULL DEFAULT 0, episode_id INTEGER NOT NULL DEFAULT 0, video_id TEXT,
                torrent_file TEXT, gid TEXT, created_at TEXT, completed_at TEXT);");
            Exec(@"CREATE TABLE IF NOT EXISTS history(
                id INTEGER PRIMARY KEY AUTOINCREMENT, file_name TEXT, url TEXT, date TEXT, size INTEGER NOT NULL DEFAULT 0,
                quality TEXT, destination TEXT, type TEXT, status TEXT, error TEXT);");
        }

        // ------------------------------------------------------------------ low level
        SqliteCommand Cmd(string sql, object[] args)
        {
            SqliteCommand cmd = _c.CreateCommand();
            cmd.CommandText = sql;
            for (int i = 0; i < args.Length; i++)
                cmd.Parameters.AddWithValue("@p" + i.ToString(Fmt.Inv), args[i] ?? DBNull.Value);
            return cmd;
        }

        public int Exec(string sql, params object[] args)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = Cmd(sql, args)) return cmd.ExecuteNonQuery();
            }
        }

        public long Insert(string sql, params object[] args)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = Cmd(sql, args)) cmd.ExecuteNonQuery();
                using (SqliteCommand c2 = _c.CreateCommand())
                {
                    c2.CommandText = "SELECT last_insert_rowid()";
                    return Convert.ToInt64(c2.ExecuteScalar());
                }
            }
        }

        public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object[] args)
        {
            lock (_gate)
            {
                using (SqliteCommand cmd = Cmd(sql, args))
                using (SqliteDataReader r = cmd.ExecuteReader())
                {
                    List<T> list = new List<T>();
                    while (r.Read()) list.Add(map(r));
                    return list;
                }
            }
        }

        static string S(SqliteDataReader r, int i) { return r.IsDBNull(i) ? null : r.GetString(i); }
        static long L(SqliteDataReader r, int i) { return r.IsDBNull(i) ? 0 : r.GetInt64(i); }
        static int? NI(SqliteDataReader r, int i) { return r.IsDBNull(i) ? (int?)null : (int)r.GetInt64(i); }
        static object Obj(int? v) { return v.HasValue ? (object)v.Value : null; }
        static object Obj(DateTime? v) { return v.HasValue ? Fmt.ToDb(v.Value) : null; }

        // ------------------------------------------------------------------ settings
        public Dictionary<string, string> GetSettings()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> kv in Query("SELECT key, value FROM settings",
                r => new KeyValuePair<string, string>(r.GetString(0), S(r, 1))))
                d[kv.Key] = kv.Value;
            return d;
        }

        public void SaveSettings(Dictionary<string, string> d)
        {
            lock (_gate)
            {
                using (SqliteTransaction tx = _c.BeginTransaction())
                {
                    foreach (KeyValuePair<string, string> kv in d)
                    {
                        using (SqliteCommand cmd = _c.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = "INSERT OR REPLACE INTO settings(key, value) VALUES(@k, @v)";
                            cmd.Parameters.AddWithValue("@k", kv.Key);
                            cmd.Parameters.AddWithValue("@v", (object)kv.Value ?? DBNull.Value);
                            cmd.ExecuteNonQuery();
                        }
                    }
                    tx.Commit();
                }
            }
        }

        // ------------------------------------------------------------------ queue
        const string QCols = "id, kind, url, title, dest_dir, file_name, final_path, status, downloaded, total, priority, sort_order, retries, error, format, extra_args, quality, show_id, episode_id, video_id, torrent_file, gid, created_at, completed_at";

        static QueueItem MapQueue(SqliteDataReader r)
        {
            QueueItem q = new QueueItem();
            q.Id = L(r, 0);
            DownloadKind k; Enum.TryParse(S(r, 1), out k); q.Kind = k;
            q.Url = S(r, 2);
            q.Title = S(r, 3);
            q.DestDir = S(r, 4);
            q.FileName = S(r, 5);
            q.FinalPath = S(r, 6);
            QueueStatus st; Enum.TryParse(S(r, 7), out st); q.Status = st;
            q.Downloaded = L(r, 8);
            q.Total = L(r, 9);
            q.Priority = (int)L(r, 10);
            q.SortOrder = (int)L(r, 11);
            q.RetriesDone = (int)L(r, 12);
            q.Error = S(r, 13);
            q.Format = S(r, 14);
            q.ExtraArgs = S(r, 15);
            q.Quality = S(r, 16);
            q.ShowId = L(r, 17);
            q.EpisodeId = L(r, 18);
            q.VideoId = S(r, 19);
            q.TorrentFile = S(r, 20);
            q.Gid = S(r, 21);
            DateTime? c = Fmt.FromDb(S(r, 22));
            q.CreatedAt = c ?? DateTime.Now;
            q.CompletedAt = Fmt.FromDb(S(r, 23));
            return q;
        }

        public List<QueueItem> LoadQueue()
        {
            return Query("SELECT " + QCols + " FROM queue ORDER BY sort_order, id", MapQueue);
        }

        public void InsertQueue(QueueItem q)
        {
            q.Id = Insert(@"INSERT INTO queue(kind, url, title, dest_dir, file_name, final_path, status, downloaded, total, priority,
                sort_order, retries, error, format, extra_args, quality, show_id, episode_id, video_id, torrent_file, gid, created_at, completed_at)
                VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17,@p18,@p19,@p20,@p21,@p22)",
                q.Kind.ToString(), q.Url, q.Title, q.DestDir, q.FileName, q.FinalPath, q.Status.ToString(), q.Downloaded, q.Total,
                q.Priority, q.SortOrder, q.RetriesDone, q.Error, q.Format, q.ExtraArgs, q.Quality, q.ShowId, q.EpisodeId,
                q.VideoId, q.TorrentFile, q.Gid, Fmt.ToDb(q.CreatedAt), Obj(q.CompletedAt));
        }

        public void UpdateQueue(QueueItem q)
        {
            Exec(@"UPDATE queue SET title=@p0, dest_dir=@p1, file_name=@p2, final_path=@p3, status=@p4, downloaded=@p5, total=@p6,
                priority=@p7, sort_order=@p8, retries=@p9, error=@p10, format=@p11, extra_args=@p12, quality=@p13, gid=@p14,
                completed_at=@p15, torrent_file=@p16 WHERE id=@p17",
                q.Title, q.DestDir, q.FileName, q.FinalPath, q.Status.ToString(), q.Downloaded, q.Total, q.Priority, q.SortOrder,
                q.RetriesDone, q.Error, q.Format, q.ExtraArgs, q.Quality, q.Gid, Obj(q.CompletedAt), q.TorrentFile, q.Id);
        }

        public void DeleteQueue(long id)
        {
            Exec("DELETE FROM queue WHERE id=@p0", id);
        }

        // ------------------------------------------------------------------ shows
        const string ShowCols = "id, kind, name, folder, auto_queue, complete, last_checked, created_at";

        static ShowRecord MapShow(SqliteDataReader r)
        {
            ShowRecord s = new ShowRecord();
            s.Id = L(r, 0);
            s.Kind = S(r, 1);
            s.Name = S(r, 2);
            s.Folder = S(r, 3);
            s.AutoQueue = L(r, 4) != 0;
            s.Complete = L(r, 5) != 0;
            s.LastChecked = Fmt.FromDb(S(r, 6));
            s.Created = Fmt.FromDb(S(r, 7)) ?? DateTime.Now;
            return s;
        }

        public List<ShowRecord> GetShows(string kind = null)
        {
            if (kind == null) return Query("SELECT " + ShowCols + " FROM shows ORDER BY name COLLATE NOCASE", MapShow);
            return Query("SELECT " + ShowCols + " FROM shows WHERE kind=@p0 ORDER BY name COLLATE NOCASE", MapShow, kind);
        }

        public ShowRecord FindShow(string kind, string name)
        {
            List<ShowRecord> l = Query("SELECT " + ShowCols + " FROM shows WHERE kind=@p0 AND name=@p1 COLLATE NOCASE", MapShow, kind, name);
            return l.Count > 0 ? l[0] : null;
        }

        public ShowRecord GetShow(long id)
        {
            List<ShowRecord> l = Query("SELECT " + ShowCols + " FROM shows WHERE id=@p0", MapShow, id);
            return l.Count > 0 ? l[0] : null;
        }

        public void InsertShow(ShowRecord s)
        {
            s.Id = Insert("INSERT INTO shows(kind, name, folder, auto_queue, complete, last_checked, created_at) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)",
                s.Kind, s.Name, s.Folder, s.AutoQueue ? 1 : 0, s.Complete ? 1 : 0, Obj(s.LastChecked), Fmt.ToDb(s.Created));
        }

        public void UpdateShow(ShowRecord s)
        {
            Exec("UPDATE shows SET name=@p0, folder=@p1, auto_queue=@p2, complete=@p3, last_checked=@p4 WHERE id=@p5",
                s.Name, s.Folder, s.AutoQueue ? 1 : 0, s.Complete ? 1 : 0, Obj(s.LastChecked), s.Id);
        }

        /// <summary>Removes the show and its saved episodes/files from DRAQ's database. Files on disk are never touched.</summary>
        public void DeleteShow(long id)
        {
            Exec("DELETE FROM episodes WHERE show_id=@p0", id);
            Exec("DELETE FROM files WHERE show_id=@p0", id);
            Exec("DELETE FROM playlists WHERE show_id=@p0", id);
            Exec("DELETE FROM shows WHERE id=@p0", id);
        }

        // ------------------------------------------------------------------ playlists
        static PlaylistRecord MapPlaylist(SqliteDataReader r)
        {
            return new PlaylistRecord
            {
                Id = L(r, 0), ShowId = L(r, 1), Season = NI(r, 2), Url = S(r, 3), Title = S(r, 4),
                ItemCount = (int)L(r, 5), LastChecked = Fmt.FromDb(S(r, 6))
            };
        }

        const string PlCols = "id, show_id, season, url, title, item_count, last_checked";

        public List<PlaylistRecord> GetPlaylists(long showId)
        {
            return Query("SELECT " + PlCols + " FROM playlists WHERE show_id=@p0 ORDER BY ifnull(season,0)", MapPlaylist, showId);
        }

        public PlaylistRecord GetPlaylist(long showId, int? season)
        {
            List<PlaylistRecord> l = Query("SELECT " + PlCols + " FROM playlists WHERE show_id=@p0 AND ifnull(season,0)=@p1",
                MapPlaylist, showId, season ?? 0);
            return l.Count > 0 ? l[0] : null;
        }

        public PlaylistRecord UpsertPlaylist(long showId, int? season, string url, string title, int count)
        {
            PlaylistRecord p = GetPlaylist(showId, season);
            if (p == null)
            {
                long id = Insert("INSERT INTO playlists(show_id, season, url, title, item_count, last_checked) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",
                    showId, Obj(season), url, title, count, Fmt.ToDb(DateTime.Now));
                return GetPlaylist(showId, season);
            }
            Exec("UPDATE playlists SET url=@p0, title=@p1, item_count=@p2, last_checked=@p3 WHERE id=@p4",
                url, title, count, Fmt.ToDb(DateTime.Now), p.Id);
            return GetPlaylist(showId, season);
        }

        public void TouchPlaylist(long id, int count)
        {
            Exec("UPDATE playlists SET item_count=@p0, last_checked=@p1 WHERE id=@p2", count, Fmt.ToDb(DateTime.Now), id);
        }

        // ------------------------------------------------------------------ episodes
        const string EpCols = "id, show_id, playlist_id, season, episode_number, position, video_id, ekey, url, title, status, local_path, discovered_at";

        static EpisodeRecord MapEpisode(SqliteDataReader r)
        {
            EpisodeRecord e = new EpisodeRecord();
            e.Id = L(r, 0);
            e.ShowId = L(r, 1);
            e.PlaylistId = L(r, 2);
            e.Season = NI(r, 3);
            e.EpisodeNumber = NI(r, 4);
            e.Position = (int)L(r, 5);
            e.VideoId = S(r, 6);
            e.Key = S(r, 7);
            e.Url = S(r, 8);
            e.Title = S(r, 9);
            EpisodeStatus st; Enum.TryParse(S(r, 10), out st); e.Status = st;
            e.LocalPath = S(r, 11);
            e.DiscoveredAt = Fmt.FromDb(S(r, 12)) ?? DateTime.Now;
            return e;
        }

        public List<EpisodeRecord> GetEpisodes(long showId)
        {
            return Query("SELECT " + EpCols + " FROM episodes WHERE show_id=@p0 ORDER BY ifnull(season,0), ifnull(episode_number, 100000), position", MapEpisode, showId);
        }

        public EpisodeRecord GetEpisode(long id)
        {
            List<EpisodeRecord> l = Query("SELECT " + EpCols + " FROM episodes WHERE id=@p0", MapEpisode, id);
            return l.Count > 0 ? l[0] : null;
        }

        public EpisodeRecord FindEpisode(long showId, string key)
        {
            List<EpisodeRecord> l = Query("SELECT " + EpCols + " FROM episodes WHERE show_id=@p0 AND ekey=@p1", MapEpisode, showId, key);
            return l.Count > 0 ? l[0] : null;
        }

        /// <summary>Inserts the episode, or updates title/number/position/url of the existing one (the status is only set for new rows
        /// unless force is true). Returns the row id.</summary>
        public long UpsertEpisode(EpisodeRecord e, bool forceStatus)
        {
            EpisodeRecord ex = FindEpisode(e.ShowId, e.Key);
            if (ex == null)
            {
                e.Id = Insert(@"INSERT INTO episodes(show_id, playlist_id, season, episode_number, position, video_id, ekey, url, title, status, local_path, discovered_at)
                    VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11)",
                    e.ShowId, e.PlaylistId, Obj(e.Season), Obj(e.EpisodeNumber), e.Position, e.VideoId, e.Key, e.Url, e.Title,
                    e.Status.ToString(), e.LocalPath, Fmt.ToDb(e.DiscoveredAt));
                return e.Id;
            }
            e.Id = ex.Id;
            Exec("UPDATE episodes SET playlist_id=@p0, season=COALESCE(@p1, season), episode_number=COALESCE(@p2, episode_number), position=@p3, url=@p4, title=@p5 WHERE id=@p6",
                e.PlaylistId, Obj(e.Season), Obj(e.EpisodeNumber), e.Position, e.Url, e.Title, ex.Id);
            if (forceStatus)
                Exec("UPDATE episodes SET status=@p0 WHERE id=@p1", e.Status.ToString(), ex.Id);
            return ex.Id;
        }

        public void SetEpisodeStatus(long id, EpisodeStatus st, string localPath)
        {
            if (localPath != null)
                Exec("UPDATE episodes SET status=@p0, local_path=@p1 WHERE id=@p2", st.ToString(), localPath, id);
            else
                Exec("UPDATE episodes SET status=@p0 WHERE id=@p1", st.ToString(), id);
        }

        // ------------------------------------------------------------------ local files
        static LocalFile MapFile(SqliteDataReader r)
        {
            return new LocalFile
            {
                Id = L(r, 0), ShowId = L(r, 1), Season = NI(r, 2), Path = S(r, 3), Size = L(r, 4),
                EpisodeNumber = NI(r, 5), Source = S(r, 6)
            };
        }

        const string FileCols = "id, show_id, season, path, size, episode_number, source";

        public List<LocalFile> GetFiles(long showId)
        {
            return Query("SELECT " + FileCols + " FROM files WHERE show_id=@p0 ORDER BY path COLLATE NOCASE", MapFile, showId);
        }

        public void UpsertFile(LocalFile f)
        {
            Exec(@"INSERT INTO files(show_id, season, path, size, episode_number, source, added_at) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6)
                   ON CONFLICT(path COLLATE NOCASE) DO UPDATE SET show_id=excluded.show_id, season=excluded.season, size=excluded.size,
                   episode_number=excluded.episode_number",
                f.ShowId, Obj(f.Season), f.Path, f.Size, Obj(f.EpisodeNumber), f.Source, Fmt.ToDb(DateTime.Now));
        }

        /// <summary>Called after a rename: keeps files, episodes and queue entries pointing at the new path.</summary>
        public void UpdatePathReferences(string oldPath, string newPath, int? episodeNumber, long showId, int? season)
        {
            int n = Exec("UPDATE files SET path=@p0, episode_number=@p1 WHERE path=@p2 COLLATE NOCASE", newPath, Obj(episodeNumber), oldPath);
            if (n == 0 && showId > 0)
            {
                long size = 0;
                try { size = new FileInfo(newPath).Length; } catch { }
                UpsertFile(new LocalFile { ShowId = showId, Season = season, Path = newPath, Size = size, EpisodeNumber = episodeNumber, Source = "rename" });
            }
            Exec("UPDATE episodes SET local_path=@p0, episode_number=@p1 WHERE local_path=@p2 COLLATE NOCASE", newPath, Obj(episodeNumber), oldPath);
            Exec("UPDATE queue SET final_path=@p0 WHERE final_path=@p1 COLLATE NOCASE", newPath, oldPath);
        }

        // ------------------------------------------------------------------ history
        public void AddHistory(HistoryRow h)
        {
            Insert("INSERT INTO history(file_name, url, date, size, quality, destination, type, status, error) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)",
                h.FileName, h.Url, Fmt.ToDb(h.Date), h.Size, h.Quality, h.Destination, h.Type, h.Status, h.Error);
        }

        public List<HistoryRow> GetHistory(string filter)
        {
            Func<SqliteDataReader, HistoryRow> map = r => new HistoryRow
            {
                Id = L(r, 0), FileName = S(r, 1), Url = S(r, 2), Date = Fmt.FromDb(S(r, 3)) ?? DateTime.Now, Size = L(r, 4),
                Quality = S(r, 5), Destination = S(r, 6), Type = S(r, 7), Status = S(r, 8), Error = S(r, 9)
            };
            string cols = "id, file_name, url, date, size, quality, destination, type, status, error";
            if (string.IsNullOrWhiteSpace(filter))
                return Query("SELECT " + cols + " FROM history ORDER BY id DESC LIMIT 2000", map);
            string like = "%" + filter.Trim().Replace("%", "").Replace("_", "") + "%";
            return Query("SELECT " + cols + " FROM history WHERE file_name LIKE @p0 OR url LIKE @p0 OR destination LIKE @p0 OR status LIKE @p0 OR type LIKE @p0 ORDER BY id DESC LIMIT 2000", map, like);
        }

        public void ClearHistory()
        {
            Exec("DELETE FROM history");
        }

        // ------------------------------------------------------------------ backup / restore
        public void Backup(string toPath)
        {
            lock (_gate)
            {
                if (File.Exists(toPath)) File.Delete(toPath);
                using (SqliteCommand cmd = _c.CreateCommand())
                {
                    cmd.CommandText = "VACUUM INTO @p";
                    cmd.Parameters.AddWithValue("@p", toPath);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>Stages a backup file; it is swapped in the next time DRAQ starts (the caller restarts the app).</summary>
        public static void StageRestore(string backupFile, string dbPath)
        {
            File.Copy(backupFile, dbPath + ".restore", true);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try
                {
                    if (_c != null)
                    {
                        using (SqliteCommand cmd = _c.CreateCommand())
                        {
                            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                            cmd.ExecuteNonQuery();
                        }
                        _c.Dispose();
                        _c = null;
                    }
                }
                catch { }
            }
        }
    }
}

