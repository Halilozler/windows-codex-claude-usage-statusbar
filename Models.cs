namespace ClaudeCodexLimits;

internal sealed record LimitWindow(
    string Label,
    double UsedPercent,
    DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
}

internal sealed record ProviderUsage(
    string Name,
    IReadOnlyList<LimitWindow> Windows,
    DateTimeOffset? UpdatedAt,
    string? Error = null,
    string? Note = null)
{
    public bool IsAvailable => Error is null && Windows.Count > 0;

    public double? LowestRemaining =>
        Windows.Count == 0 ? null : Windows.Min(window => window.RemainingPercent);
}

internal sealed record UsageSnapshot(
    ProviderUsage Claude,
    ProviderUsage Codex,
    DateTimeOffset UpdatedAt);
