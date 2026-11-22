namespace BackupMirror.Service.Runtime;

/// <summary>Main loop: schedules, realtime watchers and state persistence.</summary>
public sealed class MirrorHostedService : BackgroundService
{
    private readonly ILogger<MirrorHostedService> _logger;
    private readonly JobManager _jobs;
    private readonly ScheduleWatcher _schedule;
    private readonly RealtimeWatchManager _realtime;
    private int _reconcileRequested = 1;

    public MirrorHostedService(ILogger<MirrorHostedService> logger, JobManager jobs, ScheduleWatcher schedule,
        RealtimeWatchManager realtime)
    {
        _logger = logger;
        _jobs = jobs;
        _schedule = schedule;
        _realtime = realtime;
        _jobs.ConfigChanged += () => Interlocked.Exchange(ref _reconcileRequested, 1);
        _jobs.RunFinished += () => Interlocked.Exchange(ref _reconcileRequested, 1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Mirror loop running ({Count} job(s), armed={Armed})", _jobs.Jobs.Count, _jobs.Armed);
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _schedule.Tick();
                // Reconcile right away after a config change, otherwise every ~10 s (picks up sources that came online).
                if (Interlocked.Exchange(ref _reconcileRequested, 0) == 1 || tick % 10 == 0)
                    _realtime.Reconcile();
                await _jobs.FlushAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Host loop error");
            }

            tick++;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var job in _jobs.Jobs) _jobs.Cancel(job.Id);
        _realtime.Dispose();
        await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
        try { await _jobs.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Final state flush failed"); }
        _logger.LogInformation("Backup Mirror service stopped.");
    }
}
