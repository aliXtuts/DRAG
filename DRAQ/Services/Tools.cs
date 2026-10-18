using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DRAQ.Models;
using DRAQ.Utilities;

namespace DRAQ.Services
{
    // 1110339
    public static class ToolPaths
    {
        public static string YtDlp { get { return AppPaths.Tool("yt-dlp.exe"); } }
        public static string Ffmpeg { get { return AppPaths.Tool("ffmpeg.exe"); } }
        public static string Ffprobe { get { return AppPaths.Tool("ffprobe.exe"); } }
        public static string Aria2 { get { return AppPaths.Tool("aria2c.exe"); } }
        public static string Deno { get { return AppPaths.Tool("deno.exe"); } }

        public static List<string> Missing()
        {
            List<string> m = new List<string>();
            if (!File.Exists(YtDlp)) m.Add("yt-dlp.exe");
            if (!File.Exists(Ffmpeg)) m.Add("ffmpeg.exe");
            if (!File.Exists(Ffprobe)) m.Add("ffprobe.exe");
            if (!File.Exists(Aria2)) m.Add("aria2c.exe");
            if (!File.Exists(Deno)) m.Add("deno.exe (needed for YouTube)");
            return m;
        }

        public static void Require(string path)
        {
            if (!File.Exists(path))
                throw new DownloadException("Required tool is missing: " + Path.GetFileName(path) +
                    ". It should be in the 'tools' folder next to DRAQ.exe. Re-run build.bat.", true);
        }
    }

    // ===================================================================== HTTP probe
    public sealed class HttpProbeResult
    {
        public bool Ok;
        public string Error;
        public string FileName;
        public long Size;
        public bool Ranges;
        public string ContentType;
        public bool LooksLikeWebPage
        {
            get
            {
                string c = (ContentType ?? "").ToLowerInvariant();
                return c.Contains("text/html") || c.Contains("application/xhtml");
            }
        }
    }

    public static class HttpProbe
    {
        public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
        static readonly HttpClient Client = Create();

        static HttpClient Create()
        {
            HttpClientHandler h = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 };
            HttpClient c = new HttpClient(h);
            c.Timeout = TimeSpan.FromSeconds(25);
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return c;
        }

        public static async Task<HttpProbeResult> ProbeAsync(string url, CancellationToken ct)
        {
            HttpProbeResult r = new HttpProbeResult();
            if (!UrlUtil.IsHttp(url)) { r.Error = "That is not a valid http/https address."; return r; }
            string firstError = null;
            foreach (bool head in new[] { true, false })
            {
                try
                {
                    using (HttpRequestMessage req = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, url))
                    {
                        if (!head) req.Headers.Range = new RangeHeaderValue(0, 0);
                        using (HttpResponseMessage resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            if (!resp.IsSuccessStatusCode)
                            {
                                firstError = firstError ?? ("Server answered HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + resp.ReasonPhrase);
                                if (head) continue;
                                r.Error = firstError;
                                return r;
                            }
                            r.Ok = true;
                            r.ContentType = resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : "";
                            long len = 0;
                            if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange != null && resp.Content.Headers.ContentRange.Length.HasValue)
                            {
                                len = resp.Content.Headers.ContentRange.Length.Value;
                                r.Ranges = true;
                            }
                            else if (resp.Content.Headers.ContentLength.HasValue)
                            {
                                len = resp.Content.Headers.ContentLength.Value;
                                r.Ranges = resp.Headers.AcceptRanges.Contains("bytes");
                            }
                            r.Size = len;
                            r.FileName = NameFrom(resp);
                            return r;
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    firstError = firstError ?? "The server did not answer in time.";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    firstError = firstError ?? FriendlyNetError(ex);
                }
            }
            r.Error = firstError ?? "Could not reach the server.";
            return r;
        }

        static string NameFrom(HttpResponseMessage resp)
        {
            ContentDispositionHeaderValue cd = resp.Content.Headers.ContentDisposition;
            if (cd != null)
            {
                string n = cd.FileNameStar ?? cd.FileName;
                if (!string.IsNullOrWhiteSpace(n))
                {
                    n = n.Trim('"');
                    return FileNames.Sanitize(n, 150);
                }
            }
            Uri final = resp.RequestMessage != null ? resp.RequestMessage.RequestUri : null;
            return final != null ? FileNames.FromUrl(final.AbsoluteUri) : null;
        }

        public static string FriendlyNetError(Exception ex)
        {
            HttpRequestException hre = ex as HttpRequestException;
            if (hre != null && hre.InnerException is SocketException)
                return "Network problem: could not connect (check your internet connection or the address).";
            if (ex is TaskCanceledException) return "The server did not answer in time.";
            return "Network problem: " + ex.Message;
        }
    }

    // ===================================================================== yt-dlp
    public sealed class AudioOption
    {
        public string FormatId { get; set; }
        public string Label { get; set; }
        public override string ToString() { return Label; }
    }

    public sealed class SubOption
    {
        public string Lang { get; set; }
        public bool Auto { get; set; }
        public string Label { get; set; }
        public override string ToString() { return Label; }
    }

    public sealed class VideoProbe
    {
        public string Id;
        public string Title;
        public string WebpageUrl;
        public string Uploader;
        public double Duration;
        public bool IsPlaylist;
        public List<int> Heights = new List<int>();
        public List<AudioOption> Audios = new List<AudioOption>();
        public List<SubOption> Subs = new List<SubOption>();
    }

    public sealed class YtDlpException : Exception
    {
        public YtDlpException(string m) : base(m) { }
    }

    public static class YtDlp
    {
        static readonly string[] CommonArgs = { "--ignore-config", "--no-warnings", "--encoding", "utf-8" };

        public static List<string> Common(params string[] more)
        {
            List<string> l = new List<string>(CommonArgs);
            l.AddRange(more);
            return l;
        }

        /// <summary>Reads metadata for one video (nothing is downloaded).</summary>
        public static async Task<VideoProbe> ProbeVideoAsync(string url, CancellationToken ct)
        {
            ToolPaths.Require(ToolPaths.YtDlp);
            List<string> args = Common("-J", "--no-playlist", "--skip-download", url);
            ProcResult r = await Proc.RunAsync(ToolPaths.YtDlp, args, ct, 120000);
            if (r.Canceled) throw new OperationCanceledException(ct);
            if (r.TimedOut) throw new YtDlpException("yt-dlp took too long to read this page.");
            if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Out))
                throw new YtDlpException(FriendlyError(r.Err));

            using (JsonDocument doc = JsonDocument.Parse(FirstJson(r.Out)))
            {
                JsonElement root = doc.RootElement;
                VideoProbe p = new VideoProbe();
                p.Id = Str(root, "id");
                p.Title = Str(root, "title") ?? p.Id ?? "video";
                p.WebpageUrl = Str(root, "webpage_url") ?? url;
                p.Uploader = Str(root, "uploader") ?? Str(root, "channel");
                p.Duration = Num(root, "duration");
                p.IsPlaylist = Str(root, "_type") == "playlist";
                if (p.IsPlaylist) return p;

                HashSet<int> heights = new HashSet<int>();
                HashSet<string> seenAudio = new HashSet<string>();
                JsonElement formats;
                if (root.TryGetProperty("formats", out formats) && formats.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement f in formats.EnumerateArray())
                    {
                        string v = Str(f, "vcodec");
                        string a = Str(f, "acodec");
                        double h = Num(f, "height");
                        bool hasVideo = v != null && v != "none";
                        bool hasAudio = a != null && a != "none";
                        if (hasVideo && h > 0) heights.Add((int)h);
                        if (!hasVideo && hasAudio)
                        {
                            string id = Str(f, "format_id");
                            if (id == null || !seenAudio.Add(id)) continue;
                            string lang = Str(f, "language") ?? "und";
                            double abr = Num(f, "abr");
                            string ext = Str(f, "ext") ?? "";
                            string note = Str(f, "format_note");
                            string label = lang + (abr > 0 ? " - " + ((int)abr).ToString(CultureInfo.InvariantCulture) + "k" : "") +
                                           " - " + ext + (string.IsNullOrEmpty(note) ? "" : " (" + note + ")") + " [" + id + "]";
                            p.Audios.Add(new AudioOption { FormatId = id, Label = label });
                        }
                    }
                }
                p.Heights = heights.OrderByDescending(x => x).ToList();
                if (p.Audios.Count > 15) p.Audios = p.Audios.Take(15).ToList();

                JsonElement subs;
                if (root.TryGetProperty("subtitles", out subs) && subs.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty sp in subs.EnumerateObject())
                        p.Subs.Add(new SubOption { Lang = sp.Name, Auto = false, Label = sp.Name });
                }
                JsonElement auto;
                if (root.TryGetProperty("automatic_captions", out auto) && auto.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty sp in auto.EnumerateObject())
                    {
                        if (sp.Name == "en" || sp.Name == "ur" || sp.Name == "hi")
                            p.Subs.Add(new SubOption { Lang = sp.Name, Auto = true, Label = sp.Name + " (auto-generated)" });
                    }
                }
                return p;
            }
        }

        /// <summary>Enumerates the ENTIRE playlist (flat, nothing downloaded). Keeps playlist position and episode number separate.</summary>
        public static async Task<List<PlaylistItem>> ParsePlaylistAsync(string url, CancellationToken ct)
        {
            ToolPaths.Require(ToolPaths.YtDlp);
            List<string> args = Common("-J", "--flat-playlist", "--yes-playlist", "--skip-download", url);
            ProcResult r = await Proc.RunAsync(ToolPaths.YtDlp, args, ct, 600000);
            if (r.Canceled) throw new OperationCanceledException(ct);
            if (r.TimedOut) throw new YtDlpException("Reading the playlist took too long (10 minutes).");
            if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Out))
                throw new YtDlpException(FriendlyError(r.Err));

            List<PlaylistItem> items = new List<PlaylistItem>();
            using (JsonDocument doc = JsonDocument.Parse(FirstJson(r.Out)))
            {
                JsonElement root = doc.RootElement;
                JsonElement entries;
                if (Str(root, "_type") == "playlist" && root.TryGetProperty("entries", out entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    int counter = 0;
                    Flatten(entries, items, ref counter, 0);
                }
                else
                {
                    PlaylistItem one = new PlaylistItem();
                    one.Position = 1;
                    one.VideoId = Str(root, "id");
                    one.Url = Str(root, "webpage_url") ?? url;
                    one.Title = Str(root, "title") ?? one.VideoId ?? "video";
                    one.EpisodeNumber = EpisodeDetector.Detect(one.Title);
                    one.Season = EpisodeDetector.DetectSeason(one.Title);
                    items.Add(one);
                }
            }
            if (items.Count == 0) throw new YtDlpException("No videos were found at that address.");
            return items;
        }

        static void Flatten(JsonElement entries, List<PlaylistItem> items, ref int counter, int depth)
        {
            foreach (JsonElement e in entries.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                JsonElement sub;
                if (depth < 2 && e.TryGetProperty("entries", out sub) && sub.ValueKind == JsonValueKind.Array)
                {
                    Flatten(sub, items, ref counter, depth + 1);
                    continue;
                }
                counter++;
                PlaylistItem it = new PlaylistItem();
                it.Position = counter;
                it.VideoId = Str(e, "id");
                string title = Str(e, "title");
                string url = Str(e, "webpage_url") ?? Str(e, "url");
                string ie = Str(e, "ie_key");
                if (string.IsNullOrEmpty(url) || !UrlUtil.IsHttp(url))
                {
                    if (!string.IsNullOrEmpty(it.VideoId) && (ie == null || ie.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase)))
                        url = "https://www.youtube.com/watch?v=" + it.VideoId;
                }
                it.Url = url;
                it.Title = string.IsNullOrWhiteSpace(title) ? (it.VideoId ?? "video " + counter.ToString(CultureInfo.InvariantCulture)) : title;
                it.Unavailable = string.IsNullOrEmpty(url) ||
                                 it.Title == "[Private video]" || it.Title == "[Deleted video]" || it.Title == "[Unavailable video]";
                if (it.Unavailable) it.Status = "Unavailable";
                it.EpisodeNumber = EpisodeDetector.Detect(it.Title);
                it.Season = EpisodeDetector.DetectSeason(it.Title);
                items.Add(it);
            }
        }

        /// <summary>quality: "Best Available", "1080p", "720p"... audioFormatId optional specific audio stream.</summary>
        public static string BuildFormat(string quality, string audioFormatId)
        {
            int h = QualityHeight(quality);
            string audio = string.IsNullOrEmpty(audioFormatId) ? "ba" : audioFormatId;
            string audioMp4 = string.IsNullOrEmpty(audioFormatId) ? "ba[ext=m4a]" : audioFormatId;
            if (h <= 0)
                return "bv*[vcodec^=avc1][ext=mp4]+" + audioMp4 + "/b[vcodec^=avc1][ext=mp4]/bv*[vcodec^=avc1]+" + audio +
                       "/b[vcodec^=avc1]/bv*+" + audio + "/b[vcodec!=none]";
            string hs = h.ToString(CultureInfo.InvariantCulture);
            return "bv*[vcodec^=avc1][height<=" + hs + "][ext=mp4]+" + audioMp4 +
                   "/b[vcodec^=avc1][height<=" + hs + "][ext=mp4]/bv*[vcodec^=avc1][height<=" + hs + "]+" + audio +
                   "/b[vcodec^=avc1][height<=" + hs + "]/bv*[height<=" + hs + "]+" + audio +
                   "/b[height<=" + hs + "][vcodec!=none]/bv*+" + audio + "/b[vcodec!=none]";
        }

        public static int QualityHeight(string quality)
        {
            if (string.IsNullOrEmpty(quality)) return 0;
            Match m = Regex.Match(quality, @"(\d{3,4})p?");
            int n;
            return m.Success && int.TryParse(m.Groups[1].Value, out n) ? n : 0;
        }

        static string FirstJson(string output)
        {
            int i = output.IndexOf('{');
            return i > 0 ? output.Substring(i) : output;
        }

        static string Str(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            }
            return null;
        }

        static double Num(JsonElement e, string name)
        {
            JsonElement v;
            double d;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out d))
                return d;
            return 0;
        }

        public static string FriendlyError(string stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr)) return "yt-dlp failed without a message.";
            string line = stderr.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
                          ?? stderr.Trim().Split('\n').Last().Trim();
            string l = line.ToLowerInvariant();
            if (l.Contains("unsupported url")) return "This link is not supported by yt-dlp (no video found at that address).";
            if (l.Contains("private video") || l.Contains("video unavailable") || l.Contains("this video is not available") || l.Contains("has been removed"))
                return "The video is unavailable, private or removed.";
            if (l.Contains("sign in") || l.Contains("login required") || l.Contains("members-only") || l.Contains("age-restricted"))
                return "The site wants you to sign in (not supported): " + Trim(line);
            if (l.Contains("drm")) return "This content is DRM-protected and cannot be downloaded.";
            if (l.Contains("http error 403")) return "The server refused the request (HTTP 403).";
            if (l.Contains("http error 404")) return "Not found (HTTP 404). The link may be wrong or removed.";
            if (l.Contains("http error 429")) return "Too many requests (HTTP 429). Try again later.";
            if (l.Contains("ffmpeg") || l.Contains("postprocess")) return "FFmpeg problem: " + Trim(line);
            if (l.Contains("getaddrinfo") || l.Contains("network is unreachable") || l.Contains("timed out") || l.Contains("connection") || l.Contains("urlopen error"))
                return "Network problem: " + Trim(line);
            if (l.Contains("no space left") || l.Contains("errno 28")) return "The disk is full.";
            if (l.Contains("permission denied") || l.Contains("errno 13")) return "Permission denied writing to the destination folder.";
            return Trim(line);
        }

        static string Trim(string s)
        {
            s = Regex.Replace(s, @"^ERROR:\s*", "", RegexOptions.IgnoreCase);
            return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }
    }

    // ===================================================================== aria2 (JSON-RPC daemon)
    public sealed class Aria2Exception : Exception
    {
        public Aria2Exception(string m) : base(m) { }
    }

    public sealed class Aria2Status
    {
        public string Gid;
        public string Status;
        public long Total;
        public long Completed;
        public long Speed;
        public int ErrorCode;
        public string ErrorMessage;
        public string FollowedBy;
        public bool IsBt;
        public string BtName;
        public int Seeders;
        public int Connections;
    }

    public sealed class Aria2 : IDisposable
    {
        readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        readonly SemaphoreSlim _startLock = new SemaphoreSlim(1, 1);
        System.Diagnostics.Process _proc;
        int _port;
        string _secret;
        long _limitBytes;

        /// <summary>Returns true when a new aria2c process had to be started.</summary>
        public async Task<bool> EnsureRunningAsync()
        {
            await _startLock.WaitAsync();
            try
            {
                if (_proc != null && !_proc.HasExited) return false;
                ToolPaths.Require(ToolPaths.Aria2);
                _port = FreePort();
                _secret = Guid.NewGuid().ToString("N");
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ToolPaths.Aria2,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppPaths.DataDir
                };
                string[] a =
                {
                    "--no-conf=true", "--enable-rpc=true", "--rpc-listen-all=false",
                    "--rpc-listen-port=" + _port.ToString(CultureInfo.InvariantCulture),
                    "--rpc-secret=" + _secret,
                    "--max-concurrent-downloads=100", "--continue=true", "--auto-file-renaming=false",
                    "--allow-overwrite=false", "--file-allocation=none", "--max-connection-per-server=8", "--split=8",
                    "--min-split-size=2M", "--max-tries=5", "--retry-wait=3", "--connect-timeout=20", "--timeout=30",
                    "--bt-save-metadata=true", "--bt-load-saved-metadata=true", "--follow-torrent=true", "--seed-time=0",
                    "--enable-dht=true", "--bt-enable-lpd=true", "--enable-peer-exchange=true",
                    "--dht-file-path=" + Path.Combine(AppPaths.DataDir, "dht.dat"),
                    "--dht-file-path6=" + Path.Combine(AppPaths.DataDir, "dht6.dat"),
                    "--user-agent=" + HttpProbe.UserAgent,
                    "--quiet=true", "--summary-interval=0",
                    "--stop-with-process=" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)
                };
                foreach (string s in a) psi.ArgumentList.Add(s);
                _proc = System.Diagnostics.Process.Start(psi);
                if (_proc == null) throw new DownloadException("aria2c could not be started.", true);

                for (int i = 0; i < 60; i++)
                {
                    if (_proc.HasExited) throw new DownloadException("aria2c stopped immediately (exit code " + _proc.ExitCode.ToString(CultureInfo.InvariantCulture) + ").", false);
                    try
                    {
                        await CallAsync("aria2.getVersion");
                        break;
                    }
                    catch (Exception) { await Task.Delay(250); }
                    if (i == 59) throw new DownloadException("aria2c did not start answering.", false);
                }
                Log.Info("aria2c started on port " + _port.ToString(CultureInfo.InvariantCulture));
                if (_limitBytes > 0) await SetLimitInternalAsync(_limitBytes);
                return true;
            }
            finally { _startLock.Release(); }
        }

        static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        async Task<JsonElement> CallAsync(string method, params object[] args)
        {
            List<object> ps = new List<object>();
            ps.Add("token:" + _secret);
            foreach (object o in args) ps.Add(o);
            string body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" }, { "id", "draq" }, { "method", method }, { "params", ps }
            });
            using (StringContent content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage resp = await _http.PostAsync("http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + "/jsonrpc", content))
            {
                string text = await resp.Content.ReadAsStringAsync();
                using (JsonDocument doc = JsonDocument.Parse(text))
                {
                    JsonElement err;
                    if (doc.RootElement.TryGetProperty("error", out err))
                    {
                        JsonElement msg;
                        string m = err.TryGetProperty("message", out msg) ? msg.GetString() : "aria2 error";
                        throw new Aria2Exception(m);
                    }
                    return doc.RootElement.GetProperty("result").Clone();
                }
            }
        }

        public async Task<string> AddUriAsync(string url, Dictionary<string, string> options)
        {
            JsonElement r = await CallAsync("aria2.addUri", new List<string> { url }, options);
            return r.GetString();
        }

        public async Task<string> AddTorrentAsync(string torrentPath, Dictionary<string, string> options)
        {
            string b64 = Convert.ToBase64String(File.ReadAllBytes(torrentPath));
            JsonElement r = await CallAsync("aria2.addTorrent", b64, new List<string>(), options);
            return r.GetString();
        }

        public async Task<Aria2Status> TellStatusAsync(string gid)
        {
            string[] keys = { "gid", "status", "totalLength", "completedLength", "downloadSpeed", "errorCode", "errorMessage",
                              "followedBy", "bittorrent", "numSeeders", "connections" };
            JsonElement r = await CallAsync("aria2.tellStatus", gid, keys);
            Aria2Status s = new Aria2Status();
            s.Gid = gid;
            s.Status = Get(r, "status");
            s.Total = ToLong(Get(r, "totalLength"));
            s.Completed = ToLong(Get(r, "completedLength"));
            s.Speed = ToLong(Get(r, "downloadSpeed"));
            s.ErrorCode = (int)ToLong(Get(r, "errorCode"));
            s.ErrorMessage = Get(r, "errorMessage");
            s.Seeders = (int)ToLong(Get(r, "numSeeders"));
            s.Connections = (int)ToLong(Get(r, "connections"));
            JsonElement fb;
            if (r.TryGetProperty("followedBy", out fb) && fb.ValueKind == JsonValueKind.Array && fb.GetArrayLength() > 0)
                s.FollowedBy = fb[0].GetString();
            JsonElement bt;
            if (r.TryGetProperty("bittorrent", out bt) && bt.ValueKind == JsonValueKind.Object)
            {
                s.IsBt = true;
                JsonElement info;
                if (bt.TryGetProperty("info", out info) && info.ValueKind == JsonValueKind.Object)
                    s.BtName = Get(info, "name");
            }
            return s;
        }

        static string Get(JsonElement e, string name)
        {
            JsonElement v;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String) return v.GetString();
            return null;
        }

        static long ToLong(string s)
        {
            long n;
            return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }

        /// <summary>Stops a download but keeps partial data and its .aria2 control file so it can be resumed.</summary>
        public async Task RemoveAsync(string gid)
        {
            if (string.IsNullOrEmpty(gid)) return;
            try { await CallAsync("aria2.forceRemove", gid); } catch (Exception) { }
            try { await CallAsync("aria2.removeDownloadResult", gid); } catch (Exception) { }
        }

        public async Task SetLimitAsync(long bytesPerSecond)
        {
            _limitBytes = bytesPerSecond;
            if (_proc == null || _proc.HasExited) return;
            try { await SetLimitInternalAsync(bytesPerSecond); } catch (Exception ex) { Log.Warn("Could not set speed limit: " + ex.Message); }
        }

        Task SetLimitInternalAsync(long bytesPerSecond)
        {
            Dictionary<string, string> o = new Dictionary<string, string>();
            o["max-overall-download-limit"] = bytesPerSecond.ToString(CultureInfo.InvariantCulture);
            return CallAsync("aria2.changeGlobalOption", o);
        }

        public static string ErrorText(int code, string raw)
        {
            switch (code)
            {
                case 2: return "The server did not answer in time.";
                case 3: return "File not found on the server (HTTP 404).";
                case 5: return "Download speed was too slow.";
                case 6: return "Network problem during the download.";
                case 8: return "The server does not support resuming this download.";
                case 9: return "Not enough free disk space.";
                case 13: return "A file with that name already exists.";
                case 15: case 16: case 17: return "Could not write the file (permission or disk problem). " + raw;
                case 18: return "Could not create the destination folder.";
                case 19: return "Could not resolve the server name (check the address and your internet).";
                case 22: return "The server sent an unexpected response.";
                case 23: return "Too many redirects.";
                case 24: return "The server requires a login (HTTP 401/403).";
                case 25: case 26: return "This is not a valid .torrent file.";
                case 27: return "Invalid magnet link.";
                case 29: return "The server is temporarily unavailable.";
                default: return string.IsNullOrWhiteSpace(raw) ? "Download failed (aria2 error " + code.ToString(CultureInfo.InvariantCulture) + ")." : raw;
            }
        }

        public void Dispose()
        {
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(true); } catch { }
            _http.Dispose();
        }
    }
}

