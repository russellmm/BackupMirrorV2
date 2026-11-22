using System.Text.Json;
using System.Text.Json.Serialization;
using BackupMirror.Core.Models;

namespace BackupMirror.Core.Storage;

/// <summary>
/// Job configuration (jobs.json) and global state (appstate.json).
/// Writes are atomic (temp file + rename) and keep a .bak of the previous version; a corrupt
/// file falls back to the backup instead of being silently replaced with an empty list.
/// </summary>
public sealed class JsonJobStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _root;
    private readonly string _jobsPath;
    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonJobStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = rootDirectory;
        _jobsPath = Path.Combine(_root, "jobs.json");
        _statePath = Path.Combine(_root, "appstate.json");
        Directory.CreateDirectory(_root);
    }

    public string RootDirectory => _root;

    public async Task<IReadOnlyList<Job>> LoadJobsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadJobsUnlockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertJobAsync(Job job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ValidateJob(job);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobs = (await ReadJobsUnlockedAsync(ct).ConfigureAwait(false)).ToList();
            var index = jobs.FindIndex(j => j.Id == job.Id);
            var copy = Normalize(job);
            if (index >= 0)
            {
                copy.CreatedUtc ??= jobs[index].CreatedUtc;
                jobs[index] = copy;
            }
            else
            {
                copy.CreatedUtc ??= DateTimeOffset.UtcNow;
                jobs.Add(copy);
            }

            await AtomicJson.WriteAsync(_jobsPath, jobs.Select(Normalize).ToList(), JsonOptions, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteJobAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobs = (await ReadJobsUnlockedAsync(ct).ConfigureAwait(false)).ToList();
            jobs.RemoveAll(j => j.Id == id);
            await AtomicJson.WriteAsync(_jobsPath, jobs.Select(Normalize).ToList(), JsonOptions, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppState> LoadAppStateAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await AtomicJson.ReadAsync<AppState>(_statePath, JsonOptions, ct).ConfigureAwait(false)
                   ?? new AppState { JobsArmed = true };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAppStateAsync(AppState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AtomicJson.WriteAsync(_statePath, state, JsonOptions, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<Job>> ReadJobsUnlockedAsync(CancellationToken ct)
        => await AtomicJson.ReadAsync<List<Job>>(_jobsPath, JsonOptions, ct).ConfigureAwait(false) ?? new List<Job>();

    public static void ValidateJob(Job job)
    {
        if (job.Id == Guid.Empty)
            throw new ArgumentException("Job Id is required.", nameof(job));
        if (string.IsNullOrWhiteSpace(job.Name))
            throw new ArgumentException("Job name is required.", nameof(job));
        if (string.IsNullOrWhiteSpace(job.SourcePath))
            throw new ArgumentException("Source path is required.", nameof(job));
        if (string.IsNullOrWhiteSpace(job.DestinationPath))
            throw new ArgumentException("Destination path is required.", nameof(job));
        if (job.Direction == SyncDirection.DestinationToSource && job.Mode != SyncMode.ManualOnly)
            throw new ArgumentException("\"Sync to Destination\" changes the source folder, so it is only allowed for manual jobs.", nameof(job));
        if (!job.CopyMissing && !job.UpdateChanged && !job.DeleteExtra)
            throw new ArgumentException("Choose at least one kind of change (copy, update or delete).", nameof(job));
        if (job.KeepRemovedDays is < 0 or > 3650)
            throw new ArgumentException("Keep removed files for 0–3650 days.", nameof(job));
        if (job.Mode == SyncMode.Scheduled && job.EffectiveSchedule.Times.Count == 0)
            throw new ArgumentException("Scheduled jobs need at least one time of day.", nameof(job));
    }

    /// <summary>Trimmed copy with v1 fields folded into the v2 model and cleared.</summary>
    private static Job Normalize(Job job)
    {
        var copy = job.Clone();
        copy.Name = job.Name.Trim();
        copy.SourcePath = job.SourcePath.Trim();
        copy.DestinationPath = job.DestinationPath.Trim();
        copy.DestinationUserName = string.IsNullOrWhiteSpace(job.DestinationUserName) ? null : job.DestinationUserName.Trim();
        copy.Schedule = job.EffectiveSchedule.Clone();
        copy.Schedule.Times = copy.Schedule.Times.Distinct().Order().ToList();
        copy.Schedule.Days = copy.Schedule.Days.Distinct().Order().ToList();
        copy.Excludes = job.Excludes.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToList();
        copy.ScheduledTimeLocal = null;
        copy.LastRunUtc = null;
        copy.LastStatus = null;
        copy.LastError = null;
        return copy;
    }
}

/// <summary>Crash-safe JSON file helpers.</summary>
public static class AtomicJson
{
    public static async Task<T?> ReadAsync<T>(string path, JsonSerializerOptions options, CancellationToken ct) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                await using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length == 0) continue;
                var value = await JsonSerializer.DeserializeAsync<T>(stream, options, ct).ConfigureAwait(false);
                if (value is not null)
                {
                    if (candidate != path) PreserveCorrupt(path);
                    return value;
                }
            }
            catch (JsonException)
            {
                // try the backup next
            }
        }

        if (File.Exists(path) && new FileInfo(path).Length > 0)
            throw new InvalidDataException($"{path} is unreadable and no valid backup exists. Fix or remove the file.");
        return null;
    }

    public static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, options, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", overwrite: true); }
            catch (IOException) { /* backup is best effort */ }
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void PreserveCorrupt(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
        }
        catch
        {
            // best effort
        }
    }
}
