using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using BackupMirror.Core.Network;
using Microsoft.Win32;

namespace BackupMirror.Service.Web;

/// <summary>
/// Network shares the signed-in users know about, so the folder picker can offer them even though the
/// service (LocalSystem) cannot see per-user mapped drive letters such as X:.
/// Sources (Windows): each loaded user profile's persistent mapped drives (HKU\&lt;sid&gt;\Network) and
/// shares opened in Explorer (MountPoints2 "##server#share"). Nothing here touches the network.
/// </summary>
public static class NetworkLocations
{
    public sealed record Share(string Unc, string? Letter, string Name);

    private static readonly Regex UserSid = new(@"^S-1-5-21-[\d-]+$", RegexOptions.Compiled);

    public static IReadOnlyList<Share> Discover(IEnumerable<string>? extraPaths = null)
    {
        var found = new Dictionary<string, Share>(StringComparer.OrdinalIgnoreCase);

        void Add(string unc, string? letter)
        {
            unc = UncShareConnector.GetShareRoot(unc.Trim());
            if (!UncShareConnector.IsUncPath(unc)) return;
            var name = unc.TrimStart('\\').Split('\\') is { Length: >= 2 } p ? $"{p[1]} on {p[0]}" : unc;
            if (found.TryGetValue(unc, out var existing))
            {
                if (existing.Letter is null && letter is not null) found[unc] = existing with { Letter = letter };
                return;
            }

            found[unc] = new Share(unc, letter, name);
        }

        if (OperatingSystem.IsWindows())
        {
            try { ReadRegistry(Add); }
            catch
            {
                // registry not readable: fall back to job paths only
            }
        }

        foreach (var p in extraPaths ?? Array.Empty<string>())
            if (UncShareConnector.IsUncPath(p)) Add(p, null);

        return found.Values
            .OrderBy(s => s.Letter is null)
            .ThenBy(s => s.Letter)
            .ThenBy(s => s.Unc, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Drive letter (e.g. "X") → UNC share root, from the users' persistent mappings.</summary>
    public static IReadOnlyDictionary<string, string> MappedDrives()
        => Discover().Where(s => s.Letter is not null)
            .GroupBy(s => s.Letter!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Unc, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// If <paramref name="path"/> uses a drive letter the service cannot see but a user has mapped,
    /// returns the equivalent UNC path (X:\Photos → \\nas\share\Photos).
    /// </summary>
    public static string? ToUncIfMapped(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var m = Regex.Match(path.Trim(), @"^([A-Za-z]):(?:[\\/](.*))?$");
        if (!m.Success) return null;
        var letter = m.Groups[1].Value.ToUpperInvariant();
        try
        {
            if (new DriveInfo(letter).IsReady) return null; // the service can see it: keep the letter
        }
        catch
        {
            // not a drive for this account
        }

        if (!MappedDrives().TryGetValue(letter, out var unc)) return null;
        var rest = m.Groups[2].Value.Replace('/', '\\').Trim('\\');
        return rest.Length == 0 ? unc : unc + "\\" + rest;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadRegistry(Action<string, string?> add)
    {
        using var users = Registry.Users;
        foreach (var sid in users.GetSubKeyNames().Where(n => UserSid.IsMatch(n)))
        {
            using (var network = users.OpenSubKey($@"{sid}\Network"))
            {
                if (network is not null)
                {
                    foreach (var letter in network.GetSubKeyNames())
                    {
                        using var k = network.OpenSubKey(letter);
                        if (k?.GetValue("RemotePath") is string remote && letter.Length == 1)
                            add(remote, letter.ToUpperInvariant());
                    }
                }
            }

            using var mounts = users.OpenSubKey($@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2");
            if (mounts is null) continue;
            foreach (var name in mounts.GetSubKeyNames().Where(n => n.StartsWith("##", StringComparison.Ordinal)))
            {
                var parts = name[2..].Split('#', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) add($@"\\{parts[0]}\{parts[1]}", null);
            }
        }
    }
}
