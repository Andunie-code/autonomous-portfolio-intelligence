namespace PortfolioIntelligence.Architecture.Tests;

/// <summary>
/// Source scan of a module that must use no database, process, HTTP or holdout code (the Researcher pattern of
/// ADR-0017 §1, applied to the Allocator by ADR-0023 §1). Comment lines are ignored; obj and bin are skipped.
/// </summary>
public static class SourceScan
{
    private static readonly string[] Forbidden = ["Npgsql", "System.Diagnostics.Process", "Process.Start", "System.Net.Http", "HttpClient", "HoldoutEvaluator", "System.Data"];

    public static IReadOnlyList<string> Offenders(string directory) =>
        Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
            .Where(l => Forbidden.Any(token => l.Text.Contains(token, StringComparison.Ordinal)))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();
}
