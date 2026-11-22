using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BackupMirror.Tray;

/// <summary>
/// Native window hosting the web dashboard (WebView2, built into Windows 11).
/// Closing hides it to the tray; falls back to the default browser if WebView2 is missing.
/// </summary>
internal sealed class DashboardForm : Form
{
    private readonly string _url;
    private readonly WebView2 _web;
    private bool _reallyClose;
    private bool _initialized;
    private bool _restoreMaximized;
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 500 };

    public DashboardForm(string url, Icon icon)
    {
        _url = url;
        Text = "Backup Mirror";
        Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(420, 560);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Size = new Size(Math.Min(1360, area.Width - 80), Math.Min(900, area.Height - 60));

        // Restore where the window was last time, if that spot is still on a screen.
        if (WindowPlacement.Load() is { } saved && saved.IsVisibleOnAnyScreen())
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = saved.Bounds;
            _restoreMaximized = saved.Maximized;
        }

        // Save shortly after the user stops moving/resizing (and on maximize/restore).
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); WindowPlacement.Save(this); };
        Move += (_, _) => ScheduleSave();
        Resize += (_, _) => ScheduleSave();
        BackColor = Color.FromArgb(7, 10, 20);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(7, 10, 20) };
        Controls.Add(_web);
    }

    private void ScheduleSave()
    {
        if (!Visible) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Reveal()
    {
        if (!Visible)
        {
            Show();
            if (_restoreMaximized) { WindowState = FormWindowState.Maximized; _restoreMaximized = false; }
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        if (!_initialized) _ = InitAsync();
    }

    private async Task InitAsync()
    {
        _initialized = true;
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupMirror", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            // Links leaving the dashboard open in the real browser.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(_url, StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenExternal(e.Uri);
                }
            };
            core.DocumentTitleChanged += (_, _) => Text = core.DocumentTitle is { Length: > 0 } t ? t : "Backup Mirror";
            core.Navigate(_url + "/");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            OpenExternal(_url);
            _reallyClose = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the dashboard window: {ex.Message}\n\nIt will open in your browser instead.",
                "Backup Mirror", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            OpenExternal(_url);
        }
    }

    private static void OpenExternal(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { /* nothing else to do */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        WindowPlacement.Save(this);
        if (!_reallyClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide(); // X hides to the tray, like before
            return;
        }

        base.OnFormClosing(e);
    }

    public void CloseForReal()
    {
        _reallyClose = true;
        if (!IsDisposed) Close();
    }
}
