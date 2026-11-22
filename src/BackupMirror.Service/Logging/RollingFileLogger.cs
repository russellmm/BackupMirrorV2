using System.Text;

namespace BackupMirror.Service.Logging;

/// <summary>Size-rotated file log (service.log, service.1.log … service.4.log; 5 MB each).</summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int Keep = 4;
    private readonly string _path;
    private readonly object _gate = new();

    public RollingFileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public string FilePath => _path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    public void Dispose() { }

    private static string ShortCategory(string category)
    {
        var i = category.LastIndexOf('.');
        return i >= 0 ? category[(i + 1)..] : category;
    }

    private void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes) Rotate();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // never throw from logging
        }
    }

    private void Rotate()
    {
        var dir = Path.GetDirectoryName(_path)!;
        var name = Path.GetFileNameWithoutExtension(_path);
        var ext = Path.GetExtension(_path);
        string Numbered(int n) => Path.Combine(dir, $"{name}.{n}{ext}");

        if (File.Exists(Numbered(Keep))) File.Delete(Numbered(Keep));
        for (var n = Keep - 1; n >= 1; n--)
            if (File.Exists(Numbered(n))) File.Move(Numbered(n), Numbered(n + 1));
        File.Move(_path, Numbered(1));
    }

    public IReadOnlyList<string> Tail(int lines)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return Array.Empty<string>();
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var take = Math.Min(fs.Length, lines * 400L);
            fs.Seek(-take, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            if (take < fs.Length && all.Count > 0) all.RemoveAt(0); // partial first line
            return all.Skip(Math.Max(0, all.Count - lines)).ToList();
        }
    }

    private sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var level = logLevel switch
            {
                LogLevel.Information => "INFO",
                LogLevel.Warning => "WARN",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT",
                _ => logLevel.ToString().ToUpperInvariant()
            };
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + "    " + exception.ToString().Replace("\n", "\n    ");
            owner.Write(line);
        }
    }
}
