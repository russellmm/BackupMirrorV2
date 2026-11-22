using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace BackupMirror.Tray;

/// <summary>
/// System-tray companion: live status icon, quick actions, notifications, and the dashboard window.
/// Exiting the tray never stops the service.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private enum Health { Unknown, Ok, Running, Attention, Failed, Paused, Offline }

    private readonly ServiceClient _client = new();
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _armItem;
    private readonly ToolStripMenuItem _syncAllItem;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showWait;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<Health, Icon> _icons = new();
    private readonly Dictionary<Guid, string> _lastStatus = new();
    private DashboardForm? _window;
    private Health _health = Health.Unknown;
    private bool _armed = true;
    private bool _polling;
    private bool _firstPoll = true;

    public TrayContext(bool startHidden)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _statusItem = new ToolStripMenuItem("Connecting…") { Enabled = false };
        _syncAllItem = new ToolStripMenuItem("Sync all now", null, async (_, _) => await Act("/api/sync-all"));
        _armItem = new ToolStripMenuItem("Pause all mirrors", null, async (_, _) => await Act("/api/arm", new { armed = !_armed }));

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem("Open Backup Mirror", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_syncAllItem);
        menu.Items.Add(_armItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open in web browser", null, (_, _) => OpenBrowser()));
        menu.Items.Add(new ToolStripMenuItem("Exit tray (service keeps running)", null, (_, _) => ExitThread()));

        _icon = new NotifyIcon
        {
            Icon = IconFor(Health.Unknown),
            Text = "Backup Mirror",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _icon.BalloonTipClicked += (_, _) => ShowWindow();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => _ui.Post(_ => ShowWindow(), null), null, -1, false);

        _timer = new System.Windows.Forms.Timer { Interval = 4000 };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();

        if (!startHidden) ShowWindow();
    }

    private void ShowWindow()
    {
        if (_window is null || _window.IsDisposed)
            _window = new DashboardForm(_client.BaseUrl, LoadAppIcon());
        _window.Reveal();
    }

    private void OpenBrowser()
    {
        try { Process.Start(new ProcessStartInfo(_client.BaseUrl) { UseShellExecute = true }); }
        catch (Exception ex) { _icon.ShowBalloonTip(4000, "Backup Mirror", ex.Message, ToolTipIcon.Error); }
    }

    private async Task Act(string path, object? body = null)
    {
        var err = await _client.PostAsync(path, body);
        if (err is not null) _icon.ShowBalloonTip(5000, "Backup Mirror", err, ToolTipIcon.Warning);
        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await _client.GetStatusAsync();
            if (s is null)
            {
                SetHealth(Health.Offline, "Service not reachable");
                _syncAllItem.Enabled = _armItem.Enabled = false;
                return;
            }

            _armed = s.Armed;
            _armItem.Text = s.Armed ? "Pause all mirrors" : "Resume all mirrors";
            _armItem.Enabled = s.Jobs.Count > 0;
            _syncAllItem.Enabled = s.Armed && s.Jobs.Any(j => j.Enabled);

            var enabled = s.Jobs.Where(j => j.Enabled).ToList();
            var failed = enabled.Where(j => j.Status is "failed" or "degraded").ToList();
            var review = enabled.Where(j => j.Status == "waitingConfirm").ToList();
            var running = enabled.Where(j => j.Status is "running" or "queued").ToList();

            if (!s.Armed) SetHealth(Health.Paused, "Paused");
            else if (failed.Count > 0) SetHealth(Health.Failed, $"{failed.Count} need attention");
            else if (review.Count > 0) SetHealth(Health.Attention, $"{review.Count} waiting for review");
            else if (running.Count > 0) SetHealth(Health.Running, running.Count == 1 ? $"Syncing {running[0].Name}" : $"Syncing {running.Count} mirrors");
            else SetHealth(Health.Ok, s.Jobs.Count == 0 ? "No mirrors yet" : "All mirrors protected");

            // Notify on transitions into a state that needs the user.
            foreach (var j in s.Jobs)
            {
                _lastStatus.TryGetValue(j.Id, out var prev);
                _lastStatus[j.Id] = j.Status;
                if (_firstPoll || prev == j.Status || !j.Enabled) continue;
                if (j.Status is "failed" or "degraded")
                    _icon.ShowBalloonTip(8000, $"{j.Name} failed", j.Message ?? "Open Backup Mirror for details.", ToolTipIcon.Error);
                else if (j.Status == "waitingConfirm")
                    _icon.ShowBalloonTip(8000, $"{j.Name} needs review", $"{j.PendingDeleteCount:N0} files would be deleted from the mirror.", ToolTipIcon.Warning);
            }

            _firstPoll = false;
        }
        finally
        {
            _polling = false;
        }
    }

    private void SetHealth(Health h, string text)
    {
        _statusItem.Text = text;
        var tip = $"Backup Mirror — {text}";
        _icon.Text = tip.Length > 63 ? tip[..62] + "…" : tip;
        if (h == _health) return;
        _health = h;
        _icon.Icon = IconFor(h);
    }

    private static Icon LoadAppIcon()
    {
        using var s = typeof(TrayContext).Assembly.GetManifestResourceStream("app.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }

    /// <summary>Brand mark with a coloured status dot, rendered for the current DPI.</summary>
    private Icon IconFor(Health h)
    {
        if (_icons.TryGetValue(h, out var cached)) return cached;
        var size = SystemInformation.SmallIconSize.Width * 2 >= 40 ? 32 : 16;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var app = new Icon(LoadAppIcon(), size, size))
                g.DrawIcon(app, new Rectangle(0, 0, size, size));

            Color? dot = h switch
            {
                Health.Ok => Color.FromArgb(52, 211, 153),
                Health.Running => Color.FromArgb(34, 211, 238),
                Health.Attention => Color.FromArgb(251, 191, 36),
                Health.Failed => Color.FromArgb(248, 113, 113),
                Health.Paused => Color.FromArgb(148, 156, 178),
                Health.Offline => Color.FromArgb(110, 110, 120),
                _ => null
            };
            if (dot is { } c)
            {
                var d = size * 0.5f;
                var r = new RectangleF(size - d - 0.5f, size - d - 0.5f, d, d);
                using var border = new SolidBrush(Color.FromArgb(20, 24, 40));
                g.FillEllipse(border, RectangleF.Inflate(r, size / 16f, size / 16f));
                using var fill = new SolidBrush(c);
                g.FillEllipse(fill, r);
            }
        }

        var icon = Icon.FromHandle(bmp.GetHicon());
        _icons[h] = icon;
        return icon;
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _showWait.Unregister(null);
        _icon.Visible = false;
        _icon.Dispose();
        _window?.CloseForReal();
        _client.Dispose();
        base.ExitThreadCore();
    }
}
