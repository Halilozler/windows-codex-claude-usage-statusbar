using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudeCodexLimits;

internal sealed class DetailsForm : Form
{
    private readonly ProviderCard _claudeCard;
    private readonly ProviderCard _codexCard;
    private readonly Label _updatedLabel;
    private readonly Button _refreshButton;
    private readonly Button _remainingButton;
    private readonly Button _usedButton;
    private readonly CheckBox _taskbarPanelCheckBox;
    private readonly CheckBox _claudeVisibleCheckBox;
    private readonly CheckBox _codexVisibleCheckBox;
    private bool _updatingSettings;

    public event EventHandler? RefreshRequested;
    public event EventHandler<BarDisplayMode>? DisplayModeChanged;
    public event EventHandler<bool>? LargePanelVisibilityChanged;
    public event EventHandler<bool>? ClaudeVisibilityChanged;
    public event EventHandler<bool>? CodexVisibilityChanged;

    public DetailsForm()
    {
        Text = "Windows AI Statusbar";
        BackColor = Color.FromArgb(13, 14, 18);
        ForeColor = Color.White;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(580, 620);
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;

        var brand = new Label
        {
            AutoSize = true,
            Text = "LIMITS",
            Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
            ForeColor = Color.FromArgb(133, 139, 153),
            Location = new Point(23, 17),
            BackColor = Color.Transparent
        };
        var title = new Label
        {
            AutoSize = true,
            Text = "AI usage center",
            Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold),
            ForeColor = Color.FromArgb(247, 248, 251),
            Location = new Point(20, 34),
            BackColor = Color.Transparent
        };
        _updatedLabel = new Label
        {
            AutoSize = true,
            Text = "Connecting…",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.FromArgb(142, 148, 162),
            Location = new Point(24, 67),
            BackColor = Color.Transparent
        };

        _refreshButton = CreateGlassButton("↻  Refresh", new Size(92, 32));
        _refreshButton.Location = new Point(431, 29);
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

        var closeButton = CreateGlassButton("×", new Size(32, 32));
        closeButton.Font = new Font("Segoe UI", 14f);
        closeButton.Location = new Point(531, 29);
        closeButton.Click += (_, _) => Hide();

        _claudeCard = new ProviderCard
        {
            ProviderName = "Claude",
            ProviderLogo = BrandAssets.Claude,
            AccentColor = Color.FromArgb(224, 126, 91),
            Location = new Point(20, 96),
            Size = new Size(540, 184)
        };
        _codexCard = new ProviderCard
        {
            ProviderName = "Codex",
            ProviderLogo = BrandAssets.Codex,
            AccentColor = Color.FromArgb(45, 203, 157),
            Location = new Point(20, 294),
            Size = new Size(540, 184)
        };

        var settingsPanel = new GlassSettingsPanel
        {
            Location = new Point(20, 492),
            Size = new Size(540, 108)
        };
        var settingsTitle = new Label
        {
            AutoSize = true,
            Text = "DISPLAY",
            Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
            ForeColor = Color.FromArgb(139, 145, 160),
            Location = new Point(16, 13),
            BackColor = Color.Transparent
        };
        _remainingButton = CreateGlassButton("Remaining", new Size(104, 34));
        _remainingButton.Location = new Point(16, 39);
        _remainingButton.Click += (_, _) =>
        {
            if (!_updatingSettings)
            {
                DisplayModeChanged?.Invoke(this, BarDisplayMode.Remaining);
            }
        };
        _usedButton = CreateGlassButton("Used", new Size(104, 34));
        _usedButton.Location = new Point(127, 39);
        _usedButton.Click += (_, _) =>
        {
            if (!_updatingSettings)
            {
                DisplayModeChanged?.Invoke(this, BarDisplayMode.Used);
            }
        };

        _taskbarPanelCheckBox = new CheckBox
        {
            AutoSize = true,
            Text = "Taskbar panel",
            Font = new Font("Segoe UI Semibold", 9f),
            ForeColor = Color.FromArgb(231, 233, 239),
            BackColor = Color.Transparent,
            Location = new Point(310, 35),
            Cursor = Cursors.Hand
        };
        _taskbarPanelCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_updatingSettings)
            {
                LargePanelVisibilityChanged?.Invoke(this, _taskbarPanelCheckBox.Checked);
            }
        };

        var providersLabel = new Label
        {
            AutoSize = true,
            Text = "SHOW",
            Font = new Font("Segoe UI Semibold", 7.2f, FontStyle.Bold),
            ForeColor = Color.FromArgb(119, 125, 140),
            Location = new Point(310, 69),
            BackColor = Color.Transparent
        };
        _claudeVisibleCheckBox = CreateProviderCheckBox("Claude", new Point(361, 66));
        _claudeVisibleCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_updatingSettings)
            {
                ClaudeVisibilityChanged?.Invoke(this, _claudeVisibleCheckBox.Checked);
            }
        };
        _codexVisibleCheckBox = CreateProviderCheckBox("Codex", new Point(445, 66));
        _codexVisibleCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_updatingSettings)
            {
                CodexVisibilityChanged?.Invoke(this, _codexVisibleCheckBox.Checked);
            }
        };

        settingsPanel.Controls.Add(settingsTitle);
        settingsPanel.Controls.Add(_remainingButton);
        settingsPanel.Controls.Add(_usedButton);
        settingsPanel.Controls.Add(_taskbarPanelCheckBox);
        settingsPanel.Controls.Add(providersLabel);
        settingsPanel.Controls.Add(_claudeVisibleCheckBox);
        settingsPanel.Controls.Add(_codexVisibleCheckBox);

        Controls.Add(brand);
        Controls.Add(title);
        Controls.Add(_updatedLabel);
        Controls.Add(_refreshButton);
        Controls.Add(closeButton);
        Controls.Add(_claudeCard);
        Controls.Add(_codexCard);
        Controls.Add(settingsPanel);

        foreach (var draggable in new Control[] { this, brand, title, _updatedLabel })
        {
            draggable.MouseDown += DragWindow;
        }

        FormClosing += (_, args) =>
        {
            args.Cancel = true;
            Hide();
        };
        SizeChanged += (_, _) => ApplyRoundedRegion();
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowEffects.ApplyGlass(Handle);
        ApplyRoundedRegion();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var bounds = ClientRectangle;
        using var background = new LinearGradientBrush(
            bounds,
            Color.FromArgb(28, 29, 38),
            Color.FromArgb(10, 11, 15),
            118f);
        e.Graphics.FillRectangle(background, bounds);

        using var glowClaude = new SolidBrush(Color.FromArgb(18, 224, 126, 91));
        using var glowCodex = new SolidBrush(Color.FromArgb(13, 45, 203, 157));
        e.Graphics.FillEllipse(glowClaude, -150, -210, 500, 430);
        e.Graphics.FillEllipse(glowCodex, 315, 320, 420, 420);

        using var border = new Pen(Color.FromArgb(74, 255, 255, 255));
        using var path = GlassGraphics.RoundedRectangle(
            new RectangleF(0.5f, 0.5f, Width - 1, Height - 1),
            22);
        e.Graphics.DrawPath(border, path);
    }

    public void SetLoading(bool loading)
    {
        _refreshButton.Enabled = !loading;
        _refreshButton.Text = loading ? "Refreshing…" : "↻  Refresh";
    }

    public void UpdateSnapshot(UsageSnapshot snapshot)
    {
        _claudeCard.Usage = snapshot.Claude;
        _codexCard.Usage = snapshot.Codex;
        _updatedLabel.Text =
            $"Last checked  {snapshot.UpdatedAt:HH:mm:ss}  •  every 30s  •  Claude live read every 5 min";
    }

    public void UpdateSettings(AppSettings settings)
    {
        _updatingSettings = true;
        try
        {
            _taskbarPanelCheckBox.Checked = settings.ShowLargePanel;
            _claudeVisibleCheckBox.Checked = settings.ShowClaude;
            _codexVisibleCheckBox.Checked = settings.ShowCodex;
            _claudeCard.DisplayMode = settings.BarDisplayMode;
            _codexCard.DisplayMode = settings.BarDisplayMode;
            SetChoiceStyle(_remainingButton, settings.BarDisplayMode == BarDisplayMode.Remaining);
            SetChoiceStyle(_usedButton, settings.BarDisplayMode == BarDisplayMode.Used);
        }
        finally
        {
            _updatingSettings = false;
        }
    }

    public void ShowNearTray()
    {
        var screen = Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
        var working = screen.WorkingArea;
        Location = new Point(
            working.Right - Width - 16,
            working.Bottom - Height - 16);
        Show();
    }

    private static Button CreateGlassButton(string text, Size size)
    {
        var button = new Button
        {
            Text = text,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(42, 45, 55),
            ForeColor = Color.FromArgb(235, 237, 243),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(72, 77, 91);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(54, 58, 70);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(31, 34, 42);
        return button;
    }

    private static CheckBox CreateProviderCheckBox(string text, Point location) =>
        new()
        {
            AutoSize = true,
            Text = text,
            Font = new Font("Segoe UI Semibold", 8.2f),
            ForeColor = Color.FromArgb(218, 221, 229),
            BackColor = Color.Transparent,
            Location = location,
            Cursor = Cursors.Hand
        };

    private static void SetChoiceStyle(Button button, bool selected)
    {
        button.BackColor = selected
            ? Color.FromArgb(72, 82, 102)
            : Color.FromArgb(37, 40, 49);
        button.ForeColor = selected
            ? Color.White
            : Color.FromArgb(158, 163, 176);
        button.FlatAppearance.BorderColor = selected
            ? Color.FromArgb(126, 145, 181)
            : Color.FromArgb(63, 67, 79);
    }

    private void ApplyRoundedRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = GlassGraphics.RoundedRectangle(
            new RectangleF(0, 0, Width, Height),
            22);
        Region?.Dispose();
        Region = new Region(path);
    }

    private void DragWindow(object? sender, MouseEventArgs args)
    {
        if (args.Button != MouseButtons.Left)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr handle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter);
}

internal sealed class GlassSettingsPanel : Panel
{
    public GlassSettingsPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = GlassGraphics.RoundedRectangle(bounds, 17);
        using var brush = new LinearGradientBrush(
            bounds,
            Color.FromArgb(160, 43, 46, 56),
            Color.FromArgb(188, 23, 25, 32),
            LinearGradientMode.Horizontal);
        using var border = new Pen(Color.FromArgb(58, 255, 255, 255));
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(border, path);
        base.OnPaint(e);
    }
}

internal static class WindowEffects
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;
    private const int WcaAccentPolicy = 19;

    public static void ApplyGlass(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var enabled = 1;
            var rounded = 2;
            var acrylicBackdrop = 3;
            DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkMode,
                ref enabled,
                sizeof(int));
            DwmSetWindowAttribute(
                handle,
                DwmWindowCornerPreference,
                ref rounded,
                sizeof(int));
            DwmSetWindowAttribute(
                handle,
                DwmSystemBackdropType,
                ref acrylicBackdrop,
                sizeof(int));

            var accent = new AccentPolicy
            {
                AccentState = 4,
                AccentFlags = 2,
                GradientColor = unchecked((int)0xB8181715),
                AnimationId = 0
            };
            var accentSize = Marshal.SizeOf<AccentPolicy>();
            var accentPointer = Marshal.AllocHGlobal(accentSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = accentPointer,
                    SizeOfData = accentSize
                };
                SetWindowCompositionAttribute(handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPointer);
            }
        }
        catch
        {
            // The painted glass fallback remains available on unsupported Windows versions.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr handle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr handle,
        ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
