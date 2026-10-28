using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PortfolioIntelligence.Kernel.Storage;

namespace PortfolioIntelligence.SourceGateway;

/// <summary>Metadata of one cached response (the body is stored next to it).</summary>
public sealed record CachedEntry(string Url, DateTimeOffset FetchedAt, int Status, string? ContentType, string ContentSha256, long Bytes);

/// <summary>
/// Local cache of fetched sources (m7-protocol §2.4): outside the repository and OneDrive, keyed by the SHA-256 of the
/// URL, with the content SHA-256 checked on every read (a mismatch deletes the entry) and a TTL after which an entry is
/// not used. Default root <c>%LOCALAPPDATA%\PortfolioIntelligence\source-cache</c>.
/// </summary>
public sealed class SourceCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SourceCache(string root, string repositoryRoot)
    {
        Root = LocalCacheRoot.Validate(root, repositoryRoot, "source cache", nameof(root));
        Directory.CreateDirectory(Path.Combine(Root, "entries"));
    }

    public string Root { get; }

    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("PI_SOURCE_CACHE") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortfolioIntelligence", "source-cache");

    public string ReportsDirectory => Directory.CreateDirectory(Path.Combine(Root, "reports")).FullName;

    /// <summary>Hash mismatches seen on read (each deleted the entry).</summary>
    public int Corrupted { get; private set; }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string KeyOf(Uri uri) => Sha256(Encoding.UTF8.GetBytes(uri.AbsoluteUri));

    public async Task<(CachedEntry Entry, byte[] Body)?> GetAsync(Uri uri, TimeSpan ttl, DateTimeOffset now, CancellationToken ct)
    {
        var (meta, body) = PathsOf(uri);
        if (!File.Exists(meta) || !File.Exists(body))
        {
            return null;
        }

        var entry = JsonSerializer.Deserialize<CachedEntry>(await File.ReadAllTextAsync(meta, ct), Json)!;
        var bytes = await File.ReadAllBytesAsync(body, ct);
        if (entry.Url != uri.AbsoluteUri || Sha256(bytes) != entry.ContentSha256)
        {
            Corrupted++;
            File.Delete(meta);
            File.Delete(body);
            return null;
        }

        return now - entry.FetchedAt <= ttl ? (entry, bytes) : null;
    }

    public async Task<CachedEntry> PutAsync(Uri uri, int status, string? contentType, byte[] body, DateTimeOffset fetchedAt, CancellationToken ct)
    {
        var entry = new CachedEntry(uri.AbsoluteUri, fetchedAt, status, contentType, Sha256(body), body.LongLength);
        var (meta, file) = PathsOf(uri);
        await File.WriteAllBytesAsync(file, body, ct);
        await File.WriteAllTextAsync(meta, JsonSerializer.Serialize(entry, Json), ct);
        return entry;
    }

    private (string Meta, string Body) PathsOf(Uri uri)
    {
        var key = KeyOf(uri);
        var directory = Path.Combine(Root, "entries", key[..2]);
        Directory.CreateDirectory(directory);
        return (Path.Combine(directory, key + ".json"), Path.Combine(directory, key + ".bin"));
    }
}
