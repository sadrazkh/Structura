using Microsoft.AspNetCore.DataProtection;

namespace Structura.Web.Infrastructure.Secrets;

public interface ISecretProtector
{
    /// <summary>Encrypts a secret into the storable `protected:v1:…` form.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a stored value. Plain values (not `protected:`) pass through untouched.</summary>
    string Unprotect(string stored);

    /// <summary>True when the value is in the stored encrypted form.</summary>
    bool IsProtected(string? value);
}

public sealed class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private const string Prefix = "protected:v1:";
    private readonly IDataProtector _protector = provider.CreateProtector("Structura.Secrets.v1");

    public string Protect(string plaintext) => Prefix + _protector.Protect(plaintext);

    public string Unprotect(string stored) =>
        stored.StartsWith(Prefix, StringComparison.Ordinal)
            ? _protector.Unprotect(stored[Prefix.Length..])
            : stored;

    public bool IsProtected(string? value) =>
        value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    /// <summary>Display mask: last 4 characters of the plaintext, if long enough.</summary>
    public static string Mask(string? plaintext) =>
        string.IsNullOrEmpty(plaintext) ? ""
        : plaintext.Length >= 8 ? $"••••{plaintext[^4..]}"
        : "••••";
}
