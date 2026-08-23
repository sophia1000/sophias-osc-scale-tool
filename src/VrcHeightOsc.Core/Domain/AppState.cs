using System.Collections.ObjectModel;
using System.Globalization;

namespace VrcHeightOsc.Core.Domain;

/// <summary>
/// Thread-safe process state.  Network callbacks update this object, while
/// the UI reads immutable snapshots and never touches the live collections.
/// </summary>
public sealed class AppState
{
    public const string DefaultVrChatIp = "127.0.0.1";
    public const int DefaultVrChatOscPort = 9000;

    private readonly object _gate = new();

    private double? _eyeHeight;
    private double? _eyeHeightMin;
    private double? _eyeHeightMax;
    private bool? _scalingAllowed;
    private string _avatarId = string.Empty;
    private readonly Dictionary<string, object?> _parameters = new(StringComparer.Ordinal);

    private string? _vrChatQueryIp;
    private int? _vrChatQueryPort;
    private string? _vrChatServiceName;
    private string _vrChatOscIp = DefaultVrChatIp;
    private int _vrChatOscPort = DefaultVrChatOscPort;
    private bool _vrChatFound;
    private double _lastVrChatSeen;
    private int _queryFailCount;

    private string _localIp = DefaultVrChatIp;
    private int? _localOscPort;
    private int? _localQueryPort;
    private long _networkGeneration;
    private long _hardRestartCount;
    private string _lastStatus = "Starting...";

    private List<RuleDefinition> _rules = new();
    private UiConfig _ui = new();

    public void SetStatus(string text)
    {
        lock (_gate)
        {
            _lastStatus = text ?? string.Empty;
        }
    }

    public void UpdateValue(string path, object? value)
    {
        path = Unescape(path);
        value = ValueNormalization.FlattenSingleValue(value);

        lock (_gate)
        {
            if (path.Equals(OscPaths.EyeHeight, StringComparison.Ordinal))
            {
                var fallback = !_eyeHeight.HasValue || _eyeHeight.Value == 0.0d ? 1.6d : _eyeHeight.Value;
                _eyeHeight = ValueNormalization.SafeFloat(value, fallback);
            }
            else if (path.Equals(OscPaths.EyeHeightMin, StringComparison.Ordinal))
            {
                var fallback = _eyeHeightMin ?? 0.0d;
                _eyeHeightMin = ValueNormalization.SafeFloat(value, fallback);
            }
            else if (path.Equals(OscPaths.EyeHeightMax, StringComparison.Ordinal))
            {
                var fallback = _eyeHeightMax ?? 0.0d;
                _eyeHeightMax = ValueNormalization.SafeFloat(value, fallback);
            }
            else if (path.Equals(OscPaths.ScalingAllowed, StringComparison.Ordinal))
            {
                _scalingAllowed = ValueNormalization.Boolish(value);
            }
            else if (path.Equals(OscPaths.AvatarChange, StringComparison.Ordinal))
            {
                _avatarId = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            else if (ValueNormalization.IsAvatarParameterAddress(path))
            {
                _parameters[ValueNormalization.ParamNameFromAddress(path)] = value;
            }
        }
    }

    public void ClearRemoteConnection()
    {
        lock (_gate)
        {
            _vrChatQueryIp = null;
            _vrChatQueryPort = null;
            _vrChatServiceName = null;
            _vrChatFound = false;
            _lastVrChatSeen = 0.0d;
            _queryFailCount = 0;
            _vrChatOscIp = DefaultVrChatIp;
            _vrChatOscPort = DefaultVrChatOscPort;
        }
    }

    public void SetRemoteConnection(
        string queryIp,
        int queryPort,
        string? serviceName,
        string? oscIp,
        int oscPort,
        double? seenUnixSeconds = null)
    {
        lock (_gate)
        {
            _vrChatQueryIp = queryIp;
            _vrChatQueryPort = queryPort;
            _vrChatServiceName = serviceName;
            _vrChatOscIp = NormalizeOscIp(oscIp);
            _vrChatOscPort = oscPort;
            _vrChatFound = true;
            _lastVrChatSeen = seenUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0d;
            _queryFailCount = 0;
        }
    }

    public void MarkHeartbeatSucceeded(
        string? oscIp = null,
        int? oscPort = null,
        double? seenUnixSeconds = null)
    {
        lock (_gate)
        {
            _vrChatFound = true;
            _lastVrChatSeen = seenUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0d;
            _queryFailCount = 0;
            if (oscIp is not null)
            {
                _vrChatOscIp = NormalizeOscIp(oscIp);
            }

            if (oscPort.HasValue)
            {
                _vrChatOscPort = oscPort.Value;
            }
        }
    }

    public int MarkQueryFailure()
    {
        lock (_gate)
        {
            _queryFailCount++;
            return _queryFailCount;
        }
    }

    public void SetLocalIp(string? ip)
    {
        lock (_gate)
        {
            _localIp = string.IsNullOrWhiteSpace(ip) ? DefaultVrChatIp : ip;
        }
    }

    public void SetLocalPorts(int? oscPort, int? queryPort)
    {
        lock (_gate)
        {
            _localOscPort = oscPort;
            _localQueryPort = queryPort;
        }
    }

    public void IncrementNetworkGeneration()
    {
        lock (_gate)
        {
            _networkGeneration++;
        }
    }

    public void IncrementHardRestartCount()
    {
        lock (_gate)
        {
            _hardRestartCount++;
        }
    }

    public void ReplaceRules(IEnumerable<RuleDefinition>? rules)
    {
        lock (_gate)
        {
            _rules = rules?.Select(static rule => rule.Clone()).ToList() ?? new List<RuleDefinition>();
        }
    }

    public void MutateRules(Action<List<RuleDefinition>> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        lock (_gate)
        {
            var rules = _rules.Select(static rule => rule.Clone()).ToList();
            mutation(rules);
            _rules = rules;
        }
    }

    public void UpdateUi(Action<UiConfig> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        lock (_gate)
        {
            var ui = _ui.Clone();
            mutation(ui);
            _ui = ui;
        }
    }

    public void LoadConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            _rules = config.Rules.Select(static rule => rule.Clone()).ToList();
            _ui = config.Ui.Clone();
        }
    }

    public AppStateSnapshot Snapshot()
    {
        lock (_gate)
        {
            var parameters = new Dictionary<string, object?>(_parameters, StringComparer.Ordinal);
            var rules = _rules.Select(static rule => rule.Clone()).ToList();

            return new AppStateSnapshot(
                _eyeHeight,
                _eyeHeightMin,
                _eyeHeightMax,
                _scalingAllowed,
                _avatarId,
                new ReadOnlyDictionary<string, object?>(parameters),
                _vrChatQueryIp,
                _vrChatQueryPort,
                _vrChatServiceName,
                _vrChatOscIp,
                _vrChatOscPort,
                _vrChatFound,
                _lastVrChatSeen,
                _queryFailCount,
                _localIp,
                _localOscPort,
                _localQueryPort,
                _networkGeneration,
                _hardRestartCount,
                _lastStatus,
                rules.AsReadOnly(),
                _ui.Clone());
        }
    }

    private static string NormalizeOscIp(string? ip)
    {
        return string.IsNullOrWhiteSpace(ip) || ip is "0.0.0.0" or "::"
            ? DefaultVrChatIp
            : ip;
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value ?? string.Empty);
        }
        catch (UriFormatException)
        {
            return value ?? string.Empty;
        }
    }
}

public sealed class AppStateSnapshot
{
    public AppStateSnapshot(
        double? eyeHeight,
        double? eyeHeightMin,
        double? eyeHeightMax,
        bool? scalingAllowed,
        string avatarId,
        IReadOnlyDictionary<string, object?> parameters,
        string? vrChatQueryIp,
        int? vrChatQueryPort,
        string? vrChatServiceName,
        string vrChatOscIp,
        int vrChatOscPort,
        bool vrChatFound,
        double lastVrChatSeen,
        int queryFailCount,
        string localIp,
        int? localOscPort,
        int? localQueryPort,
        long networkGeneration,
        long hardRestartCount,
        string lastStatus,
        IReadOnlyList<RuleDefinition> rules,
        UiConfig ui)
    {
        EyeHeight = eyeHeight;
        EyeHeightMin = eyeHeightMin;
        EyeHeightMax = eyeHeightMax;
        ScalingAllowed = scalingAllowed;
        AvatarId = avatarId;
        Parameters = parameters;
        VrChatQueryIp = vrChatQueryIp;
        VrChatQueryPort = vrChatQueryPort;
        VrChatServiceName = vrChatServiceName;
        VrChatOscIp = vrChatOscIp;
        VrChatOscPort = vrChatOscPort;
        VrChatFound = vrChatFound;
        LastVrChatSeen = lastVrChatSeen;
        QueryFailCount = queryFailCount;
        LocalIp = localIp;
        LocalOscPort = localOscPort;
        LocalQueryPort = localQueryPort;
        NetworkGeneration = networkGeneration;
        HardRestartCount = hardRestartCount;
        LastStatus = lastStatus;
        Rules = rules;
        Ui = ui;
    }

    public double? EyeHeight { get; }
    public double? EyeHeightMin { get; }
    public double? EyeHeightMax { get; }
    public bool? ScalingAllowed { get; }
    public string AvatarId { get; }
    public IReadOnlyDictionary<string, object?> Parameters { get; }
    public string? VrChatQueryIp { get; }
    public int? VrChatQueryPort { get; }
    public string? VrChatServiceName { get; }
    public string VrChatOscIp { get; }
    public int VrChatOscPort { get; }
    public bool VrChatFound { get; }
    public double LastVrChatSeen { get; }
    public int QueryFailCount { get; }
    public string LocalIp { get; }
    public int? LocalOscPort { get; }
    public int? LocalQueryPort { get; }
    public long NetworkGeneration { get; }
    public long HardRestartCount { get; }
    public string LastStatus { get; }
    public IReadOnlyList<RuleDefinition> Rules { get; }
    public UiConfig Ui { get; }
}
