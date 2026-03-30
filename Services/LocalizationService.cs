using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public const string UiLanguageSystem = "system";
    public const string UiLanguageEnglish = "en";
    public const string UiLanguageJapanese = "ja";

    private static readonly ResourceManager ResourceManager =
        new("Hotkey_Translator.Resources.Strings", Assembly.GetExecutingAssembly());

    public static LocalizationService Instance { get; } = new();

    private CultureInfo _currentCulture = NormalizeSystemCulture(CultureInfo.CurrentUICulture);
    private string _currentUiLanguage = UiLanguageSystem;

    private LocalizationService()
    {
        ApplyThreadCulture(_currentCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string CurrentUiLanguage => _currentUiLanguage;

    public CultureInfo CurrentCulture => _currentCulture;

    public string this[string key] => GetString(key);

    public string NormalizeUiLanguage(string? uiLanguage)
    {
        var normalized = (uiLanguage ?? string.Empty).Trim();
        if (string.Equals(normalized, UiLanguageEnglish, StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguageEnglish;
        }

        if (string.Equals(normalized, UiLanguageJapanese, StringComparison.OrdinalIgnoreCase))
        {
            return UiLanguageJapanese;
        }

        return UiLanguageSystem;
    }

    public CultureInfo ResolveUiCulture(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ResolveUiCulture(settings.UiLanguage);
    }

    public CultureInfo ResolveUiCulture(string? uiLanguage)
    {
        var normalized = NormalizeUiLanguage(uiLanguage);
        return normalized switch
        {
            UiLanguageEnglish => CultureInfo.GetCultureInfo(UiLanguageEnglish),
            UiLanguageJapanese => CultureInfo.GetCultureInfo(UiLanguageJapanese),
            _ => NormalizeSystemCulture(CultureInfo.InstalledUICulture)
        };
    }

    public void ApplyUiLanguage(string? uiLanguage)
    {
        var normalized = NormalizeUiLanguage(uiLanguage);
        var culture = ResolveUiCulture(normalized);
        var changed = !string.Equals(_currentUiLanguage, normalized, StringComparison.Ordinal) ||
                      !string.Equals(_currentCulture.Name, culture.Name, StringComparison.Ordinal);

        _currentUiLanguage = normalized;
        _currentCulture = culture;
        ApplyThreadCulture(culture);

        if (!changed)
        {
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentUiLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetString(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return ResourceManager.GetString(key, _currentCulture) ?? key;
    }

    public string GetString(string key, params object[] args)
    {
        var template = GetString(key);
        return args.Length == 0
            ? template
            : string.Format(_currentCulture, template, args);
    }

    private static CultureInfo NormalizeSystemCulture(CultureInfo culture)
    {
        return string.Equals(culture.TwoLetterISOLanguageName, UiLanguageJapanese, StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo(UiLanguageJapanese)
            : CultureInfo.GetCultureInfo(UiLanguageEnglish);
    }

    private static void ApplyThreadCulture(CultureInfo culture)
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
