using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcHeightOsc.App.Networking;

internal sealed record RemoteHostInfo(
    [property: JsonPropertyName("NAME")] string? Name,
    [property: JsonPropertyName("OSC_IP")] string? OscIp,
    [property: JsonPropertyName("OSC_PORT")] int OscPort,
    [property: JsonPropertyName("OSC_TRANSPORT")] string? OscTransport);

internal sealed class OscQueryHttpClient : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(1.2),
    };

    private static readonly string[] RefreshPaths =
    [
        AppConstants.EyeHeight,
        AppConstants.EyeHeightMin,
        AppConstants.EyeHeightMax,
        AppConstants.ScalingAllowed,
        AppConstants.AvatarChange,
        AppConstants.AvatarParameters,
        "/",
    ];

    public async Task<RemoteHostInfo?> GetHostInfoAsync(IPAddress address, int port, CancellationToken token)
    {
        using var response = await _http.GetAsync(BaseUri(address, port) + "?HOST_INFO", token);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await JsonSerializer.DeserializeAsync<RemoteHostInfo>(stream, cancellationToken: token);
    }

    public async Task<bool> LooksLikeVrChatAsync(IPAddress address, int port, string serviceName, CancellationToken token)
    {
        var host = await GetHostInfoAsync(address, port, token);
        if (host is null)
        {
            return false;
        }

        if ((host.Name?.Contains("VRChat", StringComparison.OrdinalIgnoreCase) ?? false) ||
            serviceName.Contains("VRChat", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        using var response = await _http.GetAsync(BaseUri(address, port), token);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(token);
        return json.Contains("/chatbox/input", StringComparison.OrdinalIgnoreCase) ||
               json.Contains(AppConstants.EyeHeight, StringComparison.OrdinalIgnoreCase) ||
               json.Contains("/input/Vertical", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, object?>> ReadLiveValuesAsync(
        IPAddress address,
        int port,
        CancellationToken token)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var path in RefreshPaths)
        {
            foreach (var suffix in new[] { "?VALUE", "" })
            {
                try
                {
                    var encoded = path == "/" ? "/" : string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
                    using var response = await _http.GetAsync(BaseUri(address, port) + encoded.TrimStart('/') + suffix, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                    CollectValues(document.RootElement, values);
                    break;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // A single slow optional path should not abort the refresh.
                }
                catch (HttpRequestException)
                {
                }
                catch (JsonException)
                {
                }
            }
        }

        return values;
    }

    private static void CollectValues(JsonElement node, IDictionary<string, object?> values)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (node.TryGetProperty("FULL_PATH", out var fullPath) && fullPath.ValueKind == JsonValueKind.String &&
            node.TryGetProperty("VALUE", out var value))
        {
            values[Uri.UnescapeDataString(fullPath.GetString()!)] = ConvertValue(value);
        }

        if (!node.TryGetProperty("CONTENTS", out var contents) || contents.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var child in contents.EnumerateObject())
        {
            CollectValues(child.Value, values);
        }
    }

    private static object? ConvertValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().Select(ConvertValue).ToArray();
            return items.Length == 1 ? items[0] : items;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }

    private static string BaseUri(IPAddress address, int port)
    {
        var host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
        return $"http://{host}:{port}/";
    }

    public void Dispose() => _http.Dispose();
}
