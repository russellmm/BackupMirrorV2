using System.Globalization;

namespace BackupMirror.Core.Mirror;

/// <summary>
/// "Keep removed files": files a run deletes or overwrites are moved to
/// <c>&lt;target&gt;/.backupmirror-trash/&lt;yyyy-MM-dd_HHmmss&gt;/&lt;relative path&gt;</c> and purged after N days.
/// Works on local disks, network shares and Linux alike (unlike the Windows Recycle Bin).
/// </summary>
public static class TrashBin
{
    private const string StampFormat = "yyyy-MM-dd_HHmmss";

    public static string RunFolder(string targetRoot, DateTime localNow)
        => Path.Combine(targetRoot, ExcludeMatcher.TrashFolderName, localNow.ToString(StampFormat, CultureInfo.InvariantCulture));

    /// <summary>Deletes run folders older than <paramref name="keepDays"/>. Returns how many were removed.</summary>
    public static int Purge(string targetRoot, int keepDays, DateTime localNow)
    {
        var root = Path.Combine(targetRoot, ExcludeMatcher.TrashFolderName);
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(dir), StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
                continue; // not ours: leave it
            if (localNow - stamp < TimeSpan.FromDays(keepDays)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try again next run
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch
        {
            // best effort
        }

        return removed;
    }
}
