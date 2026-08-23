namespace VrcHeightOsc.App;

internal static class AppPaths
{
    private const string AppDataFolder = "Sophias OSC Scale Tool";

    public static string ResolveConfigPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var destinationDirectory = Path.Combine(localAppData, AppDataFolder);
        var destinationPath = Path.Combine(destinationDirectory, AppConstants.ConfigFile);

        if (File.Exists(destinationPath))
        {
            return destinationPath;
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (var candidate in LegacyConfigCandidates())
        {
            if (!File.Exists(candidate) || PathsEqual(candidate, destinationPath))
            {
                continue;
            }

            try
            {
                File.Copy(candidate, destinationPath, overwrite: false);
                return destinationPath;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                return destinationPath;
            }
            catch (UnauthorizedAccessException)
            {
                // Try the next legacy location. The destination remains the
                // authoritative path even when no migration source is readable.
            }
        }

        return destinationPath;
    }

    private static IEnumerable<string> LegacyConfigCandidates()
    {
        var projectRoot = FindProjectRoot();
        if (projectRoot is not null)
        {
            yield return Path.Combine(projectRoot, AppConstants.ConfigFile);
        }

        yield return Path.Combine(Environment.CurrentDirectory, AppConstants.ConfigFile);
        yield return Path.Combine(AppContext.BaseDirectory, AppConstants.ConfigFile);
    }

    private static string? FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VrcHeightOsc.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);
}
