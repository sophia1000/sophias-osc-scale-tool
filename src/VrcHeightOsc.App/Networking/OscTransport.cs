using BlobHandles;
using BuildSoft.OscCore;

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

    public void SetTarget(string host, int port)
    {
        lock (_clientGate)
        {
            var next = (host, port);
            if (_target == next && _client is not null)
            {
                return;
            }

            _client?.Dispose();
            _client = new OscClient(host, port);
            _target = next;
        }
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
