using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hotkey-Translator");
        SettingsPath = Path.Combine(root, "settings.json");
        CachePath = Path.Combine(root, "cache.sqlite");
    }

    public string SettingsPath { get; }
    public string CachePath { get; }
    public AppSettings Settings { get; private set; } = new();

    public async Task LoadAsync()
    {
        if (!File.Exists(SettingsPath))
        {
            Settings = new AppSettings();
            return;
        }

        var json = await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

        if (!string.IsNullOrWhiteSpace(loaded.ApiKeyProtected))
        {
            loaded.ApiKey = Unprotect(loaded.ApiKeyProtected);
        }

        Settings = loaded;
    }

    public async Task SaveAsync()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            // SECURITY: API key is protected with DPAPI for the current user profile.
            Settings.ApiKeyProtected = Protect(Settings.ApiKey);
        }
        else
        {
            Settings.ApiKeyProtected = null;
        }

        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        await File.WriteAllTextAsync(SettingsPath, json).ConfigureAwait(false);
    }

    private static string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string? Unprotect(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            var unprotected = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }
        catch
        {
            return null;
        }
    }
}
