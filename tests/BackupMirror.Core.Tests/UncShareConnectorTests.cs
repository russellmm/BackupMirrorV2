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
