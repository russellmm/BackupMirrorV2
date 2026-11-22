namespace BackupMirror.Core.Models;

/// <summary>Per-job runtime state owned by the service (persisted in state.json).</summary>
public sealed class JobRuntime
{
    public JobStatus Status { get; set; } = JobStatus.Idle;
    public string? Message { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public DateTimeOffset? LastScheduledFireUtc { get; set; }
    public RunRecord? LastRun { get; set; }

    /// <summary>Destination files a held run wants to delete (relative, '/' separated, capped).</summary>
    public List<string> PendingDeletes { get; set; } = new();
    public int PendingDeleteCount { get; set; }

    public JobRuntime Clone()
    {
        var c = (JobRuntime)MemberwiseClone();
        c.PendingDeletes = new List<string>(PendingDeletes);
        return c;
    }
}

public sealed class RunRecord
{
    public Guid RunId { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string JobName { get; set; } = "";
    public string Trigger { get; set; } = "manual";
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public JobStatus Status { get; set; }
    public int FilesScanned { get; set; }
    public int FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    public int FilesDeleted { get; set; }
    public int DeletesHeld { get; set; }
    public int Errors { get; set; }
    public string? Message { get; set; }
    public List<string> ErrorSamples { get; set; } = new();
}
