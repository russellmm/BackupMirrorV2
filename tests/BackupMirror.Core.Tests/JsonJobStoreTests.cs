using BackupMirror.Core.Models;
using BackupMirror.Core.Storage;
using FluentAssertions;

namespace BackupMirror.Core.Tests;

public class JsonJobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly JsonJobStore _store;

    public JsonJobStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BackupMirrorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new JsonJobStore(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task LoadAsync_WhenEmpty_ReturnsDefaultAppState_JobsArmedTrue()
    {
        var state = await _store.LoadAppStateAsync();
        state.JobsArmed.Should().BeTrue();
    }

    [Fact]
    public async Task LoadJobsAsync_WhenEmpty_ReturnsEmptyList()
    {
        var jobs = await _store.LoadJobsAsync();
        jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertJob_ThenReload_PersistsJob()
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Name = "Docs",
            Enabled = true,
            SourcePath = @"C:\Data\Docs",
            DestinationPath = @"\\server\backup\Docs",
            Mode = SyncMode.Scheduled,
            ScheduledTimeLocal = new TimeOnly(2, 0)
        };

        await _store.UpsertJobAsync(job);

        var reloaded = new JsonJobStore(_root);
        var jobs = await reloaded.LoadJobsAsync();
        jobs.Should().ContainSingle();
        jobs[0].Name.Should().Be("Docs");
        jobs[0].Mode.Should().Be(SyncMode.Scheduled);
        jobs[0].EffectiveSchedule.Times.Should().Equal(new TimeOnly(2, 0));
        jobs[0].ScheduledTimeLocal.Should().BeNull("v1 field is folded into Schedule on save");
        jobs[0].SourcePath.Should().Be(@"C:\Data\Docs");
        jobs[0].DestinationPath.Should().Be(@"\\server\backup\Docs");
    }

    [Fact]
    public async Task UpsertJob_EditSchedule_UpdatesExistingWithoutDuplicate()
    {
        var id = Guid.NewGuid();
        await _store.UpsertJobAsync(new Job
        {
            Id = id,
            Name = "Docs",
            Enabled = true,
            SourcePath = @"C:\Data\Docs",
            DestinationPath = @"\\server\backup\Docs",
            Mode = SyncMode.Scheduled,
            ScheduledTimeLocal = new TimeOnly(2, 0)
        });

        await _store.UpsertJobAsync(new Job
        {
            Id = id,
            Name = "Docs",
            Enabled = true,
            SourcePath = @"C:\Data\Docs",
            DestinationPath = @"\\server\backup\Docs",
            Mode = SyncMode.Scheduled,
            ScheduledTimeLocal = new TimeOnly(3, 0)
        });

        var jobs = await _store.LoadJobsAsync();
        jobs.Should().ContainSingle();
        jobs[0].EffectiveSchedule.Times.Should().Equal(new TimeOnly(3, 0));
    }

    [Fact]
    public async Task DeleteJob_RemovesJob()
    {
        var id = Guid.NewGuid();
        await _store.UpsertJobAsync(ValidJob(id, "A"));
        await _store.UpsertJobAsync(ValidJob(Guid.NewGuid(), "B"));

        await _store.DeleteJobAsync(id);

        var jobs = await _store.LoadJobsAsync();
        jobs.Should().ContainSingle(j => j.Name == "B");
    }

    [Fact]
    public async Task SaveAppState_JobsArmedFalse_PersistsAcrossReload()
    {
        await _store.SaveAppStateAsync(new AppState { JobsArmed = false });

        var reloaded = new JsonJobStore(_root);
        var state = await reloaded.LoadAppStateAsync();
        state.JobsArmed.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertJob_EmptyName_Throws()
    {
        var act = async () => await _store.UpsertJobAsync(new Job
        {
            Id = Guid.NewGuid(),
            Name = "  ",
            Enabled = true,
            SourcePath = @"C:\Data",
            DestinationPath = @"\\server\share",
            Mode = SyncMode.ManualOnly
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpsertJob_EmptySourcePath_Throws()
    {
        var act = async () => await _store.UpsertJobAsync(new Job
        {
            Id = Guid.NewGuid(),
            Name = "Job",
            Enabled = true,
            SourcePath = "",
            DestinationPath = @"\\server\share",
            Mode = SyncMode.ManualOnly
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UpsertJob_ScheduledWithoutTime_Throws()
    {
        var act = async () => await _store.UpsertJobAsync(new Job
        {
            Id = Guid.NewGuid(),
            Name = "Job",
            Enabled = true,
            SourcePath = @"C:\Data",
            DestinationPath = @"\\server\share",
            Mode = SyncMode.Scheduled,
            ScheduledTimeLocal = null
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static Job ValidJob(Guid id, string name) => new()
    {
        Id = id,
        Name = name,
        Enabled = true,
        SourcePath = $@"C:\Data\{name}",
        DestinationPath = $@"\\server\backup\{name}",
        Mode = SyncMode.ManualOnly
    };
}
