using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DRAQ.Utilities
{
    // MRAALLEY
    /// <summary>All paths are relative to the DRAQ folder (portable). Falls back to %LocalAppData% if the folder is read-only.</summary>
    public static class AppPaths
    {
        public static readonly string AppDir = AppContext.BaseDirectory;
        public static string DataDir { get; private set; }

        public static string ToolsDir { get { return Path.Combine(AppDir, "tools"); } }
        public static string LogDir { get { return Path.Combine(DataDir, "logs"); } }
        public static string DbPath { get { return Path.Combine(DataDir, "draq.db"); } }
        public static string BackupDir { get { return Path.Combine(DataDir, "backups"); } }
        public static string TorrentDir { get { return Path.Combine(DataDir, "torrents"); } }

        public static void Init()
        {
            if (!TryUse(Path.Combine(AppDir, "data")))
            {
                string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DRAQ");
                Directory.CreateDirectory(local);
                DataDir = local;
            }
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(BackupDir);
            Directory.CreateDirectory(TorrentDir);
        }

        static bool TryUse(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".w" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                DataDir = dir;
                return true;
            }
            catch { return false; }
        }

        public static string Tool(string exe) { return Path.Combine(ToolsDir, exe); }

        /// <summary>Default base download folder: D:\Downloads if a D: drive exists, otherwise the user's Downloads folder.</summary>
        public static string DefaultBase()
        {
            try
            {
                if (Directory.Exists("D:\\")) return "D:\\Downloads";
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public static class Log
    {
        static readonly object Gate = new object();
        public static void Info(string m) { Write("INFO", m); }
        public static void Warn(string m) { Write("WARN", m); }
        public static void Error(string m, Exception ex = null)
        {
            Write("ERROR", ex == null ? m : m + " :: " + ex);
        }
        static void Write(string level, string m)
        {
            try
            {
                lock (Gate)
                {
                    if (AppPaths.DataDir == null) return;
                    Directory.CreateDirectory(AppPaths.LogDir);
                    string file = Path.Combine(AppPaths.LogDir, "draq-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + m + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    public static class Fmt
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Bytes(long b)
        {
            if (b < 0) b = 0;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            if (i == 0) return b.ToString(Inv) + " B";
            return v.ToString(v >= 100 ? "0" : "0.0", Inv) + " " + u[i];
        }

        public static string Speed(long bps)
        {
            return bps <= 0 ? "" : Bytes(bps) + "/s";
        }

        public static string Eta(long sec)
        {
            if (sec <= 0) return "";
            TimeSpan t = TimeSpan.FromSeconds(Math.Min(sec, 8640000));
            if (t.TotalHours >= 1) return ((int)t.TotalHours).ToString(Inv) + "h " + t.Minutes.ToString(Inv) + "m";
            if (t.TotalMinutes >= 1) return t.Minutes.ToString(Inv) + "m " + t.Seconds.ToString(Inv) + "s";
            return t.Seconds.ToString(Inv) + "s";
        }

        public static string Date(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm", Inv) : "never";
        }

        public static string ToDb(DateTime d) { return d.ToString("yyyy-MM-dd HH:mm:ss", Inv); }

        public static DateTime? FromDb(string s)
        {
            DateTime d;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParse(s, Inv, DateTimeStyles.None, out d)) return d;
            return null;
        }
    }

    public static class UrlUtil
    {
        public static bool IsHttp(string s)
        {
            Uri u;
            return !string.IsNullOrWhiteSpace(s) && Uri.TryCreate(s.Trim(), UriKind.Absolute, out u)
                   && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        public static bool IsMagnet(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return s.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
                   && (s.IndexOf("xt=urn:btih:", StringComparison.OrdinalIgnoreCase) >= 0
                       || s.IndexOf("xt=urn:btmh:", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool LooksLikeTorrentUrl(string s)
        {
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u)) return false;
            return u.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        }

        public static string MagnetName(string magnet)
        {
            Match m = Regex.Match(magnet ?? "", @"[?&]dn=([^&]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                try { return Uri.UnescapeDataString(m.Groups[1].Value.Replace('+', ' ')); } catch { }
            }
            Match h = Regex.Match(magnet ?? "", @"btih:([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
            return h.Success ? "Torrent " + h.Groups[1].Value.Substring(0, Math.Min(8, h.Groups[1].Value.Length)) : "Torrent";
        }
    }

    public static class FileNames
    {
        static readonly char[] Bad = Path.GetInvalidFileNameChars();

        public static string Sanitize(string s, int max = 120)
        {
            if (string.IsNullOrWhiteSpace(s)) return "download";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (Array.IndexOf(Bad, c) >= 0 || char.IsControl(c)) sb.Append('_');
                else sb.Append(c);
            }
            string r = sb.ToString().Trim().TrimEnd('.', ' ');
            if (r.Length > max) r = r.Substring(0, max).TrimEnd('.', ' ');
            return r.Length == 0 ? "download" : r;
        }

        public static bool HasInvalidChars(string s)
        {
            return s != null && s.IndexOfAny(Bad) >= 0;
        }

        /// <summary>Returns a path that does not exist yet (name (2).ext ...). If overwriting is allowed returns the path unchanged.</summary>
        public static string Unique(string path, bool allowOverwrite, bool alsoCheckPart = false)
        {
            if (allowOverwrite) return path;
            if (!Taken(path, alsoCheckPart)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 10000; i++)
            {
                string cand = Path.Combine(dir, name + " (" + i.ToString(Fmt.Inv) + ")" + ext);
                if (!Taken(cand, alsoCheckPart)) return cand;
            }
            return Path.Combine(dir, name + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")" + ext);
        }

        static bool Taken(string path, bool checkPart)
        {
            return File.Exists(path) || Directory.Exists(path) || (checkPart && File.Exists(path + ".part"));
        }

        /// <summary>For yt-dlp: a base name such that no "base.*" file exists in the folder.</summary>
        public static string UniqueBase(string dir, string baseName, bool allowOverwrite)
        {
            if (allowOverwrite || !Directory.Exists(dir)) return baseName;
            if (!AnyWithBase(dir, baseName)) return baseName;
            for (int i = 2; i < 10000; i++)
            {
                string cand = baseName + " (" + i.ToString(Fmt.Inv) + ")";
                if (!AnyWithBase(dir, cand)) return cand;
            }
            return baseName + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")";
        }

        static bool AnyWithBase(string dir, string baseName)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    string n = Path.GetFileName(f);
                    if (n.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        public static string FromUrl(string url)
        {
            try
            {
                Uri u = new Uri(url);
                string last = Path.GetFileName(Uri.UnescapeDataString(u.AbsolutePath));
                if (!string.IsNullOrWhiteSpace(last)) return Sanitize(last, 150);
                return Sanitize(u.Host, 60);
            }
            catch { return "download"; }
        }

        public static string Folder(string name)
        {
            return Sanitize(name, 80);
        }

        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Warn("Could not delete " + path + ": " + ex.Message); }
        }
    }

    public static class VideoFiles
    {
        public static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".flv", ".wmv", ".mpg", ".mpeg",
            ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".vob", ".divx", ".asf", ".rmvb"
        };

        public static bool IsVideo(string path)
        {
            return Extensions.Contains(Path.GetExtension(path) ?? "");
        }

        /// <summary>Recursively finds ALL video files under a folder.</summary>
        public static List<string> Scan(string folder, bool recursive = true)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return list;
            EnumerationOptions opt = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System
            };
            foreach (string f in Directory.EnumerateFiles(folder, "*", opt))
            {
                if (IsVideo(f)) list.Add(f);
            }
            list.Sort(NaturalComparer.Instance);
            return list;
        }
    }

    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();
        static readonly Regex Num = new Regex(@"\d+", RegexOptions.Compiled);

        public int Compare(string a, string b)
        {
            if (a == null || b == null) return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            string pa = Num.Replace(a, m => m.Value.PadLeft(10, '0'));
            string pb = Num.Replace(b, m => m.Value.PadLeft(10, '0'));
            return string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Conservative episode number detection. Returns null when unsure.</summary>
    public static class EpisodeDetector
    {
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        static readonly Regex[] Patterns =
        {
            new Regex(@"S\d{1,2}\s*E(\d{1,4})(?!\d)", O),
            new Regex(@"(?<![A-Za-z])(?:episode|epi|ep|qist|qisat|kist|قسط)\s*(?:no\.?|number|نمبر)?\s*[#:.\-_]*\s*(\d{1,4})(?!\d)", O),
            new Regex(@"(?<![A-Za-z0-9])(\d{1,4})\s*(?:st|nd|rd|th)\s+(?:episode|ep)\b", O),
            new Regex(@"(?<![A-Za-z0-9])E(\d{1,3})(?![0-9A-Za-z])", O),
            new Regex(@"(?<![A-Za-z])(?:part|pt)\s*[#.\-_]*\s*(\d{1,3})(?!\d)", O)
        };

        public static int? Detect(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            string t = title.Replace('_', ' ');
            t = Regex.Replace(t, @"\.(?=\S)", " ");
            foreach (Regex r in Patterns)
            {
                Match m = r.Match(t);
                if (m.Success)
                {
                    int n;
                    if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0 && n < 5000)
                        return n;
                }
            }
            return null;
        }

        public static int? DetectFromFile(string path)
        {
            return Detect(Path.GetFileNameWithoutExtension(path));
        }

        public static int? DetectSeason(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            Match m = Regex.Match(title, @"S(\d{1,2})\s*E\d{1,4}", O);
            int n;
            if (m.Success && int.TryParse(m.Groups[1].Value, out n)) return n;
            return null;
        }
    }

    public sealed class ProcResult
    {
        public int ExitCode;
        public string Out = "";
        public string Err = "";
        public bool Canceled;
        public bool TimedOut;
        public bool StartFailed;
    }

    public static class Proc
    {
        /// <summary>Runs a process without a window. Uses ArgumentList so no manual quoting is required. Killing on cancel takes the whole process tree.</summary>
        public static async Task<ProcResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct,
            int timeoutMs = 0, Action<string> onOut = null, Action<string> onErr = null)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PATH"] = AppPaths.ToolsDir + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");

            StringBuilder so = new StringBuilder();
            StringBuilder se = new StringBuilder();
            ProcResult res = new ProcResult();

            using (Process p = new Process { StartInfo = psi })
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (timeoutMs > 0) linked.CancelAfter(timeoutMs);
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    if (onOut != null) onOut(e.Data);
                    else lock (so) { so.AppendLine(e.Data); }
                };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    if (onErr != null) onErr(e.Data);
                    lock (se) { se.AppendLine(e.Data); }
                };
                try
                {
                    p.Start();
                }
                catch (Exception ex)
                {
                    res.StartFailed = true;
                    res.ExitCode = -1;
                    res.Err = "Could not start " + Path.GetFileName(exe) + ": " + ex.Message;
                    return res;
                }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                using (linked.Token.Register(() =>
                {
                    try { if (!p.HasExited) p.Kill(true); } catch { }
                }))
                {
                    await p.WaitForExitAsync(CancellationToken.None);
                }
                p.WaitForExit();
                res.ExitCode = p.ExitCode;
                res.Canceled = ct.IsCancellationRequested;
                res.TimedOut = !ct.IsCancellationRequested && linked.IsCancellationRequested;
            }
            lock (so) { res.Out = so.ToString(); }
            lock (se) { res.Err = se.ToString(); }
            return res;
        }
    }

    public class DownloadException : Exception
    {
        public bool NoRetry { get; private set; }
        public DownloadException(string message, bool noRetry = false) : base(message) { NoRetry = noRetry; }
    }
}

