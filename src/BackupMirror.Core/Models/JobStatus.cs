namespace BackupMirror.Core.Models;

public enum JobStatus
{
    Idle = 0,
    Running = 1,
    WaitingConfirm = 2,
    Degraded = 3,
    Failed = 4,
    Success = 5,
    Stopped = 6,
    Queued = 7,
    Cancelled = 8,
    Warning = 9
}
