using System.Text.Json;

namespace BackupMirror.Tray;

/// <summary>
/// Remembers the dashboard window's position, size and maximized state between runs
/// (%LOCALAPPDATA%\BackupMirror\window.json). A saved position that is no longer on any screen
/// (monitor unplugged, resolution changed) is ignored.
/// </summary>
internal sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupMirror", "window.json");

    [System.Text.Json.Serialization.JsonIgnore]
    public Rectangle Bounds => new(X, Y, Width, Height);

    public static WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var p = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath));
            return p is { Width: > 0, Height: > 0 } ? p : null;
        }
        catch
        {
            return null; // corrupt file: use defaults
        }
    }

    public static void Save(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized) return; // keep the last real placement
        // RestoreBounds holds the normal-size bounds while the window is maximized.
        var b = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        if (b.Width <= 0 || b.Height <= 0) return;
        var p = new WindowPlacement
        {
            X = b.X, Y = b.Y, Width = b.Width, Height = b.Height,
            Maximized = form.WindowState == FormWindowState.Maximized
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(p));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // not worth bothering the user about
        }
    }

    /// <summary>True when the title bar would be reachable on one of the current screens.</summary>
    public bool IsVisibleOnAnyScreen()
    {
        var titleBar = new Rectangle(X + 40, Y, Math.Max(100, Width - 80), 32);
        return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(titleBar)
                                          && Rectangle.Intersect(s.WorkingArea, titleBar).Width >= 100);
    }
}
