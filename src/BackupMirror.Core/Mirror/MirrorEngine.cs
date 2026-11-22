using System.Diagnostics;
using BackupMirror.Core.Platform;

namespace BackupMirror.Core.Mirror;

public sealed record MirrorResult(
    int CopiesApplied,
    int DeletesApplied,
    int DeletesHeldForConfirm,
    int Skipped,
    bool RequiresDeleteConfirmation,
    IReadOnlyList<string> Errors)
{
    public long BytesCopied { get; init; }
    public static MirrorResult Empty { get; } = new(0, 0, 0, 0, false, Array.Empty<string>());
}

/// <summary>
/// Applies a <see cref="MirrorPlan"/>. Each file is copied to a temp file beside its target and
/// renamed into place, so an interrupted run never leaves a truncated file under the real name.
/// Transient I/O failures (locks, network blips) are retried with back-off.
/// </summary>
public sealed class MirrorEngine
{
    private const int BufferSize = 1 << 20;

    public Task<MirrorResult> ExecuteAsync(
        string sourceRoot,
        string destinationRoot,
        MirrorPlan plan,
        bool confirmDeletes,
        CancellationToken ct = default)
        => ExecuteAsync(destinationRoot, plan, confirmDeletes, new MirrorOptions(), null, ct);

    public Task<MirrorResult> ExecuteAsync(
        string destinationRoot,
        MirrorPlan plan,
        bool confirmDeletes,
        MirrorOptions options,
        IProgress<MirrorProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Execute(destinationRoot, plan, confirmDeletes, options, progress, ct), ct);
    }

    private static MirrorResult Execute(
        string destinationRoot,
        MirrorPlan plan,
        bool confirmDeletes,
        MirrorOptions options,
        IProgress<MirrorProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destinationRoot);

        var errors = new List<string>();
        var copiesApplied = 0;
        var deletesApplied = 0;
        var skipped = 0;
        long bytesDone = 0;
        var bytesTotal = plan.CopyBytes;
        var filesTotal = plan.Copies.Count;
        var lastReport = Stopwatch.StartNew();

        void Report(string phase, int done, string? current, bool force = false)
        {
            if (progress is null) return;
            if (!force && lastReport.ElapsedMilliseconds < 200) return;
            lastReport.Restart();
            progress.Report(new MirrorProgress(phase, done, filesTotal, bytesDone, bytesTotal, current));
        }

        foreach (var temp in plan.StaleTempFiles)
            TryDeleteFile(temp);

        foreach (var dir in plan.DirectoriesToCreate)
        {
            ct.ThrowIfCancellationRequested();
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Create folder failed {dir}: {ex.Message}");
            }
        }

        Report("copying", 0, null, force: true);
        var index = 0;
        foreach (var op in plan.Copies)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            Report("copying", index - 1, op.RelativePath);
            var before = bytesDone;
            var outcome = WithRetries(options, ct, () =>
            {
                bytesDone = before;
                CopyAtomic(op, options, ct, n =>
                {
                    bytesDone += n;
                    Report("copying", index - 1, op.RelativePath);
                });
            });

            switch (outcome)
            {
                case null:
                    copiesApplied++;
                    break;
                case FileNotFoundException or DirectoryNotFoundException when !File.Exists(op.SourceFullPath):
                    // Source vanished mid-run (normal for realtime jobs); the next run picks up the delete.
                    bytesDone = before + op.Length;
                    break;
                default:
                    bytesDone = before + op.Length;
                    skipped++;
                    errors.Add($"Copy failed {op.RelativePath}: {outcome.Message}");
                    break;
            }
        }

        Report("copying", filesTotal, null, force: true);

        var holdDeletes = DeleteSafetyGate.RequiresConfirmation(plan, options.DeleteConfirmThreshold) && !confirmDeletes;
        if (!holdDeletes)
        {
            var d = 0;
            foreach (var op in plan.Deletes)
            {
                ct.ThrowIfCancellationRequested();
                d++;
                Report("deleting", filesTotal, op.RelativePath);
                var outcome = WithRetries(options, ct, () =>
                {
                    if (!File.Exists(op.DestinationFullPath)) return;
                    ClearReadOnly(op.DestinationFullPath);
                    if (options.TrashDirectory is { } trash) MoveToTrash(op.DestinationFullPath, op.RelativePath, trash);
                    else File.Delete(op.DestinationFullPath);
                });

                if (outcome is null) deletesApplied++;
                else
                {
                    skipped++;
                    errors.Add($"Delete failed {op.RelativePath}: {outcome.Message}");
                }
            }

            foreach (var dir in plan.DirectoriesToRemove)
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not fatal: a folder that still has excluded/locked content stays.
                }
            }
        }

        Report("done", filesTotal, null, force: true);

        return new MirrorResult(
            copiesApplied,
            deletesApplied,
            holdDeletes ? plan.Deletes.Count : 0,
            skipped,
            RequiresDeleteConfirmation: holdDeletes,
            errors)
        { BytesCopied = bytesDone };
    }

    /// <summary>Moves a file about to be deleted or overwritten into the trash folder, keeping its relative path.</summary>
    private static void MoveToTrash(string fullPath, string portableRelative, string trashDirectory)
    {
        var target = Path.Combine(trashDirectory, PathRules.ToNative(portableRelative));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(fullPath, target, overwrite: true);
    }

    private static void CopyAtomic(FileOp op, MirrorOptions options, CancellationToken ct, Action<int> onBytes)
    {
        var destDir = Path.GetDirectoryName(op.DestinationFullPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        var temp = op.DestinationFullPath + ExcludeMatcher.TempSuffix;
        try
        {
            DateTime srcWrite;
            // FileShare.ReadWrite|Delete lets us copy files other programs have open for writing.
            using (var src = new FileStream(op.SourceFullPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan))
            using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                if (src.CanSeek) dst.SetLength(src.Length);
                var buffer = new byte[BufferSize];
                int read;
                long total = 0;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, read);
                    total += read;
                    onBytes(read);
                }

                dst.SetLength(total);
                dst.Flush(flushToDisk: true);
                srcWrite = File.GetLastWriteTimeUtc(op.SourceFullPath);
            }

            File.SetLastWriteTimeUtc(temp, srcWrite);
            Platform.UnixMetadata.CopyFrom(op.SourceFullPath, temp);
            if (File.Exists(op.DestinationFullPath))
            {
                ClearReadOnly(op.DestinationFullPath);
                if (options.TrashDirectory is { } trash) MoveToTrash(op.DestinationFullPath, op.RelativePath, trash);
            }

            File.Move(temp, op.DestinationFullPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    /// <summary>Runs <paramref name="action"/>, retrying transient failures. Returns null on success.</summary>
    private static Exception? WithRetries(MirrorOptions options, CancellationToken ct, Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var permanent = ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException;
                if (permanent || attempt >= options.CopyRetries) return ex;
                var delay = TimeSpan.FromMilliseconds(options.RetryBaseDelay.TotalMilliseconds * Math.Pow(3, attempt));
                if (ct.WaitHandle.WaitOne(delay)) ct.ThrowIfCancellationRequested();
            }
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
