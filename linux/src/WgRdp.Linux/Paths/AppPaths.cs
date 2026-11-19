namespace WgRdp.Paths;

/// <summary>Rutas XDG de la edición Linux. Los secretos no van en el JSON de perfiles.</summary>
public static class AppPaths
{
    private static readonly string Home =
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ConfigDir { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Home, ".config"),
        "wgrdp");

    public static string DataDir { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data
            ? data
            : Path.Combine(Home, ".local", "share"),
        "wgrdp");

    public static string StateDir { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state
            ? state
            : Path.Combine(Home, ".local", "state"),
        "wgrdp");

    public static string RuntimeDir { get; } =
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run
            ? Path.Combine(run, "wgrdp")
            : Path.Combine(DataDir, "run");

    public static string LogDir => Path.Combine(StateDir, "logs");
    public static string SecretsDir => Path.Combine(DataDir, "secrets");
    public static string TunnelStageDir => Path.Combine(ConfigDir, "tunnels");
    public static string ProfileConfsDir => Path.Combine(ConfigDir, "confs");
    public static string ProfilesPath => Path.Combine(ConfigDir, "profiles.json");
    public static string SetupPath => Path.Combine(ConfigDir, "setup.json");
    public static string DefaultConfPath => Path.Combine(ConfigDir, "default.conf");

    public static string ProfileConfPath(string profileId) => Path.Combine(ProfileConfsDir, profileId + ".conf");

    public static bool IsManagedProfileConfPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetFullPath(ProfileConfsDir) + Path.DirectorySeparatorChar;
            return full.StartsWith(dir, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static void Ensure()
    {
        foreach (var dir in new[] { ConfigDir, DataDir, StateDir, LogDir, RuntimeDir, SecretsDir, TunnelStageDir, ProfileConfsDir })
        {
            Directory.CreateDirectory(dir);
            TryPrivate(dir);
        }
    }

    public static void TryPrivate(string path)
    {
        try
        {
            var mode = Directory.Exists(path)
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, mode);
        }
        catch
        {
            // En sistemas de archivos que no admiten modos Unix, el directorio sigue siendo usable.
        }
    }
}
