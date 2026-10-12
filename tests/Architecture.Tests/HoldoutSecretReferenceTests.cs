namespace PortfolioIntelligence.Architecture.Tests;

/// <summary>
/// ADR-0009: holdout database credentials and the evaluator token exist only in the holdout
/// environment. No project, configuration file or script outside a future HoldoutEvaluator
/// project may even name them.
/// </summary>
public class HoldoutSecretReferenceTests
{
    [Fact]
    public void No_file_outside_the_holdout_evaluator_names_holdout_secrets()
    {
        var violations = HoldoutSecretScanner.FindViolations(RepositoryRoot.Path, HoldoutSecretScanner.FilesToScan(RepositoryRoot.Path));

        Assert.True(violations.Count == 0, "Holdout secret identifiers found:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Scanner_detects_a_planted_reference()
    {
        var root = Directory.CreateTempSubdirectory("pi-holdout-scan-");
        try
        {
            var planted = Path.Combine(root.FullName, "appsettings.json");
            File.WriteAllText(planted, """{ "ConnectionStrings": { "HoldoutDb": "x" } }""");
            var allowed = Directory.CreateDirectory(Path.Combine(root.FullName, "PortfolioIntelligence.HoldoutEvaluator"));
            File.WriteAllText(Path.Combine(allowed.FullName, "settings.json"), "HoldoutDb");

            var violations = HoldoutSecretScanner.FindViolations(root.FullName, Directory.EnumerateFiles(root.FullName, "*", SearchOption.AllDirectories));

            Assert.Single(violations);
            Assert.Contains("appsettings.json", violations[0]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}

internal static class HoldoutSecretScanner
{
    // Identifiers reserved for the holdout environment (ADR-0009). Matching is case-insensitive.
    // docs/ and tests/ are not scanned: they describe and exercise these names.
    private static readonly string[] ReservedIdentifiers =
    [
        "holdoutdb",
        "holdout_db",
        "connectionstrings:holdout",
        "connectionstrings__holdout",
        "evaluatortoken",
        "evaluator_token",
    ];

    private static readonly string[] ScannedExtensions = [".cs", ".csproj", ".props", ".json", ".yml", ".yaml", ".sql", ".ps1", ".sh", ".example", ".env"];

    public static IEnumerable<string> FilesToScan(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith(".git/", StringComparison.Ordinal) || relative.Contains("/bin/") || relative.Contains("/obj/")
                || relative.StartsWith("docs/", StringComparison.Ordinal) || relative.StartsWith("tests/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    public static List<string> FindViolations(string root, IEnumerable<string> files)
    {
        var violations = new List<string>();
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("HoldoutEvaluator", StringComparison.Ordinal) ||
                !ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = File.ReadAllText(file);
            foreach (var identifier in ReservedIdentifiers)
            {
                if (content.Contains(identifier, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{relative}: '{identifier}'");
                }
            }
        }

        return violations;
    }
}
