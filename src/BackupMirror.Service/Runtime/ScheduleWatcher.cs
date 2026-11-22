using BackupMirror.Core.Models;
using BackupMirror.Core.Scheduling;
using BackupMirror.Core.Time;
using Microsoft.Extensions.Options;

namespace BackupMirror.Service.Runtime;

/// <summary>
/// Fires scheduled jobs at their configured times/weekdays. A run missed while the machine
/// was off is caught up once at startup if it is within ScheduleCatchUpHours.
/// </summary>
public sealed class ScheduleWatcher
{
    private readonly JobManager _jobs;
    private readonly IClock _clock;
    private readonly ILogger<ScheduleWatcher> _logger;
    private readonly TimeSpan _maxCatchUp;

    public ScheduleWatcher(JobManager jobs, IClock clock, IOptions<ServiceOptions> options, ILogger<ScheduleWatcher> logger)
    {
        _jobs = jobs;
        _clock = clock;
        _logger = logger;
        _maxCatchUp = TimeSpan.FromHours(Math.Max(0, options.Value.ScheduleCatchUpHours));
    }

    public void Tick()
    {
        if (!_jobs.Armed) return;
        var localNow = _clock.LocalNow;

        foreach (var job in _jobs.Jobs)
        {
            if (job.Mode != SyncMode.Scheduled || !_jobs.CanAutoRun(job)) continue;
            var schedule = job.EffectiveSchedule;
            if (schedule.Times.Count == 0) continue;

            var rt = _jobs.Runtime(job.Id);
            var baseline = rt.LastScheduledFireUtc ?? job.CreatedUtc ?? DateTimeOffset.UtcNow;
            if (!ScheduleCalculator.IsDue(schedule, localNow, baseline, _maxCatchUp)) continue;

            _logger.LogInformation("Schedule firing {Name}", job.Name);
            _jobs.MarkScheduledFire(job.Id);
            _jobs.RequestRun(job.Id, "schedule");
        }
    }
}
