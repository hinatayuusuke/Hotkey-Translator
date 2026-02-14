namespace Hotkey_Translator.Services.Settings;

internal interface ISecretProtector
{
    string Protect(string value);

    string? Unprotect(string protectedValue);
}
