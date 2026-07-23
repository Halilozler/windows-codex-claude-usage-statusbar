using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ClaudeCodexLimits;

internal sealed class ProviderCard : Control
{
    private ProviderUsage? _usage;
    private BarDisplayMode _displayMode = BarDisplayMode.Remaining;

    [DefaultValue("")]
    public string ProviderName { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Image? ProviderLogo { get; set; }

    [DefaultValue(typeof(Color), "White")]
    public Color AccentColor { get; set; } = Color.White;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public ProviderUsage? Usage
    {
        get => _usage;
        set
        {
            _usage = value;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public BarDisplayMode DisplayMode
    {
        get => _displayMode;
        set
        {
            _displayMode = value;
            Invalidate();
        }
    }

    public ProviderCard()
    {
        DoubleBuffered = true;
        Height = 184;
        Font = new Font("Segoe UI", 9f);
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var background = new LinearGradientBrush(
            bounds,
            Color.FromArgb(188, 42, 44, 54),
            Color.FromArgb(214, 20, 21, 28),
            LinearGradientMode.ForwardDiagonal);
        using var path = GlassGraphics.RoundedRectangle(bounds, 18);
        using var border = new Pen(Color.FromArgb(64, 255, 255, 255));
        graphics.FillPath(background, path);
        graphics.DrawPath(border, path);

        var glow = new RectangleF(12, 12, 38, 38);
        using var glowPath = GlassGraphics.RoundedRectangle(glow, 11);
        using var glowBrush = new SolidBrush(Color.FromArgb(36, AccentColor));
        using var glowBorder = new Pen(Color.FromArgb(120, AccentColor));
        graphics.FillPath(glowBrush, glowPath);
        graphics.DrawPath(glowBorder, glowPath);

        using var logoTile = new SolidBrush(Color.FromArgb(218, 238, 239, 243));
        graphics.FillPath(logoTile, glowPath);
        if (ProviderLogo is not null)
        {
            graphics.DrawImage(ProviderLogo, RectangleF.Inflate(glow, -7, -7));
        }

        using var titleFont = new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
        using var titleBrush = new SolidBrush(Color.FromArgb(245, 246, 249));
        using var mutedBrush = new SolidBrush(Color.FromArgb(153, 158, 171));
        using var badgeFont = new Font("Segoe UI Semibold", 7.2f);
        graphics.DrawString(ProviderName, titleFont, titleBrush, 61, 12);

        var sourceText = _usage?.IsAvailable == true
            ? _usage.Note?.StartsWith("Live", StringComparison.OrdinalIgnoreCase) == true
                ? "LIVE"
                : "LOCAL"
            : "WAITING";
        DrawBadge(graphics, sourceText, badgeFont, AccentColor, new PointF(62, 36));

        if (_usage is null)
        {
            graphics.DrawString("Loading limits…", Font, mutedBrush, 18, 76);
            return;
        }

        if (!_usage.IsAvailable)
        {
            using var errorFont = new Font("Segoe UI Semibold", 10f);
            graphics.DrawString(_usage.Error ?? "No data", errorFont, titleBrush, 18, 76);
            DrawWrappedText(
                graphics,
                _usage.Note,
                Font,
                mutedBrush,
                new RectangleF(18, 104, Width - 36, 52));
            return;
        }

        var windows = OrderedWindows(_usage).Take(2).ToArray();
        var firstY = windows.Length == 1 ? 91f : 63f;
        for (var index = 0; index < windows.Length; index++)
        {
            var window = windows[index];
            var label = IsWeekly(window)
                ? "WEEKLY"
                : "5 HOUR";
            DrawWindow(
                graphics,
                window,
                label,
                new RectangleF(18, firstY + index * 56, Width - 36, 45));
        }
    }

    private void DrawWindow(
        Graphics graphics,
        LimitWindow? window,
        string label,
        RectangleF row)
    {
        using var labelFont = new Font("Segoe UI Semibold", 8f);
        using var valueFont = new Font("Segoe UI Semibold", 10f);
        using var timeFont = new Font("Segoe UI", 8f);
        using var muted = new SolidBrush(Color.FromArgb(151, 156, 170));
        using var bright = new SolidBrush(Color.FromArgb(239, 241, 246));

        graphics.DrawString(label, labelFont, muted, row.X, row.Y + 7);
        if (window is null)
        {
            graphics.DrawString("—", valueFont, muted, row.X + 91, row.Y + 4);
            return;
        }

        var shownPercent = _displayMode == BarDisplayMode.Remaining
            ? window.RemainingPercent
            : window.UsedPercent;
        var warning = window.RemainingPercent <= 15;
        var fillColor = warning ? Color.FromArgb(245, 91, 105) : AccentColor;

        const int segmentCount = 16;
        const float segmentWidth = 12;
        const float segmentGap = 4;
        var segmentX = row.X + 92;
        var segmentY = row.Y + 6;
        var filledCount = (int)Math.Round(
            Math.Clamp(shownPercent, 0, 100) / 100d * segmentCount,
            MidpointRounding.AwayFromZero);

        for (var index = 0; index < segmentCount; index++)
        {
            var segment = new RectangleF(
                segmentX + index * (segmentWidth + segmentGap),
                segmentY,
                segmentWidth,
                16);
            using var segmentBrush = new SolidBrush(
                index < filledCount
                    ? fillColor
                    : Color.FromArgb(68, 72, 84));
            graphics.FillRectangle(segmentBrush, segment);
        }

        var valueLabel = _displayMode == BarDisplayMode.Remaining ? "remaining" : "used";
        var percentText = $"%{Math.Round(shownPercent)}";
        var percentSize = graphics.MeasureString(percentText, valueFont);
        var percentX = row.Right - percentSize.Width;
        graphics.DrawString(percentText, valueFont, bright, percentX, row.Y + 2);

        var resetText = FormatReset(window.ResetsAt);
        var footer = $"{valueLabel}  ·  {resetText}";
        graphics.DrawString(footer, timeFont, muted, row.X + 92, row.Y + 27);
    }

    private static void DrawBadge(
        Graphics graphics,
        string text,
        Font font,
        Color accent,
        PointF location)
    {
        var size = graphics.MeasureString(text, font);
        var bounds = new RectangleF(location.X, location.Y, size.Width + 13, 16);
        using var path = GlassGraphics.RoundedRectangle(bounds, 8);
        using var brush = new SolidBrush(Color.FromArgb(30, accent));
        using var border = new Pen(Color.FromArgb(90, accent));
        using var textBrush = new SolidBrush(Color.FromArgb(215, accent));
        graphics.FillPath(brush, path);
        graphics.DrawPath(border, path);
        graphics.DrawString(text, font, textBrush, bounds.X + 6, bounds.Y + 1);
    }

    internal static IEnumerable<LimitWindow> OrderedWindows(ProviderUsage provider) =>
        provider.Windows.OrderBy(
            window => IsWeekly(window) ? 1 : 0);

    internal static bool IsWeekly(LimitWindow window) =>
        window.Label.Contains("Week", StringComparison.OrdinalIgnoreCase) ||
        window.Label.Contains("Hafta", StringComparison.OrdinalIgnoreCase);

    internal static string FormatReset(DateTimeOffset? reset)
    {
        if (reset is null)
        {
            return "";
        }

        var remaining = reset.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "refreshing…";
        }

        return remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining.Hours}h"
            : remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"{Math.Max(1, remaining.Minutes)}m";
    }

    internal static string FormatResetCompact(DateTimeOffset? reset)
    {
        if (reset is null)
        {
            return "";
        }

        var remaining = reset.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        return remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d{remaining.Hours}h"
            : remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h{remaining.Minutes}m"
                : $"{Math.Max(1, remaining.Minutes)}m";
    }

    private static void DrawWrappedText(
        Graphics graphics,
        string? text,
        Font font,
        Brush brush,
        RectangleF bounds)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisWord
        };
        graphics.DrawString(text, font, brush, bounds, format);
    }
}
