using System.Text;
using System.Text.RegularExpressions;

namespace PortfolioIntelligence.SourceGateway;

/// <summary>
/// A parsed robots.txt (m7-protocol §2.4): the group for our agent token, otherwise the <c>*</c> group; Allow and Disallow
/// patterns with <c>*</c> and a trailing <c>$</c>; the longest matching pattern wins and a tie goes to Allow. No rules
/// means everything is allowed; <see cref="DisallowAll"/> stands for an unreadable file.
/// </summary>
public sealed class RobotsRules
{
    private readonly IReadOnlyList<(bool Allow, string Pattern, Regex Regex)> _rules;

    private RobotsRules(IReadOnlyList<(bool, string, Regex)> rules, bool disallowAll)
    {
        _rules = rules;
        DisallowsEverything = disallowAll;
    }

    public bool DisallowsEverything { get; }

    public static RobotsRules AllowAll { get; } = new([], false);

    public static RobotsRules DisallowAll { get; } = new([], true);

    public static RobotsRules Parse(string text, string agentToken)
    {
        var groups = new List<(List<string> Agents, List<(bool, string)> Rules)>();
        (List<string> Agents, List<(bool, string)> Rules)? current = null;
        var lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            if (field == "user-agent")
            {
                if (current is null || !lastWasAgent)
                {
                    current = ([], []);
                    groups.Add(current.Value);
                }

                current.Value.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }

            lastWasAgent = false;
            if (current is not null && field is "allow" or "disallow")
            {
                current.Value.Rules.Add((field == "allow", value));
            }
        }

        var token = agentToken.ToLowerInvariant();
        var group = groups.FirstOrDefault(g => g.Agents.Any(a => a != "*" && token.Contains(a, StringComparison.Ordinal)));
        if (group.Agents is null)
        {
            group = groups.FirstOrDefault(g => g.Agents.Contains("*"));
        }

        var rules = (group.Rules ?? [])
            .Where(r => r.Item2.Length > 0) // "Disallow:" with no path allows everything
            .Select(r => (r.Item1, r.Item2, ToRegex(r.Item2)))
            .ToList();
        return new RobotsRules(rules, false);
    }

    public bool IsAllowed(string pathAndQuery)
    {
        if (DisallowsEverything)
        {
            return false;
        }

        var match = _rules.Where(r => r.Regex.IsMatch(pathAndQuery))
            .OrderByDescending(r => r.Pattern.Length).ThenByDescending(r => r.Allow)
            .FirstOrDefault();
        return match.Pattern is null || match.Allow;
    }

    private static Regex ToRegex(string pattern)
    {
        var anchored = pattern.EndsWith('$');
        var body = anchored ? pattern[..^1] : pattern;
        var regex = new StringBuilder("^");
        foreach (var c in body)
        {
            regex.Append(c == '*' ? ".*" : Regex.Escape(c.ToString()));
        }

        if (anchored)
        {
            regex.Append('$');
        }

        return new Regex(regex.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}

/// <summary>
/// robots.txt per host, read at run time and kept for the configured hours (U-M7-6): 2xx → its rules; 404 or 410 → no
/// restriction; anything else (5xx, 3xx, timeout, network error, oversize) → the whole host is disallowed. Sources in
/// <see cref="RobotsMode.ApiTerms"/> mode are not checked (the arXiv API follows its terms, S-X1, S-X5).
/// </summary>
public sealed class RobotsPolicy(SourceHttp http, SourceConfiguration configuration, TimeProvider time)
{
    private readonly Dictionary<string, (RobotsRules Rules, DateTimeOffset At, int Status)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The robots.txt outcome per host in this run (status 0 = network failure), for the provenance report.</summary>
    public IReadOnlyDictionary<string, int> Observed => _cache.ToDictionary(e => e.Key, e => e.Value.Status);

    public async Task<bool> IsAllowedAsync(Uri uri, SourceTarget target, CancellationToken ct)
    {
        if (target.Kind == SourceKind.Robots || target.Source.Robots == RobotsMode.ApiTerms)
        {
            return true;
        }

        var rules = await RulesAsync(uri.Host, ct);
        return rules.IsAllowed(uri.PathAndQuery);
    }

    private async Task<RobotsRules> RulesAsync(string host, CancellationToken ct)
    {
        if (_cache.TryGetValue(host, out var cached) && time.GetUtcNow() - cached.At < TimeSpan.FromHours(configuration.RobotsTtlHours))
        {
            return cached.Rules;
        }

        RobotsRules rules;
        int status;
        try
        {
            var fetch = await http.GetAsync(new Uri($"https://{host}/robots.txt"), ContentClass.Robots, ct);
            status = fetch.Status;
            rules = SourceHttp.IsSuccess(status) ? RobotsRules.Parse(Encoding.UTF8.GetString(fetch.Body), configuration.UserAgent.Split('/')[0])
                : SourceHttp.IsNotFound(status) ? RobotsRules.AllowAll
                : RobotsRules.DisallowAll;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or SourcePolicyViolationException or SourceTooLargeException && !ct.IsCancellationRequested)
        {
            status = 0;
            rules = RobotsRules.DisallowAll;
        }

        _cache[host] = (rules, time.GetUtcNow(), status);
        return rules;
    }
}
