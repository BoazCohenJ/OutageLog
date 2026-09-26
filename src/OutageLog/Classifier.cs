using System;
using System.Collections.Generic;
using System.Linq;

namespace OutageLog;

/// <summary>Turns one round of checks into a <see cref="Status"/>. Pure logic apart from remembering which routers answer pings.</summary>
internal sealed class Classifier
{
    // Many routers never answer pings. Only a router we've seen answer can be blamed for not answering.
    private readonly HashSet<string> _answeringGateways = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The fastest internet server taking longer than this counts as slow.</summary>
    public int SlowMs { get; set; } = 300;

    public Status Classify(ProbeResult r)
    {
        int total = r.TargetMs.Length;
        int answered = r.TargetMs.Count(ms => ms.HasValue);

        if (r.GatewayMs.HasValue && !string.IsNullOrEmpty(r.GatewayIp)) _answeringGateways.Add(r.GatewayIp);

        if (answered == 0)
        {
            if (!r.Link) return Status.NoConnection;
            if (r.GatewayMs.HasValue) return Status.ProviderOutage;
            bool gatewayKnownToAnswer = !string.IsNullOrEmpty(r.GatewayIp) && _answeringGateways.Contains(r.GatewayIp);
            return gatewayKnownToAnswer ? Status.LocalOutage : Status.InternetDown;
        }

        if (r.DnsOk == false) return Status.DnsFailure;

        int best = r.TargetMs.Where(ms => ms.HasValue).Min(ms => ms.Value);
        bool mostFailed = total >= 3 && answered * 2 < total;
        if (best > SlowMs || mostFailed) return Status.Degraded;
        return Status.Up;
    }
}
