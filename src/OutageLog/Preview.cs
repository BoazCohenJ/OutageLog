using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OutageLog;

/// <summary>
/// Renders the status window with made-up data to a PNG, off-screen and without activating it, so README
/// screenshots can be made without disturbing whoever is using the PC. Used by <c>--screenshot-status</c>.
/// </summary>
internal static class Preview
{
    private sealed class DemoSource : IStatusSource
    {
        public Sample Last { get; set; }
        public bool Paused => false;
        public string GatewayIp => "192.168.1.1";
        public string[] Targets => Prober.DefaultTargets;
        public OutageTracker Tracker { get; } = new();
        public void OpenReport(TimeSpan window) { }
        public void RunSelfTest() { }
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private const uint RenderFullContent = 2;

    public static int ScreenshotStatus(string path)
    {
        Application.EnableVisualStyles();
        var source = new DemoSource
        {
            Last = new Sample
            {
                Time = DateTime.UtcNow, Status = Status.Up, Link = true, GatewayMs = 2,
                TargetMs = new int?[] { 9, 11, 12 }, DnsOk = true, DnsMs = 14,
            },
        };
        var now = DateTime.UtcNow;
        foreach (var s in DemoData.Generate(DateTime.Today, 7, TimeZoneInfo.Local, TimeSpan.FromSeconds(2)).Where(s => s.Time <= now))
            source.Tracker.Add(s);

        using var form = new StatusForm(source)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-5000, -5000),
            ShowInTaskbar = false,
            NoActivate = true,
            Icon = Icons.AppIcon(new Size(32, 32)),
        };
        form.Show();
        form.UpdateStatus();
        Application.DoEvents();

        using var bmp = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            PrintWindow(form.Handle, hdc, RenderFullContent);
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine("Wrote " + path);
        return 0;
    }
}
