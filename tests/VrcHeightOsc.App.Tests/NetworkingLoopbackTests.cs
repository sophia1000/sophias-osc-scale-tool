using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BuildSoft.OscCore;
using VrcHeightOsc.App.Networking;
using Xunit;

namespace VrcHeightOsc.App.Tests;

public sealed class NetworkingLoopbackTests
{
    [Fact]
    public async Task OscQueryClientReadsHostInfoAndRejectsNonSuccess()
    {
        await using var server = await LoopbackHttpServer.StartAsync(async context =>
        {
            if (context.Request.Url?.Query.Contains("HOST_INFO", StringComparison.OrdinalIgnoreCase) == true)
            {
                await LoopbackHttpServer.WriteJsonAsync(context, new
                {
                    NAME = "VRChat",
                    OSC_IP = "127.0.0.1",
                    OSC_PORT = 9000,
                    OSC_TRANSPORT = "UDP",
                });
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
        });

        using var client = new OscQueryHttpClient();
        var host = await client.GetHostInfoAsync(
            IPAddress.Loopback,
            server.Port,
            CancellationToken.None);

        Assert.NotNull(host);
        Assert.Equal("VRChat", host.Name);
        Assert.Equal("127.0.0.1", host.OscIp);
        Assert.Equal(9000, host.OscPort);

        await using var nonSuccess = await LoopbackHttpServer.StartAsync(context =>
        {
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            context.Response.Close();
            return Task.CompletedTask;
        });

        var missing = await client.GetHostInfoAsync(
            IPAddress.Loopback,
            nonSuccess.Port,
            CancellationToken.None);

        Assert.Null(missing);
    }

    [Fact]
    public async Task OscQueryClientRecognizesVrChatFromRootTreeAndReadsValues()
    {
        await using var server = await LoopbackHttpServer.StartAsync(async context =>
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/" && context.Request.QueryString["VALUE"] is null)
            {
                await LoopbackHttpServer.WriteJsonAsync(context, new
                {
                    FULL_PATH = "/",
                    CONTENTS = new
                    {
                        avatar = new
                        {
                            FULL_PATH = "/avatar",
                            CONTENTS = new
                            {
                                eyeheight = new
                                {
                                    FULL_PATH = "/avatar/eyeheight",
                                    VALUE = 1.75,
                                },
                                parameters = new
                                {
                                    FULL_PATH = "/avatar/parameters",
                                    CONTENTS = new
                                    {
                                        Encoded = new
                                        {
                                            FULL_PATH = "/avatar/parameters/Foo%20Bar",
                                            VALUE = true,
                                        },
                                    },
                                },
                            },
                        },
                    },
                });
                return;
            }

            if (path == "/avatar/eyeheight" &&
                context.Request.QueryString["VALUE"] is not null)
            {
                await LoopbackHttpServer.WriteJsonAsync(context, new
                {
                    FULL_PATH = "/avatar/eyeheight",
                    VALUE = 1.75,
                });
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
        });

        using var client = new OscQueryHttpClient();
        Assert.True(await client.LooksLikeVrChatAsync(
            IPAddress.Loopback,
            server.Port,
            "Unrelated mDNS name",
            CancellationToken.None));

        var values = await client.ReadLiveValuesAsync(
            IPAddress.Loopback,
            server.Port,
            CancellationToken.None);

        Assert.Equal(1.75d, Convert.ToDouble(values["/avatar/eyeheight"]));
        Assert.Equal(true, values["/avatar/parameters/Foo Bar"]);
    }

    [Fact]
    public async Task OscTransportReceivesFloatOnLoopback()
    {
        var port = GetAvailableUdpPort();
        using var transport = new OscTransport();
        var received = new TaskCompletionSource<OscMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        transport.MessageReceived += message =>
        {
            if (message.Address == "/avatar/eyeheight")
            {
                received.TrySetResult(message);
            }
        };

        transport.Start(port);
        using var sender = new OscClient(IPAddress.Loopback.ToString(), port);
        sender.Send("/avatar/eyeheight", 1.875f);

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(received.Task, completed);

        var message = await received.Task;
        Assert.Equal("/avatar/eyeheight", message.Address);
        Assert.Single(message.Arguments);
        Assert.Equal(1.875f, Assert.IsType<float>(message.Arguments[0]));
    }

    private static int GetAvailableUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}

internal sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<HttpListenerContext, Task> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    private LoopbackHttpServer(HttpListener listener, Func<HttpListenerContext, Task> handler)
    {
        _listener = listener;
        _handler = handler;
        _loop = Task.Run(RunAsync);
    }

    public int Port { get; private init; }

    public static async Task<LoopbackHttpServer> StartAsync(Func<HttpListenerContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var port = GetAvailableTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = new LoopbackHttpServer(listener, handler)
        {
            Port = port,
        };

        await Task.Yield();
        return server;
    }

    public static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        _listener.Close();

        try
        {
            await _loop;
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await _handler(context);
            }
            catch
            {
                try
                {
                    context.Response.Abort();
                }
                catch
                {
                }
            }
        }
    }

    private static int GetAvailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
