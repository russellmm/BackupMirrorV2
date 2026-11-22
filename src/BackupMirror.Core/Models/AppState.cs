namespace BackupMirror.Core.Models;

public sealed class AppState
{
    /// <summary>
    /// Global arming flag. False = Stop all jobs (persists across reboot).
    /// </summary>
    public bool JobsArmed { get; set; } = true;
}
