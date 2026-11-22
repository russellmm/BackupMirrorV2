using System.Runtime.InteropServices;

namespace BackupMirror.Core.Platform;

/// <summary>
/// Preserves POSIX permissions (and owner/group when running as root) on mirrored files,
/// so a restore from the mirror gets the same access rights as the original. No-op on Windows.
/// </summary>
public static class UnixMetadata
{
    private static readonly bool IsRoot = !OperatingSystem.IsWindows() && SafeGeteuid() == 0;

    public static void CopyFrom(string source, string destination)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        catch
        {
            // e.g. a cifs/nfs mount that ignores modes
        }

        if (!IsRoot || !TryGetOwner(source, out var uid, out var gid)) return;
        try
        {
            lchown(destination, uid, gid);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Reads st_uid/st_gid via glibc stat(); struct layout differs per architecture.</summary>
    private static bool TryGetOwner(string path, out uint uid, out uint gid)
    {
        uid = gid = 0;
        int uidOffset;
        switch (RuntimeInformation.ProcessArchitecture)
        {
            case Architecture.X64: uidOffset = 28; break;       // dev(8) ino(8) nlink(8) mode(4) uid gid
            case Architecture.Arm64: uidOffset = 24; break;     // dev(8) ino(8) mode(4) nlink(4) uid gid
            default: return false;
        }

        var buf = Marshal.AllocHGlobal(256);
        try
        {
            if (stat(path, buf) != 0) return false;
            uid = (uint)Marshal.ReadInt32(buf, uidOffset);
            gid = (uint)Marshal.ReadInt32(buf, uidOffset + 4);
            return true;
        }
        catch
        {
            return false; // libc without an exported stat symbol (glibc < 2.33)
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static uint SafeGeteuid()
    {
        try { return geteuid(); }
        catch { return uint.MaxValue; }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buf);

    [DllImport("libc", SetLastError = true)]
    private static extern int lchown([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);
}
