using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NodaTime;

namespace AiObservatory.Data.Spend;

/// <summary>
/// Derives the idempotency key for an imported spend row, so re-importing a file lands
/// nothing rather than doubling a total.
/// </summary>
public static class SpendEntryKey
{
    /// <param name="occurrence">
    /// Zero-based index among the rows of the SAME import that share every other input.
    /// Load-bearing: without it, two genuine identical charges on one day collide and the
    /// second is silently dropped. Scoped to one file, which is a known and accepted limit
    /// (spec §6) — identical charges split across two imports still collide, and the fix
    /// is to differentiate the description.
    /// </param>
    public static string Derive(
        LocalDate occurredOn,
        string vendorKey,
        decimal amount,
        string currency,
        string? description,
        int occurrence)
    {
        // Invariant culture throughout: a machine with a comma decimal separator must not
        // derive a different key for the same charge. The pipe separator stops fields
        // running together, so ("ab","c") and ("a","bc") cannot hash alike.
        var material = string.Join('|',
            occurredOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            vendorKey.Trim().ToLowerInvariant(),
            amount.ToString("F4", CultureInfo.InvariantCulture),
            currency.Trim().ToUpperInvariant(),
            (description ?? string.Empty).Trim(),
            occurrence.ToString(CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash);   // 64 chars, comfortably inside varchar(200)
    }
}
