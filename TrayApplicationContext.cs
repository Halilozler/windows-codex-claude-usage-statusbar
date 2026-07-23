using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudeCodexLimits;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _claudeNotifyIcon;
    private readonly NotifyIcon _codexNotifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _claudeItem;
    private readonly ToolStripMenuItem _codexItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _largePanelItem;
    private readonly ToolStripMenuItem _showClaudeItem;
    private readonly ToolStripMenuItem _showCodexItem;
    private readonly ToolStripMenuItem _remainingModeItem;
    private readonly ToolStripMenuItem _usedModeItem;
    private readonly UsageService _usageService = new();
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly DetailsForm _detailsForm = new();
    private readonly TaskbarDockForm _dockForm = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly FileSystemWatcher _claudeWatcher;
    private readonly System.Threading.Timer _claudeChangeDebounce;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private UsageSnapshot? _snapshot;
    private Icon? _claudeIcon;
    private Icon? _codexIcon;

    public TrayApplicationContext(bool showOnStart)
    {
        _settingsStore = new SettingsStore(_usageService.AppDataDirectory);
        _settings = _settingsStore.Load();

        _claudeItem = new ToolStripMenuItem("Claude: loading…") { Enabled = false };
        _codexItem = new ToolStripMenuItem("Codex: loading…") { Enabled = false };
        _refreshItem = new ToolStripMenuItem("Refresh now");
        _startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupManager.IsEnabled,
            CheckOnClick = true
        };
        _largePanelItem = new ToolStripMenuItem("Taskbar panel")
        {
            Checked = _settings.ShowLargePanel,
            CheckOnClick = true
        };
        _remainingModeItem = new ToolStripMenuItem("Show remaining quota");
        _usedModeItem = new ToolStripMenuItem("Show used quota");
        var displayModeMenu = new ToolStripMenuItem("Bar display");
        displayModeMenu.DropDownItems.AddRange([_remainingModeItem, _usedModeItem]);
        _showClaudeItem = new ToolStripMenuItem("Claude")
        {
            Checked = _settings.ShowClaude,
            CheckOnClick = true
        };
        _showCodexItem = new ToolStripMenuItem("Codex")
        {
            Checked = _settings.ShowCodex,
            CheckOnClick = true
        };
        var providersMenu = new ToolStripMenuItem("Show on taskbar");
        providersMenu.DropDownItems.AddRange([_showClaudeItem, _showCodexItem]);

        var openItem = new ToolStripMenuItem("Open details");
        var exitItem = new ToolStripMenuItem("Exit");
        _menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            BackColor = Color.FromArgb(28, 30, 35),
            ForeColor = Color.FromArgb(236, 238, 243)
        };
        _menu.Items.AddRange(
        [
            openItem,
            new ToolStripSeparator(),
            _claudeItem,
            _codexItem,
            new ToolStripSeparator(),
            _largePanelItem,
            providersMenu,
            displayModeMenu,
            _refreshItem,
            _startupItem,
            new ToolStripSeparator(),
            exitItem
        ]);

        _claudeIcon = CreateProviderIcon(
            null,
            Color.FromArgb(217, 119, 87),
            BrandAssets.Claude);
        _codexIcon = CreateProviderIcon(
            null,
            Color.FromArgb(44, 196, 151),
            BrandAssets.Codex);

        _claudeNotifyIcon = new NotifyIcon
        {
            Visible = false,
            Icon = _claudeIcon,
            Text = "Claude: waiting for data",
            ContextMenuStrip = _menu
        };
        _codexNotifyIcon = new NotifyIcon
        {
            Visible = false,
            Icon = _codexIcon,
            Text = "Codex: waiting for data",
            ContextMenuStrip = _menu
        };

        openItem.Click += (_, _) => _detailsForm.ShowNearTray();
        exitItem.Click += (_, _) => ExitApplication();
        _refreshItem.Click += async (_, _) => await RefreshAsync(forceLive: true);
        _startupItem.Click += (_, _) => ToggleStartup();
        _largePanelItem.Click += (_, _) => SetLargePanelVisibility(_largePanelItem.Checked);
        _showClaudeItem.Click += (_, _) => SetProviderVisibility(true, _showClaudeItem.Checked);
        _showCodexItem.Click += (_, _) => SetProviderVisibility(false, _showCodexItem.Checked);
        _remainingModeItem.Click += (_, _) => SetDisplayMode(BarDisplayMode.Remaining);
        _usedModeItem.Click += (_, _) => SetDisplayMode(BarDisplayMode.Used);
        _claudeNotifyIcon.MouseClick += ShowDetailsOnLeftClick;
        _codexNotifyIcon.MouseClick += ShowDetailsOnLeftClick;
        _detailsForm.RefreshRequested += async (_, _) => await RefreshAsync(forceLive: true);
        _detailsForm.DisplayModeChanged += (_, mode) => SetDisplayMode(mode);
        _detailsForm.LargePanelVisibilityChanged += (_, visible) => SetLargePanelVisibility(visible);
        _detailsForm.ClaudeVisibilityChanged += (_, visible) => SetProviderVisibility(true, visible);
        _detailsForm.CodexVisibilityChanged += (_, visible) => SetProviderVisibility(false, visible);
        _dockForm.DetailsRequested += (_, _) => _detailsForm.ShowNearTray();
        _dockForm.SetMenu(_menu);

        _timer = new System.Windows.Forms.Timer
        {
            Interval = 30_000,
            Enabled = true
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        _detailsForm.CreateControl();
        _dockForm.CreateControl();
        _claudeChangeDebounce = new System.Threading.Timer(
            _ => QueueClaudeRefresh(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        _claudeWatcher = new FileSystemWatcher(
            _usageService.AppDataDirectory,
            "claude-usage.json")
        {
            NotifyFilter = NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime |
                           NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _claudeWatcher.Changed += ClaudeCacheChanged;
        _claudeWatcher.Created += ClaudeCacheChanged;
        _claudeWatcher.Renamed += ClaudeCacheRenamed;

        ApplySettings();
        _ = RefreshAsync();
        if (showOnStart)
        {
            _detailsForm.ShowNearTray();
        }
    }

    private void ShowDetailsOnLeftClick(object? sender, MouseEventArgs args)
    {
        if (args.Button == MouseButtons.Left)
        {
            _detailsForm.ShowNearTray();
        }
    }

    private void ClaudeCacheChanged(object sender, FileSystemEventArgs args) =>
        _claudeChangeDebounce.Change(300, Timeout.Infinite);

    private void ClaudeCacheRenamed(object sender, RenamedEventArgs args) =>
        _claudeChangeDebounce.Change(300, Timeout.Infinite);

    private void QueueClaudeRefresh()
    {
        try
        {
            if (_detailsForm.IsHandleCreated && !_detailsForm.IsDisposed)
            {
                _detailsForm.BeginInvoke(new Action(() => _ = RefreshAsync()));
            }
        }
        catch (InvalidOperationException)
        {
            // The application is closing.
        }
    }

    private async Task RefreshAsync(bool forceLive = false)
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            _detailsForm.SetLoading(true);
            _refreshItem.Enabled = false;
            _refreshItem.Text = "Refreshing…";

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _snapshot = await _usageService.GetSnapshotAsync(timeout.Token, forceLive);
            UpdateUi();
        }
        finally
        {
            _detailsForm.SetLoading(false);
            _refreshItem.Enabled = true;
            _refreshItem.Text = "Refresh now";
            _refreshGate.Release();
        }
    }

    private void UpdateUi()
    {
        if (_snapshot is null)
        {
            return;
        }

        _detailsForm.UpdateSnapshot(_snapshot);
        _dockForm.UpdateSnapshot(_snapshot);
        _claudeItem.Text = ProviderMenuText("Claude", _snapshot.Claude);
        _codexItem.Text = ProviderMenuText("Codex", _snapshot.Codex);

        var newClaudeIcon = CreateProviderIcon(
            DisplayedPercent(_snapshot.Claude),
            Color.FromArgb(217, 119, 87),
            BrandAssets.Claude);
        var newCodexIcon = CreateProviderIcon(
            DisplayedPercent(_snapshot.Codex),
            Color.FromArgb(44, 196, 151),
            BrandAssets.Codex);
        var oldClaudeIcon = _claudeIcon;
        var oldCodexIcon = _codexIcon;
        _claudeIcon = newClaudeIcon;
        _codexIcon = newCodexIcon;
        _claudeNotifyIcon.Icon = newClaudeIcon;
        _codexNotifyIcon.Icon = newCodexIcon;
        oldClaudeIcon?.Dispose();
        oldCodexIcon?.Dispose();

        _claudeNotifyIcon.Text = ProviderTooltip(_snapshot.Claude);
        _codexNotifyIcon.Text = ProviderTooltip(_snapshot.Codex);
    }

    private double? DisplayedPercent(ProviderUsage provider)
    {
        if (!provider.IsAvailable)
        {
            return null;
        }

        return _settings.BarDisplayMode == BarDisplayMode.Remaining
            ? provider.Windows.Min(window => window.RemainingPercent)
            : provider.Windows.Max(window => window.UsedPercent);
    }

    private string ProviderMenuText(string shortName, ProviderUsage provider)
    {
        if (!provider.IsAvailable)
        {
            return $"{shortName}: no data";
        }

        var valueLabel = _settings.BarDisplayMode == BarDisplayMode.Remaining ? "remaining" : "used";
        var details = ProviderCard.OrderedWindows(provider)
            .Take(2)
            .Select(window =>
            {
                var percent = _settings.BarDisplayMode == BarDisplayMode.Remaining
                    ? window.RemainingPercent
                    : window.UsedPercent;
                return $"{window.Label} {valueLabel} %{Math.Round(percent)}";
            });
        return $"{shortName}: {string.Join(" · ", details)}";
    }

    private string ProviderTooltip(ProviderUsage provider)
    {
        var valueLabel = _settings.BarDisplayMode == BarDisplayMode.Remaining ? "remaining" : "used";
        var details = provider.IsAvailable
            ? string.Join(
                " | ",
                ProviderCard.OrderedWindows(provider)
                    .Take(2)
                    .Select(window =>
                    {
                        var percent = _settings.BarDisplayMode == BarDisplayMode.Remaining
                            ? window.RemainingPercent
                            : window.UsedPercent;
                        return $"{window.Label} {valueLabel} %{Math.Round(percent)}";
                    }))
            : "no data";
        var text = $"{provider.Name}: {details}\nLeft click: details";
        return text.Length <= 127 ? text : text[..127];
    }

    private void SetDisplayMode(BarDisplayMode mode)
    {
        if (_settings.BarDisplayMode == mode)
        {
            ApplySettings();
            return;
        }

        _settings.BarDisplayMode = mode;
        SaveAndApplySettings();
        UpdateUi();
    }

    private void SetLargePanelVisibility(bool visible)
    {
        _settings.ShowLargePanel = visible;
        SaveAndApplySettings();
    }

    private void SetProviderVisibility(bool claude, bool visible)
    {
        var otherVisible = claude ? _settings.ShowCodex : _settings.ShowClaude;
        if (!visible && !otherVisible)
        {
            System.Media.SystemSounds.Beep.Play();
            ApplySettings();
            return;
        }

        if (claude)
        {
            _settings.ShowClaude = visible;
        }
        else
        {
            _settings.ShowCodex = visible;
        }

        SaveAndApplySettings();
    }

    private void SaveAndApplySettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (IOException ex)
        {
            MessageBox.Show(
                $"Could not save settings:\n{ex.Message}",
                "Windows AI Statusbar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        ApplySettings();
    }

    private void ApplySettings()
    {
        _remainingModeItem.Checked = _settings.BarDisplayMode == BarDisplayMode.Remaining;
        _usedModeItem.Checked = _settings.BarDisplayMode == BarDisplayMode.Used;
        _largePanelItem.Checked = _settings.ShowLargePanel;
        _showClaudeItem.Checked = _settings.ShowClaude;
        _showCodexItem.Checked = _settings.ShowCodex;
        _detailsForm.UpdateSettings(_settings);
        _dockForm.UpdateSettings(_settings);
        var panelVisible =
            _settings.ShowLargePanel &&
            (_settings.ShowClaude || _settings.ShowCodex);
        _dockForm.SetPanelVisible(panelVisible);
        _claudeNotifyIcon.Visible = !panelVisible && _settings.ShowClaude;
        _codexNotifyIcon.Visible = !panelVisible && _settings.ShowCodex;
    }

    private void ToggleStartup()
    {
        try
        {
            StartupManager.SetEnabled(_startupItem.Checked);
        }
        catch (Exception ex)
        {
            _startupItem.Checked = StartupManager.IsEnabled;
            MessageBox.Show(
                $"Could not update startup settings:\n{ex.Message}",
                "Windows AI Statusbar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ExitApplication()
    {
        _timer.Stop();
        _claudeWatcher.EnableRaisingEvents = false;
        _claudeNotifyIcon.Visible = false;
        _codexNotifyIcon.Visible = false;
        _dockForm.Hide();
        _detailsForm.Dispose();
        _dockForm.Dispose();
        _claudeNotifyIcon.Dispose();
        _codexNotifyIcon.Dispose();
        _menu.Dispose();
        _claudeWatcher.Dispose();
        _claudeChangeDebounce.Dispose();
        _claudeIcon?.Dispose();
        _codexIcon?.Dispose();
        _refreshGate.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon CreateProviderIcon(
        double? shownPercent,
        Color accent,
        Image logo)
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var backgroundBrush = new SolidBrush(Color.FromArgb(19, 20, 24));
        graphics.FillRoundedRectangle(backgroundBrush, new Rectangle(2, 2, 60, 60), 15);

        using var logoTile = new SolidBrush(Color.FromArgb(232, 240, 241, 244));
        graphics.FillRoundedRectangle(logoTile, new RectangleF(11, 6, 42, 34), 10);
        graphics.DrawImage(logo, new RectangleF(19, 9, 26, 26));

        DrawHorizontalMeter(
            graphics,
            new RectangleF(8, 44, 48, 10),
            shownPercent,
            accent);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawHorizontalMeter(
        Graphics graphics,
        RectangleF bounds,
        double? shownPercent,
        Color accent)
    {
        using var trackBrush = new SolidBrush(Color.FromArgb(63, 66, 75));
        graphics.FillRoundedRectangle(trackBrush, bounds, 5);

        if (shownPercent is null)
        {
            return;
        }

        var fraction = (float)Math.Clamp(shownPercent.Value / 100d, 0d, 1d);
        var fillWidth = Math.Max(5, bounds.Width * fraction);
        var fillBounds = new RectangleF(
            bounds.X,
            bounds.Y,
            fillWidth,
            bounds.Height);
        using var fillBrush = new SolidBrush(accent);
        graphics.FillRoundedRectangle(fillBrush, fillBounds, 5);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(28, 30, 35);
    public override Color ImageMarginGradientBegin => Color.FromArgb(28, 30, 35);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(28, 30, 35);
    public override Color ImageMarginGradientEnd => Color.FromArgb(28, 30, 35);
    public override Color MenuBorder => Color.FromArgb(58, 62, 72);
    public override Color MenuItemBorder => Color.FromArgb(69, 74, 86);
    public override Color MenuItemSelected => Color.FromArgb(48, 52, 61);
    public override Color SeparatorDark => Color.FromArgb(58, 62, 72);
    public override Color SeparatorLight => Color.FromArgb(58, 62, 72);
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(
        this Graphics graphics,
        Brush brush,
        Rectangle bounds,
        int radius) =>
        FillRoundedRectangle(
            graphics,
            brush,
            new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            radius);

    public static void FillRoundedRectangle(
        this Graphics graphics,
        Brush brush,
        RectangleF bounds,
        float radius)
    {
        var diameter = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
