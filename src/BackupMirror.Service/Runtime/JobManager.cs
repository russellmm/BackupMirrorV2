using System.Collections.Concurrent;
using System.Diagnostics;
using BackupMirror.Core.Mirror;
using BackupMirror.Core.Models;
using BackupMirror.Core.Network;
using BackupMirror.Core.Platform;
using BackupMirror.Core.Scheduling;
using BackupMirror.Core.Security;
using BackupMirror.Core.Storage;
using BackupMirror.Core.Time;
using BackupMirror.Service.Web;
using Microsoft.Extensions.Options;

namespace BackupMirror.Service.Runtime;

/// <summary>
/// Owns jobs, global state and every sync run. All mutations go through here, so job
/// config, runtime status and live events stay consistent.
/// </summary>
public sealed class JobManager
{
    private readonly JsonJobStore _store;
    private readonly RuntimeStore _runtime;
    private readonly EventHub _hub;
    private readonly IClock _clock;
    private readonly ILogger<JobManager> _logger;
    private readonly ServiceOptions _options;
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _configGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveRun> _active = new();
    private readonly ConcurrentDictionary<Guid, PendingRequest> _rerun = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastFinished = new();
    private volatile IReadOnlyList<Job> _jobs = Array.Empty<Job>();
    private AppState _app = new();

    public JobManager(JsonJobStore store, RuntimeStore runtime, EventHub hub, IClock clock,
        IOptions<ServiceOptions> options, ILogger<JobManager> logger)
    {
        _store = store;
        _runtime = runtime;
        _hub = hub;
        _clock = clock;
        _logger = logger;
        _options = options.Value;
        _concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentJobs));
    }

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public bool Armed => _app.JobsArmed;
    public IReadOnlyList<Job> Jobs => _jobs;
    public Job? Find(Guid id) => _jobs.FirstOrDefault(j => j.Id == id);
    public bool IsBusy(Guid id) => _active.ContainsKey(id);

    public bool FinishedRecently(Guid id, TimeSpan window)
        => _lastFinished.TryGetValue(id, out var t) && DateTimeOffset.UtcNow - t < window;

    public event Action? ConfigChanged;

    /// <summary>Raised after every run (the realtime manager re-checks watchers, e.g. a mirror folder that now exists).</summary>
    public event Action? RunFinished;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _runtime.LoadAsync(ct).ConfigureAwait(false);
        _app = await _store.LoadAppStateAsync(ct).ConfigureAwait(false);
        var jobs = await _store.LoadJobsAsync(ct).ConfigureAwait(false);

        // Migrate v1 runtime fields (status lived in jobs.json) into state.json, then rewrite jobs.json clean.
        var migrated = false;
        foreach (var job in jobs)
        {
            if (job.LastRunUtc is not null || job.LastStatus is not null || job.ScheduledTimeLocal is not null)
            {
                migrated = true;
                if (!_runtime.Exists(job.Id))
                {
                    _runtime.Update(job.Id, rt =>
                    {
                        rt.LastRunUtc = job.LastRunUtc;
                        rt.LastScheduledFireUtc = job.LastRunUtc;
                        rt.Status = job.LastStatus is JobStatus.Running ? JobStatus.Idle : job.LastStatus ?? JobStatus.Idle;
                        rt.Message = job.LastError;
                        if (rt.Status == JobStatus.Success) rt.LastSuccessUtc = job.LastRunUtc;
                    });
                }
            }
        }

        if (migrated)
        {
            foreach (var job in jobs)
            {
                job.CreatedUtc ??= DateTimeOffset.UtcNow;
                await _store.UpsertJobAsync(job, ct).ConfigureAwait(false);
            }

            jobs = await _store.LoadJobsAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Migrated {Count} v1 job(s) to the v2 format", jobs.Count);
        }

        _jobs = jobs;
        await _runtime.FlushAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ config

    public async Task<JobView> SaveJobAsync(Guid? id, JobInput input, CancellationToken ct)
    {
        await _configGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = id is { } eid ? Find(eid) ?? throw new KeyNotFoundException("Job not found.") : null;
            var job = existing?.Clone() ?? new Job { Id = Guid.NewGuid(), CreatedUtc = DateTimeOffset.UtcNow };

            job.Name = input.Name.Trim();
            job.Enabled = input.Enabled;
            job.SourcePath = input.SourcePath.Trim();
            job.DestinationPath = input.DestinationPath.Trim();
            job.Mode = input.Mode;
            job.Excludes = input.Excludes;
            job.Direction = input.Direction;
            job.IncludeSubfolders = input.IncludeSubfolders;
            job.IncludeHidden = input.IncludeHidden;
            job.CopyMissing = input.CopyMissing;
            job.UpdateChanged = input.UpdateChanged;
            job.DeleteExtra = input.DeleteExtra;
            job.KeepRemovedDays = Math.Clamp(input.KeepRemovedDays, 0, 3650);
            job.DeleteConfirmThreshold = Math.Clamp(input.DeleteConfirmThreshold, -1, 1_000_000);
            job.Schedule = new JobSchedule
            {
                Times = input.Times.Select(ParseTime).ToList(),
                Days = input.Days.Where(d => d is >= 0 and <= 6).Select(d => (DayOfWeek)d).ToList()
            };
            job.ScheduledTimeLocal = null;

            job.DestinationUserName = string.IsNullOrWhiteSpace(input.DestinationUserName) ? null : input.DestinationUserName.Trim();
            if (job.DestinationUserName is null)
                job.DestinationPasswordProtected = null;
            else if (input.Password is not null)
                job.DestinationPasswordProtected = input.Password.Length == 0 ? null : SecretProtector.Protect(input.Password);

            if (job.SourcePath.Length > 0 && job.DestinationPath.Length > 0 &&
                (PathRules.IsSameOrUnder(job.DestinationPath, job.SourcePath) || PathRules.IsSameOrUnder(job.SourcePath, job.DestinationPath)))
                throw new ArgumentException("Source and destination must be separate folders (neither inside the other).");

            await _store.UpsertJobAsync(job, ct).ConfigureAwait(false);
            await ReloadJobsAsync(ct).ConfigureAwait(false);

            if (existing is null)
                _runtime.Update(job.Id, rt => { rt.Status = JobStatus.Idle; rt.Message = "Never run"; });

            var saved = Find(job.Id)!;
            _logger.LogInformation("{Action} job {Name} ({Id})", existing is null ? "Created" : "Updated", saved.Name, saved.Id);
            PublishJob(saved);
            ConfigChanged?.Invoke();
            return BuildView(saved);
        }
        finally
        {
            _configGate.Release();
        }
    }

    public async Task DeleteJobAsync(Guid id, CancellationToken ct)
    {
        await _configGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Cancel(id);
            await _store.DeleteJobAsync(id, ct).ConfigureAwait(false);
            _runtime.Remove(id);
            await ReloadJobsAsync(ct).ConfigureAwait(false);
            _hub.Publish("jobRemoved", new { id });
            ConfigChanged?.Invoke();
        }
        finally
        {
            _configGate.Release();
        }
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var job = Find(id) ?? throw new KeyNotFoundException("Job not found.");
        await _configGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var copy = job.Clone();
            copy.Enabled = enabled;
            await _store.UpsertJobAsync(copy, ct).ConfigureAwait(false);
            await ReloadJobsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _configGate.Release();
        }

        if (!enabled) Cancel(id);
        PublishJob(Find(id)!);
        ConfigChanged?.Invoke();
    }

    public async Task SetArmedAsync(bool armed, CancellationToken ct)
    {
        _app.JobsArmed = armed;
        await _store.SaveAppStateAsync(_app, ct).ConfigureAwait(false);
        _logger.LogInformation(armed ? "All jobs started" : "All jobs stopped");
        if (!armed)
            foreach (var id in _active.Keys) Cancel(id);

        _hub.Publish("state", new { armed });
        foreach (var job in _jobs) PublishJob(job);
        ConfigChanged?.Invoke();

        if (armed)
        {
            // Catch up: realtime jobs resync immediately on resume (the watcher handles new attachments too).
            foreach (var job in _jobs.Where(j => j.Enabled && j.Mode == SyncMode.Realtime))
                RequestRun(job.Id, "resume");
        }
    }

    private async Task ReloadJobsAsync(CancellationToken ct)
        => _jobs = await _store.LoadJobsAsync(ct).ConfigureAwait(false);

    private static TimeOnly ParseTime(string s)
    {
        if (TimeOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var t))
            return new TimeOnly(t.Hour, t.Minute);
        throw new ArgumentException($"Invalid time '{s}'. Use HH:mm.");
    }

    // ------------------------------------------------------------------ runs

    public bool CanAutoRun(Job job) => job.Enabled && Armed;

    /// <summary>
    /// Starts a run in the background. If the job is already running the request is remembered
    /// and the job runs again right after, so changes made during a sync are never lost.
    /// </summary>
    public string? RequestRun(Guid id, string trigger, bool confirmDeletes = false) => RequestRun(id, trigger, confirmDeletes, null);

    private string? RequestRun(Guid id, string trigger, bool confirmDeletes, CompareSession? preset)
    {
        var job = Find(id);
        if (job is null) return "Job not found.";
        if (!Armed) return "All jobs are paused. Resume to sync.";
        if (!job.Enabled) return "This job is disabled.";

        if (_active.ContainsKey(id))
        {
            if (preset is not null) return "This mirror is syncing right now. Compare again when it has finished.";
            _rerun.AddOrUpdate(id,
                _ => new PendingRequest(trigger, confirmDeletes),
                (_, prev) => new PendingRequest(prev.Trigger, prev.ConfirmDeletes || confirmDeletes));
            return null;
        }

        var run = new ActiveRun(trigger, new CancellationTokenSource()) { Preset = preset };
        if (!_active.TryAdd(id, run))
        {
            if (preset is not null) return "This mirror is syncing right now. Compare again when it has finished.";
            _rerun.TryAdd(id, new PendingRequest(trigger, confirmDeletes));
            return null;
        }

        _ = Task.Run(() => RunLoopAsync(id, run, confirmDeletes));
        return null;
    }

    public bool Cancel(Guid id)
    {
        _rerun.TryRemove(id, out _);
        if (_active.TryGetValue(id, out var run))
        {
            run.Cts.Cancel();
            return true;
        }

        return false;
    }

    public void SkipPendingDeletes(Guid id)
    {
        var rt = _runtime.Update(id, rt =>
        {
            rt.PendingDeletes.Clear();
            rt.PendingDeleteCount = 0;
            if (rt.Status == JobStatus.WaitingConfirm)
            {
                rt.Status = JobStatus.Warning;
                rt.Message = "Deletes skipped. Destination keeps the extra files for now.";
            }
        });
        if (Find(id) is { } job) PublishJob(job);
    }

    private async Task RunLoopAsync(Guid id, ActiveRun run, bool confirmDeletes)
    {
        try
        {
            while (true)
            {
                var job = Find(id);
                if (job is null || !CanAutoRun(job)) break;

                await ExecuteRunAsync(job, run, confirmDeletes).ConfigureAwait(false);

                if (run.Cts.IsCancellationRequested || !_rerun.TryRemove(id, out var next)) break;
                run.Preset = null;
                run.Reset(next.Trigger);
                confirmDeletes = next.ConfirmDeletes;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run loop crashed for {JobId}", id);
        }
        finally
        {
            _active.TryRemove(id, out _);
            _lastFinished[id] = DateTimeOffset.UtcNow;
            run.Cts.Dispose();
            if (Find(id) is { } j) PublishJob(j);
            RunFinished?.Invoke();
            // A request that slipped in between the loop check and removal.
            if (_rerun.TryRemove(id, out var late)) RequestRun(id, late.Trigger, late.ConfirmDeletes);
        }
    }

    private async Task ExecuteRunAsync(Job job, ActiveRun run, bool confirmDeletes)
    {
        var ct = run.Cts.Token;
        var record = new RunRecord
        {
            JobId = job.Id,
            JobName = job.Name,
            Trigger = run.Trigger,
            StartedUtc = DateTimeOffset.UtcNow
        };

        SetStatus(job, JobStatus.Queued, "Waiting for a free slot…");
        var acquired = false;
        try
        {
            await _concurrency.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            run.StartedUtc = DateTimeOffset.UtcNow;
            run.Stopwatch.Restart();
            SetStatus(job, JobStatus.Running, "Scanning folders…");
            run.Progress = new MirrorProgress("scanning", 0, 0, 0, 0, null);
            PublishProgress(job.Id, run);

            var preset = run.Preset;
            var direction = preset?.Direction ?? job.Direction;
            var (from, to) = direction == SyncDirection.DestinationToSource
                ? (job.DestinationPath, job.SourcePath)
                : (job.SourcePath, job.DestinationPath);

            var connectError = EnsureDestinationAccess(job);
            if (connectError is not null)
            {
                Finish(job, record, JobStatus.Degraded, connectError);
                return;
            }

            if (!Directory.Exists(from))
            {
                Finish(job, record, JobStatus.Degraded, $"Folder is not reachable: {from}");
                return;
            }

            if (job.KeepRemovedDays > 0)
            {
                var purged = TrashBin.Purge(to, job.KeepRemovedDays, _clock.LocalNow);
                if (purged > 0) _logger.LogInformation("Purged {Count} expired removed-file folder(s) for {Job}", purged, job.Name);
            }

            var options = BuildOptions(job, preset?.Input, forRun: true);

            MirrorPlan plan;
            if (preset is not null)
            {
                plan = preset.Plan; // exactly what the user reviewed and kept checked
                confirmDeletes = true;
            }
            else
            {
                try
                {
                    plan = await Task.Run(() => new MirrorPlanner().Plan(from, to, options, ct), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Finish(job, record, JobStatus.Degraded, ex.Message);
                    return;
                }
            }

            record.FilesScanned = plan.SourceFileCount;
            // Synchronous reporter (Progress<T> would post out of order to the thread pool).
            var reporter = new SyncProgress(p => { run.Progress = p; PublishProgress(job.Id, run); });

            if (!plan.IsEmpty || plan.StaleTempFiles.Count > 0)
                SetStatus(job, JobStatus.Running, $"Copying {plan.Copies.Count:N0} file(s)…");

            var result = await new MirrorEngine()
                .ExecuteAsync(to, plan, confirmDeletes, options, reporter, ct)
                .ConfigureAwait(false);

            record.FilesCopied = result.CopiesApplied;
            record.BytesCopied = result.BytesCopied;
            record.FilesDeleted = result.DeletesApplied;
            record.DeletesHeld = result.DeletesHeldForConfirm;
            record.Errors = result.Errors.Count + plan.Warnings.Count;
            record.ErrorSamples = plan.Warnings.Concat(result.Errors).Take(20).ToList();

            if (result.RequiresDeleteConfirmation)
            {
                _runtime.Update(job.Id, rt =>
                {
                    rt.PendingDeleteCount = plan.Deletes.Count;
                    rt.PendingDeletes = plan.Deletes.Take(1000).Select(d => d.RelativePath).ToList();
                });
                Finish(job, record, JobStatus.WaitingConfirm,
                    $"{plan.Deletes.Count:N0} file(s) would be deleted from the {(direction == SyncDirection.DestinationToSource ? "source" : "destination")}. Review to continue.");
                return;
            }

            _runtime.Update(job.Id, rt => { rt.PendingDeletes.Clear(); rt.PendingDeleteCount = 0; });

            if (result.Errors.Count > 0 && result.CopiesApplied == 0 && result.DeletesApplied == 0 && plan.Copies.Count > 0)
                Finish(job, record, JobStatus.Failed, result.Errors[0]);
            else if (result.Errors.Count > 0 || plan.Warnings.Count > 0)
                Finish(job, record, JobStatus.Warning,
                    $"Finished with {record.Errors:N0} problem(s): {plan.Warnings.Concat(result.Errors).First()}");
            else
                Finish(job, record, JobStatus.Success, Summary(record));
        }
        catch (OperationCanceledException)
        {
            Finish(job, record, JobStatus.Cancelled, Armed ? "Cancelled." : "Stopped: all jobs paused.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run failed for {Job}", job.Name);
            Finish(job, record, JobStatus.Failed, ex.Message);
        }
        finally
        {
            if (acquired) _concurrency.Release();
            run.Progress = null;
        }
    }

    private static string Summary(RunRecord r)
    {
        if (r.FilesCopied == 0 && r.FilesDeleted == 0) return "Up to date.";
        var parts = new List<string>();
        if (r.FilesCopied > 0) parts.Add($"{r.FilesCopied:N0} copied ({FormatBytes(r.BytesCopied)})");
        if (r.FilesDeleted > 0) parts.Add($"{r.FilesDeleted:N0} deleted");
        return string.Join(", ", parts) + ".";
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    private void Finish(Job job, RunRecord record, JobStatus status, string message)
    {
        record.FinishedUtc = DateTimeOffset.UtcNow;
        record.Status = status;
        record.Message = message;

        _runtime.Update(job.Id, rt =>
        {
            rt.Status = status;
            rt.Message = message;
            rt.LastRunUtc = record.FinishedUtc;
            rt.LastRun = record;
            if (status == JobStatus.Success) rt.LastSuccessUtc = record.FinishedUtc;
        });

        // Keep history meaningful: skip no-op realtime/catch-up passes.
        var noop = status == JobStatus.Success && record.FilesCopied == 0 && record.FilesDeleted == 0
                   && record.Trigger is "realtime" or "catch-up" or "resume" or "startup";
        if (!noop)
        {
            _runtime.AddRun(record);
            _hub.Publish("run", record);
        }

        var level = status is JobStatus.Failed or JobStatus.Degraded ? LogLevel.Warning : LogLevel.Information;
        if (!noop) _logger.Log(level, "Job {Name} [{Trigger}] {Status}: {Message}", job.Name, record.Trigger, status, message);
        PublishJob(job);
    }

    private void SetStatus(Job job, JobStatus status, string message)
    {
        _runtime.Update(job.Id, rt => { rt.Status = status; rt.Message = message; });
        PublishJob(job);
    }

    private string? EnsureDestinationAccess(Job job)
    {
        if (!UncShareConnector.IsUncPath(job.DestinationPath))
            return null;

        var (user, password, error) = ResolveShareSignIn(job, job.DestinationPath);
        return error ?? UncShareConnector.EnsureConnected(job.DestinationPath, user, password);
    }

    /// <summary>
    /// The sign-in for a share: the job's own, else one saved on another job for the same share or server.
    /// Called before every run, compare and browse, so access is re-established after a reboot.
    /// </summary>
    public (string? User, string? Password, string? Error) ResolveShareSignIn(Job? job, string path)
    {
        var donor = ShareSignIn.FindDonor(_jobs, job, path);
        if (donor is null)
            return (null, null, null);

        string? password = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(donor.DestinationPasswordProtected))
                password = SecretProtector.Unprotect(donor.DestinationPasswordProtected);
        }
        catch (Exception ex)
        {
            return (null, null, $"Could not decrypt the share password saved on '{donor.Name}' ({ex.Message}). Re-enter it on that job.");
        }

        if (!ReferenceEquals(donor, job))
            _logger.LogDebug("Using the share sign-in saved on '{Donor}' for {Path}", donor.Name, path);
        return (donor.DestinationUserName, password, null);
    }

    // ------------------------------------------------------------------ views

    public JobView BuildView(Job job)
    {
        var rt = _runtime.Get(job.Id);
        _active.TryGetValue(job.Id, out var run);
        var status = rt.Status;
        var message = rt.Message;
        if (run is null && status is JobStatus.Running or JobStatus.Queued) status = JobStatus.Idle;
        if (run is null && !Armed && status != JobStatus.WaitingConfirm) status = JobStatus.Stopped;

        DateTimeOffset? next = null;
        if (job.Mode == SyncMode.Scheduled && job.Enabled && Armed)
        {
            var n = ScheduleCalculator.NextOccurrence(job.EffectiveSchedule, _clock.LocalNow);
            if (n is { } dt) next = new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)).ToUniversalTime();
        }

        ProgressDto? progress = null;
        if (run?.Progress is { } p)
        {
            var secs = Math.Max(0.5, run.Stopwatch.Elapsed.TotalSeconds);
            progress = new ProgressDto(p.Phase, p.FilesDone, p.FilesTotal, p.BytesDone, p.BytesTotal, p.CurrentFile,
                run.StartedUtc, p.BytesDone / secs);
        }

        var schedule = job.EffectiveSchedule;
        return new JobView(
            job.Id, job.Name, job.Enabled, job.SourcePath, job.DestinationPath, job.DestinationUserName,
            !string.IsNullOrEmpty(job.DestinationPasswordProtected), job.Mode,
            new ScheduleDto(schedule.Times.Select(t => t.ToString("HH:mm")).ToList(), schedule.Days.Select(d => (int)d).ToList()),
            job.Excludes, job.DeleteConfirmThreshold, job.CreatedUtc,
            status, message, rt.LastRunUtc, rt.LastSuccessUtc, next, rt.LastRun, rt.PendingDeleteCount, progress,
            run?.Trigger, job.Direction, job.IncludeSubfolders, job.IncludeHidden, job.CopyMissing, job.UpdateChanged,
            job.DeleteExtra, job.KeepRemovedDays);
    }

    public IReadOnlyList<string> PendingDeletes(Guid id) => _runtime.Get(id).PendingDeletes;
    public JobRuntime Runtime(Guid id) => _runtime.Get(id);
    public void MarkScheduledFire(Guid id) => _runtime.Update(id, rt => rt.LastScheduledFireUtc = DateTimeOffset.UtcNow);
    public IReadOnlyList<RunRecord> History(Guid? jobId, int limit) => _runtime.History(jobId, limit);
    public Task FlushAsync(CancellationToken ct) => _runtime.FlushAsync(ct);

    // ------------------------------------------------------------------ compare / preview

    private readonly ConcurrentDictionary<Guid, CompareSession> _compares = new();
    private const int MaxCompareItems = 200_000;

    private MirrorOptions BuildOptions(Job job, CompareInput? o, bool forRun)
    {
        var to = (o?.Direction ?? job.Direction) == SyncDirection.DestinationToSource ? job.SourcePath : job.DestinationPath;
        return new MirrorOptions
        {
            Excludes = new ExcludeMatcher(job.Excludes),
            DeleteConfirmThreshold = job.DeleteConfirmThreshold,
            IncludeSubfolders = o?.IncludeSubfolders ?? job.IncludeSubfolders,
            IncludeHidden = o?.IncludeHidden ?? job.IncludeHidden,
            CopyMissing = o?.CopyMissing ?? job.CopyMissing,
            UpdateChanged = o?.UpdateChanged ?? job.UpdateChanged,
            DeleteExtra = o?.DeleteExtra ?? job.DeleteExtra,
            TrashDirectory = forRun && job.KeepRemovedDays > 0 ? TrashBin.RunFolder(to, _clock.LocalNow) : null
        };
    }

    /// <summary>
    /// Plans a sync without changing anything, so the user can see (and trim) every change first.
    /// The plan is kept for 30 minutes so "Apply" runs exactly what was reviewed.
    /// </summary>
    public async Task<CompareResult> CompareAsync(Guid jobId, CompareInput input, CancellationToken ct)
    {
        var job = Find(jobId) ?? throw new KeyNotFoundException("Job not found.");
        if (input.CopyMissing == false && input.UpdateChanged == false && input.DeleteExtra == false)
            throw new ArgumentException("Choose at least one kind of change to compare.");

        var direction = input.Direction ?? job.Direction;
        var (from, to) = direction == SyncDirection.DestinationToSource
            ? (job.DestinationPath, job.SourcePath)
            : (job.SourcePath, job.DestinationPath);

        if (EnsureDestinationAccess(job) is { } connectError) throw new ArgumentException(connectError);
        if (!Directory.Exists(from)) throw new ArgumentException($"Folder is not reachable: {from}");

        var sw = Stopwatch.StartNew();
        var lastReport = 0L;
        var options = BuildOptions(job, input, forRun: false);
        options = new MirrorOptions
        {
            Excludes = options.Excludes, DeleteConfirmThreshold = options.DeleteConfirmThreshold,
            IncludeSubfolders = options.IncludeSubfolders, IncludeHidden = options.IncludeHidden,
            CopyMissing = options.CopyMissing, UpdateChanged = options.UpdateChanged, DeleteExtra = options.DeleteExtra,
            ScanProgress = n =>
            {
                if (sw.ElapsedMilliseconds - Interlocked.Read(ref lastReport) < 250) return;
                Interlocked.Exchange(ref lastReport, sw.ElapsedMilliseconds);
                _hub.Publish("compareProgress", new { jobId, scanned = n });
            }
        };

        MirrorPlan plan;
        try
        {
            plan = await Task.Run(() => new MirrorPlanner().Plan(from, to, options, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new ArgumentException(ex.Message);
        }

        // Items in execution order, like OpenDiff: create folders, copy, update, delete, remove folders.
        string Rel(string full) => PathRules.ToPortable(Path.GetRelativePath(plan.DestinationRoot, full));
        var items = new List<CompareItem>();
        var opsByIndex = new List<object>();
        void Add(int k, string rel, long size, DateTime? t, object op)
        {
            items.Add(new CompareItem(k, rel, size, t is { } u && u != default ? new DateTimeOffset(u, TimeSpan.Zero) : null));
            opsByIndex.Add(op);
        }

        foreach (var d in plan.DirectoriesToCreate.OrderBy(Rel, PathRules.Comparer)) Add(0, Rel(d), 0, null, d);
        foreach (var c in plan.Copies.Where(c => c.Kind == FileOpKind.Copy)) Add(1, c.RelativePath, c.Length, c.LastWriteTimeUtc, c);
        foreach (var c in plan.Copies.Where(c => c.Kind == FileOpKind.Update)) Add(2, c.RelativePath, c.Length, c.LastWriteTimeUtc, c);
        foreach (var d in plan.Deletes) Add(3, d.RelativePath, d.Length, d.LastWriteTimeUtc, d);
        foreach (var d in plan.DirectoriesToRemove.OrderBy(Rel, PathRules.Comparer)) Add(4, Rel(d), 0, null, d);

        var counts = new CompareCounts(
            plan.DirectoriesToCreate.Count, plan.MissingCount, plan.UpdateCount, plan.Deletes.Count, plan.DirectoriesToRemove.Count,
            plan.Copies.Where(c => c.Kind == FileOpKind.Copy).Sum(c => c.Length),
            plan.Copies.Where(c => c.Kind == FileOpKind.Update).Sum(c => c.Length),
            plan.Deletes.Sum(d => d.Length));

        var session = new CompareSession(Guid.NewGuid(), jobId, direction, plan, input, opsByIndex, DateTimeOffset.UtcNow);
        foreach (var old in _compares.Values.Where(s => s.JobId == jobId || DateTimeOffset.UtcNow - s.CreatedUtc > TimeSpan.FromMinutes(30)).ToList())
            _compares.TryRemove(old.Id, out _);
        _compares[session.Id] = session;

        var truncated = items.Count > MaxCompareItems;
        _logger.LogInformation("Compared {Job} ({From} -> {To}): {Copy} copy, {Update} update, {Delete} delete, {Dirs} folders in {Secs:0.0}s",
            job.Name, from, to, counts.Copy, counts.Update, counts.Delete, counts.CreateFolders + counts.RemoveFolders, sw.Elapsed.TotalSeconds);

        return new CompareResult(session.Id, jobId, direction, from, to, plan.SourceFileCount, plan.DestinationFileCount, counts,
            plan.Warnings.Take(50).ToList(), truncated ? items.Take(MaxCompareItems).ToList() : items, truncated,
            options.IncludeSubfolders, options.IncludeHidden, options.CopyMissing, options.UpdateChanged, options.DeleteExtra,
            job.KeepRemovedDays, sw.Elapsed.TotalSeconds);
    }

    /// <summary>Runs a reviewed comparison, minus the items the user unchecked.</summary>
    public string? ApplyCompare(Guid compareId, IReadOnlyCollection<int> skip)
    {
        if (!_compares.TryGetValue(compareId, out var session))
            return "This comparison has expired. Compare again.";
        if (DateTimeOffset.UtcNow - session.CreatedUtc > TimeSpan.FromMinutes(30))
        {
            _compares.TryRemove(compareId, out _);
            return "This comparison is older than 30 minutes. Compare again so you apply what is there now.";
        }

        var skipFiles = new HashSet<FileOp>();
        var skipDirs = new HashSet<string>(PathRules.Comparer);
        foreach (var i in skip)
        {
            if (i < 0 || i >= session.Ops.Count) continue;
            switch (session.Ops[i])
            {
                case FileOp op: skipFiles.Add(op); break;
                case string dir: skipDirs.Add(dir); break;
            }
        }

        var trimmed = session with { Plan = session.Plan.Without(skipFiles, skipDirs) };
        if (trimmed.Plan.IsEmpty) return "Nothing is checked, so there is nothing to apply.";
        var err = RequestRun(session.JobId, "compare", confirmDeletes: true, trimmed);
        if (err is null) _compares.TryRemove(compareId, out _);
        return err;
    }

    public sealed record CompareSession(Guid Id, Guid JobId, SyncDirection Direction, MirrorPlan Plan, CompareInput Input,
        IReadOnlyList<object> Ops, DateTimeOffset CreatedUtc);

    private void PublishJob(Job job) => _hub.Publish("job", BuildView(Find(job.Id) ?? job));

    private void PublishProgress(Guid id, ActiveRun run)
    {
        if (run.Progress is not { } p) return;
        var secs = Math.Max(0.5, run.Stopwatch.Elapsed.TotalSeconds);
        _hub.Publish("progress", new
        {
            jobId = id,
            progress = new ProgressDto(p.Phase, p.FilesDone, p.FilesTotal, p.BytesDone, p.BytesTotal, p.CurrentFile,
                run.StartedUtc, p.BytesDone / secs)
        });
    }

    private sealed record PendingRequest(string Trigger, bool ConfirmDeletes);

    private sealed class ActiveRun
    {
        public ActiveRun(string trigger, CancellationTokenSource cts)
        {
            Trigger = trigger;
            Cts = cts;
        }

        public string Trigger { get; private set; }
        public CancellationTokenSource Cts { get; }
        public CompareSession? Preset { get; set; }
        public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
        public volatile MirrorProgress? Progress;

        public void Reset(string trigger)
        {
            Trigger = trigger;
            StartedUtc = DateTimeOffset.UtcNow;
            Stopwatch.Restart();
        }
    }

    private sealed class SyncProgress(Action<MirrorProgress> handler) : IProgress<MirrorProgress>
    {
        public void Report(MirrorProgress value) => handler(value);
    }
}
