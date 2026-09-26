using System;
using System.Collections.Generic;
using System.Linq;
using OutageLog;
using Xunit;

namespace OutageLog.Tests;

public class OutageTrackerTests
{
    private static readonly DateTime T0 = new(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc);

    private static TrackerOptions NoWarmup => new() { Warmup = TimeSpan.Zero };

    /// <summary>Builds samples 2 s apart from a pattern: '.' up, 'P' provider outage, 'L' local, 'N' no connection, 'D' DNS, 's' slow, '|' a 10-minute gap.</summary>
    internal static List<Sample> Pattern(string pattern, DateTime? start = null, bool simulated = false)
    {
        var list = new List<Sample>();
        DateTime t = start ?? T0;
        foreach (char c in pattern)
        {
            if (c == '|')
            {
                t = t.AddMinutes(10);
                continue;
            }
            list.Add(new Sample
            {
                Time = t,
                Simulated = simulated,
                Status = c switch
                {
                    'P' => Status.ProviderOutage,
                    'L' => Status.LocalOutage,
                    'N' => Status.NoConnection,
                    'D' => Status.DnsFailure,
                    's' => Status.Degraded,
                    _ => Status.Up,
                },
                TargetMs = new int?[] { 10, 12, 14 },
            });
            t = t.AddSeconds(2);
        }
        return list;
    }

    private static OutageTracker Run(string pattern, TrackerOptions options = null)
    {
        var tracker = new OutageTracker(options ?? NoWarmup);
        foreach (var s in Pattern(pattern)) tracker.Add(s);
        tracker.Finish();
        return tracker;
    }

    [Fact]
    public void SingleFailedRoundIsABlipNotAnOutage()
    {
        var t = Run("....P....");
        Assert.Empty(t.Outages);
        Assert.Equal(1, t.Blips);
    }

    [Fact]
    public void TwoFailedRoundsMakeAnOutageStartingAtTheFirst()
    {
        var t = Run("....PPPP....");
        var o = Assert.Single(t.Outages);
        Assert.Equal(T0.AddSeconds(8), o.Start);
        Assert.Equal(T0.AddSeconds(16), o.End); // first good round
        Assert.Equal(TimeSpan.FromSeconds(8), o.Duration);
        Assert.Equal(Status.ProviderOutage, o.Cause);
        Assert.Equal(Side.Provider, o.Side);
        Assert.Equal(0, t.Blips);
    }

    [Fact]
    public void OneGoodRoundInsideAnOutageDoesNotEndIt()
    {
        var t = Run("..PPP.PPP....");
        var o = Assert.Single(t.Outages);
        Assert.Equal(T0.AddSeconds(4), o.Start);
        Assert.Equal(T0.AddSeconds(18), o.End);
    }

    [Fact]
    public void CauseIsWhatMostRoundsSaw()
    {
        var t = Run("..NNPPPPP....");
        Assert.Equal(Status.ProviderOutage, Assert.Single(t.Outages).Cause);
    }

    [Fact]
    public void GapClosesOpenOutageAsUncertain()
    {
        var t = Run("..PPPP|......");
        var o = Assert.Single(t.Outages);
        Assert.True(o.EndUncertain);
        Assert.Equal(T0.AddSeconds(10), o.End); // last failed round seen
    }

    [Fact]
    public void GapIsNotMonitoredTime()
    {
        var t = Run("......|......");
        Assert.Equal(2, t.Spans.Count);
        Assert.Equal(TimeSpan.FromSeconds(20), t.MonitoredTime);
    }

    [Fact]
    public void WarmupAfterWakeIsNotJudged()
    {
        // After the gap, the PC reconnects for 3 rounds (6 s), well inside a 30 s warmup.
        var options = new TrackerOptions { Warmup = TimeSpan.FromSeconds(30) };
        string pattern = new string('.', 20) + "|" + "NNN" + new string('.', 20);
        var t = Run(pattern, options);
        Assert.Empty(t.Outages);
        Assert.Equal(0, t.Blips);
    }

    [Fact]
    public void OutagePastWarmupIsCounted()
    {
        var options = new TrackerOptions { Warmup = TimeSpan.FromSeconds(10) };
        var t = Run("........PPPP....", options);
        Assert.Single(t.Outages);
    }

    [Fact]
    public void OutageStillOpenAtEndIsOngoing()
    {
        var t = Run("....PPPP");
        var o = Assert.Single(t.Outages);
        Assert.True(o.Ongoing);
        Assert.False(o.EndUncertain);
    }

    [Fact]
    public void DegradedIsNotAnOutageButIsTimed()
    {
        var t = Run("..ssss..");
        Assert.Empty(t.Outages);
        Assert.Equal(TimeSpan.FromSeconds(8), t.DegradedTime);
    }

    [Fact]
    public void SimulatedOutagesAreFlaggedAndNotBlips()
    {
        var tracker = new OutageTracker(NoWarmup);
        foreach (var s in Pattern("....")) tracker.Add(s);
        foreach (var s in Pattern("PPP", T0.AddSeconds(8), simulated: true)) tracker.Add(s);
        foreach (var s in Pattern("P", T0.AddSeconds(30), simulated: true)) tracker.Add(s);
        foreach (var s in Pattern("....", T0.AddSeconds(14))) tracker.Add(s);
        tracker.Finish();
        Assert.True(Assert.Single(tracker.Outages).Simulated);
        Assert.Equal(0, tracker.Blips);
    }

    [Fact]
    public void StartedAndEndedEventsFire()
    {
        var tracker = new OutageTracker(NoWarmup);
        var events = new List<string>();
        tracker.Started += _ => events.Add("start");
        tracker.Ended += _ => events.Add("end");
        foreach (var s in Pattern("..PPP...")) tracker.Add(s);
        Assert.Equal(new[] { "start", "end" }, events);
    }

    [Fact]
    public void DuplicateTimestampsAreIgnored()
    {
        var tracker = new OutageTracker(NoWarmup);
        var samples = Pattern("..PP..");
        foreach (var s in samples.Concat(samples)) tracker.Add(s);
        tracker.Finish();
        Assert.Single(tracker.Outages);
    }
}
