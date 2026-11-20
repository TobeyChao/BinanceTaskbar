using System.IO;
using System.Text.Json;
using BinanceTaskbar.Models;

namespace BinanceTaskbar.Services;

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BinanceTaskbar");

    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        Directory.CreateDirectory(SettingsDirectory);
        if (!File.Exists(SettingsPath))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            try
            {
                File.Copy(SettingsPath, SettingsPath + ".invalid", overwrite: true);
            }
            catch
            {
            }

            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        settings.Normalize();
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public static void OpenInEditor()
    {
        if (!File.Exists(SettingsPath))
        {
            Save(new AppSettings());
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = $"\"{SettingsPath}\"",
            UseShellExecute = true
        });
    }
}





