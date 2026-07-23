using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCodexLimits;

[JsonConverter(typeof(JsonStringEnumConverter<BarDisplayMode>))]
internal enum BarDisplayMode
{
    Remaining,
    Used
}

internal sealed class AppSettings
{
    public BarDisplayMode BarDisplayMode { get; set; } = BarDisplayMode.Remaining;
    public bool ShowLargePanel { get; set; } = true;
    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SettingsStore(string appDataDirectory)
    {
        _settingsPath = Path.Combine(appDataDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var temporaryPath = _settingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }
}
