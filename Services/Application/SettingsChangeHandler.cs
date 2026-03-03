using System;
using System.Collections.Generic;
using System.ComponentModel;
using Hotkey_Translator.ViewModels;

namespace Hotkey_Translator.Services.Application;

internal sealed class SettingsChangeHandler : IDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly HashSet<string> _propertyNames;
    private readonly Action _onChanged;
    private bool _isSubscribed;

    public SettingsChangeHandler(SettingsViewModel settings, IEnumerable<string> propertyNames, Action onChanged)
    {
        _settings = settings;
        _propertyNames = new HashSet<string>(propertyNames, StringComparer.Ordinal);
        _onChanged = onChanged;
    }

    public void Start()
    {
        if (_isSubscribed)
        {
            return;
        }

        _settings.PropertyChanged += OnSettingsPropertyChanged;
        _isSubscribed = true;
    }

    public void Dispose()
    {
        if (!_isSubscribed)
        {
            return;
        }

        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _isSubscribed = false;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == null || _propertyNames.Contains(e.PropertyName))
        {
            _onChanged();
        }
    }
}
