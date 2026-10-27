using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace HomeLab.Server.Authentication.Totp;

public sealed class TotpSecretProtector(IDataProtectionProvider provider)
{
    private const string Prefix = "protected:v1:";
    private readonly IDataProtector _protector = provider.CreateProtector("HomeLab.TotpSecret.v1");

    public string Protect(string secret) => Prefix + _protector.Protect(secret);
    public bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!IsProtected(value)) return value; // Existing installations are upgraded after successful login.
        try { return _protector.Unprotect(value[Prefix.Length..]); }
        catch (CryptographicException) { return null; }
    }
}
