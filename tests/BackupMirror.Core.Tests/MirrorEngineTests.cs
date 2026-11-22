using BackupMirror.Core.Mirror;
using FluentAssertions;

namespace BackupMirror.Core.Tests;

public class MirrorEngineTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _dest;

    public MirrorEngineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "BackupMirrorEng", Guid.NewGuid().ToString("N"));
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
    public async Task Execute_CopiesNewFile()
    {
        File.WriteAllText(Path.Combine(_source, "a.txt"), "hello");
        var planner = new MirrorPlanner();
        var plan = planner.Plan(_source, _dest);
        var engine = new MirrorEngine();

        var result = await engine.ExecuteAsync(_source, _dest, plan, confirmDeletes: false);

        result.CopiesApplied.Should().Be(1);
        File.ReadAllText(Path.Combine(_dest, "a.txt")).Should().Be("hello");
    }

    [Fact]
    public async Task Execute_TenOrFewerDeletes_AppliesAutomatically()
    {
        for (var i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(_dest, $"d{i}.txt"), "x");

        var plan = new MirrorPlanner().Plan(_source, _dest);
        plan.Deletes.Should().HaveCount(10);

        var result = await new MirrorEngine().ExecuteAsync(_source, _dest, plan, confirmDeletes: false);

        result.DeletesApplied.Should().Be(10);
        result.DeletesHeldForConfirm.Should().Be(0);
        Directory.GetFiles(_dest).Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_ElevenDeletes_WithoutConfirm_HoldsDeletes_AppliesCopies()
    {
        for (var i = 0; i < 11; i++)
            File.WriteAllText(Path.Combine(_dest, $"d{i}.txt"), "x");
        File.WriteAllText(Path.Combine(_source, "new.txt"), "n");

        var plan = new MirrorPlanner().Plan(_source, _dest);
        plan.Deletes.Should().HaveCount(11);

        var result = await new MirrorEngine().ExecuteAsync(_source, _dest, plan, confirmDeletes: false);

        result.CopiesApplied.Should().Be(1);
        result.DeletesApplied.Should().Be(0);
        result.DeletesHeldForConfirm.Should().Be(11);
        result.RequiresDeleteConfirmation.Should().BeTrue();
        File.Exists(Path.Combine(_dest, "new.txt")).Should().BeTrue();
        Directory.GetFiles(_dest).Should().HaveCount(12); // 11 orphans + new
    }

    [Fact]
    public async Task Execute_ElevenDeletes_WithConfirm_AppliesDeletes()
    {
        for (var i = 0; i < 11; i++)
            File.WriteAllText(Path.Combine(_dest, $"d{i}.txt"), "x");

        var plan = new MirrorPlanner().Plan(_source, _dest);
        var result = await new MirrorEngine().ExecuteAsync(_source, _dest, plan, confirmDeletes: true);

        result.DeletesApplied.Should().Be(11);
        result.RequiresDeleteConfirmation.Should().BeFalse();
        Directory.GetFiles(_dest).Should().BeEmpty();
    }

    [Fact]
    public void SafetyGate_ThresholdBoundary()
    {
        var copies = Array.Empty<FileOp>();
        var ten = Enumerable.Range(0, 10)
            .Select(i => new FileOp(FileOpKind.Delete, $"{i}.txt", "", ""))
            .ToList();
        var eleven = Enumerable.Range(0, 11)
            .Select(i => new FileOp(FileOpKind.Delete, $"{i}.txt", "", ""))
            .ToList();

        DeleteSafetyGate.RequiresConfirmation(new MirrorPlan(copies, ten)).Should().BeFalse();
        DeleteSafetyGate.RequiresConfirmation(new MirrorPlan(copies, eleven)).Should().BeTrue();
    }
}
