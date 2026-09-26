using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OutageLog;

/// <summary>Runs one round of checks: the router, three independent internet servers, and a DNS lookup.</summary>
internal sealed class Prober
{
    /// <summary>Anycast servers run by three different companies (Cloudflare, Google, Quad9), so one company's trouble can't fake an outage.</summary>
    public static readonly string[] DefaultTargets = { "1.1.1.1", "8.8.8.8", "9.9.9.9" };

    /// <summary>Documentation-only addresses (RFC 5737) that are never routed; the self-test uses them to fake an outage.</summary>
    private static readonly string[] BlackholeTargets = { "192.0.2.1", "198.51.100.1", "203.0.113.1" };

    private static readonly string[] DnsNames =
    {
        "www.microsoft.com", "www.google.com", "www.cloudflare.com", "www.wikipedia.org", "www.amazon.com",
    };

    private static readonly TimeSpan GatewayRefresh = TimeSpan.FromSeconds(15);

    private readonly string[] _targets;
    private readonly int _timeoutMs;
    private int _dnsIndex;
    private Task<(bool ok, int ms)> _dnsInFlight;
    private string _gateway;
    private bool _link;
    private DateTime _gatewayCheckedAt = DateTime.MinValue;

    public Prober(string[] targets = null, int timeoutMs = 1500)
    {
        _targets = targets is { Length: > 0 } ? targets : DefaultTargets;
        _timeoutMs = timeoutMs;
        NetworkChange.NetworkAddressChanged += (_, _) => _gatewayCheckedAt = DateTime.MinValue;
    }

    public string[] Targets => _targets;
    public string GatewayIp => _gateway;

    /// <summary>While set in the future, internet checks go to unroutable addresses to reproduce an outage.</summary>
    public DateTime SimulateUntilUtc { get; set; }

    public async Task<ProbeResult> RunAsync()
    {
        var now = DateTime.UtcNow;
        bool simulate = now < SimulateUntilUtc;
        if (now - _gatewayCheckedAt > GatewayRefresh) RefreshGateway();

        string[] targets = simulate ? BlackholeTargets : _targets;
        Task<int?> gatewayTask = _gateway != null ? PingAsync(_gateway, tcpFallback: false) : Task.FromResult<int?>(null);
        Task<int?>[] targetTasks = targets.Select(t => PingAsync(t, tcpFallback: true)).ToArray();
        Task<(bool ok, int ms)?> dnsTask = simulate ? Task.FromResult<(bool, int)?>(null) : CheckDnsAsync();

        await Task.WhenAll(targetTasks.Cast<Task>().Append(gatewayTask).Append(dnsTask)).ConfigureAwait(false);

        var dns = dnsTask.Result;
        var result = new ProbeResult
        {
            Time = now,
            GatewayIp = _gateway,
            GatewayMs = gatewayTask.Result,
            TargetMs = targetTasks.Select(t => t.Result).ToArray(),
            DnsOk = dns?.ok,
            DnsMs = dns is { ok: true } ? dns.Value.ms : null,
            Simulated = simulate,
        };
        result.Link = _link || result.TargetMs.Any(ms => ms.HasValue);
        return result;
    }

    private async Task<int?> PingAsync(string ip, bool tcpFallback)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, _timeoutMs).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success) return (int)Math.Max(1, reply.RoundtripTime);
        }
        catch (PingException)
        {
            // Treated as no answer.
        }
        catch (InvalidOperationException)
        {
        }
        // Some networks block ping; a TCP handshake on port 443 proves the server is reachable too.
        return tcpFallback ? await TcpConnectAsync(ip, 443).ConfigureAwait(false) : null;
    }

    private async Task<int?> TcpConnectAsync(string ip, int port)
    {
        var sw = Stopwatch.StartNew();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            Task connect = client.ConnectAsync(IPAddress.Parse(ip), port);
            Task done = await Task.WhenAny(connect, Task.Delay(_timeoutMs)).ConfigureAwait(false);
            if (done == connect && !connect.IsFaulted && client.Connected) return (int)Math.Max(1, sw.ElapsedMilliseconds);
            _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        return null;
    }

    /// <summary>Resolves a well-known name through the PC's DNS servers, bypassing Windows' DNS cache.</summary>
    private async Task<(bool ok, int ms)?> CheckDnsAsync()
    {
        // A lookup still running from the previous round has taken longer than a round: count it as failed.
        if (_dnsInFlight is { IsCompleted: false }) return (false, 0);

        string name = DnsNames[_dnsIndex++ % DnsNames.Length];
        _dnsInFlight = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            int rc = Native.DnsQuery(name, Native.DnsTypeA, Native.DnsQueryBypassCache | Native.DnsQueryNoHostsFile,
                IntPtr.Zero, out IntPtr records, IntPtr.Zero);
            if (records != IntPtr.Zero) Native.DnsRecordListFree(records, Native.DnsFreeRecordList);
            return (rc == 0, (int)Math.Max(1, sw.ElapsedMilliseconds));
        });
        Task done = await Task.WhenAny(_dnsInFlight, Task.Delay(Math.Max(_timeoutMs, 2500))).ConfigureAwait(false);
        return done == _dnsInFlight ? _dnsInFlight.Result : (false, 0);
    }

    /// <summary>Finds the router: the default gateway of the adapter Windows would use to reach the internet.</summary>
    private void RefreshGateway()
    {
        _gatewayCheckedAt = DateTime.UtcNow;
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => (nic: n, props: n.GetIPProperties()))
                .Select(x => (x.nic, index: IndexOf(x.props), gateway: x.props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))))
                .Where(x => x.gateway != null)
                .ToList();

            _link = candidates.Count > 0;
            if (candidates.Count == 0)
            {
                _gateway = null;
                return;
            }

            // Prefer the adapter Windows routes 1.1.1.1 through. With a full-tunnel VPN that adapter has no
            // gateway, so fall back to the first physical adapter that has one.
            uint best = 0;
            bool haveBest = Native.GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0), out best) == 0;
            var chosen = haveBest ? candidates.FirstOrDefault(c => c.index == best) : default;
            _gateway = (chosen.gateway ?? candidates[0].gateway).ToString();
        }
        catch (NetworkInformationException)
        {
            _gateway = null;
            _link = false;
        }
    }

    private static int IndexOf(IPInterfaceProperties props)
    {
        try
        {
            return props.GetIPv4Properties()?.Index ?? -1;
        }
        catch (NetworkInformationException)
        {
            return -1;
        }
    }
}
