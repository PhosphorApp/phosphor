using System.Security.Cryptography;
using System.Text;

namespace Phosphor.Plugins.Host;

/// <summary>
/// Encrypts/decrypts individual plug-in secret settings values at the <c>settings.json</c>
/// persistence boundary using Windows DPAPI (<see cref="ProtectedData"/>), so tokens can be
/// stored at rest without a separate credential store. Values are self-describing via the
/// <see cref="Tag"/> prefix, so the loader always knows whether a value is encrypted regardless
/// of the current <c>AppSettings.EncryptSecrets</c> toggle — which is what makes flipping the
/// option on/off safe.
/// </summary>
/// <remarks>
/// <para>
/// Scope is <see cref="DataProtectionScope.CurrentUser"/>: ciphertext is bound to the Windows
/// user account on this machine and will not decrypt for another user or on another machine.
/// This is intentional (it is why the option carries a "settings file is no longer portable"
/// warning) and it is <em>not</em> a defense against code already running as the same user.
/// </para>
/// <para>
/// Plug-ins never see any of this: the host decrypts on load and hands plaintext to
/// <c>IPhosphorSource.ApplySettings</c>, and encrypts (when enabled) on save.
/// </para>
/// </remarks>
public static class SecretProtector
{
    /// <summary>Prefix marking a value as DPAPI-encrypted (base64 ciphertext follows).</summary>
    public const string Tag = "enc:dpapi:";

    // Fixed, app-specific entropy so our ciphertext isn't interchangeable with other DPAPI
    // blobs on the machine. Not a secret (it ships in the binary) — just a domain separator.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Phosphor.PluginSecrets.v1");

    /// <summary>True when <paramref name="value"/> is a DPAPI-encrypted wrapper.</summary>
    public static bool IsEncrypted(string? value) =>
        value != null && value.StartsWith(Tag, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts <paramref name="plaintext"/> into a tagged <see cref="Tag"/> wrapper. Already-encrypted
    /// values and null/empty values pass through unchanged (no double-encryption). Returns the input
    /// unchanged if DPAPI is unavailable.
    /// </summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsEncrypted(plaintext))
            return plaintext;
        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Tag + Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "SecretProtector", $"Protect failed: {ex.Message}");
            return plaintext;
        }
    }

    /// <summary>
    /// Decrypts a tagged <see cref="Tag"/> wrapper back to plaintext. Plaintext (untagged) values pass
    /// through unchanged. On failure — e.g. the settings file was copied to another machine/user —
    /// returns <c>null</c> so the source degrades to "not configured" rather than crashing.
    /// </summary>
    public static string? Unprotect(string? value)
    {
        if (!IsEncrypted(value))
            return value;
        try
        {
            var cipher = Convert.FromBase64String(value!.Substring(Tag.Length));
            var bytes = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            DebugLog.Log(LogLevel.Warning, "SecretProtector", $"Unprotect failed (wrong user/machine?): {ex.Message}");
            return null;
        }
    }
}
