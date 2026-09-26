using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace OutageLog;

internal sealed class ReportOptions
{
    public DateTime FromUtc;
    public DateTime ToUtc;
    public DateTime GeneratedUtc = DateTime.UtcNow;
    public TimeZoneInfo TimeZone = TimeZoneInfo.Local;
    public TrackerOptions Tracker = new();
    public string[] Targets = Prober.DefaultTargets;
    public string Version = "";
    public string HomePage = "";
}

/// <summary>Per-day numbers for the report.</summary>
internal sealed class DayStats
{
    public DateTime Day; // local date
    public TimeSpan Monitored;
    public int Outages;
    public TimeSpan Downtime;
    public int Blips;
    public int? MedianMs;
    public int? P95Ms;
}

/// <summary>Everything the report shows, computed from logged samples. Kept separate from the HTML so it can be tested.</summary>
internal sealed class ReportData
{
    public DateTime FromUtc, ToUtc;
    public List<Outage> Outages = new();
    public List<Outage> SelfTests = new();
    public List<MonitoredSpan> Spans = new();
    public List<DateTime> BlipTimes = new();
    public List<DayStats> Days = new();
    public TimeSpan Monitored;
    public TimeSpan Downtime;
    /// <summary>Drops shorter than this many seconds are brief drops, not outages.</summary>
    public int BriefDropSeconds;
    public TimeSpan Degraded;
    public int? MedianMs;
    public Dictionary<Side, TimeSpan> DowntimeBySide = new();
    public Dictionary<Side, int> CountBySide = new();

    public int Blips => BlipTimes.Count;
    public Outage Longest => Outages.OrderByDescending(o => o.Duration).FirstOrDefault();

    /// <summary>Share of monitored time the connection worked, 0-1, or null with nothing monitored.</summary>
    public double? Availability => Monitored > TimeSpan.Zero
        ? Math.Max(0, 1 - Downtime.TotalSeconds / Monitored.TotalSeconds)
        : null;

    /// <summary>Number of logged checks the report was made from.</summary>
    public int SampleCount;

    /// <summary>Computes the report in one pass, so months of logs never have to sit in memory at once.</summary>
    public static ReportData Compute(IEnumerable<Sample> samples, ReportOptions o)
    {
        var tracker = new OutageTracker(o.Tracker);
        var latencyByDay = new Dictionary<DateTime, List<int>>();
        var allLatency = new List<int>();
        DateTime? first = null, last = null;
        int count = 0;
        foreach (var s in samples)
        {
            tracker.Add(s);
            count++;
            first ??= s.Time;
            last = s.Time;
            if (!IsLatencySample(s)) continue;
            int ms = s.BestMs.Value;
            allLatency.Add(ms);
            DateTime day = ToLocal(s.Time, o.TimeZone).Date;
            if (!latencyByDay.TryGetValue(day, out var list)) latencyByDay[day] = list = new List<int>();
            list.Add(ms);
        }
        tracker.Finish();

        var d = new ReportData
        {
            FromUtc = first.HasValue && first.Value > o.FromUtc ? first.Value : o.FromUtc,
            ToUtc = last.HasValue && last.Value < o.ToUtc ? last.Value : o.ToUtc,
            Spans = tracker.Spans,
            BlipTimes = tracker.BlipTimes,
            Degraded = tracker.DegradedTime,
            Monitored = tracker.MonitoredTime,
            BriefDropSeconds = (int)Math.Round(o.Tracker.MinDownSamples * o.Tracker.Interval.TotalSeconds),
            SampleCount = count,
            MedianMs = Percentile(allLatency, 0.5),
        };
        d.Outages = tracker.Outages.Where(x => !x.Simulated).ToList();
        d.SelfTests = tracker.Outages.Where(x => x.Simulated).ToList();
        d.Downtime = Sum(d.Outages.Select(x => x.Duration));
        foreach (Side side in Enum.GetValues(typeof(Side)))
        {
            var ofSide = d.Outages.Where(x => x.Side == side).ToList();
            d.DowntimeBySide[side] = Sum(ofSide.Select(x => x.Duration));
            d.CountBySide[side] = ofSide.Count;
        }

        if (count > 0)
        {
            DateTime firstDay = ToLocal(d.FromUtc, o.TimeZone).Date;
            DateTime lastDay = ToLocal(d.ToUtc, o.TimeZone).Date;
            for (DateTime day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                var (start, end) = DayBoundsUtc(day, o.TimeZone);
                if (!latencyByDay.TryGetValue(day, out var latency)) latency = new List<int>();
                d.Days.Add(new DayStats
                {
                    Day = day,
                    Monitored = Sum(d.Spans.Select(sp => Overlap(sp.Start, sp.End, start, end))),
                    Outages = d.Outages.Count(x => x.Start >= start && x.Start < end),
                    Downtime = Sum(d.Outages.Select(x => Overlap(x.Start, x.End, start, end))),
                    Blips = d.BlipTimes.Count(t => t >= start && t < end),
                    MedianMs = Percentile(latency, 0.5),
                    P95Ms = Percentile(latency, 0.95),
                });
            }
        }
        return d;
    }

    private static bool IsLatencySample(Sample s) =>
        !s.Simulated && s.Status is Status.Up or Status.Degraded && s.BestMs.HasValue;

    public static DateTime ToLocal(DateTime utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);

    public static (DateTime start, DateTime end) DayBoundsUtc(DateTime localDay, TimeZoneInfo tz) =>
        (FromLocal(localDay, tz), FromLocal(localDay.AddDays(1), tz));

    private static DateTime FromLocal(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // Midnight can fall in a DST gap in a few zones; step forward until it's a real time.
        while (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }

    public static TimeSpan Overlap(DateTime a0, DateTime a1, DateTime b0, DateTime b1)
    {
        DateTime s = a0 > b0 ? a0 : b0, e = a1 < b1 ? a1 : b1;
        return e > s ? e - s : TimeSpan.Zero;
    }

    private static TimeSpan Sum(IEnumerable<TimeSpan> spans) => TimeSpan.FromTicks(spans.Sum(t => t.Ticks));

    public static int? Percentile(List<int> values, double p)
    {
        if (values.Count == 0) return null;
        values.Sort();
        int index = (int)Math.Ceiling(p * values.Count) - 1;
        return values[Math.Max(0, Math.Min(values.Count - 1, index))];
    }
}

/// <summary>Renders <see cref="ReportData"/> as one self-contained HTML file: no scripts, no external files, prints cleanly.</summary>
internal static class ReportBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Side[] SideOrder = { Side.Provider, Side.Yours, Side.Dns, Side.Undetermined };

    public static string Build(IEnumerable<Sample> samples, ReportOptions o) => Render(ReportData.Compute(samples, o), o);

    public static string Render(ReportData d, ReportOptions o)
    {
        var tz = o.TimeZone;
        var h = new StringBuilder(64 * 1024);
        string period = d.Spans.Count == 0
            ? "No checks were logged in this period."
            : $"{Date(ToLocal(d.FromUtc, tz))} {Time(ToLocal(d.FromUtc, tz))} to {Date(ToLocal(d.ToUtc, tz))} {Time(ToLocal(d.ToUtc, tz))}";

        h.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">");
        h.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        h.Append("<title>Internet outage report · ").Append(Esc(Date(ToLocal(d.ToUtc, tz)))).Append("</title>");
        h.Append("<style>").Append(Css).Append("</style></head><body><main class=\"viz-root\">");

        h.Append("<header><p class=\"eyebrow\">Internet connection report</p>");
        h.Append("<h1>").Append(Esc(Headline(d))).Append("</h1>");
        h.Append("<p class=\"sub\">").Append(Esc(period)).Append(" · times in ").Append(Esc(ZoneName(tz, d.ToUtc))).Append("</p></header>");

        if (d.Spans.Count == 0)
        {
            h.Append("<p class=\"empty\">OutageLog didn't record any checks between these dates. Leave it running (it lives in the tray) and make the report again later.</p>");
            AppendFooter(h, o);
            return h.Append("</main></body></html>").ToString();
        }

        AppendSummary(h, d);
        AppendTimelines(h, d, tz);
        AppendOutageTable(h, d, tz);
        AppendDays(h, d);
        if (d.SelfTests.Count > 0) AppendSelfTests(h, d, tz);
        AppendMethod(h, d, o);
        AppendFooter(h, o);
        return h.Append("</main></body></html>").ToString();
    }

    private static string Headline(ReportData d)
    {
        if (d.Spans.Count == 0) return "No data for this period";
        int n = d.Outages.Count;
        if (n == 0) return "No outages in " + Dur(d.Monitored) + " of monitoring";
        return $"{n} outage{(n == 1 ? "" : "s")}, {Dur(d.Downtime)} offline in {Dur(d.Monitored)} of monitoring";
    }

    private static void AppendSummary(StringBuilder h, ReportData d)
    {
        h.Append("<section class=\"summary\"><div class=\"hero\"><div class=\"hero-value\">")
            .Append(Esc(Percent(d.Availability))).Append("</div><div class=\"hero-label\">of monitored time online</div></div>");
        h.Append("<div class=\"tiles\">");
        Tile(h, "Outages", d.Outages.Count.ToString(Inv));
        Tile(h, "Time offline", Dur(d.Downtime));
        Tile(h, "Longest outage", d.Longest != null ? Dur(d.Longest.Duration) : "none");
        Tile(h, "Provider side", Dur(d.DowntimeBySide[Side.Provider]));
        Tile(h, "Brief drops", d.Blips.ToString(Inv), "under " + d.BriefDropSeconds + " s, not counted as outages");
        Tile(h, "Typical ping", d.MedianMs.HasValue ? d.MedianMs.Value.ToString(Inv) + " ms" : "n/a");
        h.Append("</div></section>");

        if (d.Outages.Count == 0) return;

        // Downtime by side: a small bar chart with values at the bar ends, plus counts in the labels.
        TimeSpan max = SideOrder.Select(s => d.DowntimeBySide[s]).Max();
        h.Append("<section><h2>Where the outages were</h2><div class=\"bars\" role=\"table\" aria-label=\"Time offline by cause\">");
        foreach (var side in SideOrder)
        {
            int count = d.CountBySide[side];
            if (count == 0) continue;
            double pct = max.Ticks > 0 ? 100.0 * d.DowntimeBySide[side].Ticks / max.Ticks : 0;
            h.Append("<div class=\"bar-row\" role=\"row\"><div class=\"bar-label\" role=\"cell\"><span class=\"key ").Append(SideClass(side)).Append("\"></span>")
                .Append(Esc(StatusInfo.SideLabel(side))).Append(" <span class=\"muted\">· ").Append(count.ToString(Inv))
                .Append(count == 1 ? " outage" : " outages").Append("</span></div>")
                .Append("<div class=\"bar-track\" role=\"cell\"><div class=\"bar ").Append(SideClass(side)).Append("\" style=\"width:")
                .Append(Math.Max(0.8, pct).ToString("0.##", Inv)).Append("%\"></div><span class=\"bar-value\">")
                .Append(Esc(Dur(d.DowntimeBySide[side]))).Append("</span></div></div>");
        }
        h.Append("</div></section>");
    }

    private static void Tile(StringBuilder h, string label, string value, string note = null)
    {
        h.Append("<div class=\"tile\"><div class=\"tile-label\">").Append(Esc(label)).Append("</div><div class=\"tile-value\">")
            .Append(Esc(value)).Append("</div>");
        if (note != null) h.Append("<div class=\"tile-note\">").Append(Esc(note)).Append("</div>");
        h.Append("</div>");
    }

    private static void AppendTimelines(StringBuilder h, ReportData d, TimeZoneInfo tz)
    {
        h.Append("<section><h2>Day by day</h2>");
        h.Append("<div class=\"legend\"><span><span class=\"key online\"></span>Online</span>");
        foreach (var side in SideOrder.Where(s => d.CountBySide[s] > 0))
            h.Append("<span><span class=\"key ").Append(SideClass(side)).Append("\"></span>").Append(Esc(StatusInfo.SideLabel(side))).Append("</span>");
        h.Append("<span><span class=\"key nodata\"></span>Not monitoring (PC off, asleep or app closed)</span></div>");

        h.Append("<div class=\"timeline\"><div class=\"tl-row tl-axis\"><div class=\"tl-day\"></div><div class=\"tl-ticks\">");
        for (int hour = 0; hour <= 24; hour += 6)
            h.Append("<span").Append(hour % 12 != 0 ? " class=\"minor\"" : "").Append(" style=\"left:").Append((hour / 24.0 * 100).ToString("0.##", Inv)).Append("%\">")
                .Append(hour.ToString("00", Inv)).Append(":00</span>");
        h.Append("</div></div>");

        foreach (var day in d.Days.AsEnumerable().Reverse())
        {
            var (start, end) = ReportData.DayBoundsUtc(day.Day, tz);
            h.Append("<div class=\"tl-row\"><div class=\"tl-day\">").Append(Esc(day.Day.ToString("ddd d MMM", Inv))).Append("</div>");
            h.Append("<svg class=\"tl\" viewBox=\"0 0 1440 20\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"")
                .Append(Esc(Date(day.Day) + ": " + day.Outages + " outages, " + Dur(day.Downtime) + " offline")).Append("\">");
            h.Append("<rect class=\"tl-bg\" x=\"0\" y=\"0\" width=\"1440\" height=\"20\" rx=\"3\"/>");
            foreach (var span in d.Spans)
                Segment(h, span.Start, span.End, start, end, day.Day, tz, "online", null);
            foreach (var outage in d.Outages)
            {
                string tip = $"{Time(ToLocal(outage.Start, tz))}–{Time(ToLocal(outage.End, tz))} ({Dur(outage.Duration)}): {StatusInfo.Label(outage.Cause)}";
                Segment(h, outage.Start, outage.End, start, end, day.Day, tz, SideClass(outage.Side), tip, minWidth: 4);
            }
            h.Append("</svg><div class=\"tl-sum\">").Append(day.Outages == 0 ? "—" : Esc(day.Outages + " · " + Dur(day.Downtime))).Append("</div></div>");
        }
        h.Append("</div><p class=\"note\">Hover an outage for its times. Short outages are drawn wider than their real length so they stay visible; the table below has exact times.</p></section>");
    }

    private static void Segment(StringBuilder h, DateTime aUtc, DateTime bUtc, DateTime dayStartUtc, DateTime dayEndUtc,
        DateTime day, TimeZoneInfo tz, string cls, string tip, double minWidth = 0)
    {
        if (bUtc <= dayStartUtc || aUtc >= dayEndUtc) return;
        DateTime a = aUtc < dayStartUtc ? dayStartUtc : aUtc, b = bUtc > dayEndUtc ? dayEndUtc : bUtc;
        double x1 = Math.Max(0, (ToLocal(a, tz) - day).TotalMinutes);
        double x2 = Math.Min(1440, (ToLocal(b, tz) - day).TotalMinutes);
        if (b == dayEndUtc) x2 = 1440;
        double w = Math.Max(minWidth, x2 - x1);
        if (x1 + w > 1440) x1 = 1440 - w;
        h.Append("<rect class=\"").Append(cls).Append("\" x=\"").Append(x1.ToString("0.##", Inv)).Append("\" y=\"0\" width=\"")
            .Append(w.ToString("0.##", Inv)).Append("\" height=\"20\">");
        if (tip != null) h.Append("<title>").Append(Esc(tip)).Append("</title>");
        h.Append("</rect>");
    }

    private static void AppendOutageTable(StringBuilder h, ReportData d, TimeZoneInfo tz)
    {
        h.Append("<section><h2>Every outage</h2>");
        if (d.Outages.Count == 0)
        {
            h.Append("<p>No outages: every check succeeded or recovered within ").Append(d.BriefDropSeconds).Append(" seconds.</p></section>");
            return;
        }
        h.Append("<div class=\"table-wrap\"><table><thead><tr><th class=\"idx\">#</th><th>Started</th><th>Ended</th><th class=\"num\">Length</th><th>Verdict</th></tr></thead><tbody>");
        int i = 0;
        foreach (var x in d.Outages)
        {
            var start = ToLocal(x.Start, tz);
            var end = ToLocal(x.End, tz);
            string endText = start.Date == end.Date ? Time(end) : ShortDate(end) + " " + Time(end);
            if (x.EndUncertain) endText = "unknown (monitoring stopped at " + endText + ")";
            if (x.Ongoing) endText = "still down at " + endText;
            h.Append("<tr><td class=\"num idx\">").Append(++i).Append("</td><td class=\"when\">").Append(Esc(ShortDate(start))).Append(" <span>").Append(Esc(Time(start))).Append("</span>")
                .Append("</td><td>").Append(Esc(endText)).Append("</td><td class=\"num\">")
                .Append(Esc((x.EndUncertain || x.Ongoing ? "at least " : "") + Dur(x.Duration)))
                .Append("</td><td><span class=\"key ").Append(SideClass(x.Side)).Append("\"></span>")
                .Append(Esc(StatusInfo.Label(x.Cause))).Append("</td></tr>");
        }
        h.Append("</tbody></table></div><dl class=\"verdicts\">");
        foreach (var cause in d.Outages.Select(x => x.Cause).Distinct().OrderBy(c => (int)c))
            h.Append("<dt><span class=\"key ").Append(SideClass(StatusInfo.SideOf(cause))).Append("\"></span>").Append(Esc(StatusInfo.Label(cause)))
                .Append("</dt><dd>").Append(Esc(StatusInfo.Explain(cause))).Append("</dd>");
        h.Append("</dl>");
        h.Append("<p class=\"note\"><a download=\"outages.csv\" href=\"data:text/csv;charset=utf-8,")
            .Append(Uri.EscapeDataString(OutagesCsv(d, tz))).Append("\">Download this table as CSV</a></p></section>");
    }

    public static string OutagesCsv(ReportData d, TimeZoneInfo tz)
    {
        var sb = new StringBuilder("start,end,length_seconds,verdict,side,end_uncertain\r\n");
        foreach (var x in d.Outages)
        {
            sb.Append(ToLocal(x.Start, tz).ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append(',')
                .Append(ToLocal(x.End, tz).ToString("yyyy-MM-dd HH:mm:ss", Inv)).Append(',')
                .Append(((long)x.Duration.TotalSeconds).ToString(Inv)).Append(',')
                .Append(StatusInfo.Label(x.Cause).Replace(",", "")).Append(',')
                .Append(x.Side).Append(',')
                .Append(x.EndUncertain || x.Ongoing ? "yes" : "no").Append("\r\n");
        }
        return sb.ToString();
    }

    private static void AppendDays(StringBuilder h, ReportData d)
    {
        h.Append("<section><h2>Daily summary</h2><div class=\"table-wrap\"><table><thead><tr><th>Date</th><th class=\"num\">Monitored</th>")
            .Append("<th class=\"num\">Outages</th><th class=\"num\">Offline</th><th class=\"num\">Brief drops</th>")
            .Append("<th class=\"num\">Typical ping</th><th class=\"num\">Worst 5% ping</th></tr></thead><tbody>");
        foreach (var day in d.Days.AsEnumerable().Reverse())
        {
            h.Append("<tr><td>").Append(Esc(Date(day.Day))).Append("</td><td class=\"num\">").Append(Esc(Dur(day.Monitored)))
                .Append("</td><td class=\"num\">").Append(day.Outages).Append("</td><td class=\"num\">").Append(Esc(Dur(day.Downtime)))
                .Append("</td><td class=\"num\">").Append(day.Blips).Append("</td><td class=\"num\">").Append(Ms(day.MedianMs))
                .Append("</td><td class=\"num\">").Append(Ms(day.P95Ms)).Append("</td></tr>");
        }
        h.Append("</tbody></table></div></section>");
    }

    private static void AppendSelfTests(StringBuilder h, ReportData d, TimeZoneInfo tz)
    {
        h.Append("<section><h2>Self-tests</h2><p class=\"note\">These outages were faked by OutageLog's self-test and are left out of every number above.</p><ul>");
        foreach (var x in d.SelfTests)
            h.Append("<li>").Append(Esc(Date(ToLocal(x.Start, tz)) + " " + Time(ToLocal(x.Start, tz)) + ", " + Dur(x.Duration) + ": " + StatusInfo.Label(x.Cause)))
                .Append("</li>");
        h.Append("</ul></section>");
    }

    private static void AppendMethod(StringBuilder h, ReportData d, ReportOptions o)
    {
        int interval = (int)o.Tracker.Interval.TotalSeconds;
        h.Append("<section class=\"method\"><h2>How this was measured</h2><ul>");
        h.Append("<li>About every ").Append(interval).Append(" seconds (a little less often while servers don't answer, since each check waits up to 1.5 s) this PC checked three things at once: its router, three independent internet servers (")
            .Append(Esc(string.Join(", ", o.Targets)))
            .Append(", run by different companies), and a DNS lookup of a well-known website that bypasses Windows' DNS cache.</li>");
        h.Append("<li>Servers were pinged; if a ping went unanswered, OutageLog also tried a TCP connection on port 443, so networks that block ping don't look like outages.</li>");
        h.Append("<li>An <strong>outage</strong> is at least ").Append(o.Tracker.MinDownSamples).Append(" failed rounds in a row, and ends at the first of ")
            .Append(o.Tracker.RecoverSamples).Append(" good rounds. Shorter drops are counted as <strong>brief drops</strong>.</li>");
        h.Append("<li><strong>Provider side</strong> means the router kept answering while none of the internet servers did, so the break was past the router: the line, the modem or the provider's network. <strong>Your side</strong> means this PC lost its connection or couldn't reach the router.</li>");
        h.Append("<li>Checks aren't judged for ").Append((int)o.Tracker.Warmup.TotalSeconds)
            .Append(" seconds after monitoring starts or the PC wakes from sleep, so reconnecting isn't counted as an outage. Time when the PC was off or asleep isn't counted at all.</li>");
        h.Append("<li>This is one PC's view. If it's on Wi-Fi, a weak signal shows up as “your side”, never as “provider side”.</li>");
        h.Append("</ul></section>");
    }

    private static void AppendFooter(StringBuilder h, ReportOptions o)
    {
        h.Append("<footer>Generated ").Append(Esc(Date(ToLocal(o.GeneratedUtc, o.TimeZone)) + " " + Time(ToLocal(o.GeneratedUtc, o.TimeZone))))
            .Append(" by OutageLog ").Append(Esc(o.Version));
        if (!string.IsNullOrEmpty(o.HomePage)) h.Append(" · <a href=\"").Append(Esc(o.HomePage)).Append("\">").Append(Esc(o.HomePage)).Append("</a>");
        h.Append("</footer>");
    }

    private static string SideClass(Side side) => side switch
    {
        Side.Provider => "s-provider",
        Side.Yours => "s-yours",
        Side.Dns => "s-dns",
        _ => "s-unknown",
    };

    private static DateTime ToLocal(DateTime utc, TimeZoneInfo tz) => ReportData.ToLocal(utc, tz);
    private static string Date(DateTime local) => local.ToString("ddd d MMM yyyy", Inv);
    private static string ShortDate(DateTime local) => local.ToString("ddd d MMM", Inv);
    private static string Time(DateTime local) => local.ToString("HH:mm:ss", Inv);
    private static string Ms(int? ms) => ms.HasValue ? ms.Value.ToString(Inv) + " ms" : "—";
    private static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");

    public static string ZoneName(TimeZoneInfo tz, DateTime utc)
    {
        TimeSpan offset = tz.GetUtcOffset(utc);
        string sign = offset < TimeSpan.Zero ? "−" : "+";
        offset = offset.Duration();
        string name = tz.IsDaylightSavingTime(utc) ? tz.DaylightName : tz.StandardName;
        return $"{name} (UTC{sign}{offset.Hours:00}:{offset.Minutes:00})";
    }

    /// <summary>Availability as a percentage that never rounds a connection with outages up to 100%.</summary>
    public static string Percent(double? value)
    {
        if (!value.HasValue) return "n/a";
        double pct = Math.Floor(value.Value * 10000) / 100;
        if (value.Value < 1 && pct >= 100) pct = 99.99;
        return pct.ToString("0.00", Inv) + "%";
    }

    /// <summary>Human duration: 45 s, 7 min 12 s, 2 h 05 min, 3 d 4 h.</summary>
    public static string Dur(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        long secs = (long)Math.Round(t.TotalSeconds);
        if (secs < 60) return secs + " s";
        if (secs < 3600) return secs / 60 + " min " + (secs % 60).ToString("00", Inv) + " s";
        long mins = secs / 60;
        if (mins < 24 * 60) return mins / 60 + " h " + (mins % 60).ToString("00", Inv) + " min";
        long hours = mins / 60;
        return hours / 24 + " d " + hours % 24 + " h";
    }

    // Palette: categorical slots 1-4 of the validated default data-viz palette (light and dark steps),
    // neutral chrome, system sans. Printing always uses the light values.
    private const string Css = @"
:root{color-scheme:light;--page:#f9f9f7;--surface:#fcfcfb;--ink:#0b0b0b;--ink2:#52514e;--muted:#6f6d68;--grid:#e1e0d9;--axis:#c3c2b7;
--online:#dcdbd3;--nodata:transparent;--border:rgba(11,11,11,.10);
--provider:#2a78d6;--yours:#eb6834;--dns:#1baf7a;--unknown:#eda100}
@media screen and (prefers-color-scheme:dark){:root{color-scheme:dark;--page:#0d0d0d;--surface:#1a1a19;--ink:#fff;--ink2:#c3c2b7;--muted:#9a988f;
--grid:#2c2c2a;--axis:#383835;--online:#3a3a37;--border:rgba(255,255,255,.10);--provider:#3987e5;--yours:#d95926;--dns:#199e70;--unknown:#c98500}}
*{box-sizing:border-box}
body{margin:0;background:var(--page);color:var(--ink);font:15px/1.5 system-ui,-apple-system,'Segoe UI',sans-serif}
main{max-width:980px;margin:0 auto;padding:32px 16px 48px}
header{margin-bottom:24px}
.eyebrow{margin:0;color:var(--ink2);font-size:13px;letter-spacing:.04em;text-transform:uppercase}
h1{margin:4px 0 6px;font-size:26px;line-height:1.25;font-weight:650}
h2{font-size:17px;margin:0 0 12px;font-weight:650}
.sub,.note,.muted{color:var(--ink2)}
.note{font-size:13px;margin:10px 0 0}
section{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:20px;margin:0 0 16px}
.summary{display:grid;grid-template-columns:minmax(180px,240px) 1fr;gap:20px;align-items:center}
.hero-value{font-size:52px;font-weight:650;line-height:1.05}
.hero-label{color:var(--ink2)}
.tiles{display:grid;grid-template-columns:repeat(3,1fr);gap:12px}
.tile{border-left:1px solid var(--grid);padding:2px 0 2px 12px}
.tile-label{color:var(--ink2);font-size:13px}
.tile-value{font-size:20px;font-weight:600}
.tile-note{color:var(--muted);font-size:12px}
.bars{display:grid;gap:10px}
.bar-row{display:grid;grid-template-columns:minmax(200px,300px) 1fr;gap:12px;align-items:center}
.bar-track{display:flex;align-items:center;gap:8px}
.bar{height:20px;border-radius:0 4px 4px 0;min-width:3px}
.bar-value{font-variant-numeric:tabular-nums;white-space:nowrap}
.key{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px;vertical-align:-1px}
.s-provider{background:var(--provider);fill:var(--provider)}
.s-yours{background:var(--yours);fill:var(--yours)}
.s-dns{background:var(--dns);fill:var(--dns)}
.s-unknown{background:var(--unknown);fill:var(--unknown)}
.online{background:var(--online);fill:var(--online)}
.nodata{background:var(--surface);box-shadow:inset 0 0 0 1px var(--axis)}
.legend{display:flex;flex-wrap:wrap;gap:6px 18px;font-size:13px;color:var(--ink2);margin-bottom:12px}
.timeline{display:grid;gap:6px}
.tl-row{display:grid;grid-template-columns:92px 1fr 120px;gap:10px;align-items:center}
.tl-day{font-size:13px;color:var(--ink2);white-space:nowrap}
.tl-sum{font-size:13px;color:var(--ink2);font-variant-numeric:tabular-nums;white-space:nowrap}
.tl{width:100%;height:20px;display:block}
.tl-bg{fill:var(--surface);stroke:var(--axis);stroke-width:1;vector-effect:non-scaling-stroke}
.tl-ticks{position:relative;height:16px;font-size:11px;color:var(--muted)}
.tl-ticks span{position:absolute;transform:translateX(-50%)}
.tl-ticks span:first-child{transform:none}.tl-ticks span:last-child{transform:translateX(-100%)}
.table-wrap{overflow-x:auto}
table{border-collapse:collapse;width:100%;font-size:14px}
th{text-align:left;color:var(--ink2);font-weight:600;font-size:13px;border-bottom:1px solid var(--axis);padding:6px 10px 6px 0}
td{border-bottom:1px solid var(--grid);padding:8px 10px 8px 0;vertical-align:top}
.num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
.when{white-space:nowrap}
.verdicts{margin:14px 0 0;font-size:13px;color:var(--ink2);display:grid;grid-template-columns:max-content 1fr;gap:6px 14px}
.verdicts dt{color:var(--ink);font-weight:600;white-space:nowrap}.verdicts dd{margin:0}
.method ul{margin:0;padding-left:20px;color:var(--ink2)}.method li{margin:4px 0}
.empty{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:20px}
a{color:var(--provider)}
footer{color:var(--muted);font-size:13px;margin-top:20px}
@media (max-width:640px){.summary{grid-template-columns:1fr}.tiles{grid-template-columns:repeat(2,1fr)}
.bar-row{grid-template-columns:1fr}.tl-row{grid-template-columns:64px 1fr}.tl-sum{display:none}.tl-ticks .minor{display:none}
.idx{display:none}.when span{display:block}.verdicts{grid-template-columns:1fr}.verdicts dd{margin-bottom:6px}h1{font-size:22px}.hero-value{font-size:44px}}
@media print{:root{--page:#fff;--surface:#fff}section{break-inside:avoid;border-color:#ccc}
.s-provider,.s-yours,.s-dns,.s-unknown,.online{-webkit-print-color-adjust:exact;print-color-adjust:exact}}
";
}
