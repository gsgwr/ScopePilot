namespace ScopePilot.Services;

public static class ScopePilotDataPaths
{
    public const string EnvironmentVariable = "SCOPEPILOT_DATA_DIRECTORY";
    public static string RootDirectory => ResolveRoot(Environment.GetEnvironmentVariable(EnvironmentVariable));

    internal static string ResolveRoot(string? configuredDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScopePilot");
        return Path.GetFullPath(configuredDirectory);
    }
}
