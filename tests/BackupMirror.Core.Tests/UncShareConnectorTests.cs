using BackupMirror.Core.Network;
using FluentAssertions;

namespace BackupMirror.Core.Tests;

public class UncShareConnectorTests
{
    [Theory]
    [InlineData(@"\\192.0.2.10\backups", @"\\192.0.2.10\backups")]
    [InlineData(@"\\192.0.2.10\backups\backups", @"\\192.0.2.10\backups")]
    [InlineData(@"\\server\share\a\b\c", @"\\server\share")]
    [InlineData(@"//server/share/a", @"\\server\share")]
    public void GetShareRoot_ExtractsServerAndShare(string input, string expected)
    {
        UncShareConnector.GetShareRoot(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"C:\data", false)]
    [InlineData("", false)]
    public void IsUncPath_DetectsUnc(string path, bool expected)
    {
        UncShareConnector.IsUncPath(path).Should().Be(expected);
    }
}

public class ShareSignInTests
{
    private static BackupMirror.Core.Models.Job J(string dest, string? user = null) =>
        new() { Id = Guid.NewGuid(), Name = dest, SourcePath = @"C:\src", DestinationPath = dest, DestinationUserName = user };

    [Fact]
    public void OwnSignInWins()
    {
        var self = J(@"\\nas\a\x", "me");
        var other = J(@"\\nas\a\y", "other");
        ShareSignIn.FindDonor(new[] { other, self }, self, self.DestinationPath).Should().BeSameAs(self);
    }

    [Fact]
    public void BorrowsSameShareBeforeSameServer()
    {
        var self = J(@"\\nas\a\new");
        var server = J(@"\\NAS\b\x", "srv");
        var share = J(@"\\nas\A\y", "share");
        ShareSignIn.FindDonor(new[] { self, server, share }, self, self.DestinationPath).Should().BeSameAs(share);
    }

    [Fact]
    public void FallsBackToSameServer_NoneForOtherServers()
    {
        var self = J(@"\\nas\a\new");
        var server = J(@"\\nas\b\x", "srv");
        var elsewhere = J(@"\\other\a\x", "x");
        ShareSignIn.FindDonor(new[] { self, elsewhere, server }, self, self.DestinationPath).Should().BeSameAs(server);
        ShareSignIn.FindDonor(new[] { self, elsewhere }, self, self.DestinationPath).Should().BeNull();
        ShareSignIn.FindDonor(new[] { server }, null, @"C:\local").Should().BeNull();
    }
}
