using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DRAQ.Services;
using DRAQ.Utilities;
using DRAQ.Views;

namespace DRAQ
{
    // TUFAN
    public partial class MainWindow : Window
    {
        readonly DispatcherTimer _timer;
        bool _closingConfirmed;

        public MainWindow()
        {
            InitializeComponent();
            UpdateThemeButton();
            try
            {
                string ico = System.IO.Path.Combine(AppPaths.AppDir, "assets", "draq.ico");
                if (System.IO.File.Exists(ico))
                    Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(ico, UriKind.Absolute));
            }
            catch (Exception ex) { Log.Warn("Window icon: " + ex.Message); }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += delegate { UpdateStatus(); };
            _timer.Start();
            StateChanged += OnStateChanged;
        }

        public void GoToQueue() { Tabs.SelectedIndex = 1; }

        void UpdateStatus()
        {
            int d, q, p; long speed;
            App.Engine.GetCounts(out d, out q, out p, out speed);
            string text = "Downloading: " + d + "   |   Queued: " + q + "   |   Paused: " + p;
            if (speed > 0) text += "   |   Speed: " + Fmt.Speed(speed);
            StatusText.Text = text;
            if (App.Tray != null) App.Tray.SetTooltip(d + q > 0 ? "DRAQ - " + d + " downloading, " + q + " queued" : "DRAQ - idle");
        }

        void OnStateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && App.Settings.MinimizeToTray) Hide();
        }

        public void AutoCheckIfDue() { NewEpisodesTab.AutoCheckIfDue(); }

        public void ExitFromTray()
        {
            if (ConfirmExit()) { _closingConfirmed = true; App.RequestExit(); }
        }

        bool ConfirmExit()
        {
            int d, q, p; long s;
            App.Engine.GetCounts(out d, out q, out p, out s);
            if (d + q == 0) return true;
            return MessageBox.Show("Downloads are still running or waiting.\n\nIf you exit now they will be paused and continue automatically the next time DRAQ starts.\n\nExit DRAQ?",
                "Exit DRAQ", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_closingConfirmed && !App.ExitRequested)
            {
                if (!ConfirmExit()) { e.Cancel = true; return; }
                _closingConfirmed = true;
            }
            base.OnClosing(e);
        }

        void UpdateThemeButton()
        {
            ThemeButton.Content = App.Settings.DarkTheme ? "☀ Light mode" : "☾ Dark mode";
            ThemeButton.ToolTip = "Switch the application color theme";
        }

        void Theme_Click(object sender, RoutedEventArgs e)
        {
            App.Settings.DarkTheme = !App.Settings.DarkTheme;
            App.ApplyTheme(App.Settings.DarkTheme);
            App.Db.SaveSettings(App.Settings.ToDictionary());
            UpdateThemeButton();
        }

        void History_Click(object sender, RoutedEventArgs e)
        {
            HistoryWindow w = new HistoryWindow { Owner = this };
            w.ShowDialog();
        }

        void Settings_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow w = new SettingsWindow { Owner = this };
            w.ShowDialog();
        }
    }
}

