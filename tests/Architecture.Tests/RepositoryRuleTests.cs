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
