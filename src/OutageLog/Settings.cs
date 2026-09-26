using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace OutageLog;

/// <summary>Settings kept in %LOCALAPPDATA%\OutageLog\settings.ini, a plain key=value file you can edit by hand.</summary>
internal sealed class Settings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OutageLog";

    public static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OutageLog");

    public static string DefaultDataDir => Path.Combine(AppDir, "data");
    public static string ReportDir => Path.Combine(AppDir, "reports");
    private static string FilePath => Path.Combine(AppDir, "settings.ini");

    public int IntervalSeconds = 2;
    public int SlowMs = 300;
    public int KeepDays = 60;
    public bool Notify = true;
    public bool FirstRun = true;
    public string[] Targets = Prober.DefaultTargets;

    public TrackerOptions TrackerOptions => new() { Interval = TimeSpan.FromSeconds(IntervalSeconds) };

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            s.FirstRun = false;
            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "intervalseconds":
                        s.IntervalSeconds = ParseInt(value, s.IntervalSeconds, 1, 60);
                        break;
                    case "slowms":
                        s.SlowMs = ParseInt(value, s.SlowMs, 20, 10000);
                        break;
                    case "keepdays":
                        s.KeepDays = ParseInt(value, s.KeepDays, 0, 3650);
                        break;
                    case "notify":
                        s.Notify = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "targets":
                        var targets = value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Where(t => System.Net.IPAddress.TryParse(t, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            .ToArray();
                        if (targets.Length > 0) s.Targets = targets;
                        break;
                }
            }
        }
        catch (IOException)
        {
            // Fall back to defaults.
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllLines(FilePath, new List<string>
            {
                "# OutageLog settings. Edit while the app is closed; the tray menu changes the rest.",
                "# Seconds between rounds of checks.",
                "intervalSeconds=" + IntervalSeconds.ToString(CultureInfo.InvariantCulture),
                "# The fastest internet server answering slower than this (ms) counts as slow.",
                "slowMs=" + SlowMs.ToString(CultureInfo.InvariantCulture),
                "# Delete daily logs older than this many days (0 keeps everything).",
                "keepDays=" + KeepDays.ToString(CultureInfo.InvariantCulture),
                "notify=" + (Notify ? "true" : "false"),
                "# Internet servers to check (IPv4). Use servers run by different companies.",
                "targets=" + string.Join(" ", Targets),
            });
            FirstRun = false;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --tray");
            else key.DeleteValue(RunValue, false);
        }
    }

    private static int ParseInt(string value, int fallback, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= min && v <= max ? v : fallback;
}
