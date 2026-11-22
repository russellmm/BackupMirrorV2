using BackupMirror.Core.Mirror;
using BackupMirror.Core.Models;
using BackupMirror.Core.Storage;
using FluentAssertions;

namespace BackupMirror.Core.Tests;

/// <summary>OpenDiff-style options: change kinds, subfolders, hidden/system, keep-removed.</summary>
public sealed class SyncOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bm-opt-" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");

    public SyncOptionsTests()
    {
        Write(Path.Combine(Src, "new.txt"), "new");
        Write(Path.Combine(Src, "same.txt"), "same");
        Write(Path.Combine(Src, "changed.txt"), "version 2");
        Write(Path.Combine(Src, "sub", "deep.txt"), "deep");
        Write(Path.Combine(Dst, "same.txt"), "same");
        Write(Path.Combine(Dst, "changed.txt"), "v1");
        Write(Path.Combine(Dst, "extra.txt"), "extra");
        var t = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(Path.Combine(Src, "same.txt"), t);
        File.SetLastWriteTimeUtc(Path.Combine(Dst, "same.txt"), t);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch
        {
            // best effort
        }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private MirrorPlan Plan(MirrorOptions o) => new MirrorPlanner().Plan(Src, Dst, o);

    [Fact]
    public void Default_plans_copy_update_and_delete()
    {
        var plan = Plan(new MirrorOptions());
        plan.Copies.Where(c => c.Kind == FileOpKind.Copy).Select(c => c.RelativePath).Should().BeEquivalentTo("new.txt", "sub/deep.txt");
        plan.Copies.Where(c => c.Kind == FileOpKind.Update).Select(c => c.RelativePath).Should().Equal("changed.txt");
        plan.Deletes.Select(d => d.RelativePath).Should().Equal("extra.txt");
        plan.DirectoriesToCreate.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Each_change_kind_can_be_switched_off(bool copy, bool update, bool delete)
    {
        var plan = Plan(new MirrorOptions { CopyMissing = copy, UpdateChanged = update, DeleteExtra = delete });
        plan.MissingCount.Should().Be(copy ? 2 : 0);
        plan.UpdateCount.Should().Be(update ? 1 : 0);
        plan.Deletes.Count.Should().Be(delete ? 1 : 0);
        plan.DirectoriesToCreate.Count.Should().Be(copy ? 1 : 0, "folders are only created when copying missing files");
    }

    [Fact]
    public void Without_subfolders_only_top_level_files_are_compared()
    {
        Write(Path.Combine(Dst, "olddir", "x.txt"), "x");
        var plan = Plan(new MirrorOptions { IncludeSubfolders = false });
        plan.Copies.Select(c => c.RelativePath).Should().NotContain("sub/deep.txt");
        plan.Deletes.Select(d => d.RelativePath).Should().NotContain("olddir/x.txt");
        plan.DirectoriesToCreate.Should().BeEmpty();
        plan.DirectoriesToRemove.Should().BeEmpty();
    }

    [Fact]
    public void Hidden_on_either_side_is_left_out_when_excluded()
    {
        // Hidden only on the destination: must not look "missing" on the source side or be deleted.
        var hiddenSrc = Path.Combine(Src, OperatingSystem.IsWindows() ? "h.txt" : ".h.txt");
        Write(hiddenSrc, "src");
        var hiddenDst = Path.Combine(Dst, OperatingSystem.IsWindows() ? "h.txt" : ".h.txt");
        Write(hiddenDst, "different");
        var extraHidden = Path.Combine(Dst, OperatingSystem.IsWindows() ? "x.txt" : ".x.txt");
        Write(extraHidden, "x");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hiddenDst, FileAttributes.Hidden);
            File.SetAttributes(extraHidden, FileAttributes.Hidden);
        }

        var name = Path.GetFileName(hiddenDst);
        var extra = Path.GetFileName(extraHidden);
        var excluded = Plan(new MirrorOptions { IncludeHidden = false });
        excluded.Copies.Select(c => c.RelativePath).Should().NotContain(name);
        excluded.Deletes.Select(d => d.RelativePath).Should().NotContain(extra);

        var included = Plan(new MirrorOptions { IncludeHidden = true });
        included.Copies.Select(c => c.RelativePath).Should().Contain(name);
        included.Deletes.Select(d => d.RelativePath).Should().Contain(extra);
    }

    [Fact]
    public async Task Keep_removed_moves_deleted_and_overwritten_files_to_trash()
    {
        var trash = TrashBin.RunFolder(Dst, DateTime.Now);
        var o = new MirrorOptions { TrashDirectory = trash };
        await new MirrorEngine().ExecuteAsync(Dst, Plan(o), false, o, null);

        File.Exists(Path.Combine(Dst, "extra.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(trash, "extra.txt")).Should().Be("extra");
        File.ReadAllText(Path.Combine(trash, "changed.txt")).Should().Be("v1", "the overwritten version is kept");
        File.ReadAllText(Path.Combine(Dst, "changed.txt")).Should().Be("version 2");

        // The trash folder is never mirrored, deleted or reported.
        var next = Plan(new MirrorOptions());
        next.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Trash_purge_removes_only_expired_run_folders()
    {
        var now = new DateTime(2026, 11, 21, 12, 0, 0);
        var old = TrashBin.RunFolder(Dst, now.AddDays(-40));
        var recent = TrashBin.RunFolder(Dst, now.AddDays(-2));
        Write(Path.Combine(old, "a.txt"), "a");
        Write(Path.Combine(recent, "b.txt"), "b");
        Directory.CreateDirectory(Path.Combine(Dst, ExcludeMatcher.TrashFolderName, "not-ours"));

        TrashBin.Purge(Dst, 30, now).Should().Be(1);
        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(recent).Should().BeTrue();
        Directory.Exists(Path.Combine(Dst, ExcludeMatcher.TrashFolderName, "not-ours")).Should().BeTrue();
    }

    [Fact]
    public void Without_drops_unchecked_items()
    {
        var plan = Plan(new MirrorOptions());
        var skip = new HashSet<FileOp> { plan.Deletes[0] };
        var trimmed = plan.Without(skip, new HashSet<string>(plan.DirectoriesToCreate));
        trimmed.Deletes.Should().BeEmpty();
        trimmed.DirectoriesToCreate.Should().BeEmpty();
        trimmed.Copies.Should().HaveCount(plan.Copies.Count);
    }

    [Fact]
    public void Store_rejects_reverse_direction_on_automatic_jobs_and_empty_change_sets()
    {
        var job = new Job { Id = Guid.NewGuid(), Name = "j", SourcePath = "/a", DestinationPath = "/b", Mode = SyncMode.Realtime, Direction = SyncDirection.DestinationToSource };
        var act = () => JsonJobStore.ValidateJob(job);
        act.Should().Throw<ArgumentException>().WithMessage("*manual*");

        job.Mode = SyncMode.ManualOnly;
        act.Should().NotThrow();
        job.FromPath.Should().Be("/b");
        job.ToPath.Should().Be("/a");

        job.CopyMissing = job.UpdateChanged = job.DeleteExtra = false;
        act.Should().Throw<ArgumentException>();
    }
}
