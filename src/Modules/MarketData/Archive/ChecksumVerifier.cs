using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PortfolioIntelligence.Kernel.MarketData;

namespace PortfolioIntelligence.MarketData.Archive;

/// <summary>
/// Verifies a downloaded zip against its <c>.CHECKSUM</c> file (S-B6). Assumed format, as in sha256sum output:
/// "&lt;64 hex characters&gt;  &lt;file name&gt;" (not yet re-verified). A missing or malformed checksum file, or a
/// mismatch, rejects the file (docs/m1-data-policy.md §6).
/// </summary>
public static partial class ChecksumVerifier
{
    public static string Sha256Hex(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static string Verify(ArchiveFileKey key, byte[] zip, string? checksumFile)
    {
        if (checksumFile is null)
        {
            throw new ArchiveDataException($"{key.FileName}: checksum file is missing; the file is rejected.");
        }

        var match = ChecksumLine().Match(checksumFile.Trim());
        if (!match.Success || match.Groups["name"].Value != key.FileName)
        {
            throw new ArchiveDataException($"{key.FileName}: checksum file is malformed or names another file; the file is rejected.");
        }

        var actual = Sha256Hex(zip);
        if (!string.Equals(actual, match.Groups["hash"].Value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new ArchiveDataException($"{key.FileName}: SHA-256 mismatch; the file is rejected.");
        }

        return actual;
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>\S+)$")]
    private static partial Regex ChecksumLine();
}
