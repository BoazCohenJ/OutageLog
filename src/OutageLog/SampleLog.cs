using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OutageLog;

/// <summary>
/// Stores every round of checks as one CSV line, one file per local day: samples-2026-09-26.csv.
/// Columns: time (UTC), status, link, gateway ms, DNS ok, DNS ms, simulated, internet server ms (pipe-separated).
/// </summary>
internal sealed class SampleLog : IDisposable
{
    public const string Header = "time_utc,status,link,gateway_ms,dns_ok,dns_ms,simulated,targets_ms";

    private readonly string _dir;
    private StreamWriter _writer;
    private DateTime _writerDate;

    public SampleLog(string dir) => _dir = dir;

    public string Directory => _dir;

    public void Append(Sample s)
    {
        try
        {
            DateTime day = s.Time.ToLocalTime().Date;
            if (_writer == null || day != _writerDate)
            {
                _writer?.Dispose();
                System.IO.Directory.CreateDirectory(_dir);
                string path = PathFor(day);
                bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _writerDate = day;
                if (isNew) _writer.WriteLine(Header);
            }
            _writer.WriteLine(Format(s));
        }
        catch (IOException)
        {
            // Disk full or file locked: keep monitoring, drop this line.
            _writer?.Dispose();
            _writer = null;
        }
        catch (UnauthorizedAccessException)
        {
            _writer = null;
        }
    }

    /// <summary>Reads samples between two UTC times into a list, oldest first.</summary>
    public List<Sample> Read(DateTime fromUtc, DateTime toUtc)
    {
        var result = Stream(fromUtc, toUtc).ToList();
        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return result;
    }

    /// <summary>
    /// Streams samples between two UTC times, one daily file at a time, without loading them all.
    /// Lines are already in time order because they're appended as checks run.
    /// </summary>
    public IEnumerable<Sample> Stream(DateTime fromUtc, DateTime toUtc)
    {
        DateTime first = fromUtc.ToLocalTime().Date.AddDays(-1);
        DateTime last = toUtc.ToLocalTime().Date.AddDays(1);
        for (DateTime day = first; day <= last; day = day.AddDays(1))
        {
            string path = PathFor(day);
            if (!File.Exists(path)) continue;
            StreamReader reader;
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                reader = new StreamReader(stream, Encoding.UTF8);
            }
            catch (IOException)
            {
                continue; // Skip a file that can't be opened rather than failing the whole report.
            }
            using (reader)
            {
                string line;
                while ((line = ReadLineSafe(reader)) != null)
                {
                    var s = Parse(line);
                    if (s != null && s.Time >= fromUtc && s.Time <= toUtc) yield return s;
                }
            }
        }
    }

    private static string ReadLineSafe(StreamReader reader)
    {
        try
        {
            return reader.ReadLine();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Deletes daily files older than the given number of days.</summary>
    public void Prune(int keepDays)
    {
        if (keepDays <= 0 || !System.IO.Directory.Exists(_dir)) return;
        DateTime cutoff = DateTime.Now.Date.AddDays(-keepDays);
        foreach (string path in System.IO.Directory.GetFiles(_dir, "samples-*.csv"))
        {
            string stamp = Path.GetFileNameWithoutExtension(path).Substring("samples-".Length);
            if (DateTime.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // In use; try again next time.
                }
            }
        }
    }

    private string PathFor(DateTime localDay) =>
        Path.Combine(_dir, "samples-" + localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv");

    public static string Format(Sample s)
    {
        var sb = new StringBuilder(80);
        sb.Append(s.Time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(s.Status).Append(',');
        sb.Append(s.Link ? '1' : '0').Append(',');
        sb.Append(Num(s.GatewayMs)).Append(',');
        sb.Append(s.DnsOk switch { true => "1", false => "0", _ => "" }).Append(',');
        sb.Append(Num(s.DnsMs)).Append(',');
        sb.Append(s.Simulated ? '1' : '0').Append(',');
        sb.Append(string.Join("|", s.TargetMs.Select(Num)));
        return sb.ToString();
    }

    /// <summary>Parses one CSV line; returns null for the header, comments and damaged lines.</summary>
    public static Sample Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("time_utc") || line.StartsWith("#")) return null;
        string[] f = line.Split(',');
        if (f.Length < 8) return null;
        if (!DateTime.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
            return null;
        if (!Enum.TryParse(f[1], out Status status)) return null;
        try
        {
            return new Sample
            {
                Time = DateTime.SpecifyKind(time, DateTimeKind.Utc),
                Status = status,
                Link = f[2] == "1",
                GatewayMs = ParseNum(f[3]),
                DnsOk = f[4] == "1" ? true : f[4] == "0" ? false : null,
                DnsMs = ParseNum(f[5]),
                Simulated = f[6] == "1",
                TargetMs = f[7].Length == 0 ? Array.Empty<int?>() : f[7].Split('|').Select(ParseNum).ToArray(),
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Num(int? v) => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "";

    private static int? ParseNum(string s) =>
        s.Length == 0 ? null
        : int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v
        : throw new FormatException(s);

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
