namespace BackupMirror.Core.Storage;

/// <summary>
/// Resolves the data directory (jobs, state, logs).
/// Priority: BACKUPMIRROR_DATA env → platform service location → per-user location.
/// Windows service: C:\ProgramData\BackupMirror. Linux service/root: /var/lib/backupmirror.
/// </summary>
public static class DataPaths
{
    public const string EnvVarName = "BACKUPMIRROR_DATA";
    public const string SharedMarkerFileName = ".shared";

    public static string ProgramDataRoot =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupMirror")
            : "/var/lib/backupmirror";

    public static string LocalAppDataRoot =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupMirror")
            : Path.Combine(
                Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
                "backupmirror");

    public static string ResolveDataRoot(bool preferProgramDataForService = false)
    {
        var env = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        if (preferProgramDataForService) return ProgramDataRoot;

        if (OperatingSystem.IsWindows())
        {
            var marker = Path.Combine(ProgramDataRoot, SharedMarkerFileName);
            return File.Exists(marker) ? ProgramDataRoot : LocalAppDataRoot;
        }

        return Environment.UserName == "root" ? ProgramDataRoot : LocalAppDataRoot;
    }

    public static void EnsureLayout(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        if (OperatingSystem.IsWindows() && string.Equals(root, ProgramDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            var marker = Path.Combine(root, SharedMarkerFileName);
            if (!File.Exists(marker))
                File.WriteAllText(marker, "BackupMirror shared data root\n");
        }
    }
}
