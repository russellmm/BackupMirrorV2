using BackupMirror.Core.Models;

namespace BackupMirror.Core.Storage;

/// <summary>
/// Persists per-job runtime state and run history (state.json). The in-memory copy is
/// authoritative; saves are coalesced so frequent status changes don't hammer the disk.
/// </summary>
public sealed class RuntimeStore
{
    public const int MaxHistory = 500;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private StateFile _state = new();
    private int _dirty;

    public RuntimeStore(string rootDirectory)
    {
        _path = Path.Combine(rootDirectory, "state.json");
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var loaded = await AtomicJson.ReadAsync<StateFile>(_path, JsonJobStore.JsonOptions, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _state = loaded ?? new StateFile();
            foreach (var rt in _state.Jobs.Values)
            {
                // A run cannot survive a restart.
                if (rt.Status is JobStatus.Running or JobStatus.Queued)
                {
                    rt.Status = JobStatus.Cancelled;
                    rt.Message = "Interrupted by service restart.";
                }
            }
        }
    }

    public bool Exists(Guid jobId)
    {
        lock (_gate) return _state.Jobs.ContainsKey(jobId);
    }

    public JobRuntime Get(Guid jobId)
    {
        lock (_gate)
            return _state.Jobs.TryGetValue(jobId, out var rt) ? rt.Clone() : new JobRuntime();
    }

    public JobRuntime Update(Guid jobId, Action<JobRuntime> mutate)
    {
        lock (_gate)
        {
            if (!_state.Jobs.TryGetValue(jobId, out var rt))
                _state.Jobs[jobId] = rt = new JobRuntime();
            mutate(rt);
            _dirty = 1;
            return rt.Clone();
        }
    }

    public void Remove(Guid jobId)
    {
        lock (_gate)
        {
            _state.Jobs.Remove(jobId);
            _dirty = 1;
        }
    }

    public void AddRun(RunRecord run)
    {
        lock (_gate)
        {
            _state.History.Insert(0, run);
            if (_state.History.Count > MaxHistory)
                _state.History.RemoveRange(MaxHistory, _state.History.Count - MaxHistory);
            _dirty = 1;
        }
    }

    public IReadOnlyList<RunRecord> History(Guid? jobId, int limit)
    {
        lock (_gate)
            return _state.History.Where(r => jobId is null || r.JobId == jobId).Take(limit).ToList();
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StateFile snapshot;
            lock (_gate)
            {
                snapshot = new StateFile
                {
                    Jobs = _state.Jobs.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
                    History = _state.History.ToList()
                };
            }

            await AtomicJson.WriteAsync(_path, snapshot, JsonJobStore.JsonOptions, ct).ConfigureAwait(false);
        }
        catch
        {
            _dirty = 1;
            throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private sealed class StateFile
    {
        public Dictionary<Guid, JobRuntime> Jobs { get; set; } = new();
        public List<RunRecord> History { get; set; } = new();
    }
}
