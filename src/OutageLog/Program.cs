using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace OutageLog;

internal static class Program
{
    private const string MutexName = @"Local\OutageLog-9c41e7a3";
    private const string ShowEventName = @"Local\OutageLog-9c41e7a3-show";

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string dataDir = Option(args, "--data-dir");

        if (Has(args, "--self-test")) return WithConsole(() => SelfTest.Run(IntArg(args, "--self-test", 20), dataDir));
        if (Has(args, "--watch")) return WithConsole(() => SelfTest.Watch());
        if (Has(args, "--report")) return WithConsole(() => Report(args, dataDir, demo: false));
        if (Has(args, "--demo-report")) return WithConsole(() => Report(args, dataDir, demo: true));
        if (Has(args, "--screenshot-status")) return WithConsole(() => Preview.ScreenshotStatus(Option(args, "--screenshot-status") ?? "status.png"));
        if (Has(args, "--help") || Has(args, "-h") || Has(args, "/?")) return WithConsole(Help);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool startHidden = Has(args, "--tray");

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!firstInstance)
        {
            // Already running: bring up its window instead of starting a second copy.
            if (!startHidden) showEvent.Set();
            return 0;
        }

        var app = new TrayApp(startHidden, dataDir);
        var listener = new Thread(() =>
        {
            try
            {
                while (showEvent.WaitOne()) app.ShowFromOtherInstance();
            }
            catch (ObjectDisposedException)
            {
            }
        }) { IsBackground = true };
        listener.Start();

        Application.Run(app);
        return 0;
    }

    private static int Report(string[] args, string dataDir, bool demo)
    {
        var settings = Settings.Load();
        int days = IntArg(args, demo ? "--demo-report" : "--report", 7);
        var to = DateTime.UtcNow;
        var options = new ReportOptions
        {
            FromUtc = to.AddDays(-days),
            ToUtc = to,
            Tracker = settings.TrackerOptions,
            Targets = settings.Targets,
            Version = Version,
            HomePage = TrayApp.HomePage,
            TimeZone = Has(args, "--utc") ? TimeZoneInfo.Utc : TimeZoneInfo.Local,
        };

        IEnumerable<Sample> samples;
        if (demo)
        {
            var list = DemoData.Generate(ReportData.ToLocal(to, options.TimeZone).Date, days, options.TimeZone, options.Tracker.Interval);
            options.FromUtc = list.First().Time;
            options.ToUtc = list.Last().Time;
            samples = list;
        }
        else
        {
            samples = new SampleLog(dataDir ?? Settings.DefaultDataDir).Stream(options.FromUtc, options.ToUtc);
        }

        var data = ReportData.Compute(samples, options);
        string html = ReportBuilder.Render(data, options);
        string path = Option(args, "--out");
        if (string.IsNullOrEmpty(path))
        {
            Directory.CreateDirectory(Settings.ReportDir);
            path = Path.Combine(Settings.ReportDir, (demo ? "demo-report-" : "outage-report-") + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".html");
        }
        path = Path.GetFullPath(path);
        File.WriteAllText(path, html);
        Console.WriteLine($"Read {data.SampleCount:N0} checks. Report written to {path}");
        if (!Has(args, "--no-open")) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine($@"OutageLog {Version}: logs internet outages and shows whose side they were on.

  OutageLog.exe                 Start in the tray and show the status window
  OutageLog.exe --tray          Start in the tray only (used for starting with Windows)
  OutageLog.exe --report [days] Write an HTML report of the last N days (default 7) and open it
        --out <file>            Where to write it      --no-open   Don't open it
  OutageLog.exe --self-test [s] Check your connection, fake an outage for s seconds (default 20),
                                and show that it's detected. Uses a temporary log.
  OutageLog.exe --watch         Print every round of checks live (Ctrl+C to stop). Nothing is logged.
  OutageLog.exe --demo-report   A report made from made-up data, to see what one looks like
  --utc                         Show report times in UTC instead of local time
  --data-dir <folder>           Use a different log folder (default {Settings.DefaultDataDir})");
        return 0;
    }

    /// <summary>Runs a command-line mode, printing to the console the exe was started from.</summary>
    private static int WithConsole(Func<int> run)
    {
        Native.AttachConsole(Native.AttachParentProcess);
        try
        {
            int code = run();
            Console.Out.Flush();
            return code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static bool Has(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Option(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int IntArg(string[] args, string name, int fallback) =>
        int.TryParse(Option(args, name), out int v) && v > 0 ? v : fallback;
}
