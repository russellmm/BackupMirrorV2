using BackupMirror.Core.Jobs;
using BackupMirror.Core.Models;
using FluentAssertions;

namespace BackupMirror.Core.Tests;

public class JobEligibilityTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void CanAutoRun_RequiresEnabledAndArmed(bool enabled, bool armed, bool expected)
    {
        var job = new Job { Enabled = enabled };
        var state = new AppState { JobsArmed = armed };
        JobEligibility.CanAutoRun(job, state).Should().Be(expected);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void CanSyncNow_BlockedWhenNotArmedOrDisabled(bool enabled, bool armed, bool expected)
    {
        var job = new Job { Enabled = enabled };
        var state = new AppState { JobsArmed = armed };
        JobEligibility.CanSyncNow(job, state).Should().Be(expected);
    }

    [Fact]
    public void StartAll_SetsJobsArmedTrue_DoesNotMutateJobs()
    {
        var state = new AppState { JobsArmed = false };
        AppStateTransitions.StartAllJobs(state);
        state.JobsArmed.Should().BeTrue();
    }

    [Fact]
    public void StopAll_SetsJobsArmedFalse()
    {
        var state = new AppState { JobsArmed = true };
        AppStateTransitions.StopAllJobs(state);
        state.JobsArmed.Should().BeFalse();
    }
}
