using System;
using System.Threading;
using System.Threading.Tasks;

namespace OutageLog;

/// <summary>Runs checks on a timer, logs every round, and tracks outages live.</summary>
internal sealed class ConnectionMonitor : IDisposable
{
    private readonly Prober _prober;
    private readonly Classifier _classifier = new();
    private readonly SampleLog _log;
    private readonly TrackerOptions _options;
    private CancellationTokenSource _cts;
    private Task _loop;

    public ConnectionMonitor(Prober prober, SampleLog log, TrackerOptions options, OutageTracker tracker)
    {
        _prober = prober;
        _log = log;
        _options = options;
        Tracker = tracker;
    }

    public OutageTracker Tracker { get; }
    public Prober Prober => _prober;
    public Sample Last { get; private set; }
    public bool Paused { get; set; }

    /// <summary>Raised on a thread-pool thread after every round.</summary>
    public event Action<Sample> SampleTaken;

    public int SlowMs
    {
        get => _classifier.SlowMs;
        set => _classifier.SlowMs = value;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Fakes a provider-side outage for the given time so you can see how one is detected and reported.</summary>
    public void SimulateOutage(TimeSpan length) => _prober.SimulateUntilUtc = DateTime.UtcNow + length;

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTime started = DateTime.UtcNow;
            if (!Paused)
            {
                try
                {
                    var result = await _prober.RunAsync().ConfigureAwait(false);
                    var sample = Sample.From(result, _classifier.Classify(result));
                    _log?.Append(sample);
                    lock (Tracker) Tracker.Add(sample);
                    Last = sample;
                    SampleTaken?.Invoke(sample);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad round (a driver hiccup, say) shouldn't stop monitoring.
                }
            }
            TimeSpan wait = _options.Interval - (DateTime.UtcNow - started);
            try
            {
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(3000);
        }
        catch (AggregateException)
        {
        }
        _log?.Dispose();
    }
}
