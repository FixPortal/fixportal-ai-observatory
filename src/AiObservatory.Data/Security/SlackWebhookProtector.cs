using System.Security.Cryptography;
using System.Text;

namespace AiObservatory.Data.Security;

/// <summary>
/// AES-256-GCM protection for <see cref="Entities.NotificationSettings.SlackWebhookUrl"/> at
/// rest. The webhook URL is a bearer credential -- possession alone permits posting to the
/// channel -- so it is stored encrypted and only decrypted on the way out of the database.
/// The key comes from the <see cref="KeyEnvironmentVariable"/> environment variable (any
/// passphrase, hashed to the AES key with SHA-256), matching the house rule that secrets live
/// in infra config rather than the database. When the variable is unset the value passes
/// through unchanged, so existing self-hosted deployments keep working until they opt in by
/// setting it; values written before then stay readable because <see cref="Unprotect"/> returns
/// anything without the <see cref="EncryptedPrefix"/> marker as-is.
/// </summary>
public sealed class SlackWebhookProtector
{
    public const string KeyEnvironmentVariable = "SLACK_WEBHOOK_PROTECTION_KEY";
    public const string EncryptedPrefix = "enc:v1:";

    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public SlackWebhookProtector(string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));
    }

    /// <summary>Encrypts for storage. Output is prefixed so it is self-identifying on read.</summary>
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipherBytes = new byte[plainBytes.Length];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        }

        // nonce || tag || ciphertext -- everything Unprotect needs, in one self-contained blob.
        var blob = new byte[NonceSize + TagSize + cipherBytes.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        cipherBytes.CopyTo(blob, NonceSize + TagSize);
        return EncryptedPrefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a stored value. Values without the prefix predate encryption being switched on
    /// and are returned unchanged; tampered or wrong-key ciphertext throws
    /// <see cref="CryptographicException"/> rather than surfacing a corrupt URL to post to.
    /// </summary>
    public string Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return stored;
        }

        var blob = Convert.FromBase64String(stored[EncryptedPrefix.Length..]);
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        var cipherBytes = blob.AsSpan(NonceSize + TagSize);
        var plainBytes = new byte[cipherBytes.Length];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }

        return Encoding.UTF8.GetString(plainBytes);
    }

    // EF value-converter entry points. The environment is read per call: settings reads/writes
    // are rare, and per-call resolution keeps the design-time factory (no key) working.
    public static string? ProtectValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return FromEnvironment()?.Protect(value) ?? value;
    }

    public static string? UnprotectValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var protector = FromEnvironment();
        if (protector is null)
        {
            if (value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The stored Slack webhook URL is encrypted but {KeyEnvironmentVariable} is not set; "
                        + "restore the key or clear NotificationSettings.SlackWebhookUrl."
                );
            }

            return value;
        }

        return protector.Unprotect(value);
    }

    private static SlackWebhookProtector? FromEnvironment()
    {
        var passphrase = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        return string.IsNullOrEmpty(passphrase) ? null : new SlackWebhookProtector(passphrase);
    }
}
