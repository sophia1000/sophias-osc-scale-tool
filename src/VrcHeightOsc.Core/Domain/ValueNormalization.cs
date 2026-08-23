using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace VrcHeightOsc.Core.Domain;

public static class OscPaths
{
    public const string EyeHeight = "/avatar/eyeheight";
    public const string EyeHeightMin = "/avatar/eyeheightmin";
    public const string EyeHeightMax = "/avatar/eyeheightmax";
    public const string ScalingAllowed = "/avatar/eyeheightscalingallowed";
    public const string AvatarChange = "/avatar/change";
    public const string AvatarParametersPrefix = "/avatar/parameters/";
}

public static class ValueNormalization
{
    /// <summary>
    /// Mirrors Python's flatten_value: only one-element list-like values are
    /// repeatedly unwrapped. Strings and byte arrays are scalar values.
    /// </summary>
    public static object? FlattenSingleValue(object? value)
    {
        while (TryGetSingleElement(value, out var item))
        {
            value = item;
        }

        return value;
    }

    public static double SafeFloat(object? value, double defaultValue = 0.0d)
    {
        value = FlattenSingleValue(value);

        if (value is null)
        {
            return defaultValue;
        }

        if (value is JsonElement element)
        {
            return SafeFloat(element, defaultValue);
        }

        if (value is string text)
        {
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : defaultValue;
        }

        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception) when (value is IConvertible)
        {
            return defaultValue;
        }
    }

    public static bool Boolish(object? value)
    {
        value = FlattenSingleValue(value);

        if (value is JsonElement element)
        {
            return Boolish(element);
        }

        if (value is bool boolean)
        {
            return boolean;
        }

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return SafeFloat(value) > 0.5d;
        }

        if (value is string text)
        {
            return text.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on" or "t";
        }

        return false;
    }

    public static object? FirstArgument(IReadOnlyList<object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        return arguments.Count == 1 ? arguments[0] : arguments.ToArray();
    }

    public static string NormalizeParamName(object? name)
    {
        var value = Convert.ToString(name, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

        if (value.StartsWith(OscPaths.AvatarParametersPrefix, StringComparison.Ordinal))
        {
            value = value[OscPaths.AvatarParametersPrefix.Length..];
        }

        return Uri.UnescapeDataString(value).Trim();
    }

    public static string ParamNameFromAddress(string? address)
    {
        return NormalizeParamName(address);
    }

    public static bool IsAvatarParameterAddress(string? address)
    {
        return address is not null && address.StartsWith(OscPaths.AvatarParametersPrefix, StringComparison.Ordinal);
    }

    private static bool TryGetSingleElement(object? value, out object? item)
    {
        if (value is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
        {
            if (jsonElement.GetArrayLength() == 1)
            {
                item = jsonElement[0];
                return true;
            }

            item = null;
            return false;
        }

        if (value is IList list && value is not string && value is not byte[] && list.Count == 1)
        {
            item = list[0];
            return true;
        }

        item = null;
        return false;
    }

    private static double SafeFloat(JsonElement element, double defaultValue)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.String => SafeFloat(element.GetString(), defaultValue),
            JsonValueKind.True => 1.0d,
            JsonValueKind.False => 0.0d,
            _ => defaultValue,
        };
    }

    private static bool Boolish(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetDouble(out var number) => number > 0.5d,
            JsonValueKind.String => Boolish(element.GetString()),
            _ => false,
        };
    }
}
