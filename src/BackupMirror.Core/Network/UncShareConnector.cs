using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BackupMirror.Core.Network;

/// <summary>
/// Ensures a UNC share is reachable, optionally with explicit credentials (WNetAddConnection2).
/// </summary>
public static class UncShareConnector
{
    private const int ResourceTypeDisk = 0x1;
    private const int ErrorAlreadyAssigned = 85;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorSuccess = 0;

    public static bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var p = path.Trim().Replace('/', '\\');
        return p.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// \\server\share\folder\file → \\server\share
    /// </summary>
    public static string GetShareRoot(string uncPath)
    {
        var p = uncPath.Trim().Replace('/', '\\').TrimEnd('\\');
        if (!p.StartsWith(@"\\", StringComparison.Ordinal))
            return p;

        var rest = p[2..];
        var parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return p;
        return $@"\\{parts[0]}\{parts[1]}";
    }

    /// <summary>
    /// Connects to the UNC share root. No-op for non-UNC paths.
    /// Returns null on success, or an error message.
    /// </summary>
    public static string? EnsureConnected(string path, string? userName, string? password)
    {
        // Linux/macOS: shares are mounted by the OS (cifs/nfs in /etc/fstab); nothing to connect.
        if (!OperatingSystem.IsWindows() || !IsUncPath(path))
            return null;

        var share = GetShareRoot(path);
        if (string.IsNullOrWhiteSpace(userName))
        {
            try
            {
                if (Directory.Exists(share) || Directory.Exists(path))
                    return null;
            }
            catch (Exception ex)
            {
                return $"Cannot access '{share}' without credentials: {ex.Message}";
            }

            return $"Cannot access '{share}'. Add share username/password on the job, " +
                   "or reinstall the service with -RunAsCurrentUser if your Windows login already has access.";
        }

        var result = Connect(share, userName, password ?? "");
        if (result == ErrorSuccess || result == ErrorAlreadyAssigned)
            return null;

        if (result == ErrorSessionCredentialConflict)
        {
            Disconnect(share);
            result = Connect(share, userName, password ?? "");
            if (result == ErrorSuccess || result == ErrorAlreadyAssigned)
                return null;
        }

        var msg = new Win32Exception(result).Message;
        return $"Failed to connect to '{share}' as '{userName}' (Win32 {result}): {msg}";
    }

    private static int Connect(string share, string userName, string password)
    {
        var nr = new NetResource
        {
            dwType = ResourceTypeDisk,
            lpRemoteName = share
        };
        return WNetAddConnection2(ref nr, password, userName, 0);
    }

    private static void Disconnect(string share)
    {
        WNetCancelConnection2(share, 0, true);
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }
}
