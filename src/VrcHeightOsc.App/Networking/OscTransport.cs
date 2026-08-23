using BlobHandles;
using BuildSoft.OscCore;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace VrcHeightOsc.App.Networking;

internal sealed class OscTransport : IDisposable
{
    private readonly object _clientGate = new();
    private OscServer? _server;
    private OscClient? _client;
    private (string Host, int Port)? _target;
    private MonitorCallback? _monitor;

    public int LocalPort { get; private set; }

    public event Action<OscMessage>? MessageReceived;

    public void Start(int port)
    {
        StopServer();
        LocalPort = port;
        _server = OscServer.GetOrCreate(port);
        _monitor = OnMessage;
        _server.AddMonitorCallback(_monitor);
    }

    public string SetTarget(string host, int port)
    {
        lock (_clientGate)
        {
            var next = (host, port);
            if (_target == next && _client is not null)
            {
                return _client.Destination.Address.ToString();
            }

            _client?.Dispose();
            _client = CreateClient(host, port);
            _target = next;
            return _client.Destination.Address.ToString();
        }
    }

    private static OscClient CreateClient(string host, int port)
    {
        try
        {
            return new OscClient(host, port);
        }
        catch (SocketException original) when (
            IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address))
        {
            // Some Windows network stacks reject OscCore's connected loopback UDP
            // socket when the process also owns multicast sockets. VRChat listens on
            // every local IPv4 interface, so an active interface is a safe same-host
            // fallback while still using OscCore for serialization and transport.
            foreach (var localAddress in ActiveLocalIpv4Addresses())
            {
                try
                {
                    return new OscClient(localAddress.ToString(), port);
                }
                catch (SocketException)
                {
                    // Try the next active interface before surfacing the first error.
                }
            }

            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }

    private static IEnumerable<IPAddress> ActiveLocalIpv4Addresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .Select(adapter => new
            {
                HasGateway = adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !gateway.Address.Equals(IPAddress.Any)),
                Addresses = adapter.GetIPProperties().UnicastAddresses
                    .Select(unicast => unicast.Address)
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                      !IPAddress.IsLoopback(address))
                    .ToArray(),
            })
            .OrderByDescending(adapter => adapter.HasGateway)
            .SelectMany(adapter => adapter.Addresses)
            .Distinct();
    }

    public void ClearTarget()
    {
        lock (_clientGate)
        {
            _client?.Dispose();
            _client = null;
            _target = null;
        }
    }

    public void SendFloat(string address, float value)
    {
        lock (_clientGate)
        {
            (_client ?? throw new InvalidOperationException("VRChat OSC target is not connected."))
                .Send(address, value);
        }
    }

    private void OnMessage(BlobString address, OscMessageValues values)
    {
        try
        {
            var arguments = new object?[values.ElementCount];
            for (var i = 0; i < values.ElementCount; i++)
            {
                arguments[i] = ReadValue(values, i, values.GetTypeTag(i));
            }

            MessageReceived?.Invoke(new OscMessage(Uri.UnescapeDataString(address.ToString()), arguments));
        }
        catch
        {
            // A malformed datagram must not stop OscCore's receive thread.
        }
    }

    private static object? ReadValue(OscMessageValues values, int index, TypeTag tag) => tag switch
    {
        TypeTag.Float32 => values.ReadFloatElement(index),
        TypeTag.Float64 => values.ReadFloat64Element(index),
        TypeTag.Int32 => values.ReadIntElement(index),
        TypeTag.Int64 => values.ReadInt64Element(index),
        TypeTag.String => values.ReadStringElement(index),
        TypeTag.True or TypeTag.False => values.ReadBooleanElement(index),
        TypeTag.AsciiChar32 => values.ReadAsciiCharElement(index),
        TypeTag.Blob => values.ReadBlobElement(index),
        TypeTag.Nil or TypeTag.Infinitum => null,
        _ => null,
    };

    private void StopServer()
    {
        if (_server is not null && _monitor is not null)
        {
            _server.RemoveMonitorCallback(_monitor);
        }

        _server?.Dispose();
        _server = null;
        _monitor = null;
    }

    public void Dispose()
    {
        ClearTarget();
        StopServer();
    }
}
