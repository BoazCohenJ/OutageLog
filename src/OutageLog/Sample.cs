using System;

namespace OutageLog;

/// <summary>What one round of checks says about the connection.</summary>
internal enum Status
{
    /// <summary>Internet servers answer quickly and DNS works.</summary>
    Up,
    /// <summary>Online, but slow, or most servers didn't answer.</summary>
    Degraded,
    /// <summary>Internet servers answer by IP address, but name lookups fail, so websites don't load.</summary>
    DnsFailure,
    /// <summary>The router answers, but no internet server does: the problem is past your router.</summary>
    ProviderOutage,
    /// <summary>The router has answered before but doesn't now, and neither does the internet.</summary>
    LocalOutage,
    /// <summary>This PC has no active network connection (Wi-Fi or cable disconnected).</summary>
    NoConnection,
    /// <summary>The internet doesn't answer, and the router never answers pings, so the cause can't be narrowed down.</summary>
    InternetDown,
}

/// <summary>Which side of the router a problem is on, used to group causes in the report.</summary>
internal enum Side
{
    Provider,
    Yours,
    Undetermined,
    Dns,
}

/// <summary>Raw results of one round of checks, before classification.</summary>
internal sealed class ProbeResult
{
    public DateTime Time; // UTC
    /// <summary>True when this PC has a connected network adapter with a default gateway, or any internet check succeeded.</summary>
    public bool Link;
    public string GatewayIp;
    /// <summary>Round-trip time to the router in ms, or null if it didn't answer (or there's no router).</summary>
    public int? GatewayMs;
    /// <summary>Round-trip time to each internet server in ms, or null for no answer.</summary>
    public int?[] TargetMs = Array.Empty<int?>();
    /// <summary>Null when DNS wasn't checked.</summary>
    public bool? DnsOk;
    public int? DnsMs;
    /// <summary>Recorded while the self-test was faking an outage.</summary>
    public bool Simulated;
}

/// <summary>One logged row: the raw results plus the verdict at the time.</summary>
internal sealed class Sample
{
    public DateTime Time; // UTC
    public Status Status;
    public bool Link;
    public int? GatewayMs;
    public int?[] TargetMs = Array.Empty<int?>();
    public bool? DnsOk;
    public int? DnsMs;
    public bool Simulated;

    public static Sample From(ProbeResult r, Status status) => new()
    {
        Time = r.Time,
        Status = status,
        Link = r.Link,
        GatewayMs = r.GatewayMs,
        TargetMs = r.TargetMs,
        DnsOk = r.DnsOk,
        DnsMs = r.DnsMs,
        Simulated = r.Simulated,
    };

    /// <summary>Fastest internet server that answered, or null if none did.</summary>
    public int? BestMs
    {
        get
        {
            int? best = null;
            foreach (int? ms in TargetMs)
                if (ms.HasValue && (!best.HasValue || ms.Value < best.Value)) best = ms;
            return best;
        }
    }
}

internal static class StatusInfo
{
    public static bool IsDown(Status s) => s is not (Status.Up or Status.Degraded);

    public static Side SideOf(Status s) => s switch
    {
        Status.ProviderOutage => Side.Provider,
        Status.LocalOutage or Status.NoConnection => Side.Yours,
        Status.DnsFailure => Side.Dns,
        _ => Side.Undetermined,
    };

    /// <summary>Short label for tables and the tray.</summary>
    public static string Label(Status s) => s switch
    {
        Status.Up => "Online",
        Status.Degraded => "Slow or unstable",
        Status.DnsFailure => "DNS not answering",
        Status.ProviderOutage => "Internet down, router OK",
        Status.LocalOutage => "Router not reachable",
        Status.NoConnection => "PC not connected",
        Status.InternetDown => "Internet down",
        _ => s.ToString(),
    };

    /// <summary>One sentence explaining what was observed, for the report.</summary>
    public static string Explain(Status s) => s switch
    {
        Status.Up => "Internet servers answered and names resolved.",
        Status.Degraded => "Online, but responses were slow or most internet servers didn't answer.",
        Status.DnsFailure => "Internet servers answered by IP address, but name lookups (DNS) failed, so websites wouldn't load.",
        Status.ProviderOutage => "The router answered, but none of the independent internet servers did. The break was past the router: the line, modem or provider.",
        Status.LocalOutage => "The router stopped answering, and so did the internet. The break was between this PC and the router (Wi-Fi, cable or the router itself).",
        Status.NoConnection => "This PC had no active network connection (Wi-Fi or cable disconnected).",
        Status.InternetDown => "No internet server answered. This router doesn't answer pings, so this log can't tell whether the break was before or after it.",
        _ => "",
    };

    public static string SideLabel(Side side) => side switch
    {
        Side.Provider => "Provider side (past your router)",
        Side.Yours => "Your side (PC, Wi-Fi or router)",
        Side.Dns => "DNS only",
        _ => "Undetermined",
    };
}
