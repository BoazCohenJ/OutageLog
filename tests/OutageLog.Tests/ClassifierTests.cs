using System;
using OutageLog;
using Xunit;

namespace OutageLog.Tests;

public class ClassifierTests
{
    private static ProbeResult R(int? gateway, int?[] targets, bool? dns = true, bool link = true, string gatewayIp = "192.168.1.1") => new()
    {
        Time = DateTime.UtcNow,
        Link = link,
        GatewayIp = gatewayIp,
        GatewayMs = gateway,
        TargetMs = targets,
        DnsOk = dns,
    };

    private static readonly int?[] AllUp = { 12, 15, 18 };
    private static readonly int?[] AllDown = { null, null, null };

    [Fact]
    public void AllAnsweringIsUp() => Assert.Equal(Status.Up, new Classifier().Classify(R(2, AllUp)));

    [Fact]
    public void NoLinkIsNoConnection() =>
        Assert.Equal(Status.NoConnection, new Classifier().Classify(R(null, AllDown, dns: null, link: false, gatewayIp: null)));

    [Fact]
    public void RouterAnswersButInternetDoesNotIsProviderSide() =>
        Assert.Equal(Status.ProviderOutage, new Classifier().Classify(R(2, AllDown, dns: false)));

    [Fact]
    public void RouterThatUsedToAnswerGoingSilentIsLocal()
    {
        var c = new Classifier();
        c.Classify(R(2, AllUp));
        Assert.Equal(Status.LocalOutage, c.Classify(R(null, AllDown, dns: false)));
    }

    [Fact]
    public void RouterThatNeverAnswersPingsCannotBeBlamed()
    {
        var c = new Classifier();
        c.Classify(R(null, AllUp)); // online, router ignores pings
        Assert.Equal(Status.InternetDown, c.Classify(R(null, AllDown, dns: false)));
    }

    [Fact]
    public void AnsweringRouterIsRememberedPerAddress()
    {
        var c = new Classifier();
        c.Classify(R(2, AllUp, gatewayIp: "192.168.1.1"));
        // Moved to another network whose router never answered pings.
        Assert.Equal(Status.InternetDown, c.Classify(R(null, AllDown, dns: false, gatewayIp: "10.0.0.1")));
    }

    [Fact]
    public void DnsFailureWithInternetReachableIsDns() =>
        Assert.Equal(Status.DnsFailure, new Classifier().Classify(R(2, AllUp, dns: false)));

    [Fact]
    public void DnsNotCheckedIsNotAFailure() =>
        Assert.Equal(Status.Up, new Classifier().Classify(R(2, AllUp, dns: null)));

    [Fact]
    public void OneOfThreeAnsweringIsDegraded() =>
        Assert.Equal(Status.Degraded, new Classifier().Classify(R(2, new int?[] { 20, null, null })));

    [Fact]
    public void TwoOfThreeAnsweringIsUp() =>
        Assert.Equal(Status.Up, new Classifier().Classify(R(2, new int?[] { 20, 25, null })));

    [Fact]
    public void SlowFastestServerIsDegraded() =>
        Assert.Equal(Status.Degraded, new Classifier { SlowMs = 300 }.Classify(R(2, new int?[] { 450, 500, 600 })));

    [Fact]
    public void AnyInternetAnswerMeansLinkEvenIfAdapterUnknown() =>
        Assert.Equal(Status.Up, new Classifier().Classify(R(null, AllUp, link: false, gatewayIp: null)));
}
