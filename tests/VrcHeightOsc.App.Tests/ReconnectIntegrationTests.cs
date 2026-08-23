using System.Net;
using VRC.OSCQuery;
using VrcHeightOsc.App.Networking;
using Xunit;

namespace VrcHeightOsc.App.Tests;

public sealed class ReconnectIntegrationTests
{
    [Fact(Timeout = 45_000)]
    public async Task ReconnectsToReplacementVrChatServiceWithoutChangingLocalPorts()
    {
        await using var coordinator = new NetworkCoordinator(
            name => name.StartsWith("VRChat-Reconnect-Test-", StringComparison.Ordinal));
        await coordinator.StartAsync();

        using var first = StartFakeVrChat("VRChat-Reconnect-Test-A");
        Assert.True(await WaitUntilAsync(
            () => coordinator.Snapshot.Connected && coordinator.Snapshot.QueryPort == first.TcpPort,
            TimeSpan.FromSeconds(15)), coordinator.Snapshot.Status);

        var localOscPort = coordinator.Snapshot.LocalOscPort;
        var localQueryPort = coordinator.Snapshot.LocalQueryPort;
        first.Service.Dispose();

        using var second = StartFakeVrChat("VRChat-Reconnect-Test-B");
        Assert.True(await WaitUntilAsync(
            () => coordinator.Snapshot.Connected && coordinator.Snapshot.QueryPort == second.TcpPort,
            TimeSpan.FromSeconds(20)), coordinator.Snapshot.Status);

        Assert.Equal(localOscPort, coordinator.Snapshot.LocalOscPort);
        Assert.Equal(localQueryPort, coordinator.Snapshot.LocalQueryPort);
    }

    private static FakeService StartFakeVrChat(string name)
    {
        var tcpPort = Extensions.GetAvailableTcpPort();
        var udpPort = Extensions.GetAvailableUdpPort();
        var service = new OSCQueryServiceBuilder()
            .WithServiceName(name)
            .WithHostIP(IPAddress.Loopback)
            .WithOscIP(IPAddress.Loopback)
            .WithTcpPort(tcpPort)
            .WithUdpPort(udpPort)
            .WithDefaults()
            .Build();
        service.AddEndpoint<string>("/chatbox/input", Attributes.AccessValues.ReadWrite, null, "VRChat marker");
        service.RefreshServices();
        return new FakeService(service, tcpPort);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(100);
        }
        return predicate();
    }

    private sealed class FakeService(OSCQueryService service, int tcpPort) : IDisposable
    {
        private bool _disposed;
        public OSCQueryService Service { get; } = service;
        public int TcpPort { get; } = tcpPort;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Service.Dispose();
        }
    }
}
