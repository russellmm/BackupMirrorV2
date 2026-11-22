using BackupMirror.Core.Models;

namespace BackupMirror.Service.Web;

public sealed record ScheduleDto(IReadOnlyList<string> Times, IReadOnlyList<int> Days);

public sealed record ProgressDto(
    string Phase,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    string? CurrentFile,
    DateTimeOffset StartedUtc,
    double BytesPerSecond);

public sealed record JobView(
    Guid Id,
    string Name,
    bool Enabled,
    string SourcePath,
    string DestinationPath,
    string? DestinationUserName,
    bool HasPassword,
    SyncMode Mode,
    ScheduleDto Schedule,
    IReadOnlyList<string> Excludes,
    int DeleteConfirmThreshold,
    DateTimeOffset? CreatedUtc,
    JobStatus Status,
    string? Message,
    DateTimeOffset? LastRunUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? NextRunUtc,
    RunRecord? LastRun,
    int PendingDeleteCount,
    ProgressDto? Progress,
    string? Trigger,
    SyncDirection Direction,
    bool IncludeSubfolders,
    bool IncludeHidden,
    bool CopyMissing,
    bool UpdateChanged,
    bool DeleteExtra,
    int KeepRemovedDays);

public sealed class JobInput
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public string? DestinationUserName { get; set; }

    /// <summary>null = keep the saved password, "" = clear it, otherwise the new password.</summary>
    public string? Password { get; set; }

    public SyncMode Mode { get; set; } = SyncMode.ManualOnly;
    public List<string> Times { get; set; } = new();
    public List<int> Days { get; set; } = new();
    public List<string> Excludes { get; set; } = new();
    public int DeleteConfirmThreshold { get; set; } = 10;
    public SyncDirection Direction { get; set; } = SyncDirection.SourceToDestination;
    public bool IncludeSubfolders { get; set; } = true;
    public bool IncludeHidden { get; set; } = true;
    public bool CopyMissing { get; set; } = true;
    public bool UpdateChanged { get; set; } = true;
    public bool DeleteExtra { get; set; } = true;
    public int KeepRemovedDays { get; set; }
}

/// <summary>Compare with the job's settings, optionally overridden for this one comparison.</summary>
public sealed class CompareInput
{
    public SyncDirection? Direction { get; set; }
    public bool? IncludeSubfolders { get; set; }
    public bool? IncludeHidden { get; set; }
    public bool? CopyMissing { get; set; }
    public bool? UpdateChanged { get; set; }
    public bool? DeleteExtra { get; set; }
}

/// <summary>Action codes in execution order: 0 create folder, 1 copy, 2 update, 3 delete, 4 remove folder.</summary>
public sealed record CompareItem(int K, string P, long S, DateTimeOffset? T);

public sealed record CompareCounts(int CreateFolders, int Copy, int Update, int Delete, int RemoveFolders,
    long CopyBytes, long UpdateBytes, long DeleteBytes);

public sealed record CompareResult(
    Guid Id,
    Guid JobId,
    SyncDirection Direction,
    string From,
    string To,
    int FromFiles,
    int ToFiles,
    CompareCounts Counts,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CompareItem> Items,
    bool Truncated,
    bool IncludeSubfolders,
    bool IncludeHidden,
    bool CopyMissing,
    bool UpdateChanged,
    bool DeleteExtra,
    int KeepRemovedDays,
    double Seconds);

public sealed class ApplyInput
{
    /// <summary>Indexes (into CompareResult.Items) the user unchecked.</summary>
    public List<int> Skip { get; set; } = new();
}

public sealed record StatusView(
    bool Armed,
    string Version,
    string Machine,
    string Os,
    string DataRoot,
    DateTimeOffset StartedUtc,
    bool RemoteClient,
    bool RemoteAccessEnabled,
    IReadOnlyList<JobView> Jobs);

public sealed record ArmInput(bool Armed);
public sealed record EnableInput(bool Enabled);
public sealed record LoginInput(string Key);
public sealed record PathCheckInput(string Path, string Kind, string? UserName, string? Password, Guid? JobId);
public sealed record PathCheckResult(bool Ok, bool Exists, bool? Writable, string Message, long? FreeBytes, long? TotalBytes)
{
    /// <summary>A path the service can use instead (e.g. the UNC path behind a mapped drive letter).</summary>
    public string? Suggestion { get; init; }
}
public sealed record MkdirInput(string Path);
public sealed record DirEntry(string Name, string Path);
public sealed record RootEntry(string Name, string Path, string Kind, long? FreeBytes, long? TotalBytes)
{
    /// <summary>Secondary text (e.g. the server of a network share).</summary>
    public string? Detail { get; init; }
}

/// <summary>Folder listing request; credentials let the service open a password-protected share.</summary>
public sealed record ListInput(string Path, string? UserName, string? Password, Guid? JobId);
public sealed record DirListing(string Path, string? Parent, IReadOnlyList<DirEntry> Dirs, string? Error);
public sealed record ApiError(string Error);
