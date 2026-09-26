using System;
using System.Collections.Generic;

namespace OutageLog;

/// <summary>A believable, repeatable week of made-up samples, for the demo report and for tests.</summary>
internal static class DemoData
{
    private sealed class Event
    {
        public double Day, StartHour, Minutes;
        public Status Status;
    }

    public static List<Sample> Generate(DateTime endLocalDay, int days, TimeZoneInfo tz, TimeSpan interval, int seed = 42)
    {
        var rng = new Random(seed);
        var events = new List<Event>
        {
            new() { Day = 0, StartHour = 19.70, Minutes = 6.3, Status = Status.ProviderOutage },
            new() { Day = 1, StartHour = 20.25, Minutes = 3.1, Status = Status.ProviderOutage },
            new() { Day = 1, StartHour = 20.52, Minutes = 11.4, Status = Status.ProviderOutage },
            new() { Day = 1, StartHour = 16.10, Minutes = 1.5, Status = Status.DnsFailure },
            new() { Day = 2, StartHour = 13.20, Minutes = 0.9, Status = Status.NoConnection },
            new() { Day = 3, StartHour = 2.17, Minutes = 24.6, Status = Status.ProviderOutage },
            new() { Day = 3, StartHour = 21.08, Minutes = 2.2, Status = Status.ProviderOutage },
            new() { Day = 4, StartHour = 18.84, Minutes = 0.8, Status = Status.ProviderOutage },
            new() { Day = 4, StartHour = 20.60, Minutes = 8.7, Status = Status.ProviderOutage },
        };

        var samples = new List<Sample>();
        DateTime firstDay = endLocalDay.Date.AddDays(-(days - 1));
        for (int d = 0; d < days; d++)
        {
            DateTime day = firstDay.AddDays(d);
            // The PC is on 07:30-23:40, except one night it was left on.
            bool leftOn = d == 2;
            double on = d == 3 ? 0 : 7.5 + rng.NextDouble() * 0.5;
            double off = leftOn ? 24 : 23.3 + rng.NextDouble() * 0.4;

            for (double t = on * 3600; t < off * 3600; t += interval.TotalSeconds)
            {
                double hour = t / 3600;
                DateTime local = day.AddSeconds(t);
                if (tz.IsInvalidTime(local)) continue;
                DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, tz);

                Status status = Status.Up;
                foreach (var e in events)
                    if ((int)e.Day == d && hour >= e.StartHour && hour < e.StartHour + e.Minutes / 60) status = e.Status;
                if (status == Status.Up && rng.NextDouble() < 0.00008) status = Status.ProviderOutage; // brief drop

                // Evening congestion: slower and occasionally degraded.
                double evening = hour is >= 19 and < 23 ? 1 : 0;
                int best = 11 + (int)(rng.NextDouble() * 6 + evening * rng.NextDouble() * rng.NextDouble() * 180);
                if (status == Status.Up && best > 300) status = Status.Degraded;
                samples.Add(Make(utc, status, best, rng));
            }
        }
        return samples;
    }

    private static Sample Make(DateTime utc, Status status, int best, Random rng)
    {
        var s = new Sample { Time = utc, Status = status, Link = status != Status.NoConnection };
        switch (status)
        {
            case Status.Up:
            case Status.Degraded:
                s.GatewayMs = 1 + rng.Next(3);
                s.TargetMs = new int?[] { best, best + rng.Next(8), best + rng.Next(12) };
                s.DnsOk = true;
                s.DnsMs = best + 5 + rng.Next(20);
                break;
            case Status.DnsFailure:
                s.GatewayMs = 1 + rng.Next(3);
                s.TargetMs = new int?[] { best, best + 3, best + 6 };
                s.DnsOk = false;
                break;
            case Status.ProviderOutage:
                s.GatewayMs = 1 + rng.Next(3);
                s.TargetMs = new int?[] { null, null, null };
                break;
            default:
                s.TargetMs = new int?[] { null, null, null };
                break;
        }
        return s;
    }
}
