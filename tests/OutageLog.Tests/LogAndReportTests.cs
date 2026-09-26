using System;
using System.IO;
using System.Linq;
using OutageLog;
using Xunit;

namespace OutageLog.Tests;

public class SampleLogTests
{
    [Fact]
    public void FormatAndParseRoundTrip()
    {
        var s = new Sample
        {
            Time = new DateTime(2026, 9, 26, 21, 4, 5, 123, DateTimeKind.Utc),
            Status = Status.ProviderOutage,
            Link = true,
            GatewayMs = 2,
            TargetMs = new int?[] { null, 14, null },
            DnsOk = false,
            DnsMs = null,
            Simulated = true,
        };
        var back = SampleLog.Parse(SampleLog.Format(s));
        Assert.Equal(s.Time, back.Time);
        Assert.Equal(DateTimeKind.Utc, back.Time.Kind);
        Assert.Equal(s.Status, back.Status);
        Assert.True(back.Link);
        Assert.Equal(2, back.GatewayMs);
        Assert.Equal(new int?[] { null, 14, null }, back.TargetMs);
        Assert.False(back.DnsOk);
        Assert.Null(back.DnsMs);
        Assert.True(back.Simulated);
    }

    [Theory]
    [InlineData("")]
    [InlineData(SampleLog.Header)]
    [InlineData("garbage")]
    [InlineData("2026-09-26T21:04:05.123Z,NotAStatus,1,2,1,3,0,1|2|3")]
    [InlineData("2026-09-26T21:04:05.123Z,Up,1,x,1,3,0,1|2|3")]
    [InlineData("2026-09-26T21:04:05")] // a line cut off by a crash or power loss
    public void DamagedLinesAreSkipped(string line) => Assert.Null(SampleLog.Parse(line));

    [Fact]
    public void AppendThenReadBack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "outagelog-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTime.UtcNow;
            using (var log = new SampleLog(dir))
            {
                foreach (var s in OutageTrackerTests.Pattern("..PPP..", now.AddMinutes(-1))) log.Append(s);
            }
            var read = new SampleLog(dir).Read(now.AddMinutes(-5), now.AddMinutes(5));
            Assert.Equal(7, read.Count);
            Assert.Equal(3, read.Count(s => s.Status == Status.ProviderOutage));
            Assert.True(read.Zip(read.Skip(1), (a, b) => a.Time < b.Time).All(x => x));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

public class ReportTests
{
    private static readonly DateTime T0 = new(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc);

    private static ReportOptions Options(DateTime from, DateTime to) => new()
    {
        FromUtc = from,
        ToUtc = to,
        GeneratedUtc = to,
        TimeZone = TimeZoneInfo.Utc,
        Tracker = new TrackerOptions { Warmup = TimeSpan.Zero },
        Version = "test",
    };

    [Fact]
    public void ComputesTotalsAndAvailability()
    {
        // 100 rounds (200 s monitored with the last round), one 10 s provider outage, one brief drop.
        string pattern = new string('.', 20) + "PPPPP" + new string('.', 30) + "P" + new string('.', 44);
        var samples = OutageTrackerTests.Pattern(pattern, T0);
        var d = ReportData.Compute(samples, Options(T0, T0.AddHours(1)));
        Assert.Single(d.Outages);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Downtime);
        Assert.Equal(TimeSpan.FromSeconds(198), d.Monitored);
        Assert.Equal(1, d.Blips);
        Assert.Equal(TimeSpan.FromSeconds(10), d.DowntimeBySide[Side.Provider]);
        Assert.InRange(d.Availability.Value, 0.949, 0.950);
        Assert.Equal(4, d.BriefDropSeconds);
    }

    [Fact]
    public void SelfTestsAreListedButNotCounted()
    {
        var samples = OutageTrackerTests.Pattern("..........", T0)
            .Concat(OutageTrackerTests.Pattern("PPPP", T0.AddSeconds(20), simulated: true))
            .Concat(OutageTrackerTests.Pattern("..........", T0.AddSeconds(28)))
            .ToList();
        var d = ReportData.Compute(samples, Options(T0, T0.AddHours(1)));
        Assert.Empty(d.Outages);
        Assert.Single(d.SelfTests);
        Assert.Equal(TimeSpan.Zero, d.Downtime);
        string html = ReportBuilder.Render(d, Options(T0, T0.AddHours(1)));
        Assert.Contains("Self-tests", html);
        Assert.Contains("No outages", html);
    }

    [Fact]
    public void OutageAcrossMidnightCountsOnBothDays()
    {
        var start = new DateTime(2026, 9, 26, 23, 59, 50, DateTimeKind.Utc);
        var samples = OutageTrackerTests.Pattern("..PPPPPPPPPP....", start);
        var d = ReportData.Compute(samples, Options(start.AddMinutes(-1), start.AddMinutes(2)));
        Assert.Equal(2, d.Days.Count);
        Assert.Equal(TimeSpan.FromSeconds(6), d.Days[0].Downtime);  // 23:59:54 - 00:00:00
        Assert.Equal(TimeSpan.FromSeconds(14), d.Days[1].Downtime); // 00:00:00 - 00:00:14
        Assert.Equal(1, d.Days[0].Outages);
        Assert.Equal(0, d.Days[1].Outages);
    }

    [Fact]
    public void EmptyPeriodSaysSo()
    {
        string html = ReportBuilder.Build(Array.Empty<Sample>(), Options(T0, T0.AddDays(1)));
        Assert.Contains("didn't record any checks", html);
    }

    [Fact]
    public void DemoWeekRendersWithAllSections()
    {
        var samples = DemoData.Generate(new DateTime(2026, 9, 26), 5, TimeZoneInfo.Utc, TimeSpan.FromSeconds(2));
        var o = Options(samples.First().Time, samples.Last().Time);
        o.Tracker = new TrackerOptions();
        var d = ReportData.Compute(samples, o);
        Assert.InRange(d.Outages.Count, 8, 12);
        Assert.Contains(d.Outages, x => x.Cause == Status.DnsFailure);
        Assert.Contains(d.Outages, x => x.Side == Side.Yours);
        Assert.Equal(5, d.Days.Count);
        string html = ReportBuilder.Render(d, o);
        foreach (string section in new[] { "Where the outages were", "Day by day", "Every outage", "Daily summary", "How this was measured" })
            Assert.Contains(section, html);
        Assert.DoesNotContain("<script", html);
    }

    [Theory]
    [InlineData(1.0, "100.00%")]
    [InlineData(0.99999, "99.99%")]
    [InlineData(0.98765, "98.76%")]
    public void PercentNeverRoundsUpTo100(double value, string expected) => Assert.Equal(expected, ReportBuilder.Percent(value));

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(45, "45 s")]
    [InlineData(432, "7 min 12 s")]
    [InlineData(7500, "2 h 05 min")]
    [InlineData(273600, "3 d 4 h")]
    public void DurationsReadNaturally(int seconds, string expected) => Assert.Equal(expected, ReportBuilder.Dur(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TextIsHtmlEscaped()
    {
        var o = Options(T0, T0.AddHours(1));
        o.Version = "<b>1</b>";
        string html = ReportBuilder.Build(OutageTrackerTests.Pattern("....", T0), o);
        Assert.Contains("&lt;b&gt;1&lt;/b&gt;", html);
    }
}
