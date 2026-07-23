using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ClaudeCodexLimits;

internal sealed class UsageService
{
    private static readonly HttpClient ClaudeHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _appDataDirectory;
    private readonly string _claudeCachePath;

    public string AppDataDirectory => _appDataDirectory;

    public UsageService()
    {
        _appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodexLimits");
        Directory.CreateDirectory(_appDataDirectory);
        _claudeCachePath = Path.Combine(_appDataDirectory, "claude-usage.json");
    }

    public async Task<UsageSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var claudeTask = ReadClaudeAsync(cancellationToken);
        var codexTask = ReadCodexAsync(cancellationToken);
        await Task.WhenAll(claudeTask, codexTask);

        return new UsageSnapshot(
            await claudeTask,
            await codexTask,
            DateTimeOffset.Now);
    }

    private async Task<ProviderUsage> ReadClaudeAsync(CancellationToken cancellationToken)
    {
        string? directError = null;
        try
        {
            return await ReadClaudeDirectAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is
            IOException or
            JsonException or
            HttpRequestException or
            InvalidOperationException or
            UnauthorizedAccessException or
            TaskCanceledException)
        {
            directError = ex.Message;
        }

        var cached = await ReadClaudeCacheAsync(cancellationToken);
        if (cached.IsAvailable)
        {
            var note = "Live request failed; showing the last safe Claude cache.";
            return cached with { Name = "Claude", Note = note };
        }

        return new ProviderUsage(
            "Claude",
            [],
            null,
            "Could not retrieve Claude limits",
            directError ?? "No Claude session was found.");
    }

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
            throw new InvalidOperationException(
                "No Claude session was found. Run 'claude auth login' once; the terminal does not need to remain open.");
        }

        string? accessToken;
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
                throw new InvalidOperationException("No Claude OAuth session was found.");
            }

            accessToken = oauth.TryGetProperty("accessToken", out var tokenElement)
                ? tokenElement.GetString()
                : oauth.TryGetProperty("access_token", out tokenElement)
                    ? tokenElement.GetString()
                    : null;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "The Claude OAuth session is empty. Run 'claude auth login' once.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("WindowsAIStatusbar/3.0.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await ClaudeHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Claude usage service returned HTTP {(int)response.StatusCode}.");
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
                Note: "Live · Anthropic account")
            : throw new JsonException("No limit window was found in the Claude response.");
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

    private async Task<ProviderUsage> ReadClaudeCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_claudeCachePath))
            {
                return new ProviderUsage(
                    "Claude Code",
                    [],
                    null,
                    "No data yet",
                    "Open Claude Code and send one message; limits will arrive through the safe status-line bridge.");
            }

            await using var stream = new FileStream(
                _claudeCachePath,
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

            DateTimeOffset? updatedAt = null;
            if (root.TryGetProperty("updated_at", out var updatedElement) &&
                updatedElement.TryGetInt64(out var updatedUnix))
            {
                updatedAt = DateTimeOffset.FromUnixTimeSeconds(updatedUnix);
            }

            var note = updatedAt is not null && DateTimeOffset.Now - updatedAt > TimeSpan.FromHours(8)
                ? "Claude data may be stale; it refreshes automatically while Claude Code is open."
                : null;

            return windows.Count > 0
                ? new ProviderUsage("Claude Code", windows, updatedAt, Note: note)
                : new ProviderUsage(
                    "Claude Code",
                    [],
                    updatedAt,
                    "The status line has not produced limit data yet",
                    "It appears automatically after a message in Claude Code.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ProviderUsage(
                "Claude Code",
                [],
                null,
                "Could not read the Claude cache",
                ex.Message);
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

        if (!value.TryGetProperty("used_percentage", out var usedElement) ||
            !usedElement.TryGetDouble(out var usedPercent))
        {
            return;
        }

        DateTimeOffset? resetsAt = null;
        if (value.TryGetProperty("resets_at", out var resetElement) &&
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
