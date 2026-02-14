using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services.Settings;

namespace Hotkey_Translator.Services;

public sealed class SettingsService
{
    private readonly ISettingsRepository _repository;
    private readonly ISecretProtector _secretProtector;

    public SettingsService()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hotkey-Translator");
        SettingsPath = Path.Combine(root, "settings.json");
        CachePath = Path.Combine(root, "cache.sqlite");
        _repository = new JsonSettingsRepository(SettingsPath);
        _secretProtector = new DpapiSecretProtector();
    }

    internal SettingsService(
        string settingsPath,
        string cachePath,
        ISettingsRepository repository,
        ISecretProtector secretProtector)
    {
        SettingsPath = settingsPath;
        CachePath = cachePath;
        _repository = repository;
        _secretProtector = secretProtector;
    }

    public string SettingsPath { get; }
    public string CachePath { get; }
    public AppSettings Settings { get; private set; } = new();

    public async Task LoadAsync()
    {
        var loaded = await _repository.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        if (loaded is null)
        {
            Settings = CreateFirstRunDefaults();
            return;
        }

        if (!string.IsNullOrWhiteSpace(loaded.ApiKeyProtected))
        {
            loaded.ApiKey = _secretProtector.Unprotect(loaded.ApiKeyProtected);
        }

        if (!string.IsNullOrWhiteSpace(loaded.DeepLApiKeyProtected))
        {
            loaded.DeepLApiKey = _secretProtector.Unprotect(loaded.DeepLApiKeyProtected);
        }

        Settings = loaded;
    }

    private static AppSettings CreateFirstRunDefaults()
    {
        var settings = new AppSettings();

        // WHY: keep first-run PaddleOCR-VL behavior aligned with current recommended operational profile.
        settings.PaddleVlMaxPixels = 500000;
        settings.PaddleVlLayoutThreshold = null;
        settings.PaddleVlMaxNewTokens = 512;
        settings.PaddleVlMergeLayoutBlocks = false;
        settings.PaddleVlUseOcrForImageBlock = null;
        settings.PaddleVlUseLayoutDetection = true;
        settings.PaddleVlEnableHpi = false;
        settings.PaddleVlUseTensorrt = null;

        return settings;
    }

    public async Task SaveAsync()
    {
        if (!string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            // SECURITY: API key is protected with DPAPI for the current user profile.
            Settings.ApiKeyProtected = _secretProtector.Protect(Settings.ApiKey);
        }
        else
        {
            Settings.ApiKeyProtected = null;
        }

        if (!string.IsNullOrWhiteSpace(Settings.DeepLApiKey))
        {
            // SECURITY: DeepL key is protected with DPAPI for the current user profile.
            Settings.DeepLApiKeyProtected = _secretProtector.Protect(Settings.DeepLApiKey);
        }
        else
        {
            Settings.DeepLApiKeyProtected = null;
        }

        await _repository.SaveAsync(Settings, CancellationToken.None).ConfigureAwait(false);
    }
}
