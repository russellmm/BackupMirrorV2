namespace BackupMirror.Tray;

internal static class Program
{
    private const string MutexName = @"Local\BackupMirror.Tray";
    internal const string ShowEventName = @"Local\BackupMirror.Tray.Show";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // Already running: ask that instance to show its window.
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                ev.Set();
            }
            catch
            {
                // the other instance is still starting
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayContext(startHidden));
    }
}
