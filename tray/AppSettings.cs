using System.Text.Json;

namespace AIContextSyncTray;

internal sealed class AppSettings
{
    public bool StartServiceWithTray { get; set; } = false;
    public bool ShowNotifications { get; set; } = true;
    public HashSet<string> DisabledServiceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnmonitoredServiceIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                var json = File.ReadAllText(AppPaths.SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                settings.DisabledServiceIds = new HashSet<string>(
                    settings.DisabledServiceIds ?? [],
                    StringComparer.OrdinalIgnoreCase);
                settings.UnmonitoredServiceIds = new HashSet<string>(
                    settings.UnmonitoredServiceIds ?? [],
                    StringComparer.OrdinalIgnoreCase);
                return settings;
            }
        }
        catch
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.RuntimeRoot);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.SettingsPath, json);
    }
}
