using BackupMirror.Core.Network;
using BackupMirror.Core.Platform;

namespace BackupMirror.Service.Web;

/// <summary>Server-side folder browsing for the dashboard's folder picker (works headless/over SSH).</summary>
public static class FileBrowser
{
    private static readonly HashSet<string> PseudoFs = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "devtmpfs", "devpts", "tmpfs", "cgroup", "cgroup2", "securityfs", "pstore", "bpf",
        "debugfs", "tracefs", "configfs", "fusectl", "mqueue", "hugetlbfs", "autofs", "binfmt_misc", "rpc_pipefs",
        "efivarfs", "squashfs", "overlay", "nsfs", "ramfs", "fuse.snapfuse", "fuse.portal", "fuse.gvfsd-fuse"
    };

    public static IReadOnlyList<RootEntry> Roots(IEnumerable<string>? knownPaths = null)
    {
        var roots = new List<RootEntry>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    var label = !string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.VolumeLabel
                        : d.DriveType switch { DriveType.Fixed => "Local Disk", DriveType.Network => "Network", DriveType.Removable => "Removable", DriveType.CDRom => "Optical", _ => "Drive" };
                    roots.Add(new RootEntry($"{label} ({d.Name.TrimEnd('\\')})", d.RootDirectory.FullName,
                        d.DriveType == DriveType.Network ? "network" : d.DriveType == DriveType.Removable ? "removable" : "drive",
                        d.AvailableFreeSpace, d.TotalSize));
                }
                catch
                {
                    // drive went away
                }
            }

            // Shares the signed-in users have mapped or opened (the service can't see their drive letters).
            // Listed without contacting the network, so an offline server never slows the picker down.
            foreach (var share in NetworkLocations.Discover(knownPaths))
            {
                var parts = share.Unc.TrimStart('\\').Split('\\');
                var label = share.Letter is { } l ? $"{parts[^1]} ({l}:)" : $"{parts[^1]}";
                roots.Add(new RootEntry(label, share.Unc, "share", null, null) { Detail = parts[0] });
            }

            return roots;
        }

        roots.Add(Root("Filesystem", "/", "drive"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home)) roots.Add(Root("Home", home, "home"));

        try
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ');
                if (parts.Length < 3) continue;
                var mount = parts[1].Replace("\\040", " ");
                var fs = parts[2];
                if (mount == "/" || PseudoFs.Contains(fs) || mount.StartsWith("/proc") || mount.StartsWith("/sys")
                    || mount.StartsWith("/dev") || mount.StartsWith("/run") || mount.StartsWith("/snap")
                    || mount.StartsWith("/boot") || mount.StartsWith("/usr/lib/wsl")) continue;
                var kind = fs is "cifs" or "smb3" or "nfs" or "nfs4" or "sshfs" or "fuse.sshfs" or "9p" or "drvfs" ? "network" : "drive";
                roots.Add(Root(Path.GetFileName(mount) is { Length: > 0 } n ? n : mount, mount, kind));
            }
        }
        catch
        {
            // not Linux /proc
        }

        foreach (var common in new[] { "/mnt", "/media", "/srv" })
            if (Directory.Exists(common) && roots.All(r => r.Path != common))
                roots.Add(Root(common, common, "folder"));

        return roots.GroupBy(r => r.Path).Select(g => g.First()).ToList();
    }

    private static RootEntry Root(string name, string path, string kind)
    {
        long? free = null, total = null;
        try
        {
            var d = new DriveInfo(path);
            free = d.AvailableFreeSpace;
            total = d.TotalSize;
        }
        catch
        {
            // not all mounts report space
        }

        return new RootEntry(name, path, kind, free, total);
    }

    public static DirListing List(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new DirListing("", null, Array.Empty<DirEntry>(), "No path.");
        try
        {
            var full = Path.GetFullPath(path.Trim());
            var info = new DirectoryInfo(full);
            var parent = info.Parent?.FullName;
            if (UncShareConnector.IsUncPath(full) && parent is not null && UncShareConnector.GetShareRoot(full).Length >= full.TrimEnd('\\').Length)
                parent = null; // \\server\share has no browsable parent
            if (!info.Exists)
            {
                // Windows reports "does not exist" for shares it can't open; enumerate to surface the real reason
                // (access denied, wrong password, server offline) so the picker can ask for a sign-in.
                if (UncShareConnector.IsUncPath(full))
                {
                    try { Directory.EnumerateFileSystemEntries(full).Take(1).ToList(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return new DirListing(full, parent, Array.Empty<DirEntry>(), ex is UnauthorizedAccessException
                            ? "Access denied. The service can't open this share without a sign-in."
                            : ex.Message);
                    }
                }

                return new DirListing(full, parent, Array.Empty<DirEntry>(), "Folder does not exist.");
            }

            var dirs = info.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                .Where(d => !d.Name.StartsWith('$'))
                .OrderBy(d => d.Name, PathRules.Comparer)
                .Take(5000)
                .Select(d => new DirEntry(d.Name, d.FullName))
                .ToList();
            return new DirListing(full, parent, dirs, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return new DirListing(path, null, Array.Empty<DirEntry>(), ex.Message);
        }
    }

    public static PathCheckResult Check(string path, bool destination)
    {
        if (string.IsNullOrWhiteSpace(path)) return new PathCheckResult(false, false, null, "Enter a folder.", null, null);
        try
        {
            var full = Path.GetFullPath(path.Trim());
            long? free = null, total = null;
            try
            {
                if (!UncShareConnector.IsUncPath(full))
                {
                    var d = new DriveInfo(Path.GetPathRoot(full)!);
                    free = d.AvailableFreeSpace;
                    total = d.TotalSize;
                }
            }
            catch
            {
                // ignore
            }

            if (Directory.Exists(full))
            {
                if (!destination)
                {
                    Directory.EnumerateFileSystemEntries(full).Take(1).ToList();
                    return new PathCheckResult(true, true, null, "Folder found and readable.", free, total);
                }

                var probe = Path.Combine(full, $".bm-write-test-{Guid.NewGuid():N}");
                try
                {
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    return new PathCheckResult(true, true, true, "Folder found and writable.", free, total);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new PathCheckResult(false, true, false, $"Folder exists but is not writable: {ex.Message}", free, total);
                }
            }

            if (!destination) return new PathCheckResult(false, false, null, "Folder not found.", free, total);

            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
            while (parent is not null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
            return parent is null
                ? new PathCheckResult(false, false, null, "Neither the folder nor any parent folder is reachable.", free, total)
                : new PathCheckResult(true, false, null, "Folder will be created on the first sync.", free, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return new PathCheckResult(false, false, null, ex.Message, null, null);
        }
    }
}
