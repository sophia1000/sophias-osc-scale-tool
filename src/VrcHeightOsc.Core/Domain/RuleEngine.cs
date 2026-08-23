using System.Collections.ObjectModel;

namespace VrcHeightOsc.Core.Domain;

public sealed record HeightCommand(
    double Height,
    bool Smooth,
    double SmoothTime,
    string? Parameter = null);

public sealed class RuleEvaluationResult
{
    public RuleEvaluationResult(
        IReadOnlyList<HeightCommand>? commands = null,
        IReadOnlyList<string>? statusMessages = null)
    {
        Commands = commands ?? Array.Empty<HeightCommand>();
        StatusMessages = statusMessages ?? Array.Empty<string>();
    }

    public IReadOnlyList<HeightCommand> Commands { get; }
    public IReadOnlyList<string> StatusMessages { get; }
    public bool HasCommands => Commands.Count != 0;
}

/// <summary>
/// Evaluates trigger/follow rules in the same order and with the same edge,
/// cooldown, deadband, and limit semantics as the Python implementation.
/// Runtime rule state lives here instead of in snapshots, so a snapshot can
/// safely cross from an OSC callback to another worker/UI thread.
/// </summary>
public sealed class RuleEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RuleRuntimeState> _runtime = new(StringComparer.Ordinal);
    private readonly Func<double> _nowUnixSeconds;

    public RuleEngine(Func<double>? nowUnixSeconds = null)
    {
        _nowUnixSeconds = nowUnixSeconds ?? GetUnixSeconds;
    }

    public RuleEvaluationResult Evaluate(
        string address,
        object? value,
        AppStateSnapshot snapshot,
        double? nowUnixSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        address = Unescape(address);
        if (!ValueNormalization.IsAvatarParameterAddress(address))
        {
            return new RuleEvaluationResult();
        }

        var parameterName = ValueNormalization.ParamNameFromAddress(address);
        var now = nowUnixSeconds ?? _nowUnixSeconds();
        var commands = new List<HeightCommand>();
        var statusMessages = new List<string>();

        lock (_gate)
        {
            for (var index = 0; index < snapshot.Rules.Count; index++)
            {
                var rule = snapshot.Rules[index];
                if (!rule.Enabled || !string.Equals(
                        ValueNormalization.NormalizeParamName(rule.Parameter),
                        parameterName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var runtime = GetRuntime(index, rule);
                var numeric = ValueNormalization.SafeFloat(
                    value,
                    ValueNormalization.Boolish(value) ? 1.0d : 0.0d);

                if (string.Equals(rule.Mode, "follow", StringComparison.Ordinal))
                {
                    EvaluateFollowRule(
                        rule,
                        runtime,
                        parameterName,
                        numeric,
                        snapshot,
                        now,
                        commands,
                        statusMessages);
                    continue;
                }

                var active = IsActive(rule, value, numeric);
                var shouldFire = active;
                if (rule.RisingEdgeOnly)
                {
                    shouldFire = active && !runtime.WasActive;
                }

                if (now - runtime.LastFire < Math.Max(0.0d, rule.Cooldown))
                {
                    shouldFire = false;
                }

                runtime.WasActive = active;

                if (shouldFire)
                {
                    AddRuleAction(
                        rule,
                        runtime,
                        parameterName,
                        snapshot,
                        ignoreCooldown: false,
                        now,
                        commands,
                        statusMessages);
                }
            }
        }

        return new RuleEvaluationResult(
            new ReadOnlyCollection<HeightCommand>(commands),
            new ReadOnlyCollection<string>(statusMessages));
    }

    /// <summary>
    /// Performs the UI "Test" action for one rule.  Pass a non-negative index
    /// when the rule came from an ordered state snapshot so its cooldown and
    /// edge state are shared with Evaluate.
    /// </summary>
    public RuleEvaluationResult TriggerRuleAction(
        RuleDefinition rule,
        AppStateSnapshot snapshot,
        bool ignoreCooldown = true,
        int ruleIndex = -1,
        double? nowUnixSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = nowUnixSeconds ?? _nowUnixSeconds();
        var commands = new List<HeightCommand>();
        var statusMessages = new List<string>();

        lock (_gate)
        {
            var runtime = GetRuntime(ruleIndex, rule);
            AddRuleAction(
                rule,
                runtime,
                ValueNormalization.NormalizeParamName(rule.Parameter),
                snapshot,
                ignoreCooldown,
                now,
                commands,
                statusMessages);
        }

        return new RuleEvaluationResult(
            new ReadOnlyCollection<HeightCommand>(commands),
            new ReadOnlyCollection<string>(statusMessages));
    }

    public void ResetRuntime()
    {
        lock (_gate)
        {
            _runtime.Clear();
        }
    }

    private void EvaluateFollowRule(
        RuleDefinition rule,
        RuleRuntimeState runtime,
        string parameterName,
        double value,
        AppStateSnapshot snapshot,
        double now,
        ICollection<HeightCommand> commands,
        ICollection<string> statusMessages)
    {
        var deadband = Math.Max(0.0d, rule.FollowDeadband);
        if (runtime.LastFollowValue.HasValue &&
            Math.Abs(value - runtime.LastFollowValue.Value) < deadband)
        {
            return;
        }

        var raw = HeightMath.MapFollowHeight(rule, value);
        var current = snapshot.EyeHeight ?? 1.6d;
        var target = HeightMath.ApplyRuleHeightLimits(rule, current, raw);

        runtime.LastFollowValue = value;

        if (!target.HasValue)
        {
            statusMessages.Add($"Follow rule '{parameterName}' blocked by height limit.");
            return;
        }

        if (runtime.LastFollowHeight.HasValue &&
            Math.Abs(target.Value - runtime.LastFollowHeight.Value) < 0.0005d)
        {
            return;
        }

        runtime.LastFollowHeight = target.Value;
        runtime.LastFire = now;
        commands.Add(new HeightCommand(target.Value, rule.SmoothEnabled, rule.SmoothTime, parameterName));
    }

    private static bool IsActive(RuleDefinition rule, object? value, double numeric)
    {
        return rule.Condition switch
        {
            "true" => ValueNormalization.Boolish(value),
            "false" => !ValueNormalization.Boolish(value),
            "above" => numeric > rule.Threshold,
            "below" => numeric < rule.Threshold,
            _ => false,
        };
    }

    private static void AddRuleAction(
        RuleDefinition rule,
        RuleRuntimeState runtime,
        string parameterName,
        AppStateSnapshot snapshot,
        bool ignoreCooldown,
        double now,
        ICollection<HeightCommand> commands,
        ICollection<string> statusMessages)
    {
        if (!ignoreCooldown && now - runtime.LastFire < Math.Max(0.0d, rule.Cooldown))
        {
            return;
        }

        var current = snapshot.EyeHeight ?? 1.6d;
        double raw;

        if (string.Equals(rule.Mode, "follow", StringComparison.Ordinal))
        {
            var value = snapshot.Parameters.TryGetValue(
                ValueNormalization.NormalizeParamName(rule.Parameter),
                out var stored)
                ? stored
                : rule.FollowInputMin;
            raw = HeightMath.MapFollowHeight(
                rule,
                ValueNormalization.SafeFloat(value, rule.FollowInputMin));
        }
        else
        {
            raw = string.Equals(rule.Action, "set", StringComparison.Ordinal)
                ? rule.HeightValue
                : current + rule.HeightValue;
        }

        var target = HeightMath.ApplyRuleHeightLimits(rule, current, raw);
        if (!target.HasValue)
        {
            statusMessages.Add($"Rule '{parameterName}' blocked by height limit.");
            return;
        }

        runtime.LastFire = now;
        commands.Add(new HeightCommand(target.Value, rule.SmoothEnabled, rule.SmoothTime, parameterName));
    }

    private RuleRuntimeState GetRuntime(int ruleIndex, RuleDefinition rule)
    {
        var key = MakeRuleKey(ruleIndex, rule);
        if (!_runtime.TryGetValue(key, out var runtime))
        {
            runtime = new RuleRuntimeState(rule);
            _runtime[key] = runtime;
        }

        return runtime;
    }

    private static string MakeRuleKey(int ruleIndex, RuleDefinition rule)
    {
        return $"{ruleIndex}\u001f{ValueNormalization.NormalizeParamName(rule.Parameter)}";
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

    private static double GetUnixSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0d;
    }

    private sealed class RuleRuntimeState
    {
        public RuleRuntimeState(RuleDefinition rule)
        {
            LastFire = rule.LastFire;
            WasActive = rule.WasActive;
            LastFollowValue = rule.LastFollowValue;
            LastFollowHeight = rule.LastFollowHeight;
        }

        public double LastFire { get; set; }
        public bool WasActive { get; set; }
        public double? LastFollowValue { get; set; }
        public double? LastFollowHeight { get; set; }
    }
}
