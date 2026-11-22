using BackupMirror.Core.Models;

namespace BackupMirror.Core.Jobs;

public static class JobEligibility
{
    public static bool CanAutoRun(Job job, AppState state)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(state);
        return job.Enabled && state.JobsArmed;
    }

    public static bool CanSyncNow(Job job, AppState state)
    {
        // Spec: Sync now only when globally armed and job enabled.
        return CanAutoRun(job, state);
    }
}

public static class AppStateTransitions
{
    public static void StartAllJobs(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.JobsArmed = true;
    }

    public static void StopAllJobs(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.JobsArmed = false;
    }
}
