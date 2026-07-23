using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace ClaudeCodexLimits;

internal static class BrandAssets
{
    public static Bitmap Claude { get; } =
        LoadThumbnail("ClaudeCodexLimits.Assets.claude-seeklogo.png");

    public static Bitmap Codex { get; } =
        LoadThumbnail("ClaudeCodexLimits.Assets.openai-codex-seeklogo.png");

    private static Bitmap LoadThumbnail(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Logo resource was not found: {resourceName}");
        using var source = Image.FromStream(stream);
        var thumbnail = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(thumbnail);
        graphics.Clear(Color.Transparent);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, 128, 128));
        return thumbnail;
    }
}
