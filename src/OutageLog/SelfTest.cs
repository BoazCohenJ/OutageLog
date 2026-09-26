using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace OutageLog;

/// <summary>
/// Command-line checks: <c>--self-test</c> runs real checks against your connection, fakes an outage by pointing the
/// internet checks at addresses that are never routed, and confirms the outage is detected, classified and ended.
/// </summary>
internal static class SelfTest
{
    public static int Run(int outageSeconds, string dataDir)
    {
        var interval = TimeSpan.FromSeconds(2);
        var options = new TrackerOptions { Interval = interval, Warmup = TimeSpan.FromSeconds(4) };
        bool tempDir = dataDir == null;
        dataDir ??= Path.Combine(Path.GetTempPath(), "OutageLog-selftest-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        var tracker = new OutageTracker(options);
        var log = new SampleLog(dataDir);
        var prober = new Prober();
        var monitor = new ConnectionMonitor(prober, log, options, tracker);
        tracker.Started += o => Console.WriteLine($"  >> outage detected: {StatusInfo.Label(o.Cause)}");
        tracker.Ended += o => Console.WriteLine($"  >> outage over after {ReportBuilder.Dur(o.Duration)}");
        monitor.SampleTaken += Print;

        TimeSpan lead = TimeSpan.FromSeconds(10), tail = TimeSpan.FromSeconds(14);
        Console.WriteLine($"OutageLog {Program.Version} self-test. Real checks for {lead.TotalSeconds:0} s, then a faked outage for {outageSeconds} s, then {tail.TotalSeconds:0} s of real checks.");
        Console.WriteLine($"Log folder: {dataDir}");
        monitor.Start();
        Thread.Sleep(lead);
        Console.WriteLine($"  -- faking an outage: internet checks now go to unroutable test addresses for {outageSeconds} s");
        monitor.SimulateOutage(TimeSpan.FromSeconds(outageSeconds));
        Thread.Sleep(TimeSpan.FromSeconds(outageSeconds) + tail);
        monitor.Dispose();

        Outage[] simulated;
        lock (tracker)
        {
            tracker.Finish();
            simulated = tracker.Outages.Where(o => o.Simulated).ToArray();
        }

        string report = Path.Combine(dataDir, "self-test-report.html");
        var samples = new SampleLog(dataDir).Read(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(report, ReportBuilder.Build(samples, new ReportOptions
        {
            FromUtc = DateTime.UtcNow.AddHours(-1),
            ToUtc = DateTime.UtcNow,
            Tracker = options,
            Version = Program.Version,
            HomePage = TrayApp.HomePage,
        }));

        var hit = simulated.FirstOrDefault(o => !o.Ongoing && !o.EndUncertain);
        bool lengthOk = hit != null && Math.Abs(hit.Duration.TotalSeconds - outageSeconds) <= 3 * interval.TotalSeconds + 2;
        Console.WriteLine();
        if (hit != null && lengthOk)
        {
            Console.WriteLine($"PASS: the faked outage was detected as \"{StatusInfo.Label(hit.Cause)}\" ({StatusInfo.SideLabel(hit.Side)}), lasting {ReportBuilder.Dur(hit.Duration)}.");
            if (hit.Cause == Status.InternetDown)
                Console.WriteLine("      Your router doesn't answer pings, so real outages will be logged as \"Internet down\" without a side.");
        }
        else if (hit != null)
        {
            Console.WriteLine($"FAIL: an outage was detected, but it lasted {ReportBuilder.Dur(hit.Duration)} instead of about {outageSeconds} s.");
        }
        else
        {
            Console.WriteLine("FAIL: the faked outage wasn't detected.");
        }
        Console.WriteLine($"Report: {report}");
        if (tempDir) Console.WriteLine("(The log is in a temporary folder and isn't mixed with your real log.)");
        return hit != null && lengthOk ? 0 : 2;
    }

    /// <summary>Prints every round of real checks until Ctrl+C. Nothing is written to disk.</summary>
    public static int Watch()
    {
        var options = new TrackerOptions();
        var monitor = new ConnectionMonitor(new Prober(), null, options, new OutageTracker(options));
        monitor.SampleTaken += Print;
        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            done.Set();
        };
        Console.WriteLine("Checking every 2 seconds. Ctrl+C stops.");
        monitor.Start();
        done.Wait();
        monitor.Dispose();
        return 0;
    }

    private static void Print(Sample s)
    {
        string targets = string.Join("  ", s.TargetMs.Select(ms => ms.HasValue ? $"{ms,4} ms" : "   --  "));
        string gw = s.GatewayMs.HasValue ? $"{s.GatewayMs,4} ms" : "   --  ";
        string dns = s.DnsOk switch { true => $"{s.DnsMs,4} ms", false => " fail  ", _ => "   --  " };
        Console.WriteLine($"{s.Time.ToLocalTime():HH:mm:ss}  {StatusInfo.Label(s.Status),-24} router {gw}  internet {targets}  dns {dns}{(s.Simulated ? "  [fake]" : "")}");
    }
}
