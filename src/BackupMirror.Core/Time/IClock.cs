using BackupMirror.Core.Models;

namespace BackupMirror.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateTime LocalNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTime LocalNow => DateTime.Now;
}
