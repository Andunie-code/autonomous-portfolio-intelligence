using System.Text.Json;

namespace PortfolioIntelligence.Architecture.Tests;

/// <summary>Repository guardrails from architecture.md §10 (T17) and the secret-handling rules.</summary>
public class RepositoryRuleTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData("secrets/")]
    [InlineData("data/")]
    [InlineData("*.pem")]
    [InlineData("*.zip")]
    [InlineData(".claude/settings.local.json")]
    [InlineData("deploy/postgres/pg_hba.local.conf")]
    public void Gitignore_excludes_sensitive_paths(string pattern)
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot.Path, ".gitignore")).Select(l => l.Trim());

        Assert.Contains(pattern, lines);
    }

    [Theory]
    [InlineData("Read(.env)")]
    [InlineData("Read(~/.ssh/**)")]
    [InlineData("Bash(ssh *)")]
    [InlineData("Bash(scp *)")]
    [InlineData("PowerShell(ssh *)")]
    [InlineData("PowerShell(scp *)")]
    [InlineData("WebFetch(domain:data.binance.vision)")]
    [InlineData("Bash(docker exec*)")]
    [InlineData("PowerShell(docker exec*)")]
    [InlineData("Bash(docker cp*)")]
    [InlineData("PowerShell(docker cp*)")]
    [InlineData("Bash(*--allow-network*)")]
    [InlineData("PowerShell(*--allow-network*)")]
    [InlineData("Bash(*AllowNetwork*)")]
    [InlineData("PowerShell(*AllowNetwork*)")]
    [InlineData("Read(~/AppData/Roaming/Microsoft/UserSecrets/**)")]
    [InlineData("Read(**/UserSecrets/**)")]
    [InlineData("Bash(*user-secrets*)")]
    [InlineData("PowerShell(*user-secrets*)")]
    [InlineData("Bash(*Gemini__ApiKey*)")]
    [InlineData("PowerShell(*Gemini__ApiKey*)")]
    [InlineData("WebFetch(domain:data-api.binance.vision)")]
    [InlineData("WebFetch(domain:api.binance.com)")]
    [InlineData("PowerShell(*api.binance.com*)")]
    [InlineData("PowerShell(*-File*m5-bootstrap*)")]
    [InlineData("PowerShell(*-File*m5-deploy*)")]
    [InlineData("PowerShell(*-File*m5-henv*)")]
    [InlineData("PowerShell(*-File*m5-seed*)")]
    [InlineData("PowerShell(*-File*m5-migrate*)")]
    [InlineData("PowerShell(*-File*m5-paper-start*)")]
    [InlineData("PowerShell(*-File*m5-paper-report*)")]
    [InlineData("PowerShell(*-File*m5-backup*)")]
    [InlineData("PowerShell(*-File*m5-restore-test*)")]
    [InlineData("PowerShell(*-File*m5-cohort1*)")]
    [InlineData("Bash(*-File*m5-cohort1*)")]
    [InlineData("PowerShell(*-File*m5-update*)")]
    [InlineData("Bash(*-File*m5-update*)")]
    [InlineData("Bash(docker compose exec*)")]
    [InlineData("PowerShell(docker compose exec*)")]
    [InlineData("PowerShell(docker compose*holdout-evaluator*)")]
    [InlineData("PowerShell(docker compose*henv-probe*)")]
    [InlineData("PowerShell(docker compose*trading-host*)")]
    [InlineData("PowerShell(docker compose*--profile*)")]
    [InlineData("PowerShell(*psql*holdout*)")]
    [InlineData("WebFetch(domain:testnet.binance.vision)")]
    [InlineData("Bash(curl *testnet.binance.vision*)")]
    [InlineData("Bash(wget *testnet.binance.vision*)")]
    [InlineData("PowerShell(*testnet.binance.vision*)")]
    [InlineData("PowerShell(*-File*m6-*)")]
    [InlineData("Bash(*-File*m6-*)")]
    [InlineData("WebFetch(domain:export.arxiv.org)")]
    [InlineData("Bash(*export.arxiv.org*)")]
    [InlineData("PowerShell(*export.arxiv.org*)")]
    [InlineData("Bash(*arxiv.org/pdf*)")]
    [InlineData("PowerShell(*arxiv.org/pdf*)")]
    [InlineData("Bash(*-File*m7-*)")]
    [InlineData("PowerShell(*-File*m7-*)")]
    [InlineData("Bash(*-File*m8-*)")]
    [InlineData("PowerShell(*-File*m8-*)")]
    public void Claude_project_settings_deny_holdout_and_secret_access(string rule)
    {
        Assert.Contains(rule, ReadPermissionList("deny"));
    }

    [Theory]
    [InlineData("Edit(**/*Holdout*)")]
    [InlineData("Edit(**/*Holdout*/**)")]
    public void Claude_project_settings_require_approval_for_holdout_code(string rule)
    {
        Assert.Contains(rule, ReadPermissionList("ask"));
    }

    [Fact]
    public void Claude_project_settings_contain_no_allow_rules()
    {
        // Project settings only restrict; any allowance stays a per-user decision.
        using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.False(settings.RootElement.GetProperty("permissions").TryGetProperty("allow", out _));
    }

    [Theory]
    [InlineData("## Holdout kuralları")]
    [InlineData("## Sırlar")]
    [InlineData("## Onay gerektiren işlemler")]
    [InlineData("**M6 Testnet (m6-protocol §1, §8, §11; D33):**")]
    public void Claude_instructions_contain_required_sections(string heading)
    {
        Assert.Contains(heading, File.ReadAllText(Path.Combine(RepositoryRoot.Path, "CLAUDE.md")));
    }

    private static string SettingsPath => Path.Combine(RepositoryRoot.Path, ".claude", "settings.json");

    private static List<string> ReadPermissionList(string name)
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        return settings.RootElement.GetProperty("permissions").GetProperty(name)
            .EnumerateArray().Select(e => e.GetString()!).ToList();
    }
}
