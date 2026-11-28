using BackupMirror.Core.Models;

namespace BackupMirror.Core.Network;

/// <summary>
/// Picks the share sign-in to use for a UNC path. A job's own sign-in wins; a job without one borrows the
/// sign-in saved on another job for the same share (or, failing that, the same server). The service runs as
/// LocalSystem and its share connections don't survive a reboot or a dropped session, so every run reconnects
/// with these saved credentials instead of relying on a connection some other job happened to make.
/// </summary>
public static class ShareSignIn
{
    public static Job? FindDonor(IEnumerable<Job> jobs, Job? self, string path)
    {
        if (!UncShareConnector.IsUncPath(path))
            return null;
        if (self is not null && !string.IsNullOrWhiteSpace(self.DestinationUserName))
            return self;

        var share = UncShareConnector.GetShareRoot(path);
        var server = GetServer(path);
        Job? sameServer = null;
        foreach (var j in jobs)
        {
            if (string.IsNullOrWhiteSpace(j.DestinationUserName) || !UncShareConnector.IsUncPath(j.DestinationPath))
                continue;
            if (string.Equals(UncShareConnector.GetShareRoot(j.DestinationPath), share, StringComparison.OrdinalIgnoreCase))
                return j;
            if (sameServer is null && string.Equals(GetServer(j.DestinationPath), server, StringComparison.OrdinalIgnoreCase))
                sameServer = j;
        }

        return sameServer;
    }

    /// <summary>\server\share\x → server</summary>
    public static string GetServer(string uncPath)
    {
        var p = uncPath.Trim().Replace('/', '\\').TrimStart('\\');
        var i = p.IndexOf('\\');
        return i < 0 ? p : p[..i];
    }
}
