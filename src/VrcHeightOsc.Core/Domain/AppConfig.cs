using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcHeightOsc.Core.Domain;

public sealed class AppConfig
{
    public const int CurrentVersion = 3;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("ui")]
    public UiConfig Ui { get; set; } = new();

    [JsonPropertyName("rules")]
    public List<RuleDefinition> Rules { get; set; } = new();

    public AppConfig Clone()
    {
        return new AppConfig
        {
            Version = Version,
            Ui = Ui.Clone(),
            Rules = Rules.Select(static rule => rule.Clone()).ToList(),
        };
    }
}

/// <summary>
/// Values stored below the Python v3 <c>ui</c> object.
///
/// The extension-data bag keeps UI settings that a newer build may add.  It
/// also makes this type safe to use while the WinForms UI is being migrated.
/// </summary>
public sealed class UiConfig
{
    [JsonPropertyName("geometry")]
    public string Geometry { get; set; } = "1160x800";

    [JsonPropertyName("height_value")]
    public double HeightValue { get; set; } = 1.6d;

    [JsonPropertyName("smooth_enabled")]
    public bool SmoothEnabled { get; set; }

    [JsonPropertyName("smooth_time")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string SmoothTime { get; set; } = "0.35";

    [JsonPropertyName("local_osc_port")]
    public int? LocalOscPort { get; set; }

    [JsonPropertyName("local_query_port")]
    public int? LocalQueryPort { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public UiConfig Clone()
    {
        Dictionary<string, JsonElement>? extensionData = null;
        if (ExtensionData is not null)
        {
            extensionData = ExtensionData.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Clone(),
                StringComparer.Ordinal);
        }

        return new UiConfig
        {
            Geometry = Geometry,
            HeightValue = HeightValue,
            SmoothEnabled = SmoothEnabled,
            SmoothTime = SmoothTime,
            LocalOscPort = LocalOscPort,
            LocalQueryPort = LocalQueryPort,
            ExtensionData = extensionData,
        };
    }
}

/// <summary>
/// The Python UI writes <c>smooth_time</c> as a string, but older hand-edited
/// files sometimes contain a JSON number.  Accept both while always writing
/// the string form used by v3.
/// </summary>
internal sealed class StringOrNumberJsonConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            JsonTokenType.Number => reader.GetDouble().ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            JsonTokenType.Null => string.Empty,
            _ => throw new JsonException($"Expected a string or number, got {reader.TokenType}."),
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
