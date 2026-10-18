using System;
using System.Drawing;
using System.Windows;
using WF = System.Windows.Forms;
using DRAQ.Utilities;

namespace DRAQ.Services
{
    /// <summary>System tray icon: Open DRAQ, Pause all, Resume all, queue status, Exit.</summary>
    public sealed class TrayService : IDisposable
    {
        readonly WF.NotifyIcon _icon;
        readonly WF.ToolStripMenuItem _status;
        readonly Window _window;

        public TrayService(Window window)
        {
            _window = window;
            _icon = new WF.NotifyIcon();
            _icon.Text = "DRAQ";
            _icon.Icon = LoadIcon();
            _icon.Visible = true;

            WF.ContextMenuStrip menu = new WF.ContextMenuStrip();
            menu.Items.Add("Open DRAQ", null, delegate (object s, EventArgs e) { ShowWindow(); });
            menu.Items.Add("Pause all", null, delegate (object s, EventArgs e) { App.Engine.PauseAll(); });
            menu.Items.Add("Resume all", null, delegate (object s, EventArgs e) { App.Engine.ResumeAll(); });
            _status = new WF.ToolStripMenuItem("Queue: idle");
            _status.Enabled = false;
            menu.Items.Add(_status);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate (object s, EventArgs e) { App.Current.Dispatcher.BeginInvoke(new Action(() => ((MainWindow)_window).ExitFromTray())); });
            menu.Opening += delegate (object s, System.ComponentModel.CancelEventArgs e) { UpdateStatus(); };
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += delegate (object s, EventArgs e) { ShowWindow(); };
        }

        static Icon LoadIcon()
        {
            try
            {
                Icon i = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (i != null) return i;
            }
            catch (Exception ex) { Log.Warn("Tray icon: " + ex.Message); }
            return SystemIcons.Application;
        }

        void UpdateStatus()
        {
            int d, q, p; long sp;
            App.Engine.GetCounts(out d, out q, out p, out sp);
            _status.Text = (d + q + p) == 0
                ? "Queue: idle"
                : "Queue: " + d + " downloading, " + q + " queued, " + p + " paused" + (sp > 0 ? " (" + Fmt.Speed(sp) + ")" : "");
        }

        public void SetTooltip(string text)
        {
            if (text.Length > 60) text = text.Substring(0, 60);
            _icon.Text = text;
        }

        public void ShowWindow()
        {
            App.Current.Dispatcher.BeginInvoke(new Action(delegate
            {
                _window.Show();
                if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
                _window.Activate();
            }));
        }

        public void Balloon(string title, string text)
        {
            try { _icon.ShowBalloonTip(6000, title, text, WF.ToolTipIcon.Info); }
            catch (Exception ex) { Log.Warn("Balloon failed: " + ex.Message); }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}

