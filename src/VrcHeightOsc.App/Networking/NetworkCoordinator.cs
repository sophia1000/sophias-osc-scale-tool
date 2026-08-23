using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using VRC.OSCQuery;

namespace VrcHeightOsc.App.Networking;

internal sealed class NetworkCoordinator : IAsyncDisposable
{
    private sealed record Candidate(string Name, IPAddress Address, int Port);

    private readonly object _stateGate = new();
    private readonly object _transportGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<Candidate> _candidates = new();
    private readonly Dictionary<string, DateTimeOffset> _badEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly OscTransport _osc = new();
    private readonly OscQueryHttpClient _http = new();
    private readonly Func<string, bool>? _candidateFilter;

    private MeaModDiscovery? _discovery;
    private OSCQueryService? _queryService;
    private Task? _supervisor;
    private int _localOscPort;
    private int _localQueryPort;
    private int _generation;
    private int _restartCount;
    private int _heartbeatFailures;
    private int _hardReconnectRequested;
    private string? _lastCandidateDiagnostic;
    private string? _connectedServiceName;
    private IPAddress? _queryAddress;
    private int? _queryPort;
    private string _oscHost = "127.0.0.1";
    private int _oscPort = 9000;
    private string _status = "Starting...";

    internal NetworkCoordinator(Func<string, bool>? candidateFilter = null)
    {
        _candidateFilter = candidateFilter;
    }

    public event Action<OscMessage>? MessageReceived;
    public event Action<string, object?>? RemoteValueReceived;
    public event Action<NetworkStateSnapshot>? StateChanged;

    public NetworkStateSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
            {
                return new NetworkStateSnapshot(
                    _queryAddress is not null && _queryPort.HasValue,
                    _queryAddress?.ToString(),
                    _queryPort,
                    _oscHost,
                    _oscPort,
                    _localOscPort,
                    _localQueryPort,
                    _generation,
                    _restartCount,
                    _status);
            }
        }
    }

    public async Task StartAsync()
    {
        try
        {
            await RebuildLocalServicesAsync("initial start", _shutdown.Token);
        }
        catch
        {
            // Keep a supervisor alive so a transient bind/mDNS failure can
            // repair itself without requiring the entire application to restart.
            Interlocked.Exchange(ref _hardReconnectRequested, 1);
            throw;
        }
        finally
        {
            _supervisor ??= Task.Run(() => SupervisorLoopAsync(_shutdown.Token));
        }
    }

    public void RequestHardReconnect()
    {
        Interlocked.Exchange(ref _hardReconnectRequested, 1);
        SetStatus("Hard reconnect requested.");
    }

    public async Task RefreshValuesAsync(CancellationToken token = default)
    {
        IPAddress? address;
        int? port;
        lock (_stateGate)
        {
            address = _queryAddress;
            port = _queryPort;
        }

        if (address is null || port is null)
        {
            SetStatus("Cannot refresh values while VRChat is disconnected.");
            return;
        }

        var values = await _http.ReadLiveValuesAsync(address, port.Value, token);
        foreach (var (path, value) in values)
        {
            RemoteValueReceived?.Invoke(path, value);
            SetLocalQueryValue(path, value);
        }

        SetStatus(values.Count > 0
            ? $"Refreshed {values.Count} OSCQuery values."
            : "VRChat returned no live values.");
    }

    public void SendHeight(float height)
    {
        lock (_transportGate)
        {
            _osc.SendFloat(AppConstants.EyeHeight, height);
        }
    }

    public void SetLocalQueryValue(string path, object? value)
    {
        lock (_transportGate)
        {
            var service = _queryService;
            if (service is null)
            {
                return;
            }

            try
            {
                service.SetValue(path, value switch
                {
                    object[] array => array,
                    null => Array.Empty<object>(),
                    _ => new[] { value },
                });
            }
            catch
            {
                // HTTP metadata is helpful but must never break live OSC handling.
            }
        }
    }

    private async Task SupervisorLoopAsync(CancellationToken token)
    {
        var lastDiscoveryRefresh = DateTimeOffset.MinValue;
        var lastValueRefresh = DateTimeOffset.MinValue;
        var lastLogScan = DateTimeOffset.MinValue;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (Interlocked.Exchange(ref _hardReconnectRequested, 0) != 0)
                {
                    await RebuildLocalServicesAsync("manual request", token);
                    lastDiscoveryRefresh = DateTimeOffset.MinValue;
                }

                if (Snapshot.Connected)
                {
                    if (!await HeartbeatAsync(token))
                    {
                        _heartbeatFailures++;
                        if (_heartbeatFailures >= 3)
                        {
                            Disconnect("VRChat heartbeat lost; rediscovering.", quarantine: true);
                            _heartbeatFailures = 0;
                            _queryService?.RefreshServices();
                        }
                        else
                        {
                            SetStatus($"VRChat heartbeat missed ({_heartbeatFailures}/3). Retrying...");
                        }
                    }
                    else
                    {
                        _heartbeatFailures = 0;
                        if (DateTimeOffset.UtcNow - lastValueRefresh >= TimeSpan.FromSeconds(2))
                        {
                            await RefreshValuesAsync(token);
                            lastValueRefresh = DateTimeOffset.UtcNow;
                        }
                    }
                }
                else
                {
                    if (DateTimeOffset.UtcNow - lastDiscoveryRefresh >= TimeSpan.FromSeconds(3))
                    {
                        _queryService?.RefreshServices();
                        foreach (var profile in _queryService?.GetOSCQueryServices() ?? [])
                        {
                            Enqueue(profile);
                        }
                        lastDiscoveryRefresh = DateTimeOffset.UtcNow;
                    }

                    if (DateTimeOffset.UtcNow - lastLogScan >= TimeSpan.FromSeconds(5))
                    {
                        foreach (var port in FindVrChatQueryPortsFromLogs())
                        {
                            _candidates.Enqueue(new Candidate($"VRChat-log-{port}", IPAddress.Loopback, port));
                        }
                        lastLogScan = DateTimeOffset.UtcNow;
                    }

                    await TryCandidatesAsync(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus($"Network supervisor recovered from {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> HeartbeatAsync(CancellationToken token)
    {
        IPAddress? address;
        int? port;
        lock (_stateGate)
        {
            address = _queryAddress;
            port = _queryPort;
        }

        if (address is null || port is null)
        {
            return false;
        }

        try
        {
            var host = await _http.GetHostInfoAsync(address, port.Value, token);
            if (host is null)
            {
                return false;
            }

            UpdateOscTarget(host, address);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task TryCandidatesAsync(CancellationToken token)
    {
        ExpireBadEndpoints();
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<Candidate>();
        while (_candidates.TryDequeue(out var queuedCandidate))
        {
            pending.Add(queuedCandidate);
        }

        foreach (var candidate in pending
                     .OrderBy(item => item.Name.StartsWith("VRChat-log-", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (_candidateFilter is not null && !_candidateFilter(candidate.Name))
            {
                continue;
            }

            foreach (var address in CandidateAddresses(candidate.Address))
            {
                var key = $"{address}:{candidate.Port}";
                if (!attempted.Add(key) || IsBad(key))
                {
                    continue;
                }

                _lastCandidateDiagnostic = $"Trying VRChat OSCQuery candidate {key}.";
                try
                {
                    var host = await _http.GetHostInfoAsync(address, candidate.Port, token);
                    if (host is null)
                    {
                        MarkBad(key, TimeSpan.FromSeconds(5));
                        _lastCandidateDiagnostic = $"OSCQuery candidate {key} returned no HOST_INFO.";
                        continue;
                    }

                    var namedVrChat = candidate.Name.Contains("VRChat", StringComparison.OrdinalIgnoreCase) ||
                                      (host.Name?.Contains("VRChat", StringComparison.OrdinalIgnoreCase) ?? false);
                    if (!namedVrChat && !await _http.LooksLikeVrChatAsync(address, candidate.Port, candidate.Name, token))
                    {
                        _lastCandidateDiagnostic = $"OSCQuery candidate {key} was not identified as VRChat.";
                        continue;
                    }

                    lock (_stateGate)
                    {
                        _connectedServiceName = candidate.Name;
                        _queryAddress = address;
                        _queryPort = candidate.Port;
                        _status = $"Connected to VRChat OSCQuery at {address}:{candidate.Port}.";
                    }

                    UpdateOscTarget(host, address);
                    _lastCandidateDiagnostic = null;
                    PublishState();
                    try
                    {
                        await RefreshValuesAsync(token);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        SetStatus($"Connected to VRChat OSCQuery at {address}:{candidate.Port}; initial value refresh timed out.");
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Connected to VRChat OSCQuery at {address}:{candidate.Port}; initial value refresh deferred ({ex.GetType().Name}).");
                    }
                    return;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    MarkBad(key, TimeSpan.FromSeconds(3));
                    _lastCandidateDiagnostic = $"OSCQuery candidate {key} timed out; continuing discovery.";
                    Disconnect(_lastCandidateDiagnostic, quarantine: false);
                }
                catch (Exception ex)
                {
                    MarkBad(key, TimeSpan.FromSeconds(5));
                    _lastCandidateDiagnostic = $"OSCQuery candidate {key} failed ({ex.GetType().Name}: {ex.Message}); continuing discovery.";
                    Disconnect(_lastCandidateDiagnostic, quarantine: false);
                }
            }
        }

        SetStatus(attempted.Count == 0
            ? _lastCandidateDiagnostic ?? "Searching for VRChat OSCQuery..."
            : _lastCandidateDiagnostic ?? $"No VRChat OSCQuery candidate validated. Tried {string.Join(", ", attempted)}.");
    }

    private void UpdateOscTarget(RemoteHostInfo host, IPAddress queryAddress)
    {
        var targetHost = string.IsNullOrWhiteSpace(host.OscIp) || host.OscIp is "0.0.0.0" or "::"
            ? queryAddress.ToString()
            : host.OscIp;
        var targetPort = host.OscPort is > 0 and <= 65535 ? host.OscPort : 9000;

        lock (_transportGate)
        {
            targetHost = _osc.SetTarget(targetHost!, targetPort);
        }
        lock (_stateGate)
        {
            _oscHost = targetHost!;
            _oscPort = targetPort;
        }
        PublishState();
    }

    private async Task RebuildLocalServicesAsync(string reason, CancellationToken token)
    {
        await _lifecycleGate.WaitAsync(token);
        try
        {
            lock (_transportGate)
            {
                Disconnect($"Rebuilding local OSC/OSCQuery: {reason}", quarantine: false);
                DisposeLocalServices();

                _localOscPort = _localOscPort == 0 ? Extensions.GetAvailableUdpPort() : _localOscPort;
                _localQueryPort = _localQueryPort == 0 ? Extensions.GetAvailableTcpPort() : _localQueryPort;
                _generation++;
                _restartCount++;

                _osc.Start(_localOscPort);
                _osc.MessageReceived += OnOscMessage;

                _discovery = new MeaModDiscovery();
                _discovery.OnOscQueryServiceAdded += Enqueue;

                _queryService = new OSCQueryServiceBuilder()
                    .WithServiceName($"{AppConstants.Name}-{Environment.ProcessId}")
                    .WithHostIP(IPAddress.Loopback)
                    .WithOscIP(IPAddress.Loopback)
                    .WithTcpPort(_localQueryPort)
                    .WithUdpPort(_localOscPort)
                    .WithDiscovery(_discovery)
                    .StartHttpServer()
                    .AdvertiseOSC()
                    .AdvertiseOSCQuery()
                    .Build();

                AddLocalEndpoints(_queryService);
                _queryService.RefreshServices();
            }
            SetStatus($"Local services ready: OSC {_localOscPort}, OSCQuery {_localQueryPort}.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static void AddLocalEndpoints(OSCQueryService service)
    {
        service.AddEndpoint<string>(AppConstants.AvatarChange, Attributes.AccessValues.WriteOnly, null, "Avatar change event");
        service.AddEndpoint<float>(AppConstants.EyeHeight, Attributes.AccessValues.ReadWrite, null, "Avatar eye height");
        service.AddEndpoint<float>(AppConstants.EyeHeightMin, Attributes.AccessValues.WriteOnly, null, "Udon minimum height");
        service.AddEndpoint<float>(AppConstants.EyeHeightMax, Attributes.AccessValues.WriteOnly, null, "Udon maximum height");
        service.AddEndpoint<bool>(AppConstants.ScalingAllowed, Attributes.AccessValues.WriteOnly, null, "Scaling allowed");
        service.RootNode.AddNode(new OSCQueryNode(AppConstants.AvatarParameters)
        {
            Access = Attributes.AccessValues.WriteOnly,
            Description = "Avatar parameters from VRChat",
            Contents = new Dictionary<string, OSCQueryNode>(),
        });
    }

    private void OnOscMessage(OscMessage message)
    {
        SetLocalQueryValue(message.Address, message.Value);
        MessageReceived?.Invoke(message);
    }

    private void Enqueue(OSCQueryServiceProfile profile)
    {
        if (profile is null || profile.address is null || profile.port is < 1 or > 65535)
        {
            return;
        }

        var serviceName = profile.name ?? string.Empty;
        if (serviceName.Contains(AppConstants.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!serviceName.Contains("VRChat", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_candidateFilter is not null && !_candidateFilter(serviceName))
        {
            return;
        }

        _candidates.Enqueue(new Candidate(serviceName, profile.address, profile.port));
    }

    private void Disconnect(string reason, bool quarantine)
    {
        lock (_stateGate)
        {
            if (quarantine && _queryAddress is not null && _queryPort is not null)
            {
                MarkBad($"{_queryAddress}:{_queryPort}", TimeSpan.FromSeconds(5));
            }

            _connectedServiceName = null;
            _queryAddress = null;
            _queryPort = null;
            _oscHost = "127.0.0.1";
            _oscPort = 9000;
            _status = reason;
        }
        lock (_transportGate)
        {
            _osc.ClearTarget();
        }
        PublishState();
    }

    private void SetStatus(string status)
    {
        lock (_stateGate)
        {
            _status = status;
        }
        PublishState();
    }

    private void PublishState() => StateChanged?.Invoke(Snapshot);

    private static IEnumerable<IPAddress> CandidateAddresses(IPAddress advertised)
    {
        yield return IPAddress.Loopback;
        if (!advertised.Equals(IPAddress.Loopback))
        {
            yield return advertised;
        }
    }

    private bool IsBad(string key)
    {
        lock (_badEndpoints)
        {
            return _badEndpoints.TryGetValue(key, out var until) && until > DateTimeOffset.UtcNow;
        }
    }

    private void MarkBad(string key, TimeSpan duration)
    {
        lock (_badEndpoints)
        {
            _badEndpoints[key] = DateTimeOffset.UtcNow + duration;
        }
    }

    private void ExpireBadEndpoints()
    {
        lock (_badEndpoints)
        {
            foreach (var key in _badEndpoints.Where(pair => pair.Value <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray())
            {
                _badEndpoints.Remove(key);
            }
        }
    }

    private static IEnumerable<int> FindVrChatQueryPortsFromLogs()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "VRChat", "VRChat");
        if (!Directory.Exists(logDirectory))
        {
            yield break;
        }

        var pattern = new Regex(@"(?:OSCQuery|_oscjson\._tcp).*?(?:on|port[: ]+)\s*(\d{4,5})", RegexOptions.IgnoreCase);
        var seen = new HashSet<int>();
        foreach (var file in Directory.EnumerateFiles(logDirectory, "output_log*.*")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(1))
        {
            string startupSection;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var buffer = new char[700_000];
                var count = reader.ReadBlock(buffer, 0, buffer.Length);
                startupSection = new string(buffer, 0, count);
            }
            catch
            {
                continue;
            }

            foreach (Match match in pattern.Matches(startupSection).Cast<Match>().Reverse())
            {
                if (int.TryParse(match.Groups[1].Value, out var port) && port is >= 1024 and <= 65535 && seen.Add(port))
                {
                    yield return port;
                    yield break;
                }
            }
        }
    }

    private void DisposeLocalServices()
    {
        lock (_transportGate)
        {
            _osc.MessageReceived -= OnOscMessage;
            _queryService?.Dispose();
            _queryService = null;
            // OSCQueryService owns and disposes the discovery passed to it.
            _discovery = null;
            _osc.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_supervisor is not null)
        {
            try
            {
                await _supervisor;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _lifecycleGate.WaitAsync();
        try
        {
            DisposeLocalServices();
            _http.Dispose();
            _shutdown.Dispose();
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }
}
