using BackupMirror.Core.Mirror;
using BackupMirror.Core.Models;
using BackupMirror.Core.Platform;
using Microsoft.Extensions.Options;

namespace BackupMirror.Service.Runtime;

/// <summary>
/// Watches realtime jobs. Source changes trigger a debounced sync; deletes/renames on the
/// destination (someone removed a mirrored file) trigger a repair sync. Watcher overflows
/// or errors restart the watcher and force a full resync, and a periodic catch-up pass
/// covers anything a watcher cannot see (e.g. changes made directly on a network share).
/// </summary>
public sealed class RealtimeWatchManager : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);
    // Our own temp-file renames are filtered by name; this only absorbs late events from our own deletes.
    private static readonly TimeSpan OwnWriteWindow = TimeSpan.FromSeconds(1.5);

    private readonly JobManager _jobs;
    private readonly ILogger<RealtimeWatchManager> _logger;
    private readonly TimeSpan _catchUpInterval;
    private readonly Dictionary<Guid, WatchSet> _watchers = new();
    private readonly object _gate = new();
    private DateTimeOffset _lastCatchUp = DateTimeOffset.UtcNow;

    public RealtimeWatchManager(JobManager jobs, IOptions<ServiceOptions> options, ILogger<RealtimeWatchManager> logger)
    {
        _jobs = jobs;
        _logger = logger;
        _catchUpInterval = TimeSpan.FromMinutes(Math.Max(1, options.Value.RealtimeCatchUpMinutes));
    }

    public void Reconcile()
    {
        var desired = _jobs.Jobs
            .Where(j => j.Mode == SyncMode.Realtime && _jobs.CanAutoRun(j))
            .ToDictionary(j => j.Id);

        var started = new List<Guid>();
        lock (_gate)
        {
            foreach (var (id, set) in _watchers.ToList())
            {
                if (!desired.TryGetValue(id, out var job) || set.Broken || !set.Matches(job))
                {
                    set.Dispose();
                    _watchers.Remove(id);
                    if (set.Broken) _logger.LogInformation("Restarting watcher for {Job}", set.Name);
                }
            }

            foreach (var (id, job) in desired)
            {
                // A new job's mirror folder only exists after its first sync: attach the destination watcher then.
                if (_watchers.TryGetValue(id, out var live))
                {
                    if (live.Destination is null && !live.DestinationUnsupported && SafeExists(job.DestinationPath)) AttachDestination(live, job);
                    continue;
                }

                if (!SafeExists(job.SourcePath)) continue;
                var set = TryStart(job);
                if (set is null) continue;
                _watchers[id] = set;
                started.Add(id);
            }
        }

        // First attach (startup, new job, resume, watcher restart): full sync to catch up.
        foreach (var id in started) _jobs.RequestRun(id, "startup");

        if (DateTimeOffset.UtcNow - _lastCatchUp >= _catchUpInterval)
        {
            _lastCatchUp = DateTimeOffset.UtcNow;
            foreach (var id in desired.Keys.Except(started)) _jobs.RequestRun(id, "catch-up");
        }
    }

    private WatchSet? TryStart(Job job)
    {
        var set = new WatchSet(job, this);
        try
        {
            set.Source = new FileSystemWatcher(job.SourcePath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                               | NotifyFilters.Size | NotifyFilters.CreationTime
            };
            set.Source.Changed += (_, e) => set.OnSource(e.FullPath);
            set.Source.Created += (_, e) => set.OnSource(e.FullPath);
            set.Source.Deleted += (_, e) => set.OnSource(e.FullPath);
            set.Source.Renamed += (_, e) => { set.OnSource(e.OldFullPath); set.OnSource(e.FullPath); };
            set.Source.Error += (_, e) => set.OnError(e.GetException());
            set.Source.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Cannot watch source of {Job}: {Message}", job.Name, ex.Message);
            set.Dispose();
            return null;
        }

        if (SafeExists(job.DestinationPath)) AttachDestination(set, job);
        _logger.LogInformation("Realtime watching {Name}: {Path}", job.Name, job.SourcePath);
        return set;
    }

    private void AttachDestination(WatchSet set, Job job)
    {
        try
        {
            {
                set.Destination = new FileSystemWatcher(job.DestinationPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                set.Destination.Deleted += (_, e) => set.OnDestination(e.FullPath);
                set.Destination.Renamed += (_, e) => set.OnDestination(e.OldFullPath);
                set.Destination.Error += (_, _) => { /* best effort: catch-up covers it */ };
                set.Destination.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex)
        {
            // Many network shares don't support change notifications; the catch-up pass covers them.
            _logger.LogDebug("Destination watch unavailable for {Job}: {Message}", job.Name, ex.Message);
            set.Destination?.Dispose();
            set.Destination = null;
            set.DestinationUnsupported = true;
        }
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var set in _watchers.Values) set.Dispose();
            _watchers.Clear();
        }
    }

    private sealed class WatchSet : IDisposable
    {
        private readonly RealtimeWatchManager _owner;
        private readonly Guid _id;
        private readonly string _source;
        private readonly string _dest;
        private readonly string _excludesKey;
        private readonly ExcludeMatcher _excludes;
        private readonly object _gate = new();
        private Timer? _timer;
        private DateTimeOffset _firstEvent;
        private bool _disposed;

        public WatchSet(Job job, RealtimeWatchManager owner)
        {
            _owner = owner;
            _id = job.Id;
            Name = job.Name;
            _source = job.SourcePath;
            _dest = job.DestinationPath;
            _excludesKey = string.Join('\n', job.Excludes);
            _excludes = new ExcludeMatcher(job.Excludes);
        }

        public string Name { get; }
        public FileSystemWatcher? Source { get; set; }
        public FileSystemWatcher? Destination { get; set; }
        public bool DestinationUnsupported { get; set; }
        public volatile bool Broken;

        public bool Matches(Job job) =>
            string.Equals(job.SourcePath, _source, PathRules.Comparison)
            && string.Equals(job.DestinationPath, _dest, PathRules.Comparison)
            && string.Join('\n', job.Excludes) == _excludesKey;

        public void OnSource(string fullPath)
        {
            var rel = PathRules.ToPortable(Path.GetRelativePath(_source, fullPath));
            if (_excludes.IsExcluded(rel)) return;
            Schedule();
        }

        public void OnDestination(string fullPath)
        {
            if (fullPath.EndsWith(ExcludeMatcher.TempSuffix, StringComparison.OrdinalIgnoreCase)) return;
            // Ignore our own renames/deletes while syncing.
            if (_owner._jobs.IsBusy(_id) || _owner._jobs.FinishedRecently(_id, OwnWriteWindow)) return;
            Schedule();
        }

        public void OnError(Exception ex)
        {
            _owner._logger.LogWarning("Watcher problem for {Job} ({Message}); resyncing", Name, ex.Message);
            Broken = true; // Reconcile restarts the watcher, which forces a full sync.
        }

        private void Schedule()
        {
            lock (_gate)
            {
                if (_disposed) return;
                var now = DateTimeOffset.UtcNow;
                if (_timer is null)
                {
                    _firstEvent = now;
                    _timer = new Timer(_ => Fire(), null, Quiet, Timeout.InfiniteTimeSpan);
                    return;
                }

                // Continuous activity: keep debouncing, but never wait longer than MaxWait.
                var remainingMax = MaxWait - (now - _firstEvent);
                _timer.Change(remainingMax < Quiet ? (remainingMax > TimeSpan.Zero ? remainingMax : TimeSpan.Zero) : Quiet,
                    Timeout.InfiniteTimeSpan);
            }
        }

        private void Fire()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                if (_disposed) return;
            }

            _owner._jobs.RequestRun(_id, "realtime");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }

            foreach (var w in new[] { Source, Destination })
            {
                if (w is null) continue;
                try { w.EnableRaisingEvents = false; } catch { /* ignore */ }
                w.Dispose();
            }
        }
    }
}
