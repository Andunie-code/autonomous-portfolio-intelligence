using PortfolioIntelligence.Kernel.Storage;

namespace PortfolioIntelligence.MarketData.Archive;

/// <summary>
/// Local cache for raw archive files (docs/m1-data-policy.md §3 item 7). Raw files must never live in the
/// repository or in a OneDrive-synchronised folder, so such paths are rejected.
/// </summary>
public sealed class ArchiveCacheDirectory
{
    public ArchiveCacheDirectory(string root, string repositoryRoot)
    {
        Root = LocalCacheRoot.Validate(root, repositoryRoot, "archive cache", nameof(root));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortfolioIntelligence", "archive-cache");

    public string PathFor(string archivePath) => Path.Combine(Root, "files", archivePath.Replace('/', Path.DirectorySeparatorChar));

    public string IncidentLogPath => Path.Combine(Root, "incidents.jsonl");

    public string ReportsDirectory => Directory.CreateDirectory(Path.Combine(Root, "reports")).FullName;
}
