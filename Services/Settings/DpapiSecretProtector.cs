using System;
using System.Security.Cryptography;
using System.Text;

namespace Hotkey_Translator.Services.Settings;

internal sealed class DpapiSecretProtector : ISecretProtector
{
    public string Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string? Unprotect(string protectedValue)
    {
        try
        {
            var bytes = Convert.FromBase64String(protectedValue);
            var unprotected = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(unprotected);
        }
        catch
        {
            return null;
        }
    }
}
