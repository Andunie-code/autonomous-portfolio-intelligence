namespace PortfolioIntelligence.MarketData.Archive;

/// <summary>
/// Local cache for raw archive files (docs/m1-data-policy.md §3 item 7). Raw files must never live in the
/// repository or in a OneDrive-synchronised folder, so such paths are rejected.
/// </summary>
public sealed class ArchiveCacheDirectory
{
    public ArchiveCacheDirectory(string root, string repositoryRoot)
    {
        var full = Path.GetFullPath(root);
        if (IsInside(full, Path.GetFullPath(repositoryRoot)))
        {
            throw new ArgumentException("The archive cache must be outside the repository.", nameof(root));
        }

        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var oneDrive = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(oneDrive) && IsInside(full, Path.GetFullPath(oneDrive)))
            {
                throw new ArgumentException("The archive cache must be outside OneDrive.", nameof(root));
            }
        }

        if (full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The archive cache must be outside OneDrive.", nameof(root));
        }

        Root = full;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortfolioIntelligence", "archive-cache");

    public string PathFor(string archivePath) => Path.Combine(Root, "files", archivePath.Replace('/', Path.DirectorySeparatorChar));

    public string IncidentLogPath => Path.Combine(Root, "incidents.jsonl");

    public string ReportsDirectory => Directory.CreateDirectory(Path.Combine(Root, "reports")).FullName;

    private static bool IsInside(string path, string parent)
    {
        var normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return (path + Path.DirectorySeparatorChar).StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }
}
