namespace Hotkey_Translator.Services.Application;

internal enum ResourceBootstrapIntent
{
    AppLoad,
    SettingsSave
}

internal readonly record struct ResourceBootstrapConfirmationResult(bool Approved, bool SettingsChanged);

internal sealed record ResourceBootstrapItem(
    string DisplayName,
    string Detail,
    bool IsDefinite,
    long? KnownDownloadBytes,
    string? ApprovalKey);

internal sealed class ResourceBootstrapPlan
{
    public ResourceBootstrapPlan(IReadOnlyList<ResourceBootstrapItem> items)
    {
        Items = items;
    }

    public IReadOnlyList<ResourceBootstrapItem> Items { get; }

    public bool RequiresConfirmation => Items.Count > 0;
}
