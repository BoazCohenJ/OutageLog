using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OutageLog;

internal sealed class TrayApp : ApplicationContext, IStatusSource
{
    public const string HomePage = "https://boazcohenj.github.io/OutageLog/";
    private static readonly TimeSpan LoadHistory = TimeSpan.FromDays(7);
    private static readonly TimeSpan SelfTestLength = TimeSpan.FromSeconds(20);

    private readonly Settings _settings;
    private readonly string _dataDir;
    private readonly SampleLog _log;
    private readonly OutageTracker _tracker;
    private readonly ConnectionMonitor _monitor;
    private readonly NotifyIcon _tray;
    private readonly Control _invoker = new();
    private readonly Dictionary<Color, Icon> _icons = new();
    private readonly ToolStripMenuItem _notifyItem, _startupItem, _pauseItem;
    private readonly Queue<DateTime> _recentNotifications = new();
    private StatusForm _form;
    private Status? _shownStatus;
    private bool _started;

    public TrayApp(bool startHidden, string dataDir)
    {
        _invoker.CreateControl();
        _settings = Settings.Load();
        _dataDir = dataDir ?? Settings.DefaultDataDir;
        _log = new SampleLog(_dataDir);
        _tracker = new OutageTracker(_settings.TrackerOptions);
        _monitor = new ConnectionMonitor(new Prober(_settings.Targets), _log, _settings.TrackerOptions, _tracker) { SlowMs = _settings.SlowMs };
        _monitor.SampleTaken += s => _invoker.BeginInvoke(new Action(() => OnSample(s)));

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Show status", null, (_, _) => ShowStatus()) { Font = new Font(menu.Font, FontStyle.Bold) });
        var report = new ToolStripMenuItem("Make a report");
        report.DropDownItems.Add("Last 24 hours", null, (_, _) => OpenReport(TimeSpan.FromDays(1)));
        report.DropDownItems.Add("Last 7 days", null, (_, _) => OpenReport(TimeSpan.FromDays(7)));
        report.DropDownItems.Add("Last 30 days", null, (_, _) => OpenReport(TimeSpan.FromDays(30)));
        report.DropDownItems.Add("Everything logged", null, (_, _) => OpenReport(TimeSpan.FromDays(3650)));
        menu.Items.Add(report);
        menu.Items.Add("Open log folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        _notifyItem = new ToolStripMenuItem("Notify me about outages", null, (_, _) =>
        {
            _settings.Notify = !_settings.Notify;
            _settings.Save();
            SyncMenu();
        });
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            try
            {
                Settings.StartWithWindows = !Settings.StartWithWindows;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't change the startup setting: " + ex.Message, "OutageLog");
            }
            SyncMenu();
        });
        _pauseItem = new ToolStripMenuItem("Pause monitoring", null, (_, _) =>
        {
            _monitor.Paused = !_monitor.Paused;
            SyncMenu();
            UpdateTray();
        });
        menu.Items.Add(_notifyItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Run self-test (fake a 20-second outage)", null, (_, _) => RunSelfTest());
        menu.Items.Add("Website and help", null, (_, _) => OpenUrl(HomePage));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => SyncMenu();

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Icon = IconFor(null), Text = "OutageLog: starting" };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowStatus();
        };
        _tray.BalloonTipClicked += (_, _) => ShowStatus();

        if (_settings.FirstRun)
        {
            _settings.Save();
            // Logging only helps if it's running when the internet drops, so start with Windows by default.
            try
            {
                Settings.StartWithWindows = true;
            }
            catch
            {
            }
            _tray.ShowBalloonTip(10000, "OutageLog is running",
                "It checks your connection every few seconds and logs every drop. It starts with Windows; right-click the tray icon to make a report.",
                ToolTipIcon.Info);
        }

        // Load recent history first so the status window shows earlier outages, then start checking.
        Task.Run(() =>
        {
            try
            {
                _log.Prune(_settings.KeepDays);
                var history = _log.Stream(DateTime.UtcNow - LoadHistory, DateTime.UtcNow);
                lock (_tracker)
                    foreach (var s in history) _tracker.Add(s);
            }
            catch (Exception)
            {
                // Unreadable history shouldn't stop monitoring.
            }
            // Subscribe only now, so outages replayed from history don't pop up as notifications.
            _tracker.Started += o => _invoker.BeginInvoke(new Action(() => OnOutageStarted(o)));
            _tracker.Ended += o => _invoker.BeginInvoke(new Action(() => OnOutageEnded(o)));
            _started = true;
            _monitor.Start();
        });

        if (!startHidden) ShowStatus();
    }

    public OutageTracker Tracker => _tracker;
    public Sample Last => _monitor.Last;
    public bool Paused => _monitor.Paused;
    public string GatewayIp => _monitor.Prober.GatewayIp;
    public string[] Targets => _monitor.Prober.Targets;

    public void ShowFromOtherInstance() => _invoker.BeginInvoke(new Action(() => ShowStatus()));

    private void OnSample(Sample s)
    {
        UpdateTray();
        _form?.UpdateStatus();
    }

    private void UpdateTray()
    {
        var last = _monitor.Last;
        Status? status = _monitor.Paused || last == null ? null : last.Status;
        if (status != _shownStatus || _tray.Icon == null)
        {
            _tray.Icon = IconFor(status);
            _shownStatus = status;
        }
        string text = _monitor.Paused ? "paused" : !_started || last == null ? "starting" : StatusInfo.Label(last.Status);
        int today;
        lock (_tracker)
            today = _tracker.Outages.Count(o => !o.Simulated && o.Start.ToLocalTime().Date == DateTime.Today)
                    + (_tracker.Current is { Simulated: false } c && c.Start.ToLocalTime().Date == DateTime.Today ? 1 : 0);
        string tip = $"OutageLog: {text}, {today} outage{(today == 1 ? "" : "s")} today";
        _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    private Icon IconFor(Status? status)
    {
        Color color = Icons.ColorFor(status);
        if (!_icons.TryGetValue(color, out var icon))
        {
            icon = Icons.Make(color, SystemInformation.SmallIconSize.Width);
            _icons[color] = icon;
        }
        return icon;
    }

    private void OnOutageStarted(Outage o)
    {
        _form?.UpdateStatus();
        Notify("Internet outage: " + StatusInfo.Label(o.Cause) + (o.Simulated ? " (self-test)" : ""),
            StatusInfo.Explain(o.Cause) + " OutageLog is logging it.", ToolTipIcon.Warning);
    }

    private void OnOutageEnded(Outage o)
    {
        _form?.UpdateStatus();
        if (o.EndUncertain) return; // Monitoring stopped mid-outage (sleep); nothing useful to say.
        Notify("Back online" + (o.Simulated ? " (self-test)" : ""),
            $"The connection was down for {ReportBuilder.Dur(o.Duration)} ({StatusSideText(o)}). It's in the log and the next report.",
            ToolTipIcon.Info);
    }

    private static string StatusSideText(Outage o) => StatusInfo.SideLabel(o.Side).ToLowerInvariant();

    private void Notify(string title, string text, ToolTipIcon icon)
    {
        if (!_settings.Notify) return;
        // A flapping connection shouldn't bury the desktop in balloons: at most 4 per 10 minutes.
        while (_recentNotifications.Count > 0 && DateTime.UtcNow - _recentNotifications.Peek() > TimeSpan.FromMinutes(10))
            _recentNotifications.Dequeue();
        if (_recentNotifications.Count >= 4) return;
        _recentNotifications.Enqueue(DateTime.UtcNow);
        _tray.ShowBalloonTip(6000, title, text, icon);
    }

    public void RunSelfTest()
    {
        if (!_started)
        {
            MessageBox.Show("OutageLog is still starting. Try again in a few seconds.", "OutageLog");
            return;
        }
        _monitor.SimulateOutage(SelfTestLength);
        _tray.ShowBalloonTip(6000, "Self-test started",
            "For 20 seconds OutageLog checks addresses that never answer, as if the internet were down. Your real connection isn't touched. The outage is marked as a self-test and left out of report totals.",
            ToolTipIcon.Info);
    }

    public void OpenReport(TimeSpan window)
    {
        try
        {
            var to = DateTime.UtcNow;
            var samples = _log.Stream(to - window, to);
            string html = ReportBuilder.Build(samples, new ReportOptions
            {
                FromUtc = to - window,
                ToUtc = to,
                Tracker = _settings.TrackerOptions,
                Targets = _settings.Targets,
                Version = Program.Version,
                HomePage = HomePage,
            });
            Directory.CreateDirectory(Settings.ReportDir);
            string path = Path.Combine(Settings.ReportDir, "outage-report-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".html");
            File.WriteAllText(path, html);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't make the report: " + ex.Message, "OutageLog");
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            Process.Start(new ProcessStartInfo(_dataDir) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void SyncMenu()
    {
        _notifyItem.Checked = _settings.Notify;
        _pauseItem.Checked = _monitor.Paused;
        try
        {
            _startupItem.Checked = Settings.StartWithWindows;
        }
        catch
        {
            _startupItem.Checked = false;
        }
    }

    private void ShowStatus()
    {
        if (_form == null || _form.IsDisposed)
            _form = new StatusForm(this) { Icon = Icons.AppIcon(new Size(32, 32)) };
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        _form.UpdateStatus();
    }

    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _monitor.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        base.ExitThreadCore();
    }
}
