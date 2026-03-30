using System;
using System.Windows.Data;
using System.Windows.Markup;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.UI.Localization;

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            return string.Empty;
        }

        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key
        };
        return binding.ProvideValue(serviceProvider);
    }
}
