using BackupMirror.Core.Mirror;
using BackupMirror.Core.Models;
using BackupMirror.Core.Scheduling;
using BackupMirror.Core.Security;
using BackupMirror.Core.Storage;
using FluentAssertions;

namespace BackupMirror.Core.Tests;

public class ExcludeMatcherTests
{
    [Theory]
    [InlineData("node_modules", "web/node_modules/a.js", true)]
    [InlineData("node_modules", "node_modules", true)]
    [InlineData("*.tmp", "a/b/c.tmp", true)]
    [InlineData("*.tmp", "a/b/c.tmpx", false)]
    [InlineData("cache/**", "cache/x/y.bin", true)]
    [InlineData("cache/**", "other/cache/y.bin", false)]
    [InlineData("photos/*.raw", "photos/a.raw", true)]
    [InlineData("photos/*.raw", "photos/sub/a.raw", false)]
    [InlineData("**/build", "src/app/build/out.o", true)]
    [InlineData("~$*", "docs/~$report.docx", true)]
    public void Matches_gitignore_style(string pattern, string path, bool expected)
        => new ExcludeMatcher(new[] { pattern }).IsExcluded(path).Should().Be(expected);

    [Fact]
    public void Always_excludes_in_flight_temp_files()
        => ExcludeMatcher.None.IsExcluded("a/file.txt" + ExcludeMatcher.TempSuffix).Should().BeTrue();
}

public sealed class MirrorV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bm-v2-" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");

    public MirrorV2Tests()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch
        {
            // best effort
        }
    }

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private async Task<MirrorResult> Run(MirrorOptions? o = null, bool confirm = false, IProgress<MirrorProgress>? progress = null)
    {
        o ??= new MirrorOptions();
        var plan = new MirrorPlanner().Plan(Src, Dst, o);
        return await new MirrorEngine().ExecuteAsync(Dst, plan, confirm, o, progress);
    }

    [Fact]
    public async Task Excluded_files_are_neither_copied_nor_deleted()
    {
        Write(Path.Combine(Src, "keep.txt"));
        Write(Path.Combine(Src, "node_modules", "lib.js"));
        Write(Path.Combine(Dst, "scratch.tmp")); // excluded on the destination too: must survive

        await Run(new MirrorOptions { Excludes = new ExcludeMatcher(new[] { "node_modules", "*.tmp" }) });

        File.Exists(Path.Combine(Dst, "keep.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(Dst, "node_modules")).Should().BeFalse();
        File.Exists(Path.Combine(Dst, "scratch.tmp")).Should().BeTrue();
    }

    [Fact]
    public async Task Empty_directories_are_mirrored_and_removed()
    {
        Directory.CreateDirectory(Path.Combine(Src, "empty", "nested"));
        Directory.CreateDirectory(Path.Combine(Dst, "gone", "deeper"));

        await Run();

        Directory.Exists(Path.Combine(Dst, "empty", "nested")).Should().BeTrue();
        Directory.Exists(Path.Combine(Dst, "gone")).Should().BeFalse();
    }

    [Fact]
    public async Task Copies_are_atomic_and_leave_no_temp_files()
    {
        Write(Path.Combine(Src, "a", "big.bin"), new string('z', 3 * 1024 * 1024));
        Write(Path.Combine(Dst, "a", "big.bin" + ExcludeMatcher.TempSuffix), "half-written leftover");

        var plan = new MirrorPlanner().Plan(Src, Dst, new MirrorOptions());
        plan.StaleTempFiles.Should().HaveCount(1);
        plan.Deletes.Should().BeEmpty("leftover temp files never count towards the delete safety limit");

        await new MirrorEngine().ExecuteAsync(Dst, plan, false, new MirrorOptions(), null);

        File.ReadAllText(Path.Combine(Dst, "a", "big.bin")).Length.Should().Be(3 * 1024 * 1024);
        Directory.EnumerateFiles(Dst, "*" + ExcludeMatcher.TempSuffix, SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Read_only_destination_file_is_replaced()
    {
        Write(Path.Combine(Src, "r.txt"), "new contents");
        var dest = Path.Combine(Dst, "r.txt");
        Write(dest, "old");
        File.SetAttributes(dest, FileAttributes.ReadOnly);

        var result = await Run();

        result.Errors.Should().BeEmpty();
        File.ReadAllText(dest).Should().Be("new contents");
    }

    [Fact]
    public async Task Negative_threshold_never_holds_deletes()
    {
        for (var i = 0; i < 25; i++) Write(Path.Combine(Dst, $"old{i}.txt"));

        var result = await Run(new MirrorOptions { DeleteConfirmThreshold = -1 });

        result.RequiresDeleteConfirmation.Should().BeFalse();
        result.DeletesApplied.Should().Be(25);
    }

    [Fact]
    public async Task Custom_threshold_holds_deletes_but_still_copies()
    {
        Write(Path.Combine(Src, "new.txt"));
        for (var i = 0; i < 4; i++) Write(Path.Combine(Dst, $"old{i}.txt"));

        var result = await Run(new MirrorOptions { DeleteConfirmThreshold = 3 });

        result.RequiresDeleteConfirmation.Should().BeTrue();
        result.DeletesHeldForConfirm.Should().Be(4);
        File.Exists(Path.Combine(Dst, "new.txt")).Should().BeTrue();
        File.Exists(Path.Combine(Dst, "old0.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Reports_byte_progress()
    {
        Write(Path.Combine(Src, "p.bin"), new string('p', 2 * 1024 * 1024));
        var reports = new List<MirrorProgress>();

        var result = await Run(progress: new SyncProgress(reports.Add));

        result.BytesCopied.Should().Be(2 * 1024 * 1024);
        reports.Should().Contain(p => p.Phase == "done" && p.BytesDone == 2 * 1024 * 1024);
    }

    [Fact]
    public async Task Unix_permissions_are_preserved()
    {
        if (OperatingSystem.IsWindows()) return;
        var src = Path.Combine(Src, "secret.txt");
        Write(src, "s");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(src, mode);

        await Run();

        File.GetUnixFileMode(Path.Combine(Dst, "secret.txt")).Should().Be(mode);
    }

    [Fact]
    public void Nested_source_and_destination_are_rejected()
    {
        var act = () => new MirrorPlanner().Plan(Src, Path.Combine(Src, "inside"), new MirrorOptions());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Timestamps_within_tolerance_are_equal()
    {
        Write(Path.Combine(Src, "t.txt"), "same");
        Write(Path.Combine(Dst, "t.txt"), "same");
        var t = DateTime.UtcNow.AddMinutes(-5);
        File.SetLastWriteTimeUtc(Path.Combine(Src, "t.txt"), t);
        File.SetLastWriteTimeUtc(Path.Combine(Dst, "t.txt"), t.AddSeconds(1));

        new MirrorPlanner().Plan(Src, Dst, new MirrorOptions()).Copies.Should().BeEmpty();
    }

    [Fact]
    public async Task Unreadable_source_folder_never_deletes_its_mirror()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return; // needs POSIX permissions as non-root
        Write(Path.Combine(Src, "locked", "a.txt"));
        Write(Path.Combine(Src, "ok.txt"));
        await Run();
        File.SetUnixFileMode(Path.Combine(Src, "locked"), UnixFileMode.None);
        try
        {
            var plan = new MirrorPlanner().Plan(Src, Dst, new MirrorOptions());
            plan.SourceIncomplete.Should().BeTrue();
            plan.Warnings.Should().NotBeEmpty();
            plan.Deletes.Should().BeEmpty();
        }
        finally
        {
            File.SetUnixFileMode(Path.Combine(Src, "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private sealed class SyncProgress(Action<MirrorProgress> a) : IProgress<MirrorProgress>
    {
        public void Report(MirrorProgress value) => a(value);
    }
}

public class ScheduleCalculatorTests
{
    private static readonly JobSchedule Weekdays2am = new()
    {
        Times = { new TimeOnly(2, 0) },
        Days = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
    };

    [Fact]
    public void Next_skips_excluded_days()
    {
        var saturdayNoon = new DateTime(2026, 11, 21, 12, 0, 0); // Saturday
        ScheduleCalculator.NextOccurrence(Weekdays2am, saturdayNoon).Should().Be(new DateTime(2026, 11, 23, 2, 0, 0));
    }

    [Fact]
    public void Multiple_times_pick_the_nearest()
    {
        var s = new JobSchedule { Times = { new TimeOnly(2, 0), new TimeOnly(14, 0) } };
        var morning = new DateTime(2026, 11, 21, 9, 0, 0);
        ScheduleCalculator.NextOccurrence(s, morning).Should().Be(new DateTime(2026, 11, 21, 14, 0, 0));
        ScheduleCalculator.PreviousOccurrence(s, morning).Should().Be(new DateTime(2026, 11, 21, 2, 0, 0));
    }

    [Fact]
    public void Due_once_after_occurrence_and_not_again()
    {
        var s = new JobSchedule { Times = { new TimeOnly(2, 0) } };
        var now = new DateTime(2026, 11, 21, 2, 0, 30);
        var before = new DateTimeOffset(new DateTime(2026, 11, 20, 23, 0, 0)).ToUniversalTime();
        ScheduleCalculator.IsDue(s, now, before, TimeSpan.FromHours(12)).Should().BeTrue();
        ScheduleCalculator.IsDue(s, now, DateTimeOffset.UtcNow, TimeSpan.FromHours(12)).Should().BeFalse();
    }

    [Fact]
    public void Missed_runs_older_than_catch_up_window_are_skipped()
    {
        var s = new JobSchedule { Times = { new TimeOnly(2, 0) } };
        var evening = new DateTime(2026, 11, 21, 20, 0, 0);
        var lastWeek = DateTimeOffset.UtcNow.AddDays(-7);
        ScheduleCalculator.IsDue(s, evening, lastWeek, TimeSpan.FromHours(12)).Should().BeFalse();
        ScheduleCalculator.IsDue(s, evening, lastWeek, TimeSpan.FromHours(24)).Should().BeTrue();
    }
}

public sealed class StorageV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bm-store-" + Guid.NewGuid().ToString("N"));

    public StorageV2Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private static Job NewJob(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, SourcePath = "/a", DestinationPath = "/b", Mode = SyncMode.ManualOnly
    };

    [Fact]
    public async Task Corrupt_jobs_file_falls_back_to_backup()
    {
        var store = new JsonJobStore(_root);
        await store.UpsertJobAsync(NewJob("one"));
        await store.UpsertJobAsync(NewJob("two")); // .bak now holds "one"
        File.WriteAllText(Path.Combine(_root, "jobs.json"), "{ not json");

        var jobs = await store.LoadJobsAsync();

        jobs.Select(j => j.Name).Should().Equal("one");
        Directory.EnumerateFiles(_root, "jobs.json.corrupt-*").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Corrupt_jobs_file_without_backup_throws_instead_of_returning_empty()
    {
        File.WriteAllText(Path.Combine(_root, "jobs.json"), "garbage");
        var act = async () => await new JsonJobStore(_root).LoadJobsAsync();
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Legacy_v1_fields_are_read_and_dropped_on_save()
    {
        File.WriteAllText(Path.Combine(_root, "jobs.json"), """
            [{"id":"6f1c1c1e-1111-4a4a-9b9b-222222222222","name":"Old","enabled":true,"sourcePath":"C:\\a","destinationPath":"\\\\s\\b",
              "mode":"scheduled","scheduledTimeLocal":"02:30:00","lastStatus":"success","lastError":null,"lastRunUtc":"2026-07-01T02:30:00+00:00"}]
            """);
        var store = new JsonJobStore(_root);
        var job = (await store.LoadJobsAsync()).Single();
        job.LastStatus.Should().Be(JobStatus.Success);
        job.EffectiveSchedule.Times.Should().Equal(new TimeOnly(2, 30));

        await store.UpsertJobAsync(job);
        var text = File.ReadAllText(Path.Combine(_root, "jobs.json"));
        text.Should().NotContain("scheduledTimeLocal").And.NotContain("lastStatus");
        (await store.LoadJobsAsync()).Single().EffectiveSchedule.Times.Should().Equal(new TimeOnly(2, 30));
    }

    [Fact]
    public async Task Runtime_store_marks_interrupted_runs_and_caps_history()
    {
        var rt = new RuntimeStore(_root);
        var id = Guid.NewGuid();
        rt.Update(id, r => r.Status = JobStatus.Running);
        for (var i = 0; i < RuntimeStore.MaxHistory + 20; i++) rt.AddRun(new RunRecord { JobId = id });
        await rt.FlushAsync();

        var reloaded = new RuntimeStore(_root);
        await reloaded.LoadAsync();
        reloaded.Get(id).Status.Should().Be(JobStatus.Cancelled);
        reloaded.History(null, 10_000).Should().HaveCount(RuntimeStore.MaxHistory);
    }

    [Fact]
    public void Secret_round_trips_on_this_platform()
    {
        SecretProtector.Initialize(_root);
        var p = SecretProtector.Protect("hunter2 – ünïcode");
        p.Should().NotContain("hunter2");
        SecretProtector.Unprotect(p).Should().Be("hunter2 – ünïcode");
        if (!OperatingSystem.IsWindows())
        {
            p.Should().StartWith("aes:");
            File.GetUnixFileMode(Path.Combine(_root, "secret.key")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
