namespace BackupMirror.Core.Mirror;

public enum FileOpKind
{
    /// <summary>File missing on the target: copied in.</summary>
    Copy,

    /// <summary>File on both sides but different (size or time): target overwritten.</summary>
    Update,

    /// <summary>File only on the target: removed.</summary>
    Delete
}

/// <summary>One planned file change. "Source" is the folder being copied from, "Destination" the folder being changed.</summary>
public sealed record FileOp(FileOpKind Kind, string RelativePath, string SourceFullPath, string DestinationFullPath, long Length = 0)
{
    public DateTime LastWriteTimeUtc { get; init; }
}

public sealed record MirrorPlan(
    IReadOnlyList<FileOp> Copies,
    IReadOnlyList<FileOp> Deletes)
{
    /// <summary>Source directories missing on the destination (full destination paths, parents first).</summary>
    public IReadOnlyList<string> DirectoriesToCreate { get; init; } = Array.Empty<string>();

    /// <summary>Destination directories absent from the source (full paths, deepest first).</summary>
    public IReadOnlyList<string> DirectoriesToRemove { get; init; } = Array.Empty<string>();

    /// <summary>Stale temp files from interrupted copies; always cleaned, never counted as deletes.</summary>
    public IReadOnlyList<string> StaleTempFiles { get; init; } = Array.Empty<string>();

    /// <summary>Folders/files that could not be read while scanning (job continues without them).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public int SourceFileCount { get; init; }
    public int DestinationFileCount { get; init; }

    /// <summary>
    /// True when part of the source could not be read. Deletes are then suppressed under the unreadable
    /// folders so an unreadable folder never wipes its mirror.
    /// </summary>
    public bool SourceIncomplete { get; init; }

    public string SourceRoot { get; init; } = "";
    public string DestinationRoot { get; init; } = "";

    public long CopyBytes => Copies.Sum(c => c.Length);
    public int MissingCount => Copies.Count(c => c.Kind == FileOpKind.Copy);
    public int UpdateCount => Copies.Count(c => c.Kind == FileOpKind.Update);

    public bool IsEmpty => Copies.Count == 0 && Deletes.Count == 0
                           && DirectoriesToCreate.Count == 0 && DirectoriesToRemove.Count == 0;

    /// <summary>A copy of this plan without the given operations (used when the user unchecks items in Compare).</summary>
    public MirrorPlan Without(ISet<FileOp> skipFiles, ISet<string> skipDirs) => this with
    {
        Copies = Copies.Where(c => !skipFiles.Contains(c)).ToList(),
        Deletes = Deletes.Where(d => !skipFiles.Contains(d)).ToList(),
        DirectoriesToCreate = DirectoriesToCreate.Where(d => !skipDirs.Contains(d)).ToList(),
        DirectoriesToRemove = DirectoriesToRemove.Where(d => !skipDirs.Contains(d)).ToList()
    };
}

public sealed class MirrorOptions
{
    public ExcludeMatcher Excludes { get; init; } = ExcludeMatcher.None;

    /// <summary>Timestamps within this window are treated as equal (FAT/SMB/cifs rounding).</summary>
    public TimeSpan TimestampTolerance { get; init; } = TimeSpan.FromSeconds(2);

    public int DeleteConfirmThreshold { get; init; } = DeleteSafetyGate.DefaultFileThreshold;

    public int CopyRetries { get; init; } = 3;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Recurse into subfolders (off = only files directly in the root).</summary>
    public bool IncludeSubfolders { get; init; } = true;

    /// <summary>
    /// Include hidden/system entries. When off, an entry is left out if it (or a folder above it) is
    /// hidden or system on EITHER side, so a file hidden on one side only never looks missing.
    /// On Linux/macOS dot-files count as hidden.
    /// </summary>
    public bool IncludeHidden { get; init; } = true;

    public bool CopyMissing { get; init; } = true;
    public bool UpdateChanged { get; init; } = true;
    public bool DeleteExtra { get; init; } = true;

    /// <summary>
    /// When set, files that would be deleted or overwritten on the target are moved here instead
    /// (keeping their relative path), so they can be recovered.
    /// </summary>
    public string? TrashDirectory { get; init; }

    /// <summary>Called with the running count of entries scanned (both sides).</summary>
    public Action<int>? ScanProgress { get; init; }
}

public sealed record MirrorProgress(
    string Phase,
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    string? CurrentFile);
