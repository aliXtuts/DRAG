using System;
using System.Collections.Generic;
using System.IO;
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
    public partial class DownloadView : UserControl
    {
        sealed class Choice
        {
            public string Label;
            public string Value;
            public override string ToString() { return Label; }
        }

        DownloadKind? _kind;
        string _analyzedUrl;
        HttpProbeResult _http;
        VideoProbe _video;
        bool _destTouched;
        bool _settingDest;
        bool _busy;

        public DownloadView()
        {
            InitializeComponent();
            SetDest(App.Settings.DownloadFolder);
        }

        void SetDest(string path)
        {
            _settingDest = true;
            DestBox.Text = path;
            _settingDest = false;
        }

        void DestBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_settingDest) _destTouched = true;
        }

        void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (UrlBox.Text.Trim() != _analyzedUrl)
            {
                _kind = null;
                _analyzedUrl = null;
                VideoOptions.Visibility = Visibility.Collapsed;
                InfoText.Text = "Nothing checked yet.";
            }
        }

        void Paste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText()) UrlBox.Text = Clipboard.GetText().Trim();
            }
            catch (Exception ex) { Log.Warn("Clipboard: " + ex.Message); }
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            string p = UiUtil.PickFolder(DestBox.Text, "Choose the folder for this download");
            if (p != null) { DestBox.Text = p; _destTouched = true; }
        }

        async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            await AnalyzeAsync();
        }

        void SetBusy(bool busy, string text)
        {
            _busy = busy;
            StartButton.IsEnabled = !busy;
            BusyText.Text = text ?? "";
        }

        /// <summary>Detects what the link is. Returns true when it can be downloaded.</summary>
        async Task<bool> AnalyzeAsync()
        {
            if (_busy) return false;
            string url = UrlBox.Text.Trim();
            if (url.Length == 0) { UiUtil.Info("Paste a link first."); return false; }
            _kind = null;
            VideoOptions.Visibility = Visibility.Collapsed;

            if (UrlUtil.IsMagnet(url) || (UrlUtil.IsHttp(url) && UrlUtil.LooksLikeTorrentUrl(url)))
            {
                _kind = DownloadKind.Torrent;
                _analyzedUrl = url;
                InfoText.Text = "Torrent detected. It will be downloaded with DRAQ's built-in torrent engine and appear in the Queue.";
                if (!_destTouched) SetDest(App.Settings.TorrentsFolder);
                return true;
            }
            if (!UrlUtil.IsHttp(url))
            {
                InfoText.Text = "That is not a valid link. Enter an http/https address or a magnet link.";
                return false;
            }

            SetBusy(true, "Checking the link...");
            try
            {
                using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                {
                    HttpProbeResult probe = await HttpProbe.ProbeAsync(url, cts.Token);
                    if (probe.Ok && !probe.LooksLikeWebPage)
                    {
                        _http = probe;
                        _kind = DownloadKind.Direct;
                        _analyzedUrl = url;
                        InfoText.Text = "Direct file\nName: " + probe.FileName + "\nSize: " + (probe.Size > 0 ? Fmt.Bytes(probe.Size) : "unknown") +
                                        "\nResume supported: " + (probe.Ranges ? "yes" : "unknown / no");
                        return true;
                    }

                    BusyText.Text = "Looking for a video on the page...";
                    try
                    {
                        VideoProbe v = await YtDlp.ProbeVideoAsync(url, cts.Token);
                        if (v.IsPlaylist)
                        {
                            InfoText.Text = "This link is a playlist. Please use the Dramas or Series tab to download playlists, so every episode is listed and you can choose.";
                            return false;
                        }
                        _video = v;
                        _kind = DownloadKind.Video;
                        _analyzedUrl = url;
                        FillVideoOptions(v);
                        return true;
                    }
                    catch (YtDlpException ex)
                    {
                        InfoText.Text = "No downloadable video or file was found.\n" + ex.Message +
                                        (probe.Ok ? "" : "\n(" + probe.Error + ")");
                        return false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                InfoText.Text = "Checking the link took too long.";
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Analyze failed", ex);
                InfoText.Text = "Could not check the link: " + ex.Message;
                return false;
            }
            finally { SetBusy(false, ""); }
        }

        void FillVideoOptions(VideoProbe v)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Video: ").Append(v.Title);
            if (!string.IsNullOrEmpty(v.Uploader)) sb.Append("\nFrom: ").Append(v.Uploader);
            if (v.Duration > 0)
            {
                TimeSpan t = TimeSpan.FromSeconds(v.Duration);
                sb.Append("\nLength: ").Append(t.TotalHours >= 1 ? ((int)t.TotalHours).ToString() + ":" + t.ToString("mm\\:ss") : t.ToString("m\\:ss"));
            }
            if (v.Heights.Count == 0 && v.Audios.Count > 0)
                sb.Append("\n\nWARNING: this site only offered AUDIO streams, no video. For YouTube this usually means yt-dlp or deno.exe in the tools folder is missing/outdated. Use Settings > Update yt-dlp and run build.bat update.");
            InfoText.Text = sb.ToString();

            List<Choice> q = new List<Choice> { new Choice { Label = "Best Available", Value = "Best Available" } };
            foreach (int h in v.Heights) q.Add(new Choice { Label = h + "p", Value = h + "p" });
            QualityBox.ItemsSource = q;
            Choice def = q.FirstOrDefault(c => c.Value == App.Settings.DefaultQuality) ?? q[0];
            QualityBox.SelectedItem = def;

            List<Choice> a = new List<Choice>
            {
                new Choice { Label = "Default (best audio)", Value = "" },
                new Choice { Label = "Audio only (MP3)", Value = "MP3" }
            };
            foreach (AudioOption ao in v.Audios) a.Add(new Choice { Label = "Stream: " + ao.Label, Value = ao.FormatId });
            AudioBox.ItemsSource = a;
            AudioBox.SelectedIndex = 0;

            List<Choice> s = new List<Choice> { new Choice { Label = "None", Value = "" } };
            foreach (SubOption so in v.Subs) s.Add(new Choice { Label = so.Label, Value = (so.Auto ? "auto:" : "sub:") + so.Lang });
            SubsBox.ItemsSource = s;
            SubsBox.SelectedIndex = 0;

            VideoOptions.Visibility = Visibility.Visible;
        }

        async void Start_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlBox.Text.Trim();
            if (!_kind.HasValue || _analyzedUrl != url)
            {
                if (!await AnalyzeAsync()) return;
            }

            string err;
            string dest = DestBox.Text.Trim();
            if (!UiUtil.IsUsableFolder(dest, out err)) { UiUtil.Warn(err); return; }
            dest = Path.GetFullPath(dest);

            QueueItem item = new QueueItem { Url = url, DestDir = dest, Priority = 1 };
            long needed = 0;
            switch (_kind.Value)
            {
                case DownloadKind.Direct:
                    item.Kind = DownloadKind.Direct;
                    item.Title = _http.FileName ?? FileNames.FromUrl(url);
                    item.FileName = item.Title;
                    item.Total = _http.Size;
                    needed = _http.Size;
                    break;
                case DownloadKind.Video:
                    item.Kind = DownloadKind.Video;
                    item.Url = string.IsNullOrEmpty(_video.WebpageUrl) ? url : _video.WebpageUrl;
                    item.Title = _video.Title;
                    item.VideoId = _video.Id;
                    ApplyVideoChoices(item);
                    break;
                default:
                    item.Kind = DownloadKind.Torrent;
                    item.Title = UrlUtil.IsMagnet(url) ? UrlUtil.MagnetName(url) : FileNames.FromUrl(url);
                    break;
            }

            if (!UiUtil.CheckDiskSpace(dest, needed)) return;

            AddResult res = App.Engine.AddMany(new[] { item }, true);
            if (res.Added.Count > 0)
            {
                InfoText.Text = "Added to the queue: " + item.Title + "\nYou can follow it in the Queue tab.";
                UrlBox.Text = "";
                _destTouched = false;
                SetDest(App.Settings.DownloadFolder);
            }
            else
            {
                InfoText.Text = "Not added: this link is already in the queue (or already downloaded).";
            }
        }

        void ApplyVideoChoices(QueueItem item)
        {
            Choice q = QualityBox.SelectedItem as Choice;
            Choice a = AudioBox.SelectedItem as Choice;
            Choice s = SubsBox.SelectedItem as Choice;
            string quality = q != null ? q.Value : "Best Available";
            item.Quality = quality;
            List<string> extra = new List<string>();

            if (a != null && a.Value == "MP3")
            {
                item.Format = "ba/b";
                item.Quality = "Audio only";
                extra.Add("-x"); extra.Add("--audio-format"); extra.Add("mp3");
            }
            else
            {
                item.Format = YtDlp.BuildFormat(quality, a != null && !string.IsNullOrEmpty(a.Value) ? a.Value : null);
            }

            if (s != null && !string.IsNullOrEmpty(s.Value) && (a == null || a.Value != "MP3"))
            {
                bool auto = s.Value.StartsWith("auto:", StringComparison.Ordinal);
                string lang = s.Value.Substring(s.Value.IndexOf(':') + 1);
                extra.Add(auto ? "--write-auto-subs" : "--write-subs");
                extra.Add("--sub-langs"); extra.Add(lang);
                extra.Add("--embed-subs");
            }
            item.ExtraArgs = string.Join("\n", extra);
        }
    }
}

