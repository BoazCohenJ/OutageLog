using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace OutageLog;

/// <summary>What the status window shows and can do; the tray app implements it, and so does the screenshot preview.</summary>
internal interface IStatusSource
{
    Sample Last { get; }
    bool Paused { get; }
    string GatewayIp { get; }
    string[] Targets { get; }
    OutageTracker Tracker { get; }
    void OpenReport(TimeSpan window);
    void RunSelfTest();
}

/// <summary>The window behind the tray icon: live status, the last 24 hours, and recent outages.</summary>
internal sealed class StatusForm : Form
{
    private readonly IStatusSource _app;
    private readonly Panel _dot;
    private readonly Label _status, _detail, _summary;
    private readonly ListView _list;
    private int _listVersion = -1;

    /// <summary>Show without taking focus (used for off-screen screenshots).</summary>
    public bool NoActivate { get; set; }

    protected override bool ShowWithoutActivation => NoActivate;

    public StatusForm(IStatusSource app)
    {
        _app = app;
        Text = "OutageLog";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(620, 460);
        MinimumSize = new Size(480, 360);

        _dot = new Panel { Size = new Size(18, 18), Margin = new Padding(0, 8, 10, 0) };
        _dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Icons.ColorFor(_app.Paused ? null : _app.Last?.Status));
            e.Graphics.FillEllipse(brush, 1, 1, 15, 15);
        };
        _status = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Text = "Starting…" };
        _detail = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8) };
        _summary = new Label { AutoSize = true, Margin = new Padding(3, 4, 3, 8) };

        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        head.Controls.Add(_dot);
        head.Controls.Add(_status);

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Dock = DockStyle.Fill,
        };
        _list.Columns.Add("Started", 150);
        _list.Columns.Add("Length", 110);
        _list.Columns.Add("What the checks saw", 320);

        var report = new Button { Text = "Make a report…", AutoSize = true };
        var reportMenu = new ContextMenuStrip();
        reportMenu.Items.Add("Last 24 hours", null, (_, _) => _app.OpenReport(TimeSpan.FromDays(1)));
        reportMenu.Items.Add("Last 7 days", null, (_, _) => _app.OpenReport(TimeSpan.FromDays(7)));
        reportMenu.Items.Add("Last 30 days", null, (_, _) => _app.OpenReport(TimeSpan.FromDays(30)));
        reportMenu.Items.Add("Everything logged", null, (_, _) => _app.OpenReport(TimeSpan.FromDays(3650)));
        report.Click += (_, _) => reportMenu.Show(report, new Point(0, report.Height));
        var selfTest = new Button { Text = "Run self-test", AutoSize = true };
        selfTest.Click += (_, _) => _app.RunSelfTest();
        var help = new LinkLabel { Text = "Website and help", AutoSize = true, Margin = new Padding(12, 9, 3, 3) };
        help.LinkClicked += (_, _) => TrayApp.OpenUrl(TrayApp.HomePage);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(report);
        buttons.Controls.Add(selfTest);
        buttons.Controls.Add(help);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(head, 0, 0);
        layout.Controls.Add(_detail, 0, 1);
        layout.Controls.Add(_summary, 0, 2);
        layout.Controls.Add(new Label { Text = "Outages in the last 7 days", AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(3, 4, 3, 4) }, 0, 3);
        layout.Controls.Add(_list, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);
    }

    public void UpdateStatus()
    {
        if (IsDisposed || !Visible) return;
        var last = _app.Last;
        _dot.Invalidate();

        if (_app.Paused)
        {
            _status.Text = "Paused";
            _detail.Text = "Monitoring is paused. Nothing is being logged.";
        }
        else if (last == null)
        {
            _status.Text = "Starting…";
            _detail.Text = "Running the first checks.";
        }
        else
        {
            _status.Text = StatusInfo.Label(last.Status) + (last.Simulated ? " (self-test)" : "");
            string gateway = _app.GatewayIp == null ? "no router found"
                : last.GatewayMs.HasValue ? $"router {last.GatewayMs} ms" : "router doesn't answer pings";
            string targets = string.Join(" · ", _app.Targets.Select((t, i) =>
                t + " " + (i < last.TargetMs.Length && last.TargetMs[i].HasValue ? last.TargetMs[i] + " ms" : "no answer")));
            string dns = last.DnsOk switch { true => $"DNS {last.DnsMs} ms", false => "DNS failed", _ => "DNS not checked" };
            _detail.Text = $"{gateway} · {targets} · {dns}";
        }

        var since = DateTime.UtcNow.AddDays(-1);
        TimeSpan monitored, down;
        int count;
        lock (_app.Tracker)
        {
            var tr = _app.Tracker;
            monitored = TimeSpan.FromTicks(tr.Spans.Sum(s => ReportData.Overlap(s.Start, s.End, since, DateTime.UtcNow).Ticks));
            var outages = tr.Outages.Where(o => !o.Simulated && o.End > since).ToList();
            if (tr.Current is { Simulated: false } cur) outages.Add(cur);
            count = outages.Count;
            down = TimeSpan.FromTicks(outages.Sum(o => ReportData.Overlap(o.Start, o.End, since, DateTime.UtcNow).Ticks));
        }
        double? availability = monitored > TimeSpan.Zero ? 1 - down.TotalSeconds / monitored.TotalSeconds : null;
        _summary.Text = $"Last 24 hours: {ReportBuilder.Percent(availability)} online · {count} outage{(count == 1 ? "" : "s")} · " +
                        $"{ReportBuilder.Dur(down)} offline · {ReportBuilder.Dur(monitored)} monitored";

        RefreshList();
    }

    private void RefreshList()
    {
        lock (_app.Tracker)
        {
            var tr = _app.Tracker;
            int version = tr.Outages.Count * 2 + (tr.Current != null ? 1 : 0);
            if (version == _listVersion && tr.Current == null) return;
            _listVersion = version;

            var since = DateTime.UtcNow.AddDays(-7);
            var rows = tr.Outages.Where(o => o.End > since).ToList();
            if (tr.Current != null) rows.Add(tr.Current);

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var o in rows.OrderByDescending(o => o.Start))
            {
                bool open = ReferenceEquals(o, tr.Current);
                string length = open ? "ongoing, " + ReportBuilder.Dur(DateTime.UtcNow - o.Start)
                    : (o.EndUncertain ? "at least " : "") + ReportBuilder.Dur(o.Duration);
                var item = new ListViewItem(o.Start.ToLocalTime().ToString("ddd d MMM HH:mm:ss"));
                item.SubItems.Add(length);
                item.SubItems.Add(StatusInfo.Label(o.Cause) + (o.Simulated ? " (self-test)" : ""));
                if (o.Simulated) item.ForeColor = SystemColors.GrayText;
                _list.Items.Add(item);
            }
            if (rows.Count == 0) _list.Items.Add(new ListViewItem("No outages logged.") { ForeColor = SystemColors.GrayText });
            _list.EndUpdate();
        }
    }
}
