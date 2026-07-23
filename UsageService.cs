using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodexLimits;

/// <summary>
/// The local Claude session is missing, empty, or expired. Retrying quickly
/// cannot help, so the caller backs off further than for a transient failure.
/// </summary>
internal sealed class ClaudeAuthException(string message) : InvalidOperationException(message);

internal sealed class UsageService
{
    private const string ClaudeOAuthTokenUrl =
        "https://platform.claude.com/v1/oauth/token";
    private const string ClaudeCodeClientId =
        "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private static readonly HttpClient ClaudeHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // The Anthropic usage endpoint rate-limits aggressively. Two minutes keeps
    // Claude Desktop/Cowork readings reasonably fresh while leaving margin
    // above the observed rate-limit floor. Terminal Claude Code sessions still
    // arrive immediately through the status-line bridge.
    private static readonly TimeSpan AccountPollInterval = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ForcedLiveInterval = TimeSpan.FromSeconds(95);

    // Claude Code writes its own rate-limit payload through the status-line
    // bridge every time it renders, which is exactly when usage moves. A recent
    // write is as current as a live read and costs nothing.
    private static readonly TimeSpan BridgeLiveWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CacheStaleAfter = TimeSpan.FromMinutes(10);

    private readonly string _appDataDirectory;
    private readonly string _bridgeCachePath;
    private readonly string _liveCachePath;
    private readonly SemaphoreSlim _claudeGate = new(1, 1);

    private ProviderUsage? _lastLiveClaude;
    private DateTimeOffset _lastLiveAttempt = DateTimeOffset.MinValue;
    private TimeSpan _liveBackoff = TimeSpan.Zero;
    private bool _seeded;

    private sealed record ClaudeSession(
        string AccessToken,
        string? RefreshToken,
        DateTimeOffset? ExpiresAt,
        string CredentialPath);

    public string AppDataDirectory => _appDataDirectory;

    public UsageService()
    {
        _appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodexLimits");
        Directory.CreateDirectory(_appDataDirectory);
        _bridgeCachePath = Path.Combine(_appDataDirectory, "claude-usage.json");
        _liveCachePath = Path.Combine(_appDataDirectory, "claude-live-cache.json");
    }

    public async Task<UsageSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken,
        bool forceLive = false)
    {
        var claudeTask = ReadClaudeAsync(forceLive, cancellationToken);
        var codexTask = ReadCodexAsync(cancellationToken);
        await Task.WhenAll(claudeTask, codexTask);

        return new UsageSnapshot(
            await claudeTask,
            await codexTask,
            DateTimeOffset.Now);
    }

    private async Task<ProviderUsage> ReadClaudeAsync(
        bool forceLive,
        CancellationToken cancellationToken)
    {
        await _claudeGate.WaitAsync(cancellationToken);
        try
        {
            // A restart should not spend a request when the previous run already
            // read the account moments ago; that call would only return HTTP 429
            // and push the process into a needless backoff.
            if (!_seeded)
            {
                _seeded = true;
                var seed = await ReadClaudeCacheFileAsync(_liveCachePath, cancellationToken);
                if (seed?.UpdatedAt is { } seededAt &&
                    DateTimeOffset.Now - seededAt < AccountPollInterval)
                {
                    _lastLiveClaude = seed with
                    {
                        Note = $"Live · Anthropic account · read {seededAt.ToLocalTime():HH:mm}"
                    };
                    _lastLiveAttempt = seededAt;
                }
            }

            // A window that just rolled over makes the held reading meaningless,
            // so treat it like a manual refresh rather than waiting out the
            // full interval.
            var rolledOver = _lastLiveClaude?.Windows.Any(
                window => window.ResetsAt is { } at && at <= DateTimeOffset.Now) == true;

            // The backoff applies to manual refreshes too, so repeated clicking
            // cannot stampede into a longer lockout.
            var interval = forceLive || rolledOver
                ? ForcedLiveInterval + _liveBackoff
                : AccountPollInterval + _liveBackoff;

            if (DateTimeOffset.Now - _lastLiveAttempt < interval)
            {
                // Still inside the throttle window: reuse what is already known
                // instead of spending a request that would come back HTTP 429.
                // The status-line bridge writes while Claude Code is in use, so
                // it can be newer than the last live read.
                var bridge = await ReadClaudeCacheFileAsync(_bridgeCachePath, cancellationToken);
                if (_lastLiveClaude is not null &&
                    (bridge?.UpdatedAt ?? DateTimeOffset.MinValue) <= (_lastLiveClaude.UpdatedAt ?? DateTimeOffset.MinValue))
                {
                    return _lastLiveClaude;
                }

                return await ReadClaudeFallbackAsync(
                    _liveBackoff > TimeSpan.Zero ? "waiting before the next live attempt" : null,
                    cancellationToken);
            }

            _lastLiveAttempt = DateTimeOffset.Now;

            string reason;
            try
            {
                var live = await ReadClaudeDirectAsync(cancellationToken);
                _liveBackoff = TimeSpan.Zero;
                _lastLiveClaude = live;
                await WriteClaudeLiveCacheAsync(live, cancellationToken);
                return live;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is
                IOException or
                JsonException or
                HttpRequestException or
                InvalidOperationException or
                UnauthorizedAccessException or
                OperationCanceledException)
            {
                _liveBackoff = NextBackoff(ex, _liveBackoff);
                reason = DescribeClaudeFailure(ex);
            }

            return await ReadClaudeFallbackAsync(reason, cancellationToken);
        }
        finally
        {
            _claudeGate.Release();
        }
    }

    private static TimeSpan NextBackoff(Exception failure, TimeSpan current) => failure switch
    {
        ClaudeAuthException => TimeSpan.FromMinutes(5),
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => TimeSpan.FromSeconds(
            Math.Min(
                MaximumBackoff.TotalSeconds,
                current <= TimeSpan.Zero ? 60d : current.TotalSeconds * 2d)),
        HttpRequestException http when (int?)http.StatusCode >= 500 => TimeSpan.FromMinutes(1),
        _ => TimeSpan.FromSeconds(30)
    };

    private static string DescribeClaudeFailure(Exception failure) => failure switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            "Anthropic is rate-limiting the usage endpoint; retrying shortly.",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } or
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "The Claude session was rejected. Open Claude Code once to renew it.",
        OperationCanceledException => "The live Claude request timed out.",
        _ => failure.Message
    };

    /// <summary>
    /// Returns the freshest usable Claude reading when the live call is
    /// unavailable, labelled with its real age so stale numbers are obvious.
    /// </summary>
    private async Task<ProviderUsage> ReadClaudeFallbackAsync(
        string? reason,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(ProviderUsage Usage, string Source, bool FromBridge)>();
        if (_lastLiveClaude is not null)
        {
            candidates.Add((_lastLiveClaude, "Anthropic account", false));
        }

        var liveCache = await ReadClaudeCacheFileAsync(_liveCachePath, cancellationToken);
        if (liveCache is not null)
        {
            candidates.Add((liveCache, "Anthropic account", false));
        }

        var bridgeCache = await ReadClaudeCacheFileAsync(_bridgeCachePath, cancellationToken);
        if (bridgeCache is not null)
        {
            candidates.Add((bridgeCache, "Claude Code status line", true));
        }

        var best = candidates
            .OrderByDescending(candidate => candidate.Usage.UpdatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (best.Usage is null)
        {
            return new ProviderUsage(
                "Claude",
                [],
                null,
                "Could not retrieve Claude limits",
                reason ?? "No Claude session was found. Sign in to Claude Code once.");
        }

        // A fresh bridge write is Claude Code's own reading, not a stale copy of
        // ours, so it earns the live label even when the HTTP call just failed.
        if (best.FromBridge &&
            best.Usage.UpdatedAt is { } writtenAt &&
            DateTimeOffset.Now - writtenAt < BridgeLiveWindow)
        {
            return best.Usage with
            {
                Name = "Claude",
                Note = $"Live · Claude Code · read {writtenAt.ToLocalTime():HH:mm}"
            };
        }

        return AsCached(best.Usage, best.Source, reason);
    }

    private static ProviderUsage AsCached(ProviderUsage usage, string source, string? reason)
    {
        var now = DateTimeOffset.Now;

        // A window whose reset time has passed has already rolled over, so the
        // cached percentage no longer describes anything real.
        var windows = usage.Windows
            .Where(window => window.ResetsAt is null || window.ResetsAt > now)
            .ToArray();

        var note = usage.UpdatedAt is { } updatedAt
            ? $"Cached {updatedAt.ToLocalTime():HH:mm} · {source} · {FormatAge(now - updatedAt)} old"
            : $"Cached · {source}";

        if (reason is not null)
        {
            note = $"{note} · {reason}";
        }

        if (windows.Length == 0)
        {
            return new ProviderUsage(
                "Claude",
                [],
                usage.UpdatedAt,
                "Cached Claude limits expired",
                reason ?? "Every cached window has already reset.");
        }

        if (usage.UpdatedAt is { } stamp && now - stamp > CacheStaleAfter)
        {
            note = $"{note} · may be out of date";
        }

        return usage with
        {
            Name = "Claude",
            Windows = windows,
            Note = note
        };
    }

    private static string FormatAge(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "<1 min",
        { TotalMinutes: < 60 } => $"{(int)age.TotalMinutes} min",
        { TotalHours: < 24 } => $"{(int)age.TotalHours} h",
        _ => $"{(int)age.TotalDays} d"
    };

    private static async Task<ProviderUsage> ReadClaudeDirectAsync(CancellationToken cancellationToken)
    {
        var configDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            configDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude");
        }

        var credentialPath = Path.Combine(configDirectory, ".credentials.json");
        if (!File.Exists(credentialPath))
        {
            throw new ClaudeAuthException(
                "No Claude session was found. Sign in to Claude Code once; the terminal does not need to remain open.");
        }

        var session = await ReadClaudeSessionAsync(credentialPath, cancellationToken);
        if (session.ExpiresAt is { } expiry &&
            expiry <= DateTimeOffset.Now.AddSeconds(30))
        {
            session = await RefreshClaudeSessionAsync(session, cancellationToken);
        }

        try
        {
            return await RequestClaudeUsageAsync(session.AccessToken, cancellationToken);
        }
        catch (HttpRequestException ex) when (
            ex.StatusCode == HttpStatusCode.Unauthorized &&
            !string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            session = await RefreshClaudeSessionAsync(session, cancellationToken);
            return await RequestClaudeUsageAsync(session.AccessToken, cancellationToken);
        }
    }

    private static async Task<ClaudeSession> ReadClaudeSessionAsync(
        string credentialPath,
        CancellationToken cancellationToken)
    {
        string? accessToken;
        string? refreshToken;
        DateTimeOffset? expiresAt;
        await using (var stream = new FileStream(
            credentialPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            using var credentialDocument = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            var root = credentialDocument.RootElement;
            JsonElement oauth;
            if ((!root.TryGetProperty("claudeAiOauth", out oauth) &&
                 !root.TryGetProperty("oauth", out oauth)) ||
                oauth.ValueKind != JsonValueKind.Object)
            {
                throw new ClaudeAuthException("No Claude OAuth session was found.");
            }

            accessToken = oauth.TryGetProperty("accessToken", out var tokenElement)
                ? tokenElement.GetString()
                : oauth.TryGetProperty("access_token", out tokenElement)
                    ? tokenElement.GetString()
                    : null;
            refreshToken = oauth.TryGetProperty("refreshToken", out var refreshElement)
                ? refreshElement.GetString()
                : oauth.TryGetProperty("refresh_token", out refreshElement)
                    ? refreshElement.GetString()
                    : null;
            expiresAt = ReadExpiry(oauth);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ClaudeAuthException(
                "The Claude OAuth session is empty. Sign in to Claude Code once.");
        }

        return new ClaudeSession(
            accessToken,
            refreshToken,
            expiresAt,
            credentialPath);
    }

    private static async Task<ClaudeSession> RefreshClaudeSessionAsync(
        ClaudeSession session,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            throw new ClaudeAuthException(
                "The Claude session expired and has no refresh token. Sign in to Claude Code once.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ClaudeOAuthTokenUrl);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("WindowsAIStatusbar/3.0.4");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                refresh_token = session.RefreshToken,
                client_id = ClaudeCodeClientId
            }),
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await ClaudeHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ClaudeAuthException(
                $"Claude session refresh returned HTTP {(int)response.StatusCode}.");
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var accessElement)
            ? accessElement.GetString()
            : null;
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshElement)
            ? refreshElement.GetString()
            : session.RefreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var expiryElement) &&
                        expiryElement.ValueKind == JsonValueKind.Number &&
                        expiryElement.TryGetInt64(out var seconds)
            ? seconds
            : 0L;

        if (string.IsNullOrWhiteSpace(accessToken) || expiresIn <= 0)
        {
            throw new ClaudeAuthException(
                "Claude session refresh returned an incomplete response.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        var refreshed = new ClaudeSession(
            accessToken,
            refreshToken,
            expiresAt,
            session.CredentialPath);
        await PersistClaudeSessionAsync(refreshed, cancellationToken);
        return refreshed;
    }

    private static async Task PersistClaudeSessionAsync(
        ClaudeSession session,
        CancellationToken cancellationToken)
    {
        JsonNode? root;
        await using (var stream = new FileStream(
            session.CredentialPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        if (root is not JsonObject rootObject)
        {
            throw new ClaudeAuthException("The Claude credential store is not a JSON object.");
        }

        var oauth = rootObject["claudeAiOauth"] as JsonObject ??
                    rootObject["oauth"] as JsonObject;
        if (oauth is null)
        {
            throw new ClaudeAuthException("No Claude OAuth session was found.");
        }

        oauth["accessToken"] = session.AccessToken;
        oauth["refreshToken"] = session.RefreshToken;
        oauth["expiresAt"] = session.ExpiresAt?.ToUnixTimeMilliseconds();

        var temporaryPath = session.CredentialPath + $".statusbar-{Environment.ProcessId}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                rootObject.ToJsonString(JsonOptions),
                cancellationToken);
            File.Replace(temporaryPath, session.CredentialPath, null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<ProviderUsage> RequestClaudeUsageAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("WindowsAIStatusbar/3.0.4");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await ClaudeHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Claude usage service returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            responseStream,
            cancellationToken: cancellationToken);
        var windows = new List<LimitWindow>();
        AddClaudeDirectWindow(document.RootElement, "five_hour", "5-hour", windows);
        AddClaudeDirectWindow(document.RootElement, "seven_day", "Weekly", windows);

        return windows.Count > 0
            ? new ProviderUsage(
                "Claude",
                windows,
                DateTimeOffset.Now,
                Note: $"Live · Anthropic account · read {DateTimeOffset.Now:HH:mm}")
            : throw new JsonException("No limit window was found in the Claude response.");
    }

    private static DateTimeOffset? ReadExpiry(JsonElement oauth)
    {
        if (!oauth.TryGetProperty("expiresAt", out var element) &&
            !oauth.TryGetProperty("expires_at", out element))
        {
            return null;
        }

        var milliseconds = element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(element.GetString(), out var parsed) => parsed,
            _ => 0L
        };

        return milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
    }

    private static void AddClaudeDirectWindow(
        JsonElement root,
        string propertyName,
        string label,
        ICollection<LimitWindow> windows)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("utilization", out var usedElement) ||
            usedElement.ValueKind != JsonValueKind.Number ||
            !usedElement.TryGetDouble(out var usedPercent))
        {
            return;
        }

        DateTimeOffset? resetsAt = null;
        if (value.TryGetProperty("resets_at", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetElement.GetString(), out var parsedReset))
        {
            resetsAt = parsedReset;
        }

        windows.Add(new LimitWindow(label, usedPercent, resetsAt));
    }

    /// <summary>
    /// Reads one of the on-disk snapshots. Returns <see langword="null"/> when
    /// the file is missing, unreadable, or carries no usable window.
    /// </summary>
    private static async Task<ProviderUsage?> ReadClaudeCacheFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var windows = new List<LimitWindow>();

            AddClaudeWindow(root, "five_hour", "5-hour", windows);
            AddClaudeWindow(root, "seven_day", "Weekly", windows);
            if (windows.Count == 0)
            {
                return null;
            }

            DateTimeOffset? updatedAt = null;
            if (root.TryGetProperty("updated_at", out var updatedElement) &&
                updatedElement.ValueKind == JsonValueKind.Number &&
                updatedElement.TryGetInt64(out var updatedUnix))
            {
                updatedAt = DateTimeOffset.FromUnixTimeSeconds(updatedUnix);
            }

            return new ProviderUsage("Claude", windows, updatedAt);
        }
        catch (Exception ex) when (ex is
            IOException or
            JsonException or
            InvalidOperationException or
            UnauthorizedAccessException)
        {
            // A malformed or partially written cache is never worth crashing
            // over; the caller simply treats it as one absent source.
            return null;
        }
    }

    /// <summary>
    /// Mirrors a successful live reading to disk so a restart starts from real
    /// data instead of whatever the status-line bridge last wrote.
    /// </summary>
    private async Task WriteClaudeLiveCacheAsync(ProviderUsage usage, CancellationToken cancellationToken)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["updated_at"] = (usage.UpdatedAt ?? DateTimeOffset.Now).ToUnixTimeSeconds()
            };

            foreach (var window in usage.Windows)
            {
                var key = window.Label == "Weekly" ? "seven_day" : "five_hour";
                payload[key] = new Dictionary<string, object?>
                {
                    ["used_percentage"] = window.UsedPercent,
                    ["resets_at"] = window.ResetsAt?.ToUnixTimeSeconds()
                };
            }

            var temporaryPath = _liveCachePath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(payload, JsonOptions),
                cancellationToken);
            File.Move(temporaryPath, _liveCachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is an optimisation; losing it is not worth surfacing.
        }
    }

    private static void AddClaudeWindow(
        JsonElement root,
        string propertyName,
        string label,
        ICollection<LimitWindow> windows)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // TryGetDouble / TryGetInt64 throw on a non-Number token rather than
        // returning false, and the status-line bridge writes null for a window
        // it has no data for, so every read is guarded by ValueKind first.
        if (!value.TryGetProperty("used_percentage", out var usedElement) ||
            usedElement.ValueKind != JsonValueKind.Number ||
            !usedElement.TryGetDouble(out var usedPercent))
        {
            return;
        }

        DateTimeOffset? resetsAt = null;
        if (value.TryGetProperty("resets_at", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number &&
            resetElement.TryGetInt64(out var resetUnix))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
        }

        windows.Add(new LimitWindow(label, usedPercent, resetsAt));
    }

    private async Task<ProviderUsage> ReadCodexAsync(CancellationToken cancellationToken)
    {
        var codexPath = FindCodexExecutable();
        if (codexPath is null)
        {
            return new ProviderUsage(
                "Codex",
                [],
                null,
                "Codex CLI was not found",
                "Codex CLI must be installed and signed in.");
        }

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = codexPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _appDataDirectory
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.Environment["RUST_LOG"] = "error";

            process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Could not start Codex app-server.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            await WriteJsonLineAsync(process, new
            {
                method = "initialize",
                id = 0,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "windows_ai_statusbar",
                        title = "Windows AI Statusbar",
                        version = "3.0.0"
                    },
                    capabilities = new { experimentalApi = false }
                }
            });

            var initialized = false;
            while (!timeout.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (!root.TryGetProperty("id", out var idElement))
                {
                    continue;
                }

                if (idElement.ValueKind == JsonValueKind.Number && idElement.GetInt32() == 0 && !initialized)
                {
                    initialized = true;
                    await WriteJsonLineAsync(process, new
                    {
                        method = "initialized",
                        @params = new { }
                    });
                    await WriteJsonLineAsync(process, new
                    {
                        method = "account/rateLimits/read",
                        id = 1,
                        @params = (object?)null
                    });
                    continue;
                }

                if (idElement.ValueKind == JsonValueKind.Number && idElement.GetInt32() == 1)
                {
                    if (root.TryGetProperty("error", out var errorElement))
                    {
                        var errorMessage = errorElement.TryGetProperty("message", out var errorMessageElement)
                            ? errorMessageElement.GetString()
                            : "Codex limit request failed.";
                        throw new InvalidOperationException(errorMessage);
                    }

                    if (!root.TryGetProperty("result", out var result))
                    {
                        throw new InvalidOperationException("Codex response is incomplete.");
                    }

                    return ParseCodexResult(result);
                }
            }

            throw new TimeoutException("Codex limit request timed out.");
        }
        catch (Exception ex) when (ex is
            IOException or
            JsonException or
            InvalidOperationException or
            TimeoutException or
            OperationCanceledException)
        {
            return new ProviderUsage(
                "Codex",
                [],
                null,
                "Could not retrieve Codex limits",
                ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? "The operation was cancelled."
                    : ex.Message);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    process.StandardInput.Close();
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best effort shutdown.
                }
                process.Dispose();
            }
        }
    }

    private static async Task WriteJsonLineAsync(Process process, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await process.StandardInput.WriteLineAsync(json);
        await process.StandardInput.FlushAsync();
    }

    private static ProviderUsage ParseCodexResult(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimits", out var rateLimits) ||
            rateLimits.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Codex rateLimits field was not found.");
        }

        var windows = new List<LimitWindow>();
        AddCodexWindow(rateLimits, "primary", windows);
        AddCodexWindow(rateLimits, "secondary", windows);

        var plan = rateLimits.TryGetProperty("planType", out var planElement)
            ? planElement.GetString()
            : null;
        var note = string.IsNullOrWhiteSpace(plan) ? null : $"Plan: {plan}";

        return windows.Count > 0
            ? new ProviderUsage("Codex", windows, DateTimeOffset.Now, Note: note)
            : new ProviderUsage(
                "Codex",
                [],
                DateTimeOffset.Now,
                "No active limit window was found",
                note);
    }

    private static void AddCodexWindow(
        JsonElement rateLimits,
        string propertyName,
        ICollection<LimitWindow> windows)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !window.TryGetProperty("usedPercent", out var usedElement) ||
            !usedElement.TryGetDouble(out var usedPercent))
        {
            return;
        }

        long? durationMinutes = null;
        if (window.TryGetProperty("windowDurationMins", out var durationElement) &&
            durationElement.TryGetInt64(out var duration))
        {
            durationMinutes = duration;
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var resetElement) &&
            resetElement.TryGetInt64(out var resetUnix))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
        }

        var label = durationMinutes switch
        {
            <= 360 and > 0 => "5-hour",
            >= 9_000 => "Weekly",
            > 0 => $"{Math.Round(durationMinutes.Value / 60d)}-hour",
            _ => propertyName == "primary" ? "Primary limit" : "Secondary limit"
        };

        windows.Add(new LimitWindow(label, usedPercent, resetsAt));
    }

    private static string? FindCodexExecutable()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(
                appData,
                "npm",
                "node_modules",
                "@openai",
                "codex",
                "node_modules",
                "@openai",
                "codex-win32-x64",
                "vendor",
                "x86_64-pc-windows-msvc",
                "bin",
                "codex.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "codex",
                "codex.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "codex.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }
}
