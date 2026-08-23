using System.Text.Json;
using System.Text.Json.Serialization;
using VrcHeightOsc.Core.Domain;

namespace VrcHeightOsc.Core.Config;

/// <summary>
/// Loads and atomically saves the existing Python v3 JSON format.
/// </summary>
public sealed class JsonConfigStore : IDisposable
{
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly string _path;
    private Timer? _saveTimer;
    private bool _disposed;
    private string? _lastError;

    public JsonConfigStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A configuration path is required.", nameof(path));
        }

        _path = System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    public AppConfig Load()
    {
        lock (_saveGate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    SetLastError(null);
                    return new AppConfig();
                }

                var json = File.ReadAllText(_path);
                var config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions) ?? new AppConfig();
                config.Ui ??= new UiConfig();
                config.Rules ??= new List<RuleDefinition>();

                foreach (var rule in config.Rules)
                {
                    rule.Parameter ??= string.Empty;
                    rule.Parameter = ValueNormalization.NormalizeParamName(rule.Parameter);
                    rule.ResetRuntimeState();
                }

                SetLastError(null);
                return config;
            }
            catch (Exception exception)
            {
                SetLastError($"Config load failed: {exception.Message}");
                return new AppConfig();
            }
        }
    }

    public void SaveNow(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ThrowIfDisposed();

        lock (_saveGate)
        {
            try
            {
                var persisted = PrepareForSave(config);
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    using (var stream = new FileStream(
                               temporaryPath,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None,
                               bufferSize: 4096,
                               options: FileOptions.SequentialScan | FileOptions.WriteThrough))
                    {
                        JsonSerializer.Serialize(stream, persisted, SerializerOptions);
                        stream.Flush(flushToDisk: true);
                    }

                    ReplaceAtomically(temporaryPath, _path);
                }
                finally
                {
                    TryDelete(temporaryPath);
                }

                SetLastError(null);
            }
            catch (Exception exception)
            {
                SetLastError($"Config save failed: {exception.Message}");
            }
        }
    }

    public void ScheduleSave(
        Func<AppConfig> snapshotProvider,
        TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ThrowIfDisposed();

        var dueTime = delay ?? TimeSpan.FromMilliseconds(350);
        if (dueTime < TimeSpan.Zero)
        {
            dueTime = TimeSpan.Zero;
        }

        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(
                static state =>
                {
                    var request = (SaveRequest)state!;
                    try
                    {
                        request.Store.SaveNow(request.Provider());
                    }
                    catch (Exception exception)
                    {
                        request.Store.SetLastError($"Config save failed: {exception.Message}");
                    }
                },
                new SaveRequest(this, snapshotProvider),
                dueTime,
                Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }

    private static AppConfig PrepareForSave(AppConfig source)
    {
        var persisted = source.Clone();
        persisted.Version = AppConfig.CurrentVersion;

        foreach (var rule in persisted.Rules)
        {
            rule.Parameter = ValueNormalization.NormalizeParamName(rule.Parameter);
            rule.ResetRuntimeState();
        }

        return persisted;
    }

    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            File.Move(temporaryPath, destinationPath);
            return;
        }

        try
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch (IOException)
        {
            // Some filesystems do not support File.Replace even on Windows.
            // The temporary file is still complete, so replace it as the
            // compatibility fallback.
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed cleanup must not hide the original save exception.
        }
    }

    private void SetLastError(string? value)
    {
        lock (_gate)
        {
            _lastError = value;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private sealed record SaveRequest(JsonConfigStore Store, Func<AppConfig> Provider);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
