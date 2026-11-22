using System.Text.Json.Serialization;

namespace BackupMirror.Core.Models;

/// <summary>
/// Job configuration. Runtime status lives in <see cref="JobRuntime"/> so editing a job
/// can never race with a sync writing its status.
/// </summary>
public sealed class Job
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";

    /// <summary>Optional share username (DOMAIN\user or user). Windows UNC destinations only.</summary>
    public string? DestinationUserName { get; set; }

    /// <summary>Protected password (DPAPI on Windows, AES-GCM key file on Linux). Never plaintext.</summary>
    public string? DestinationPasswordProtected { get; set; }

    public SyncMode Mode { get; set; } = SyncMode.ManualOnly;

    /// <summary>Times of day and weekdays for <see cref="SyncMode.Scheduled"/> jobs.</summary>
    public JobSchedule? Schedule { get; set; }

    /// <summary>Glob patterns excluded on both sides (e.g. <c>*.tmp</c>, <c>node_modules</c>, <c>cache/**</c>).</summary>
    public List<string> Excludes { get; set; } = new();

    /// <summary>Hold deletes for confirmation when a run would delete more than this many files.</summary>
    public int DeleteConfirmThreshold { get; set; } = 10;

    public DateTimeOffset? CreatedUtc { get; set; }

    /// <summary>
    /// Which folder is changed. <see cref="SyncDirection.SourceToDestination"/> ("Sync to Source") makes the
    /// destination match the source (backup). <see cref="SyncDirection.DestinationToSource"/>
    /// ("Sync to Destination") makes the source match the destination (restore) and is only allowed for manual jobs.
    /// </summary>
    public SyncDirection Direction { get; set; } = SyncDirection.SourceToDestination;

    public bool IncludeSubfolders { get; set; } = true;
    public bool IncludeHidden { get; set; } = true;
    public bool CopyMissing { get; set; } = true;
    public bool UpdateChanged { get; set; } = true;
    public bool DeleteExtra { get; set; } = true;

    /// <summary>Days to keep files a sync deletes or overwrites (in .backupmirror-trash on the changed side); 0 = off.</summary>
    public int KeepRemovedDays { get; set; }

    /// <summary>Folder being read for this job's direction.</summary>
    [JsonIgnore]
    public string FromPath => Direction == SyncDirection.DestinationToSource ? DestinationPath : SourcePath;

    /// <summary>Folder being changed for this job's direction.</summary>
    [JsonIgnore]
    public string ToPath => Direction == SyncDirection.DestinationToSource ? SourcePath : DestinationPath;

    // ---- Legacy v1 fields: read for migration, never written. ----

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeOnly? ScheduledTimeLocal { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastRunUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JobStatus? LastStatus { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastError { get; set; }

    /// <summary>Schedule with v1 <see cref="ScheduledTimeLocal"/> folded in.</summary>
    [JsonIgnore]
    public JobSchedule EffectiveSchedule
    {
        get
        {
            if (Schedule is { Times.Count: > 0 }) return Schedule;
            if (ScheduledTimeLocal is { } t) return new JobSchedule { Times = { t } };
            return Schedule ?? new JobSchedule();
        }
    }

    public Job Clone()
    {
        var copy = (Job)MemberwiseClone();
        copy.Excludes = new List<string>(Excludes);
        copy.Schedule = Schedule?.Clone();
        return copy;
    }
}

public sealed class JobSchedule
{
    /// <summary>Local times of day.</summary>
    public List<TimeOnly> Times { get; set; } = new();

    /// <summary>Days the schedule applies; empty means every day.</summary>
    public List<DayOfWeek> Days { get; set; } = new();

    public JobSchedule Clone() => new() { Times = new(Times), Days = new(Days) };
}
