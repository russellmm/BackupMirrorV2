namespace BackupMirror.Core.Platform;

/// <summary>
/// File-system rules that differ between Windows/macOS (case-insensitive) and Linux (case-sensitive).
/// </summary>
public static class PathRules
{
    public static bool CaseInsensitive { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparer Comparer { get; } =
        CaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } =
        CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Relative path using '/' separators (stable across platforms, used for matching and display).</summary>
    public static string ToPortable(string relativePath) => relativePath.Replace('\\', '/');

    /// <summary>Relative path using the platform separator.</summary>
    public static string ToNative(string portableRelative) =>
        portableRelative.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>True when <paramref name="child"/> is the same as or nested under <paramref name="parent"/>.</summary>
    public static bool IsSameOrUnder(string child, string parent)
    {
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (string.Equals(c, p, Comparison)) return true;
        return c.StartsWith(p + Path.DirectorySeparatorChar, Comparison);
    }
}
