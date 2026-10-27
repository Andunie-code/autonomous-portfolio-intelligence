using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PortfolioIntelligence.Execution.Exchange;

/// <summary>
/// Deterministic client order ids (m6-protocol §2.3): <c>pi-&lt;p&gt;-&lt;h&gt;</c>, p ∈ {e, s, x, c} for entry, stop, exit and
/// close, h = the first 26 characters of the lower-case base32 SHA-256 of run|sleeve|symbol|decisionBar|purpose|attempt.
/// The same intent always gives the same id, also after a restart; only a NeverAccepted order raises the attempt.
/// The character set is the Spot FIX rule <c>[a-zA-Z0-9-_]{1,36}</c>, the narrowest documented one (S-B13).
/// </summary>
public static partial class ClientOrderIds
{
    public const string SystemPrefix = "pi-";
    public const string DrillPrefix = "drill-";

    public static string For(long runId, int sleeve, string symbol, DateTimeOffset decisionBar, OrderPurpose purpose, int attempt)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{runId}|{sleeve}|{symbol}|{decisionBar.UtcDateTime:O}|{purpose}|{attempt}");
        return $"{SystemPrefix}{Letter(purpose)}-{Hash(key)}";
    }

    /// <summary>Id of a drill order: outside the strategy runs' namespace (m6-protocol §8.3).</summary>
    public static string Drill(string scenario, int sequence, string nonce) =>
        $"{DrillPrefix}{Hash(string.Create(CultureInfo.InvariantCulture, $"{scenario}|{sequence}|{nonce}"))[..20]}";

    public static bool IsValid(string id) => Valid().IsMatch(id);

    public static bool IsSystem(string id) => id.StartsWith(SystemPrefix, StringComparison.Ordinal);

    private static char Letter(OrderPurpose purpose) => purpose switch
    {
        OrderPurpose.Entry => 'e',
        OrderPurpose.Stop => 's',
        OrderPurpose.Exit => 'x',
        OrderPurpose.Close => 'c',
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private static string Hash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var s = new StringBuilder(26);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5 && s.Length < 26)
            {
                s.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            if (s.Length == 26)
            {
                break;
            }
        }

        return s.ToString();
    }

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,36}$")]
    private static partial Regex Valid();
}
