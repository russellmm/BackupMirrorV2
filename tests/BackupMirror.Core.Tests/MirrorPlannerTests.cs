using BackupMirror.Core.Mirror;
using FluentAssertions;

namespace BackupMirror.Core.Tests;

public class MirrorPlannerTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _dest;

    public MirrorPlannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BackupMirrorPlan", Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "src");
        _dest = Path.Combine(_root, "dst");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_dest);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch { /* ignore */ }
    }

    [Fact]
    public void Plan_NewFileOnSource_RequestsCopy()
    {
        File.WriteAllText(Path.Combine(_source, "a.txt"), "hello");

        var plan = new MirrorPlanner().Plan(_source, _dest);

        plan.Copies.Should().ContainSingle(op => op.RelativePath == "a.txt");
        plan.Deletes.Should().BeEmpty();
    }

    [Fact]
    public void Plan_IdenticalFile_NoOps()
    {
        var content = "same";
        var srcFile = Path.Combine(_source, "a.txt");
        var dstFile = Path.Combine(_dest, "a.txt");
        File.WriteAllText(srcFile, content);
        File.Copy(srcFile, dstFile);
        var srcInfo = new FileInfo(srcFile);
        File.SetLastWriteTimeUtc(dstFile, srcInfo.LastWriteTimeUtc);

        var plan = new MirrorPlanner().Plan(_source, _dest);

        plan.Copies.Should().BeEmpty();
        plan.Deletes.Should().BeEmpty();
    }

    [Fact]
    public void Plan_ChangedSize_RequestsCopy()
    {
        var srcFile = Path.Combine(_source, "a.txt");
        var dstFile = Path.Combine(_dest, "a.txt");
        File.WriteAllText(srcFile, "new content longer");
        File.WriteAllText(dstFile, "old");

        var plan = new MirrorPlanner().Plan(_source, _dest);

        plan.Copies.Should().ContainSingle(op => op.RelativePath == "a.txt");
    }

    [Fact]
    public void Plan_OrphanOnDest_RequestsDelete()
    {
        File.WriteAllText(Path.Combine(_dest, "gone.txt"), "orphan");

        var plan = new MirrorPlanner().Plan(_source, _dest);

        plan.Deletes.Should().ContainSingle(op => op.RelativePath == "gone.txt");
        plan.Copies.Should().BeEmpty();
    }

    [Fact]
    public void Plan_NestedRelativePaths_UsePortableForwardSlashes()
    {
        var dir = Path.Combine(_source, "sub");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "b.txt"), "x");

        var plan = new MirrorPlanner().Plan(_source, _dest);

        plan.Copies.Should().ContainSingle(op => op.RelativePath == @"sub/b.txt");
    }

    [Fact]
    public void Plan_RenameOnSource_IsDeleteOldPlusCopyNew()
    {
        File.WriteAllText(Path.Combine(_dest, "old.txt"), "data");
        File.WriteAllText(Path.Combine(_source, "new.txt"), "data");

        var plan = new MirrorPlanner().Plan(_source, _dest);

        plan.Copies.Should().ContainSingle(op => op.RelativePath == "new.txt");
        plan.Deletes.Should().ContainSingle(op => op.RelativePath == "old.txt");
    }
}
