using System;
using System.Collections.Generic;
using System.Linq;

namespace OutageLog;

/// <summary>A confirmed period without a working internet connection.</summary>
internal sealed class Outage
{
    public DateTime Start; // UTC, time of the first failed check
    public DateTime End;   // UTC, time of the first successful check afterwards
    public readonly Dictionary<Status, int> Counts = new();
    /// <summary>Monitoring stopped (sleep, shutdown, app closed) before the connection came back, so the real end is unknown.</summary>
    public bool EndUncertain;
    /// <summary>Still down when the data ends.</summary>
    public bool Ongoing;
    /// <summary>Produced by the self-test, not a real outage.</summary>
    public bool Simulated;

    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    /// <summary>The verdict most checks during the outage agreed on.</summary>
    public Status Cause => Counts.Count == 0
        ? Status.InternetDown
        : Counts.OrderByDescending(kv => kv.Value).ThenBy(kv => (int)kv.Key).First().Key;

    public Side Side => StatusInfo.SideOf(Cause);
}

/// <summary>A stretch of time during which checks ran without interruption.</summary>
internal sealed class MonitoredSpan
{
    public DateTime Start, End; // UTC
    public TimeSpan Duration => End - Start;
}

internal sealed class TrackerOptions
{
    /// <summary>Time between rounds of checks.</summary>
    public TimeSpan Interval = TimeSpan.FromSeconds(2);
    /// <summary>Consecutive failed rounds before a drop counts as an outage. Shorter drops count as blips.</summary>
    public int MinDownSamples = 2;
    /// <summary>Consecutive good rounds before an outage counts as over.</summary>
    public int RecoverSamples = 2;
    /// <summary>A longer pause between rounds means monitoring stopped (sleep, shutdown, app closed).</summary>
    public TimeSpan GapThreshold = TimeSpan.FromSeconds(20);
    /// <summary>Checks right after monitoring (re)starts aren't judged, so reconnecting after wake-up isn't logged as an outage.</summary>
    public TimeSpan Warmup = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Folds a time-ordered stream of samples into outages, blips and monitored time.
/// Pure logic: the same code runs live in the tray and over saved logs for the report.
/// </summary>
internal sealed class OutageTracker
{
    private readonly TrackerOptions _o;
    private readonly List<Sample> _pending = new();
    private Sample _prev, _lastJudged;
    private DateTime _runStart;
    private MonitoredSpan _span;
    private Outage _open;
    private int _recoverCount;
    private DateTime _recoverStart;

    public OutageTracker(TrackerOptions options = null) => _o = options ?? new TrackerOptions();

    public List<Outage> Outages { get; } = new();
    public List<MonitoredSpan> Spans { get; } = new();
    /// <summary>Drops shorter than <see cref="TrackerOptions.MinDownSamples"/> rounds.</summary>
    public int Blips { get; private set; }
    public List<DateTime> BlipTimes { get; } = new();
    public TimeSpan DegradedTime { get; private set; }

    /// <summary>The outage in progress, if any.</summary>
    public Outage Current => _open;

    public event Action<Outage> Started;
    public event Action<Outage> Ended;

    public TimeSpan MonitoredTime => TimeSpan.FromTicks(Spans.Sum(s => s.Duration.Ticks));

    public void Add(Sample s)
    {
        if (_prev != null)
        {
            if (s.Time <= _prev.Time) return; // duplicate or out of order
            if (s.Time - _prev.Time > _o.GapThreshold)
            {
                CloseForGap();
                _runStart = s.Time;
            }
        }
        else
        {
            _runStart = s.Time;
        }
        _prev = s;

        if (s.Time - _runStart < _o.Warmup) return;

        if (_lastJudged != null && _span != null && _lastJudged.Time >= _runStart)
        {
            if (_lastJudged.Status == Status.Degraded) DegradedTime += s.Time - _lastJudged.Time;
            _span.End = s.Time;
        }
        else
        {
            _span = new MonitoredSpan { Start = s.Time, End = s.Time };
            Spans.Add(_span);
        }
        _lastJudged = s;

        bool down = StatusInfo.IsDown(s.Status);
        if (_open == null)
        {
            if (down)
            {
                _pending.Add(s);
                if (_pending.Count >= _o.MinDownSamples) Open();
            }
            else if (_pending.Count > 0)
            {
                if (!_pending.Any(p => p.Simulated))
                {
                    Blips++;
                    BlipTimes.Add(_pending[0].Time);
                }
                _pending.Clear();
            }
        }
        else if (down)
        {
            _recoverCount = 0;
            Count(_open, s);
        }
        else
        {
            if (_recoverCount == 0) _recoverStart = s.Time;
            if (++_recoverCount >= _o.RecoverSamples) Close(_recoverStart, uncertain: false, ongoing: false);
        }
    }

    /// <summary>Closes an outage still open at the end of the data, marking it as ongoing.</summary>
    public void Finish()
    {
        _pending.Clear();
        if (_open == null) return;
        if (_recoverCount > 0) Close(_recoverStart, uncertain: false, ongoing: false);
        else Close(_lastJudged.Time, uncertain: false, ongoing: true);
    }

    private void Open()
    {
        _open = new Outage { Start = _pending[0].Time, End = _pending[_pending.Count - 1].Time };
        foreach (var p in _pending) Count(_open, p);
        _pending.Clear();
        _recoverCount = 0;
        Started?.Invoke(_open);
    }

    private static void Count(Outage outage, Sample s)
    {
        outage.Counts.TryGetValue(s.Status, out int n);
        outage.Counts[s.Status] = n + 1;
        outage.End = s.Time;
        if (s.Simulated) outage.Simulated = true;
    }

    private void CloseForGap()
    {
        _pending.Clear();
        if (_open != null)
        {
            if (_recoverCount > 0) Close(_recoverStart, uncertain: false, ongoing: false);
            else Close(_lastJudged.Time, uncertain: true, ongoing: false);
        }
        _span = null;
    }

    private void Close(DateTime end, bool uncertain, bool ongoing)
    {
        var outage = _open;
        _open = null;
        _recoverCount = 0;
        outage.End = end;
        outage.EndUncertain = uncertain;
        outage.Ongoing = ongoing;
        Outages.Add(outage);
        Ended?.Invoke(outage);
    }
}
