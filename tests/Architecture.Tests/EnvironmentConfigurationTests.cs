namespace PortfolioIntelligence.Architecture.Tests;

/// <summary>
/// Static checks of the M5 local topology (docs/m5-protocol.md §4.3–§4.4; H27 and the pg_hba rules). These pin
/// the configuration files; the real-environment behaviour is H-ENV's job (scripts/m5-henv.ps1).
/// </summary>
public class EnvironmentConfigurationTests
{
    private static readonly string Root = RepositoryRoot.Path;

    /// <summary>Services of a compose file: name → the lines of its block (two-space indented keys under "services:").</summary>
    private static Dictionary<string, List<string>> Services(string relativePath)
    {
        var lines = File.ReadAllLines(Path.Combine(Root, relativePath));
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var inServices = false;
        List<string>? current = null;
        foreach (var line in lines)
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                inServices = line.TrimEnd() == "services:";
                current = null;
                continue;
            }

            if (!inServices || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("   ", StringComparison.Ordinal) && line.TrimEnd().EndsWith(':'))
            {
                current = [];
                result[line.Trim().TrimEnd(':')] = current;
            }
            else
            {
                current?.Add(line.Trim());
            }
        }

        return result;
    }

    private static IEnumerable<(string File, string Service, List<string> Lines)> AllServices() =>
        new[] { "docker-compose.yml", Path.Combine("src", "HoldoutEvaluator", "compose.holdout.yml") }
            .SelectMany(f => Services(f).Select(s => (f, s.Key, s.Value)));

    [Fact]
    public void Every_service_has_memory_and_cpu_limits()
    {
        var services = AllServices().ToList();
        Assert.Equal(["henv-probe-research", "henv-probe-trading", "holdout-evaluator", "postgres", "research-host", "trading-host"],
            services.Select(s => s.Service).Order(StringComparer.Ordinal));
        Assert.All(services, s =>
        {
            Assert.Contains(s.Lines, l => l.StartsWith("mem_limit:", StringComparison.Ordinal));
            Assert.Contains(s.Lines, l => l.StartsWith("cpus:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Only_loopback_ports_are_published_and_the_evaluator_publishes_none()
    {
        foreach (var (_, service, lines) in AllServices())
        {
            var ports = lines.Where(l => l.StartsWith("- \"", StringComparison.Ordinal) && l.Contains(":5432\"", StringComparison.Ordinal) || l.Contains(":8080\"", StringComparison.Ordinal)).ToList();
            Assert.All(ports, p => Assert.StartsWith("- \"127.0.0.1:", p));
            if (service.StartsWith("holdout-evaluator", StringComparison.Ordinal) || service.StartsWith("henv", StringComparison.Ordinal) || service == "research-host")
            {
                Assert.DoesNotContain("ports:", lines);
            }
        }

        var compose = File.ReadAllText(Path.Combine(Root, "docker-compose.yml"));
        Assert.Contains("\"127.0.0.1:${PI_DEV_DB_PORT:-55432}:5432\"", compose);
        Assert.Contains("\"127.0.0.1:55490:8080\"", compose);
    }

    [Fact]
    public void Networks_and_static_addresses_match_the_topology_and_pg_hba()
    {
        var compose = File.ReadAllText(Path.Combine(Root, "docker-compose.yml"));
        foreach (var (network, subnet, isInternal) in new[] { ("ops", "172.30.1.0/24", false), ("eval", "172.30.2.0/24", true), ("holdout", "172.30.3.0/24", true), ("egress", "172.30.4.0/24", false) })
        {
            var block = compose[compose.IndexOf($"\n  {network}:\n", StringComparison.Ordinal)..];
            block = block[..block.IndexOf("subnet:", StringComparison.Ordinal)];
            Assert.Equal(isInternal, block.Contains("internal: true", StringComparison.Ordinal));
            Assert.Contains($"subnet: {subnet}", compose);
        }

        var services = AllServices().ToDictionary(s => s.Service, s => s.Lines);
        Assert.Contains("ipv4_address: 172.30.3.20", services["holdout-evaluator"]);
        Assert.Contains("ipv4_address: 172.30.1.20", services["trading-host"]);
        Assert.Contains("ipv4_address: 172.30.1.30", services["research-host"]);
        Assert.DoesNotContain(services["trading-host"], l => l.StartsWith("eval:", StringComparison.Ordinal) || l.StartsWith("holdout:", StringComparison.Ordinal));
        Assert.DoesNotContain(services["research-host"], l => l.StartsWith("holdout:", StringComparison.Ordinal));
        Assert.DoesNotContain(services["holdout-evaluator"], l => l.StartsWith("ops:", StringComparison.Ordinal));
        Assert.Contains("networks: [ops]", services["henv-probe-trading"]);
    }

    [Fact]
    public void Research_host_holds_only_its_login_and_the_submit_only_token()
    {
        // architecture §10, ADR-0019 §5: never a holdout role, status_relay or the read-status token.
        var services = AllServices().ToDictionary(s => s.Service, s => s.Lines);
        Assert.Contains("secrets: [research_app, submit_token]", services["research-host"]);
        Assert.Equal(1, services["research-host"].Count(l => l.StartsWith("secrets:", StringComparison.Ordinal)));
        Assert.DoesNotContain(services["research-host"], l => l.Contains("eval_", StringComparison.Ordinal) || l.Contains("status_relay", StringComparison.Ordinal)
                                                              || l.Contains("read_status_token", StringComparison.Ordinal) || l.Contains("Holdout", StringComparison.Ordinal));
        Assert.DoesNotContain(services["trading-host"], l => l.Contains("submit_token", StringComparison.Ordinal));
    }

    private static List<string[]> HbaRules() => File.ReadAllLines(Path.Combine(Root, "deploy", "postgres", "pg_hba.conf"))
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();

    [Fact]
    public void Pg_hba_admits_holdout_only_from_the_evaluator_and_the_superuser_only_on_the_socket()
    {
        var rules = HbaRules();
        Assert.Equal(["local", "all", "all", "scram-sha-256"], rules[0]);
        Assert.Equal(["host", "all", "pi_dev", "0.0.0.0/0", "reject"], rules[1]);
        var holdout = rules.Where(r => r[0] == "host" && r[1].Split(',').Any(d => d.StartsWith("holdout", StringComparison.Ordinal))).ToList();
        Assert.Equal(2, holdout.Count);
        Assert.Equal(["host", "holdout,holdout_henv", "eval_owner,eval_registry,eval_diagnostic,eval_prospective", "172.30.3.20/32", "scram-sha-256"], holdout[0]);
        Assert.Equal("reject", holdout[1][^1]);
        Assert.Equal("all", holdout[1][2]);
        Assert.True(rules.IndexOf(holdout[1]) == rules.IndexOf(holdout[0]) + 1);
        // No other rule names a holdout database or an eval_* role; holdout_synth is refused by the final rules.
        Assert.DoesNotContain(rules.Except(holdout), r => r.Length > 2 && (r[1].Contains("holdout", StringComparison.Ordinal) || r[2].Contains("eval_", StringComparison.Ordinal)));
        var relay = rules.Where(r => r.Length > 2 && r[2] == "status_relay").ToList();
        Assert.Equal(["host", "ops", "status_relay", "172.30.3.20/32", "scram-sha-256"], relay[0]);
        Assert.Equal("reject", relay[1][^1]);
        Assert.Equal(["host", "all", "all", "0.0.0.0/0", "reject"], rules[^2]);
        Assert.Equal(["host", "all", "all", "::/0", "reject"], rules[^1]);
        Assert.All(rules.Where(r => r[^1] != "reject"), r => Assert.Equal("scram-sha-256", r[^1]));
    }

    [Fact]
    public void Build_contexts_exclude_secrets_and_local_configuration()
    {
        var ignore = File.ReadAllLines(Path.Combine(Root, ".dockerignore")).Select(l => l.Trim()).ToList();
        foreach (var pattern in new[] { ".env", ".env.*", "**/secrets", "**/secrets/**", ".git", "data", "**/bin", "**/obj" })
        {
            Assert.Contains(pattern, ignore);
        }
    }
}
