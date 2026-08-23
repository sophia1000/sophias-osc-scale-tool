using System.Globalization;
using VrcHeightOsc.App.Networking;
using VrcHeightOsc.Core.Config;
using VrcHeightOsc.Core.Domain;

namespace VrcHeightOsc.App;

internal sealed class AppController : IAppController
{
    private readonly AppState _state = new();
    private readonly RuleEngine _rules = new();
    private readonly NetworkCoordinator _network = new();
    private readonly JsonConfigStore _config;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _smoothingGate = new();
    private CancellationTokenSource? _smoothing;
    private bool _started;
    private bool _disposed;

    public AppController()
    {
        _config = new JsonConfigStore(AppPaths.ResolveConfigPath());
        _network.MessageReceived += OnOscMessage;
        _network.RemoteValueReceived += OnRemoteValue;
        _network.StateChanged += OnNetworkState;
    }

    public AppStateSnapshot Snapshot => _state.Snapshot();
    public NetworkStateSnapshot NetworkSnapshot => _network.Snapshot;
    public event Action? StateChanged;

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var loaded = _config.Load();
        _state.LoadConfig(loaded);
        _state.SetStatus(_config.LastError ?? "Configuration loaded.");
        NotifyStateChanged();

        try
        {
            await _network.StartAsync();
        }
        catch (Exception ex)
        {
            _state.SetStatus($"Network startup failed: {ex.Message}");
            NotifyStateChanged();
        }
    }

    public Task SetHeightAsync(double height, bool smooth, double smoothSeconds)
    {
        var target = HeightMath.Clamp(height, HeightMath.MinOscHeight, HeightMath.MaxOscHeight);
        CancellationToken token;
        lock (_smoothingGate)
        {
            _smoothing?.Cancel();
            _smoothing?.Dispose();
            _smoothing = new CancellationTokenSource();
            token = _smoothing.Token;
        }

        return SendHeightCoreAsync(target, smooth, smoothSeconds, token);
    }

    public Task AddHeightAsync(double delta, bool smooth, double smoothSeconds)
    {
        var current = Snapshot.EyeHeight ?? Snapshot.Ui.HeightValue;
        return SetHeightAsync(current + delta, smooth, smoothSeconds);
    }

    public async Task TestRuleAsync(int index)
    {
        var snapshot = Snapshot;
        if (index < 0 || index >= snapshot.Rules.Count)
        {
            return;
        }

        var result = _rules.TriggerRuleAction(snapshot.Rules[index], snapshot, true, index);
        await ApplyRuleResultAsync(result);
    }

    public void AddRule()
    {
        _state.MutateRules(rules => rules.Add(new RuleDefinition()));
        RulesChanged();
    }

    public void UpdateRule(int index, RuleDefinition rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _state.MutateRules(rules =>
        {
            if (index >= 0 && index < rules.Count)
            {
                rule.Parameter = ValueNormalization.NormalizeParamName(rule.Parameter);
                rules[index] = rule.Clone();
            }
        });
        RulesChanged();
    }

    public void RemoveRule(int index)
    {
        _state.MutateRules(rules =>
        {
            if (index >= 0 && index < rules.Count)
            {
                rules.RemoveAt(index);
            }
        });
        RulesChanged();
    }

    public void UpdateUi(Action<UiConfig> mutation)
    {
        _state.UpdateUi(mutation);
        ScheduleSave();
        NotifyStateChanged();
    }

    public void RequestHardReconnect() => _network.RequestHardReconnect();

    public async Task RefreshValuesAsync()
    {
        try
        {
            await _network.RefreshValuesAsync();
        }
        catch (Exception ex)
        {
            _state.SetStatus($"Value refresh failed: {ex.Message}");
            NotifyStateChanged();
        }
    }

    public Task SaveAsync()
    {
        var config = BuildConfigSnapshot();
        return Task.Run(() =>
        {
            _config.SaveNow(config);
            if (_config.LastError is not null)
            {
                _state.SetStatus(_config.LastError);
                NotifyStateChanged();
            }
        });
    }

    private void OnOscMessage(OscMessage message)
    {
        _state.UpdateValue(message.Address, message.Value);
        NotifyStateChanged();

        if (!message.Address.StartsWith(OscPaths.AvatarParametersPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var result = _rules.Evaluate(message.Address, message.Value, Snapshot);
        _ = ApplyRuleResultAsync(result);
    }

    private void OnRemoteValue(string path, object? value)
    {
        _state.UpdateValue(path, value);
        NotifyStateChanged();
    }

    private void OnNetworkState(NetworkStateSnapshot network)
    {
        _state.SetLocalPorts(network.LocalOscPort, network.LocalQueryPort);
        if (network.Connected && network.QueryHost is not null && network.QueryPort is not null)
        {
            _state.SetRemoteConnection(
                network.QueryHost,
                network.QueryPort.Value,
                null,
                network.OscHost,
                network.OscPort);
        }
        else
        {
            _state.ClearRemoteConnection();
        }
        _state.SetStatus(network.Status);
        NotifyStateChanged();
    }

    private async Task ApplyRuleResultAsync(RuleEvaluationResult result)
    {
        foreach (var status in result.StatusMessages)
        {
            _state.SetStatus(status);
        }

        foreach (var command in result.Commands)
        {
            await SetHeightAsync(command.Height, command.Smooth, command.SmoothTime);
        }

        NotifyStateChanged();
    }

    private async Task SendHeightCoreAsync(double target, bool smooth, double smoothSeconds, CancellationToken token)
    {
        try
        {
            await _sendGate.WaitAsync(token);
            try
            {
                var start = Snapshot.EyeHeight ?? target;
                if (!smooth || smoothSeconds <= 0.01d)
                {
                    SendImmediate(target, quiet: false);
                    return;
                }

                var duration = HeightMath.Clamp(smoothSeconds, 0.02d, 10.0d);
                var steps = Math.Max(2, (int)(duration * 30.0d));
                var delay = TimeSpan.FromSeconds(duration / steps);
                for (var step = 1; step <= steps; step++)
                {
                    token.ThrowIfCancellationRequested();
                    var t = (double)step / steps;
                    var eased = t * t * (3.0d - 2.0d * t);
                    SendImmediate(start + (target - start) * eased, quiet: true);
                    await Task.Delay(delay, token);
                }

                SendImmediate(target, quiet: false);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _state.SetStatus($"Could not send height: {ex.Message}");
            NotifyStateChanged();
        }
    }

    private void SendImmediate(double value, bool quiet)
    {
        var height = (float)HeightMath.Clamp(value, HeightMath.MinOscHeight, HeightMath.MaxOscHeight);
        _network.SendHeight(height);
        _state.UpdateValue(OscPaths.EyeHeight, (double)height);
        _network.SetLocalQueryValue(OscPaths.EyeHeight, height);
        if (!quiet)
        {
            _state.SetStatus($"Sent height {height.ToString("0.000", CultureInfo.InvariantCulture)} m.");
        }
        NotifyStateChanged();
    }

    private void RulesChanged()
    {
        _rules.ResetRuntime();
        ScheduleSave();
        NotifyStateChanged();
    }

    private void ScheduleSave() => _config.ScheduleSave(BuildConfigSnapshot);

    private AppConfig BuildConfigSnapshot()
    {
        var snapshot = Snapshot;
        return new AppConfig
        {
            Version = AppConfig.CurrentVersion,
            Ui = snapshot.Ui.Clone(),
            Rules = snapshot.Rules.Select(rule => rule.Clone()).ToList(),
        };
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        lock (_smoothingGate)
        {
            _smoothing?.Cancel();
        }

        await SaveAsync();
        await _network.DisposeAsync();
        _network.MessageReceived -= OnOscMessage;
        _network.RemoteValueReceived -= OnRemoteValue;
        _network.StateChanged -= OnNetworkState;
        _config.Dispose();
        _smoothing?.Dispose();
        _sendGate.Dispose();
    }
}
