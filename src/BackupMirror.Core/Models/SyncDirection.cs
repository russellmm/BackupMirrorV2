namespace BackupMirror.Core.Models;

public enum SyncDirection
{
    /// <summary>"Sync using Source": the destination is made to match the source (normal backup).</summary>
    SourceToDestination = 0,

    /// <summary>"Sync using Destination": the source is made to match the destination (restore). Manual jobs only.</summary>
    DestinationToSource = 1
}
