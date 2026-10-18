using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using DRAQ.Database;
using DRAQ.Models;
using DRAQ.Services;
using DRAQ.Utilities;

namespace DRAQ
{
    // ALIFAIZ
    public partial class App : Application
    {
        public static Db Db { get; private set; }
        public static AppSettings Settings { get; private set; }
        public static QueueEngine Engine { get; private set; }
        public static Library Library { get; private set; }
        public static TrayService Tray { get; private set; }
        public static MainWindow MainWin { get; private set; }
        public static bool ExitRequested { get; private set; }

        public static event Action LibraryChanged;

        public static void ApplyTheme(bool dark)
        {
            SetThemeBrush("Accent", dark ? "#8BA8FF" : "#2563EB");
            SetThemeBrush("AppBg", dark ? "#17191F" : "#FFFFFF");
            SetThemeBrush("PanelBg", dark ? "#22252D" : "#F5F7FA");
            SetThemeBrush("ControlBg", dark ? "#2B2F39" : "#FFFFFF");
            SetThemeBrush("ControlBorder", dark ? "#484E5C" : "#D1D5DB");
            SetThemeBrush("MainText", dark ? "#F1F3F8" : "#111827");
            SetThemeBrush("Muted", dark ? "#B5BBC8" : "#6B7280");
            SetThemeBrush("GridLine", dark ? "#3A3F49" : "#E5E7EB");
        }

        static void SetThemeBrush(string key, string color)
        {
            Current.Resources[key] = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }

        public static void RaiseLibraryChanged()
        {
            Action h = LibraryChanged;
            if (h != null) h();
        }

        Mutex _mutex;
        DispatcherTimer _autoTimer;
        bool _ownsMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try
            {
                AppPaths.Init();
            }
            catch (Exception ex)
            {
                MessageBox.Show("DRAQ cannot create its data folder:\n\n" + ex.Message, "DRAQ", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            _mutex = new Mutex(true, "Local\\DRAQ_SingleInstance_v1", out _ownsMutex);
            if (!_ownsMutex)
            {
                MessageBox.Show("DRAQ is already running (look for its icon in the system tray).", "DRAQ", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            SessionEnding += (s, a) => { ExitRequested = true; };
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log.Error("Unhandled exception", a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, a) => { Log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

            try
            {
                Db = new Db(AppPaths.DbPath);
                Settings = new AppSettings();
                Settings.Load(Db.GetSettings());
                ApplyTheme(Settings.DarkTheme);
                Engine = new QueueEngine(Db, Settings, Dispatcher);
                Library = new Library(Db, Engine, Settings);
                Engine.Notification += OnEngineNotification;
                Engine.QueueIdle += OnQueueIdle;
                Engine.ConfirmDuplicates = delegate (int n)
                {
                    return MessageBox.Show(n.ToString() + " item(s) are already in the queue or already downloaded.\n\nAdd them again?",
                        "Duplicates", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                };
                Engine.Start();
            }
            catch (Exception ex)
            {
                Log.Error("Startup failed", ex);
                MessageBox.Show("DRAQ could not start:\n\n" + ex.Message + "\n\nDetails were written to the log folder.", "DRAQ",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            MainWin = new MainWindow();
            MainWindow = MainWin;
            Tray = new TrayService(MainWin);

            bool startHidden = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
            if (startHidden) MainWin.Hide();
            else MainWin.Show();

            _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _autoTimer.Tick += (s, a) => MainWin.AutoCheckIfDue();
            _autoTimer.Start();

            var missing = ToolPaths.Missing();
            if (missing.Count > 0)
            {
                MessageBox.Show("These required tools were not found in the 'tools' folder next to DRAQ.exe:\n\n  " + string.Join("\n  ", missing) +
                                "\n\nDownloads that need them will fail. Run build.bat again to obtain them.", "DRAQ - missing tools",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("UI exception", e.Exception);
            e.Handled = true;
            MessageBox.Show("Something went wrong, but DRAQ is still running:\n\n" + e.Exception.Message +
                            "\n\n(Details were written to the log folder.)", "DRAQ", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        void OnEngineNotification(string kind, string title, string message)
        {
            bool show = true;
            switch (kind)
            {
                case "done": show = Settings.NotifyCompleted; break;
                case "queue": show = Settings.NotifyCompleted; break;
                case "fail": show = Settings.NotifyFailed; break;
                case "new": show = Settings.NotifyNewEpisodes; break;
            }
            if (show) Notify(title, message);
        }

        public static void Notify(string title, string message)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (Tray != null) Tray.Balloon(title, message);
            }));
        }

        void OnQueueIdle()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 60 /c \"DRAQ: all downloads are finished\"") { CreateNoWindow = true, UseShellExecute = false });
                    MessageBoxResult r = MessageBox.Show("All downloads are finished.\n\nThis PC will shut down in 60 seconds.\nClick Cancel to stop the shutdown.",
                        "DRAQ - shutting down", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                    if (r == MessageBoxResult.Cancel)
                        Process.Start(new ProcessStartInfo("shutdown", "/a") { CreateNoWindow = true, UseShellExecute = false });
                }
                catch (Exception ex) { Log.Error("Shutdown command failed", ex); }
            }));
        }

        public static void ApplyStartupSetting()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (k == null) return;
                    if (Settings.StartWithWindows)
                    {
                        string exe = Environment.ProcessPath;
                        k.SetValue("DRAQ", "\"" + exe + "\"" + (Settings.StartMinimized ? " --tray" : ""));
                    }
                    else k.DeleteValue("DRAQ", false);
                }
            }
            catch (Exception ex) { Log.Error("Could not update the Windows startup entry", ex); }
        }

        /// <summary>Ends the program (called from the tray menu and from the window's close).</summary>
        public static void RequestExit()
        {
            ExitRequested = true;
            Application.Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { if (_autoTimer != null) _autoTimer.Stop(); } catch { }
            try { if (Engine != null) Engine.Dispose(); } catch (Exception ex) { Log.Error("Engine shutdown failed", ex); }
            try { if (Tray != null) Tray.Dispose(); } catch { }
            try { if (Db != null) Db.Dispose(); } catch { }
            try { if (_mutex != null && _ownsMutex) _mutex.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}

