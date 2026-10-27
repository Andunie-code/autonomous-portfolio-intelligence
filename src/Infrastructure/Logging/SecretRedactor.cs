using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace PortfolioIntelligence.Infrastructure.Logging;

/// <summary>
/// Masks secrets in log text. Two layers:
/// 1. Value-based: every configuration value whose key looks secret (password, token, api key, ...)
///    is masked wherever it appears.
/// 2. Pattern-based: well-known credential shapes are masked even if they never came from configuration.
/// Secrets read from files (Docker secrets: the Ops API token, the Testnet key, login passwords) are passed as
/// additional values, since they never enter the configuration (M6, m6-protocol §8.2).
/// </summary>
public sealed partial class SecretRedactor
{
    public const string Mask = "***REDACTED***";

    // Values shorter than this are not registered, otherwise trivial strings would be masked everywhere.
    private const int MinimumSecretLength = 6;

    private readonly string[] _secretValues;

    public SecretRedactor(IEnumerable<string> secretValues)
    {
        _secretValues = secretValues
            .Where(v => !string.IsNullOrWhiteSpace(v) && v.Length >= MinimumSecretLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length) // longest first so overlapping secrets are fully masked
            .ToArray();
    }

    public static SecretRedactor FromConfiguration(IConfiguration configuration, IEnumerable<string>? additionalSecrets = null)
    {
        var values = new List<string>(additionalSecrets ?? []);
        foreach (var (key, value) in configuration.AsEnumerable())
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var lastSegment = key[(key.LastIndexOf(':') + 1)..];
            if (SecretKeyName().IsMatch(lastSegment))
            {
                values.Add(value);
            }

            if (key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in ConnectionStringPassword().Matches(value))
                {
                    values.Add(match.Groups["value"].Value);
                }
            }
        }

        return new SecretRedactor(values);
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        foreach (var secret in _secretValues)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        text = ConnectionStringPassword().Replace(text, m => $"{m.Groups["key"].Value}={Mask}");
        foreach (var pattern in KnownCredentialPatterns)
        {
            text = pattern.Replace(text, Mask);
        }

        return text;
    }

    private static readonly Regex[] KnownCredentialPatterns =
    [
        GoogleApiKey(),
        AnthropicApiKey(),
        GitHubToken(),
        BearerToken(),
        PrivateKeyBlock(),
        RequestSignature(),
        ExchangeApiKeyHeader(),
    ];

    [GeneratedRegex(@"(?i)(password|passwd|pwd|secret|token|apikey|api_key|privatekey|private_key|credential)")]
    private static partial Regex SecretKeyName();

    [GeneratedRegex(@"(?i)(?<key>password|pwd)\s*=\s*(?<value>[^;""'\s]+)")]
    private static partial Regex ConnectionStringPassword();

    [GeneratedRegex(@"AIza[0-9A-Za-z_\-]{35}")]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"sk-ant-[0-9A-Za-z_\-]{20,}")]
    private static partial Regex AnthropicApiKey();

    [GeneratedRegex(@"(gh[pousr]_[0-9A-Za-z]{30,}|github_pat_[0-9A-Za-z_]{20,})")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"(?i)bearer\s+[0-9A-Za-z_\-\.=]{16,}")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKeyBlock();

    // A signed exchange request's HMAC (S-B11): masked wherever a URL is logged.
    [GeneratedRegex(@"(?i)signature=[0-9a-f]{16,}")]
    private static partial Regex RequestSignature();

    [GeneratedRegex(@"(?i)X-MBX-APIKEY[""':=\s]+[^\s""',;]+")]
    private static partial Regex ExchangeApiKeyHeader();
}
