using System.Text.Json.Serialization;
using System.ComponentModel;

namespace VrcHeightOsc.Core.Domain;

/// <summary>
/// A persisted rule from the Python v3 configuration format.
///
/// The runtime properties intentionally remain on this type because the old
/// configuration writer emitted them.  JsonConfigStore resets them before a
/// save so they are still schema-compatible without persisting stale runtime
/// state.
/// </summary>
public sealed class RuleDefinition
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("parameter")]
    public string Parameter { get; set; } = string.Empty;

    [JsonPropertyName("height_value")]
    public double HeightValue { get; set; } = 1.6d;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "trigger";

    [JsonPropertyName("condition")]
    public string Condition { get; set; } = "true";

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; } = 0.5d;

    [JsonPropertyName("action")]
    public string Action { get; set; } = "set";

    [JsonPropertyName("cooldown")]
    public double Cooldown { get; set; } = 1.0d;

    [JsonPropertyName("rising_edge_only")]
    public bool RisingEdgeOnly { get; set; } = true;

    [JsonPropertyName("smooth_enabled")]
    public bool SmoothEnabled { get; set; }

    [JsonPropertyName("smooth_time")]
    public double SmoothTime { get; set; } = 0.35d;

    [JsonPropertyName("limit_enabled")]
    public bool LimitEnabled { get; set; }

    [JsonPropertyName("limit_min")]
    public double LimitMin { get; set; } = 0.5d;

    [JsonPropertyName("limit_max")]
    public double LimitMax { get; set; } = 2.0d;

    [JsonPropertyName("limit_behavior")]
    public string LimitBehavior { get; set; } = "clamp";

    [JsonPropertyName("follow_input_min")]
    public double FollowInputMin { get; set; } = 0.0d;

    [JsonPropertyName("follow_input_max")]
    public double FollowInputMax { get; set; } = 1.0d;

    [JsonPropertyName("follow_height_min")]
    public double FollowHeightMin { get; set; } = 0.5d;

    [JsonPropertyName("follow_height_max")]
    public double FollowHeightMax { get; set; } = 2.0d;

    [JsonPropertyName("follow_deadband")]
    public double FollowDeadband { get; set; } = 0.005d;

    [JsonPropertyName("last_fire")]
    [Browsable(false)]
    public double LastFire { get; set; }

    [JsonPropertyName("was_active")]
    [Browsable(false)]
    public bool WasActive { get; set; }

    [JsonPropertyName("last_follow_value")]
    [Browsable(false)]
    public double? LastFollowValue { get; set; }

    [JsonPropertyName("last_follow_height")]
    [Browsable(false)]
    public double? LastFollowHeight { get; set; }

    public RuleDefinition Clone()
    {
        return new RuleDefinition
        {
            Enabled = Enabled,
            Parameter = Parameter,
            HeightValue = HeightValue,
            Mode = Mode,
            Condition = Condition,
            Threshold = Threshold,
            Action = Action,
            Cooldown = Cooldown,
            RisingEdgeOnly = RisingEdgeOnly,
            SmoothEnabled = SmoothEnabled,
            SmoothTime = SmoothTime,
            LimitEnabled = LimitEnabled,
            LimitMin = LimitMin,
            LimitMax = LimitMax,
            LimitBehavior = LimitBehavior,
            FollowInputMin = FollowInputMin,
            FollowInputMax = FollowInputMax,
            FollowHeightMin = FollowHeightMin,
            FollowHeightMax = FollowHeightMax,
            FollowDeadband = FollowDeadband,
            LastFire = LastFire,
            WasActive = WasActive,
            LastFollowValue = LastFollowValue,
            LastFollowHeight = LastFollowHeight,
        };
    }

    public void ResetRuntimeState()
    {
        LastFire = 0.0d;
        WasActive = false;
        LastFollowValue = null;
        LastFollowHeight = null;
    }
}
