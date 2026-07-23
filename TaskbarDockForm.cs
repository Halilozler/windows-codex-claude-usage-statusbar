using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeCodexLimits;

internal sealed class TaskbarDockForm : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x0010;
    private const byte AcSrcOver = 0;
    private const byte AcSrcAlpha = 1;
    private const int UlwAlpha = 2;

    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly System.Windows.Forms.Timer _clockTimer;
    private readonly System.Windows.Forms.Timer _clickRestoreTimer;
    private readonly WinEventDelegate _foregroundEventDelegate;
    private readonly IntPtr _foregroundEventHook;
    private UsageSnapshot? _snapshot;
    private BarDisplayMode _displayMode = BarDisplayMode.Remaining;
    private bool _showClaude = true;
    private bool _showCodex = true;
    private ContextMenuStrip? _menu;
    private Point _screenLocation;
    private int _positionTick;
    private bool _panelEnabled;
    private IntPtr _taskbarOwner;

    public event EventHandler? DetailsRequested;

    public TaskbarDockForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ClientSize = new Size(334, 40);

        _clockTimer = new System.Windows.Forms.Timer
        {
            Interval = 250,
            Enabled = true
        };
        _clockTimer.Tick += (_, _) =>
        {
            SynchronizeWithTaskbar();
            if (!Visible)
            {
                return;
            }

            _positionTick++;
            if (_positionTick % 20 == 0)
            {
                Reposition();
            }

            if (_positionTick % 4 == 0)
            {
                RenderLayeredWindow();
            }
        };

        _clickRestoreTimer = new System.Windows.Forms.Timer
        {
            Interval = 140
        };
        _clickRestoreTimer.Tick += (_, _) =>
        {
            _clickRestoreTimer.Stop();
            Reposition();
            RenderLayeredWindow();
        };

        _foregroundEventDelegate = ForegroundWindowChanged;
        _foregroundEventHook = SetWinEventHook(
            3,
            3,
            IntPtr.Zero,
            _foregroundEventDelegate,
            0,
            0,
            0);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExLayered;
            return parameters;
        }
    }

    public void UpdateSnapshot(UsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        RenderLayeredWindow();
    }

    public void UpdateSettings(AppSettings settings)
    {
        _displayMode = settings.BarDisplayMode;
        _showClaude = settings.ShowClaude;
        _showCodex = settings.ShowCodex;
        if (Visible)
        {
            Reposition();
        }
        else
        {
            RenderLayeredWindow();
        }
    }

    public void SetMenu(ContextMenuStrip menu)
    {
        _menu = menu;
        menu.Closed += (_, _) =>
        {
            if (Visible)
            {
                Reposition();
                RenderLayeredWindow();
            }
        };
    }

    public void SetPanelVisible(bool visible)
    {
        _panelEnabled = visible;
        if (!visible)
        {
            Hide();
            return;
        }

        SynchronizeWithTaskbar();
    }

    public void Reposition()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var taskbarRect))
        {
            var fallback = Screen.PrimaryScreen?.Bounds ?? Screen.FromPoint(Cursor.Position).Bounds;
            PlaceTopmost(
                fallback.Left + 8,
                fallback.Bottom - Height - 4,
                Width,
                Height);
            return;
        }

        AttachToTaskbar(taskbar);

        var taskbarHeight = Math.Max(1, taskbarRect.Bottom - taskbarRect.Top);
        var taskbarWidth = Math.Max(1, taskbarRect.Right - taskbarRect.Left);
        if (taskbarWidth < taskbarHeight)
        {
            var screen = Screen.FromHandle(taskbar);
            PlaceTopmost(
                screen.WorkingArea.Left + 8,
                screen.WorkingArea.Bottom - Height - 8,
                Width,
                Height);
            return;
        }

        var compactWidth = DesiredWidth;
        var height = Math.Clamp(taskbarHeight - 6, 34, 42);
        int x;
        if (TaskbarIconsAreCentered())
        {
            x = taskbarRect.Left + 8;
        }
        else
        {
            var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            NativeRect trayRect = default;
            var hasTray = tray != IntPtr.Zero && GetWindowRect(tray, out trayRect);
            var rightEdge = hasTray ? trayRect.Left - 8 : taskbarRect.Right - 170;
            x = Math.Max(taskbarRect.Left + 8, rightEdge - compactWidth);
        }

        PlaceTopmost(
            x,
            taskbarRect.Top + Math.Max(2, (taskbarHeight - height) / 2),
            compactWidth,
            height);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Reposition();
        RenderLayeredWindow();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            DetailsRequested?.Invoke(this, EventArgs.Empty);
            Reposition();
            RenderLayeredWindow();
            _clickRestoreTimer.Stop();
            _clickRestoreTimer.Start();
        }
        else if (e.Button == MouseButtons.Right)
        {
            _menu?.Show(Cursor.Position);
        }
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is 0x001A or 0x007E or 0x031A)
        {
            BeginInvoke(new Action(() =>
            {
                Reposition();
                RenderLayeredWindow();
            }));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clockTimer.Dispose();
            _clickRestoreTimer.Dispose();
            if (_foregroundEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_foregroundEventHook);
            }
        }

        base.Dispose(disposing);
    }

    private void PlaceTopmost(int x, int y, int width, int height)
    {
        _screenLocation = new Point(x, y);
        SetWindowPos(
            Handle,
            HwndTopmost,
            x,
            y,
            width,
            height,
            SwpNoActivate);
        RenderLayeredWindow();
    }

    private void ForegroundWindowChanged(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(new Action(SynchronizeWithTaskbar));
            }
        }
        catch (InvalidOperationException)
        {
            // The application is shutting down.
        }
    }

    private void SynchronizeWithTaskbar()
    {
        if (!_panelEnabled)
        {
            if (Visible)
            {
                Hide();
            }

            return;
        }

        var taskbar = FindWindow("Shell_TrayWnd", null);
        var shouldHide =
            taskbar == IntPtr.Zero ||
            !IsWindowVisible(taskbar) ||
            !TaskbarIntersectsItsScreen(taskbar) ||
            ForegroundWindowCoversMonitor(taskbar);
        if (shouldHide)
        {
            if (Visible)
            {
                Hide();
            }

            return;
        }

        AttachToTaskbar(taskbar);
        if (!Visible)
        {
            Show();
        }

        Reposition();
        RenderLayeredWindow();
    }

    private void AttachToTaskbar(IntPtr taskbar)
    {
        if (_taskbarOwner == taskbar || taskbar == IntPtr.Zero)
        {
            return;
        }

        SetWindowLongPtr(Handle, -8, taskbar);
        _taskbarOwner = taskbar;
    }

    private static bool TaskbarIntersectsItsScreen(IntPtr taskbar)
    {
        if (!GetWindowRect(taskbar, out var nativeRect))
        {
            return false;
        }

        var taskbarRect = Rectangle.FromLTRB(
            nativeRect.Left,
            nativeRect.Top,
            nativeRect.Right,
            nativeRect.Bottom);
        var screenBounds = Screen.FromHandle(taskbar).Bounds;
        var intersection = Rectangle.Intersect(taskbarRect, screenBounds);
        var horizontal = taskbarRect.Width >= taskbarRect.Height;
        return horizontal
            ? intersection.Height >= Math.Min(12, taskbarRect.Height)
            : intersection.Width >= Math.Min(12, taskbarRect.Width);
    }

    private static bool ForegroundWindowCoversMonitor(IntPtr taskbar)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == taskbar)
        {
            return false;
        }

        if (!GetWindowRect(foreground, out var foregroundRect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(foreground, 2);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        const int tolerance = 2;
        return
            foregroundRect.Left <= monitorInfo.Monitor.Left + tolerance &&
            foregroundRect.Top <= monitorInfo.Monitor.Top + tolerance &&
            foregroundRect.Right >= monitorInfo.Monitor.Right - tolerance &&
            foregroundRect.Bottom >= monitorInfo.Monitor.Bottom - tolerance;
    }

    private void RenderLayeredWindow()
    {
        if (!IsHandleCreated || IsDisposed || !Visible || Width <= 0 || Height <= 0)
        {
            return;
        }

        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            using var hitSurface = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
            graphics.FillRectangle(hitSurface, 0, 0, Width, Height);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            DrawContents(graphics);
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
        var previousObject = SelectObject(memoryDc, bitmapHandle);
        try
        {
            var destination = new NativePoint(_screenLocation.X, _screenLocation.Y);
            var source = new NativePoint(0, 0);
            var size = new NativeSize(Width, Height);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            UpdateLayeredWindow(
                Handle,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                UlwAlpha);
            DwmFlush();
        }
        finally
        {
            SelectObject(memoryDc, previousObject);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void DrawContents(Graphics graphics)
    {
        var lightTheme = TaskbarUsesLightTheme();
        var primary = lightTheme
            ? Color.FromArgb(238, 27, 29, 34)
            : Color.FromArgb(242, 244, 246, 250);
        var secondary = lightTheme
            ? Color.FromArgb(166, 52, 56, 65)
            : Color.FromArgb(174, 193, 198, 208);
        var inactive = lightTheme
            ? Color.FromArgb(82, 38, 41, 48)
            : Color.FromArgb(86, 220, 223, 229);

        var x = 0f;
        if (_showClaude)
        {
            DrawProvider(
                graphics,
                _snapshot?.Claude,
                BrandAssets.Claude,
                Color.FromArgb(225, 222, 117, 80),
                primary,
                secondary,
                inactive,
                new RectangleF(x, 0, 166, Height));
            x += 166;
        }

        if (_showClaude && _showCodex)
        {
            using var separator = new Pen(
                lightTheme
                    ? Color.FromArgb(54, 31, 34, 40)
                    : Color.FromArgb(54, 235, 238, 244));
            graphics.DrawLine(separator, x + 3, 8, x + 3, Height - 8);
            x += 8;
        }

        if (_showCodex)
        {
            DrawProvider(
                graphics,
                _snapshot?.Codex,
                BrandAssets.Codex,
                Color.FromArgb(225, 18, 166, 128),
                primary,
                secondary,
                inactive,
                new RectangleF(x, 0, 160, Height));
        }
    }

    private void DrawProvider(
        Graphics graphics,
        ProviderUsage? provider,
        Image logo,
        Color accent,
        Color primary,
        Color secondary,
        Color inactive,
        RectangleF bounds)
    {
        var iconSize = Math.Min(25f, bounds.Height - 10);
        var iconBounds = new RectangleF(
            bounds.X + 1,
            bounds.Y + (bounds.Height - iconSize) / 2f,
            iconSize,
            iconSize);
        DrawLogo(graphics, logo, iconBounds);

        var contentX = bounds.X + 31;
        if (provider is null)
        {
            DrawStatus(graphics, "Loading…", primary, contentX, bounds.Height / 2f - 7);
            return;
        }

        if (!provider.IsAvailable)
        {
            DrawStatus(graphics, "No data", secondary, contentX, bounds.Height / 2f - 7);
            return;
        }

        var windows = ProviderCard.OrderedWindows(provider).Take(2).ToArray();
        if (windows.Length == 1)
        {
            DrawLimitRow(
                graphics,
                windows[0],
                accent,
                primary,
                secondary,
                inactive,
                contentX,
                bounds.Y + (bounds.Height - 16) / 2f);
            return;
        }

        for (var index = 0; index < windows.Length; index++)
        {
            DrawLimitRow(
                graphics,
                windows[index],
                accent,
                primary,
                secondary,
                inactive,
                contentX,
                bounds.Y + 2 + index * 18);
        }
    }

    private void DrawLimitRow(
        Graphics graphics,
        LimitWindow window,
        Color accent,
        Color primary,
        Color secondary,
        Color inactive,
        float x,
        float y)
    {
        using var labelFont = new Font("Segoe UI Semibold", 5.9f);
        using var valueFont = new Font("Segoe UI Semibold", 6.8f);
        using var timeFont = new Font("Segoe UI Semibold", 6.5f, FontStyle.Bold);
        using var primaryBrush = new SolidBrush(primary);
        using var secondaryBrush = new SolidBrush(secondary);

        var label = ProviderCard.IsWeekly(window)
            ? "7D"
            : "5H";
        graphics.DrawString(label, labelFont, secondaryBrush, x, y + 2);

        var shownPercent = _displayMode == BarDisplayMode.Remaining
            ? window.RemainingPercent
            : window.UsedPercent;
        var warning = window.RemainingPercent <= 15;
        var fillColor = warning ? Color.FromArgb(236, 238, 83, 98) : accent;

        const int segmentCount = 5;
        const float segmentWidth = 6;
        const float segmentGap = 2;
        var segmentX = x + 18;
        var filledCount = (int)Math.Round(
            Math.Clamp(shownPercent, 0, 100) / 100d * segmentCount,
            MidpointRounding.AwayFromZero);
        for (var index = 0; index < segmentCount; index++)
        {
            using var brush = new SolidBrush(index < filledCount ? fillColor : inactive);
            graphics.FillRectangle(
                brush,
                segmentX + index * (segmentWidth + segmentGap),
                y + 3,
                segmentWidth,
                9);
        }

        var percentX = segmentX + segmentCount * (segmentWidth + segmentGap) + 1;
        graphics.DrawString($"%{Math.Round(shownPercent)}", valueFont, primaryBrush, percentX, y);

        var reset = ProviderCard.FormatResetCompact(window.ResetsAt);
        if (reset.Length > 0)
        {
            graphics.DrawString(reset, timeFont, primaryBrush, percentX + 31, y + 1);
        }
    }

    private int DesiredWidth =>
        (_showClaude ? 166 : 0) +
        (_showClaude && _showCodex ? 8 : 0) +
        (_showCodex ? 160 : 0);

    private static void DrawLogo(Graphics graphics, Image logo, RectangleF bounds)
    {
        using var tile = new SolidBrush(Color.FromArgb(74, 255, 255, 255));
        using var tilePath = GlassGraphics.RoundedRectangle(bounds, 6);
        graphics.FillPath(tile, tilePath);
        var imageBounds = RectangleF.Inflate(bounds, -3.5f, -3.5f);
        graphics.DrawImage(logo, imageBounds);
    }

    private static void DrawStatus(
        Graphics graphics,
        string text,
        Color color,
        float x,
        float y)
    {
        using var brush = new SolidBrush(color);
        using var font = new Font("Segoe UI Semibold", 7f);
        graphics.DrawString(text, font, brush, x, y);
    }

    private static bool TaskbarIconsAreCentered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            var configuredAlignment = key?.GetValue("TaskbarAl");
            return configuredAlignment is int alignment
                ? alignment == 1
                : OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        }
        catch
        {
            return false;
        }
    }

    private static bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int theme || theme != 0;
        }
        catch
        {
            return true;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(
        IntPtr parentHandle,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr handle,
        int index,
        IntPtr newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr handle, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr handle,
        IntPtr destinationDeviceContext,
        ref NativePoint destination,
        ref NativeSize size,
        IntPtr sourceDeviceContext,
        ref NativePoint source,
        int colorKey,
        ref BlendFunction blend,
        int flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventDelegate eventDelegate,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr eventHook);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    private delegate void WinEventDelegate(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);
}

internal static class GlassGraphics
{
    public static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
