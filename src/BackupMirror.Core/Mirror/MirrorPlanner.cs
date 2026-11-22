using BackupMirror.Core.Platform;

namespace BackupMirror.Core.Mirror;

/// <summary>
/// Compares a source and destination tree and produces the operations that make the destination
/// match the source, limited by <see cref="MirrorOptions"/> (copy missing / update changed / delete extra,
/// subfolders, hidden/system). Unreadable folders are reported as warnings instead of failing the job,
/// and nothing under an unreadable source folder is ever deleted from the destination.
/// </summary>
public sealed class MirrorPlanner
{
    public MirrorPlan Plan(string sourceRoot, string destinationRoot)
        => Plan(sourceRoot, destinationRoot, new MirrorOptions());

    public MirrorPlan Plan(string sourceRoot, string destinationRoot, MirrorOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(options);

        sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        destinationRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));

        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Source not found: {sourceRoot}");
        if (PathRules.IsSameOrUnder(destinationRoot, sourceRoot) || PathRules.IsSameOrUnder(sourceRoot, destinationRoot))
            throw new InvalidOperationException("Source and destination must not contain each other.");

        var warnings = new List<string>();
        var scanned = 0;
        void Tick()
        {
            scanned++;
            if (options.ScanProgress is { } cb && scanned % 500 == 0) cb(scanned);
        }

        var src = Inventory(sourceRoot, options, warnings, ct, collectTemp: false, Tick);
        var dst = Directory.Exists(destinationRoot)
            ? Inventory(destinationRoot, options, warnings, ct, collectTemp: true, Tick)
            : Tree.Empty;
        options.ScanProgress?.Invoke(scanned);

        // Hidden/system filtering happens after both scans so a file hidden on one side only
        // (Samba marks dot-files hidden, for example) is skipped rather than looking missing.
        bool Hidden(string rel) => !options.IncludeHidden && (src.Hidden.Contains(rel) || dst.Hidden.Contains(rel));

        bool UnderUnreadable(string rel) =>
            src.Unreadable.Any(u => rel.Equals(u, PathRules.Comparison) || rel.StartsWith(u + "/", PathRules.Comparison));

        var copies = new List<FileOp>();
        foreach (var (rel, s) in src.Files)
        {
            if (Hidden(rel)) continue;
            var target = Path.Combine(destinationRoot, PathRules.ToNative(rel));
            if (!dst.Files.TryGetValue(rel, out var d))
            {
                if (options.CopyMissing)
                    copies.Add(new FileOp(FileOpKind.Copy, rel, s.FullPath, target, s.Length) { LastWriteTimeUtc = s.LastWriteTimeUtc });
            }
            else if (options.UpdateChanged && NeedsCopy(s, d, options.TimestampTolerance))
            {
                copies.Add(new FileOp(FileOpKind.Update, rel, s.FullPath, target, s.Length) { LastWriteTimeUtc = s.LastWriteTimeUtc });
            }
        }

        var deletes = new List<FileOp>();
        if (options.DeleteExtra)
        {
            foreach (var (rel, d) in dst.Files)
            {
                if (src.Files.ContainsKey(rel) || Hidden(rel) || UnderUnreadable(rel)) continue;
                deletes.Add(new FileOp(FileOpKind.Delete, rel, Path.Combine(sourceRoot, PathRules.ToNative(rel)), d.FullPath, d.Length)
                {
                    LastWriteTimeUtc = d.LastWriteTimeUtc
                });
            }
        }

        var dirsToCreate = options.CopyMissing
            ? src.Dirs
                .Where(rel => !dst.Dirs.Contains(rel) && !Hidden(rel))
                .OrderBy(r => r.Length)
                .Select(rel => Path.Combine(destinationRoot, PathRules.ToNative(rel)))
                .ToList()
            : new List<string>();

        var dirsToRemove = options.DeleteExtra
            ? dst.Dirs
                .Where(rel => !src.Dirs.Contains(rel) && !UnderUnreadable(rel) && !Hidden(rel))
                .OrderByDescending(r => r.Length)
                .Select(rel => Path.Combine(destinationRoot, PathRules.ToNative(rel)))
                .ToList()
            : new List<string>();

        copies.Sort((a, b) => PathRules.Comparer.Compare(a.RelativePath, b.RelativePath));
        deletes.Sort((a, b) => PathRules.Comparer.Compare(a.RelativePath, b.RelativePath));

        return new MirrorPlan(copies, deletes)
        {
            DirectoriesToCreate = dirsToCreate,
            DirectoriesToRemove = dirsToRemove,
            StaleTempFiles = dst.TempFiles,
            Warnings = warnings,
            SourceFileCount = src.Files.Count,
            DestinationFileCount = dst.Files.Count,
            SourceIncomplete = src.Unreadable.Count > 0,
            SourceRoot = sourceRoot,
            DestinationRoot = destinationRoot
        };
    }

    private static bool NeedsCopy(FileMeta source, FileMeta dest, TimeSpan tolerance)
        => source.Length != dest.Length
           || (source.LastWriteTimeUtc - dest.LastWriteTimeUtc).Duration() > tolerance;

    private static bool IsHiddenAttr(FileSystemInfo e)
    {
        try { return (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
        catch { return false; }
    }

    private static Tree Inventory(string root, MirrorOptions options, List<string> warnings, CancellationToken ct,
        bool collectTemp, Action tick)
    {
        var tree = new Tree();
        var stack = new Stack<(string Full, string Rel, bool Hidden)>();
        stack.Push((root, "", false));
        var enumOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dirFull, dirRel, dirHidden) = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dirFull).EnumerateFileSystemInfos("*", enumOptions).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (dirRel.Length == 0) throw; // root itself unreadable: the run must fail
                tree.Unreadable.Add(dirRel);
                warnings.Add($"Cannot read folder '{dirFull}': {ex.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                var rel = dirRel.Length == 0 ? entry.Name : dirRel + "/" + entry.Name;
                if (collectTemp && entry is FileInfo && entry.Name.EndsWith(ExcludeMatcher.TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    tree.TempFiles.Add(entry.FullName);
                    continue;
                }

                if (options.Excludes.IsExcluded(rel)) continue;
                tick();
                var hidden = dirHidden || IsHiddenAttr(entry);
                if (hidden) tree.Hidden.Add(rel);

                if (entry is DirectoryInfo dir)
                {
                    if (!options.IncludeSubfolders) continue;
                    // Never follow junctions/symlinked folders: avoids loops and escaping the job root.
                    if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || dir.LinkTarget is not null) continue;
                    tree.Dirs.Add(rel);
                    stack.Push((dir.FullName, rel, hidden));
                }
                else if (entry is FileInfo file)
                {
                    try
                    {
                        tree.Files[rel] = new FileMeta(file.FullName, file.Length, file.LastWriteTimeUtc);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"Cannot read file '{file.FullName}': {ex.Message}");
                        tree.Unreadable.Add(rel);
                    }
                }
            }
        }

        return tree;
    }

    internal static string NormalizeRelative(string relativePath) => PathRules.ToPortable(relativePath);

    private sealed record FileMeta(string FullPath, long Length, DateTime LastWriteTimeUtc);

    private sealed class Tree
    {
        public static readonly Tree Empty = new();
        public Dictionary<string, FileMeta> Files { get; } = new(PathRules.Comparer);
        public HashSet<string> Dirs { get; } = new(PathRules.Comparer);
        public HashSet<string> Hidden { get; } = new(PathRules.Comparer);
        public List<string> Unreadable { get; } = new();
        public List<string> TempFiles { get; } = new();
    }
}
