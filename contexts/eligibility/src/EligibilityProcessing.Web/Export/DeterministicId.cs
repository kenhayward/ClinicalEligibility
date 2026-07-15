using System.Security.Cryptography;
using System.Text;

namespace EligibilityProcessing.Web.Export;

/// <summary>
/// Name-based deterministic GUID generation (RFC 4122 §4.3 — UUID version 5,
/// SHA-1). The same (namespace, name) pair always yields the same GUID, on any
/// machine, forever. .NET 8 has no built-in <c>Guid.CreateVersion5</c> (added in
/// .NET 9), so it is implemented here.
///
/// Used by the audit-CSV export to build a persistent per-row key from stable
/// content rather than from a volatile surrogate id — see
/// <see cref="AuthoringCriteriaAuditCsv"/>.
/// </summary>
public static class DeterministicId
{
    /// <summary>
    /// A UUIDv5 derived from <paramref name="ns"/> and <paramref name="name"/>.
    /// Deterministic: identical inputs → identical output GUID.
    /// </summary>
    public static Guid Create(Guid ns, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Namespace bytes must be in big-endian (network) order before hashing,
        // per RFC 4122. Guid.ToByteArray() emits the first three fields
        // little-endian on all platforms, so swap them.
        var nsBytes = ns.ToByteArray();
        SwapToBigEndian(nsBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);

        byte[] hash;
        using (var sha1 = SHA1.Create())
        {
            sha1.TransformBlock(nsBytes, 0, nsBytes.Length, null, 0);
            sha1.TransformFinalBlock(nameBytes, 0, nameBytes.Length);
            hash = sha1.Hash!;
        }

        // Take the first 16 bytes of the 20-byte SHA-1 digest.
        var result = new byte[16];
        Array.Copy(hash, result, 16);

        // Set the version (5) in the high nibble of byte 6, and the RFC 4122
        // variant (10xx) in the high bits of byte 8.
        result[6] = (byte)((result[6] & 0x0F) | 0x50);
        result[8] = (byte)((result[8] & 0x3F) | 0x80);

        // Convert back from big-endian layout to the Guid constructor's
        // little-endian expectation for the first three fields.
        SwapToBigEndian(result);
        return new Guid(result);
    }

    // Swaps the byte order of the first three Guid fields (4-2-2 bytes) between
    // little-endian (Guid's in-memory layout) and big-endian (RFC 4122 wire
    // order). The operation is its own inverse.
    private static void SwapToBigEndian(byte[] guid)
    {
        Array.Reverse(guid, 0, 4);
        Array.Reverse(guid, 4, 2);
        Array.Reverse(guid, 6, 2);
    }
}
